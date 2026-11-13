using System.Xml;
using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;
using RimDev.Core.Metadata;

namespace RimDev.Core.Checks;

/// <summary>
/// ★ 本期最有价值的一条：把 XML 元素名对着目标 Def 类的**真实字段表**校验。
///
/// 动机（项目历史）：静态校验（XML 良构 + defName 去重）**永远查不出「字段名不存在」**。
/// 本项目 10 类加载期错误里 6 类是名字不存在，而当时静态校验全绿。
/// 例：canBeDamagedByExplosions / canBeDeconstructed / deconstructibleBlocks 三个字段
/// 在程序集里**根本不存在**，却写成 XML 后被游戏报"doesn't correspond to any field"。
///
/// 实现要点：
///  - 复用 <see cref="MemberInfoIndex"/>：字段枚举 = Public|NonPublic|Instance（不含 Static），
///    大小写敏感。★ 这一条不能改：VerbProperties.forcedMissRadius 是 private 字段但合法。
///  - ★★ 第 3 期 Task 2b 的**两条机制**（全库 30246 条 RD4001 里绝大多数假阳的根源）：
///    <b>机制 A</b>：解析顺序 = **def 元素上的 `Class=` 优先，其次元素名**。RimWorld 就是按 `Class=`
///      实例化并反射填字段的；用元素名（如 `ThingDef`）去查字段会把 `&lt;Def Class="Rimatomics…"&gt;`、
///      `&lt;ThingDef Class="CombatExtended.AmmoDef"&gt;` 里的合法字段全报成错。
///    <b>机制 B</b>：名字**含 `.`** 时禁止退化到简单名。`VEF.Abilities.AbilityDef` 在 Managed 里不存在，
///      `Find()` 却取末段 `AbilityDef` 兜底命中 `RimWorld.AbilityDef`，于是拿 VEF 自定义类型的字段
///      去查原版类型（实测 17623 条）。名字不含 `.`（`ThingDef`/`HediffDef`）仍走原逻辑 ——
///      这条路径必须保留抓真缺陷的能力（如 `ThingDef` 上写 `renderNodeProperties`）。
///  - 只校验**能确定目标 C# 类型**的 def；解析不到的报 RD2017（info）而不是当作错误。
///  - 只校验**顶层字段与一级子元素**：`&lt;comps&gt;&lt;li Class="..."&gt;` 这类多态节点的内层字段
///    需要按 Class 分派，属第 3 期后续（本 Task 明确不动；上面的「机制 A」只针对 **def 元素自身**的 Class=）。
///  - ★★ 第 3 期 Task 5c：**归属键 = (def 起始行, 该行上第几个 def)**，不是「def 起始行」。
///    动机（`08` §5.6 取证）：RimWorld **完全接受**一个 XML 文件里多个 def 落在同一物理行（紧凑排版很常见），
///    而原实现按 `defLine` 单键建桶 ⇒ 同一行的 N 个 def **各自被喂了 N 个 def 的全部一级子元素的并集**，
///    于是「另一个 def 的字段」被拿本 def 的类型去校验（全库 66 条 RD4001 假阳 / `active` 31 条）。
///    ★ 「张冠李戴」不能换成「整段偏移」：桶键的**制造侧与消费侧必须对「什么算一个 def」用同一套判据**。
///    本实现把这件事做成三条同时成立的不变量（任何一条被破坏都会当场变红）：
///      (i) **单一判据**：桶的制造侧（<see cref="ScanDefs"/>）与消费侧（<see cref="Run"/> 的
///          `(line, ordinal)` 归组）都只把 **depth==1 的元素**当成 def，序数在同一套文档序里数；
///      (ii) **可对账**：<see cref="ProbeStructure"/> 把「元素扫描实际数到的 def 行/序号」与
///          「DefScanner 报出的 RawDef 行号」并列返回，可在真语料上逐文件断言两者相等
///          （见自测 `phase3-task5c/★ 结构对账`）；
///      (iii) **错位必须响亮地失败**：<see cref="Run"/> 先验「元素扫描数到的 def 个数 == 本文件参与校验的
///          RawDef 个数」且逐个行号一致；不一致就报 **RD2001（error）** 并**跳过该文件**
///          —— 绝不静默按行号取一个可能属于邻居的桶（静默的错答案比报错贵得多）。
///      ⇒ 取任务书 Step 1 的 **(a) 路线**，但把它做成「(a) + 结构性防错位」：键里带序数，
///        同时用 (iii) 把「两侧判据不一致」这一失败模式从**静默**变成**响亮**。
///  - ★ **每个文件只读一次**（Task 6 修正 ⑤）：按 <c>ctx.Defs.GroupBy(d =&gt; d.FileAbs)</c> 分组，
///    文件打开一次、按 <c>Line</c> 把元素归属到对应 def。若对每个 def 各读一遍自己的文件，
///    active 作用域 14480 个 def、<c>--scope all</c> 342520 个 def 会变成几十万次读盘。
/// </summary>
public sealed class FieldNameCheck : ICheck
{
    // ★★ 第 3 期 Task 2c：本规则现在建模引擎 DoFieldSearch 的**全部五条分支**（缺任一条都会造假阳或漏报）：
    //   ① 直接按名命中（先大小写敏感，再大小写不敏感；后者引擎会 Log.Error「xml tags are now case-sensitive」⇒ 我们报 RD4005）；
    //   ② 字段别名 LoadAliasAttribute（EqualsIgnoreCase）⇒ 命中即**不报** RD4001；
    //   ②' 别名命中后若字段带 [Unsaved] 且 allowLoading=false ⇒ 引擎 Log.Error 并返回 null ⇒ RD4009(error)。
    //       ★ 可达性（逐行 + IL 实证）：直接命中会**提前 return**，所以 [Unsaved] 检查**只在别名路径上**可达；
    //         原版 41 个别名字段里 0 个带 [Unsaved]，故全库 RD4009 = 0（不是漏实现）。
    //   ④ ①②都没命中时：元素属性 IgnoreIfNoMatchingField="true"（InvariantCultureIgnoreCase），
    //      或类型属性 IgnoreSavedElementAttribute.elementToIgnore 命中（OrdinalIgnoreCase）
    //      ⇒ 引擎**故意不打**那条 doesn't correspond to any field ⇒ 我们报 RD4010(info)，**不报** RD4001；
    //   ⑤ 全不成立 ⇒ 引擎真的 Log.Error ⇒ RD4001(error)。
    //   元素属性由本类的元素扫描层采集（ScanDefs —— 原来只取元素名与行号、丢掉属性；Task 5c 起同时产出 def 归属键）。
    public string Id => Codes.FieldNotFound;

    public IEnumerable<Diagnostic> Run(CheckContext ctx)
    {
        ManagedAssemblyCatalog? catalog = null;
        try { catalog = ManagedAssemblyCatalog.Load(ReadManagedDir()); }
        catch { /* 元数据不可用时静默跳过（v1 已有独立命令覆盖该场景） */ }
        // ★ 形态确定：不在 catch 里 yield break ——
        //   迭代器里 try/catch 与 yield 的组合限制不靠记忆，改成「先赋值，再判 null」。
        if (catalog is null) yield break;

        // ★ 报告过滤器在【每个文件的分组内】做：只报目标 mod 的 def，文件仍然只读一次。
        foreach (var g in ctx.Defs.GroupBy(d => d.FileAbs, StringComparer.OrdinalIgnoreCase))
        {
            var defsInFile = new List<RawDef>();
            foreach (var d in g)
                if (ctx.TargetModPackageId is null || d.ModPackageId == ctx.TargetModPackageId)
                    defsInFile.Add(d);
            if (defsInFile.Count == 0) continue;

            // ★★ 第 3 期 Task 5c：结构化读一次文件 —— 每个 def 的**身份（起始行 + 该行上的序号）**
            //   与它的**一级子元素**在同一遍里产出，杜绝「两次分开判定 def」造成的错位。
            var scannedDefs = ScanDefs(g.Key);

            // ★★ 第 5c 的**响亮失败点**：桶的制造侧（本类的元素扫描）与消费侧（DefScanner 产出的 RawDef）
            //   必须对「什么算一个 def」给出**同一个集合**（个数相同 + 逐个行号相同）。
            //   不一致时**绝不**按行号取一个可能属于邻居 def 的桶 —— 那会把「张冠李戴」换成「整段偏移」，
            //   症状同样静默。这里报 RD2001(error) 并跳过整个文件，属于工具边界错误，不是内容错误。
            if (scannedDefs.Count != defsInFile.Count
                || !scannedDefs.Select(x => x.DefLine).SequenceEqual(defsInFile.Select(x => x.Line)))
            {
                yield return new Diagnostic(Codes.XmlParseFailed, Severity.Error, Scope.Offline,
                    $"def 归属键与 DefScanner 的 def 集合不一致（{scannedDefs.Count} vs {defsInFile.Count}），"
                    + "本文件的字段名校验被跳过以免把邻居 def 的字段当成本 def 的",
                    "这是本工具的边界错误：元素扫描（depth==1）与 DefScanner（depth==1）本应给出同一集合；"
                    + "把该文件与出现次数报给工具作者",
                    FileAbs: g.Key, Line: defsInFile[0].Line,
                    ModPackageId: defsInFile[0].ModPackageId,
                    Evidence: new Dictionary<string, string>
                    {
                        ["scannedDefs"] = scannedDefs.Count.ToString(),
                        ["rawDefs"] = defsInFile.Count.ToString(),
                        ["scannedLines"] = string.Join(",", scannedDefs.Take(8).Select(x => x.DefLine)),
                        ["rawLines"] = string.Join(",", defsInFile.Take(8).Select(x => x.Line)),
                    });
                continue;
            }

            // 桶的键 = 「def 起始行 + 该行上第几个 def」。序数按**同一个文档序**数，
            // 与 ScanDefs 的 ordinals 是同一套算式（见 ReadElementsByDef 的注释）。
            var elementsByDefKey = scannedDefs
                .GroupBy(x => x.DefLine)
                .SelectMany(line => line.Select((d, i) => (Line: line.Key, Ordinal: i, d.Children)))
                .ToDictionary(x => (x.Line, x.Ordinal), x => x.Children);

            for (int defIndex = 0; defIndex < defsInFile.Count; defIndex++)
            {
                var d = defsInFile[defIndex];

                // ★ 消费侧按 DefScanner 的**文件内文档序**（= ctx.Defs 的给出顺序）重算同一个序数
                //   —— 这就是 parity 门禁在保护的那条不变量。故意不依赖上文的 scannedDefs 下标，
                //   这样「两侧顺序漂移」也能被下面的 TryGetValue 兜住（取不到就跳过，不会取错）。
                if (!elementsByDefKey.TryGetValue((d.Line, OrdinalWithinLine(defsInFile, defIndex)), out var elements))
                    continue;
                // ★★ 第 3 期 Task 2b 机制 A + B：
                //   解析顺序 = **def 元素上的 Class= 优先，其次元素名**（RimWorld 就是按 Class= 实例化的）。
                //   并交给 ResolveXmlTypeName：名字含 '.' 时【禁止】退化到简单名（机制 B）。
                //   两条合起来把全库 30246 条 RD4001 里的假阳拿掉，而不伤及对原版元素名的真校验。
                var typeName = d.ResolvedTypeName;
                var type = catalog.ResolveXmlTypeName(typeName);
                if (type is null)
                {
                    yield return new Diagnostic(Codes.UnknownDefType, Severity.Info, Scope.Offline,
                        UnknownTypeMessage(d, typeName),
                        UnknownTypeHint(d),
                        FileAbs: d.FileAbs, Line: d.Line,
                        ModPackageId: d.ModPackageId, DefName: d.DefName, DefType: d.DefType,
                        Evidence: new Dictionary<string, string>
                        {
                            ["resolvedTypeName"] = typeName,
                            ["resolutionSource"] = d.ClassAttr is null ? "elementName" : "ClassAttribute",
                            ["simpleNameFallback"] = typeName.Contains('.') ? "forbidden(dotted)" : "attempted"
                        });
                    continue;
                }

                var members = catalog.MembersOf(type);

                foreach (var (elementName, line, ignoreIfNoMatchingField) in elements)
                {
                    if (elementName is "defName" or "ParentName" or "Name" or "Abstract" or "MayRequire") continue;

                    // ═══ ① 直接按名命中（引擎 DirectGetFieldByName）⇒ 引擎直接 return，什么都不报。
                    if (members.FindField(elementName) is not null) continue;

                    // ═══ ② 字段别名（引擎 DoFieldSearch 的第二个分支）—— RD4001 的第二条抑制条件。
                    //   实测语料（1.6.4871 rev591）；41 个字段 / 26 个类型 / 28 个别名，例如
                    //   PawnKindDef.defaultFactionDef 带 [LoadAlias("defaultFactionType")]
                    //   ⇒ <defaultFactionType> 引擎照收（全库 2565 条 RD4001 属这类假阳）。
                    if (members.FindFieldByAlias(elementName) is { } aliasField)
                    {
                        // 引擎在【别名分支之后】才检查 [Unsaved]：直接命中会提前 return，根本走不到这段
                        // （IL: 0x0010 brfalse → 0x0019 ret；0x00BC 才是 TryGetAttribute<UnsavedAttribute>()）。
                        // 而原版 41 个别名字段里 0 个带 [Unsaved] ⇒ 这条在原版类型上恒不触发；但它是引擎
                        // **真会 Log.Error 并丢弃值**的一类，必须留着（mod 类型将来会有）。
                        if (RimWorldAttributes.TryGetUnsavedAllowLoading(aliasField, out var allowLoading)
                            && !allowLoading)
                        {
                            yield return new Diagnostic(Codes.UnsavedFieldBlocked, Severity.Error, Scope.Offline,
                                $"{typeName} 的字段 `{aliasField.Name}` 带 [Unsaved]（allowLoading=false），"
                                + $"而 XML 用它的别名 `{elementName}` 写了值；引擎会报错并丢弃该值",
                                "删掉该元素；[Unsaved] 由 C# 类型定义决定，XML 侧改不掉",
                                FileAbs: d.FileAbs, Line: line,
                                ModPackageId: d.ModPackageId, DefName: d.DefName, DefType: d.DefType,
                                Evidence: new Dictionary<string, string>
                                {
                                    ["defTypeFullName"] = type.FullName ?? type.Name,
                                    ["validatedTypeName"] = typeName,
                                    ["resolutionSource"] = d.ClassAttr is null ? "elementName" : "ClassAttribute",
                                    ["matchedBy"] = "alias",
                                    ["alias"] = elementName,
                                    ["fieldName"] = aliasField.Name,
                                    ["fieldDeclaringType"] = aliasField.DeclaringType?.FullName ?? "(unknown)",
                                    ["fieldType"] = aliasField.FieldType.Name,
                                    ["allowLoading"] = "false"
                                });
                        }
                        // 别名命中 ⇒ 引擎把值填进 aliasField ⇒ **不许**报 RD4001。
                        continue;
                    }

                    if (members.FindProperty(elementName) is not null) continue;

                    // ═══ ④ 抑制（引擎的 flag 分支）：引擎**故意不打**那条 doesn't correspond to any field。
                    //   引擎原文：先看元素属性 IgnoreIfNoMatchingField 是否等于 "true"
                    //   （StringComparison.InvariantCultureIgnoreCase）；不成立再看**类型属性**
                    //   IgnoreSavedElementAttribute.elementToIgnore 是否等于元素名（OrdinalIgnoreCase）。
                    string? suppressedBy = null;
                    string? suppressedDetail = null;
                    if (ignoreIfNoMatchingField is not null
                        && ignoreIfNoMatchingField.Equals("true", StringComparison.InvariantCultureIgnoreCase))
                    {
                        suppressedBy = "elementAttribute";
                        suppressedDetail = ignoreIfNoMatchingField;
                    }
                    else if (members.IgnoresSavedElement(elementName))
                    {
                        suppressedBy = "typeAttribute";
                        suppressedDetail = elementName;
                    }
                    if (suppressedBy is not null)
                    {
                        yield return new Diagnostic(Codes.MissingFieldSuppressed, Severity.Info, Scope.Offline,
                            $"{typeName} 上没有字段 `{elementName}`，但"
                            + (suppressedBy == "elementAttribute"
                                ? "该元素带 IgnoreIfNoMatchingField，"
                                : "所属类型声明了 IgnoreSavedElementAttribute，")
                            + "引擎据此**故意不报**（因此不计入 RD4001）",
                            suppressedBy == "elementAttribute"
                                ? "跨版本兼容写法：引擎不报错，但该元素的值会被静默忽略"
                                : "类型级忽略：引擎不报错，但该元素的值会被静默忽略",
                            FileAbs: d.FileAbs, Line: line,
                            ModPackageId: d.ModPackageId, DefName: d.DefName, DefType: d.DefType,
                            Evidence: new Dictionary<string, string>
                            {
                                ["suppressedBy"] = suppressedBy,
                                // ★ 第 3 期 Task 3 修掉 CS8601（基线构建里有 1 条警告，与本 Task 的交付无关）：
                                //   suppressedDetail 声明为 string?，但能走到这里的两个分支都给它赋了非 null 值，
                                //   `?? ""` 是纯消除可空警告，不改变任何行为。
                                ["suppressedDetail"] = suppressedDetail ?? "",
                                ["defTypeFullName"] = type.FullName ?? type.Name,
                                ["validatedTypeName"] = typeName,
                                ["resolutionSource"] = d.ClassAttr is null ? "elementName" : "ClassAttribute"
                            });
                        continue;
                    }

                    // ═══ ⑤ 三条分支全不成立 ⇒ 引擎真的 Log.Error（我们报 RD4001 / RD4005）。
                    var suggestions = members.Suggest(elementName, 5);
                    var caseOnly = suggestions.FirstOrDefault(s => string.Equals(s, elementName, StringComparison.OrdinalIgnoreCase));
                    yield return new Diagnostic(
                        caseOnly is not null ? Codes.CaseMismatch : Codes.FieldNotFound,
                        Severity.Error, Scope.Offline,
                        $"{typeName} 上没有字段 `{elementName}`",
                        caseOnly is not null ? $"大小写不符，正确写法是 {caseOnly}" : "用候选名替换",
                        FileAbs: d.FileAbs, Line: line,
                        ModPackageId: d.ModPackageId, DefName: d.DefName, DefType: d.DefType,
                        Suggestions: suggestions,
                        Evidence: new Dictionary<string, string>
                        {
                            ["defTypeFullName"] = type.FullName ?? type.Name,
                            // ★ Task 2b：把「校验用的是哪个名字、来自哪里」写进证据 ——
                            //   这样以后看到 `defTypeFullName` 与元素名不同，就能一眼分辨
                            //   「Class= 指定」与「旧版的简单名兜底命中」（后者已不可能再发生）。
                            ["validatedTypeName"] = typeName,
                            ["resolutionSource"] = d.ClassAttr is null ? "elementName" : "ClassAttribute",
                            ["publicFields"] = members.PublicFieldCount.ToString(),
                            ["allFields"] = members.AllFieldCount.ToString()
                        });
                }
            }
        }
    }

    /// <summary>
    /// RD2017 的文案：按「类型名来自哪里」分三种形状 —— 三者都是【工具边界】，不是内容错误。
    /// 分开写的理由：使用者看到这类 info 时需要知道「我该去干什么」。
    /// <list type="bullet">
    /// <item>Class= 指向 mod 类型 → mod 自己的程序集没加载，本工具校验不了；</item>
    /// <item>元素名含 '.' → 看起来是完整类型名，但我们【不】退化到简单名（否则会把
    ///   `VEF.Abilities.AbilityDef` 的字段拿去查 `RimWorld.AbilityDef`）；</item>
    /// <item>普通元素名解析不到 → mod 自定义 Def 类型（如 TF_MechModuleDef）。</item>
    /// </list>
    /// </summary>
    private static string UnknownTypeMessage(RawDef d, string typeName)
    {
        if (!string.IsNullOrWhiteSpace(d.ClassAttr))
            return $"Def 元素写的是 Class=\"{typeName}\"，该类型不在已加载程序集里，字段名校验被跳过";
        if (typeName.Contains('.'))
            return $"Def 元素名 `{typeName}` 看起来是完整类型名（含命名空间），在 Managed 里找不到；"
                 + "不退化到简单名以免误报，字段名校验被跳过";
        return $"Def 类型 `{typeName}` 在 Managed 里找不到对应 C# 类型，字段名校验被跳过";
    }

    /// <summary>RD2017 的建议文案（与 <see cref="UnknownTypeMessage"/> 同一套分支）。</summary>
    private static string UnknownTypeHint(RawDef d)
    {
        if (!string.IsNullOrWhiteSpace(d.ClassAttr))
            return "Class= 指向的类型需要 mod 自己的程序集；本期只加载原版 Assembly-CSharp.dll，故只能报 RD2017（info）";
        if (d.ResolvedTypeName.Contains('.'))
            return "带命名空间的类型名只在【全名精确匹配】时才算解析成功（简单名兜底会拿另一个类型的字段表校验，那是假阳的根源）";
        return "自定义 Def 类型需要 mod 自己的程序集；本期只校验原版类型";
    }

    /// <summary>
    /// ★★ 第 3 期 Task 5c 的**归属键**：<c>(def 起始行, 该行上第几个 def)</c>。
    ///
    /// 为什么必须有第二维：RimWorld 的 XML 里**同一物理行可以写多个 def**（紧凑排版很常见，
    /// 本机 ThreatFleet `TF_Buildings.xml:9` 一行 4 个 def），而按行号单键建桶会让同一行的每个 def
    /// 都拿到「这行上全部 def 的一级子元素并集」⇒ 邻居 def 的字段被拿本 def 的类型校验（RD4001 假阳）。
    ///
    /// ★ 序数的口径（制造侧与消费侧必须是**同一个算式**，否则会从「张冠李戴」变成「整段偏移」）：
    ///   把 <see cref="ScanDefs"/> 返回的 def 按 <c>DefLine</c> **按文档序**分组，组内第 i 个（0 基）的序数就是 i。
    ///   消费侧对 <c>ctx.Defs</c> 的同文件切片做完全相同的分组（见 <see cref="OrdinalWithinLine"/>）。
    ///   两侧能对齐的前提是「两者的文件内顺序都等于文档序」—— 由 <see cref="Run"/> 的 parity 门禁
    ///   （个数 + 逐个行号）显式守住：不成立就报 RD2001 并跳过该文件，绝不静默取错桶。
    /// </summary>
    public static Dictionary<(int Line, int Ordinal), List<(string Name, int Line, string? IgnoreIfNoMatchingField)>>
        ReadElementsByDef(string fileAbs)
    {
        var byKey = new Dictionary<(int, int), List<(string Name, int Line, string? IgnoreIfNoMatchingField)>>();
        foreach (var lineGroup in ScanDefs(fileAbs).GroupBy(x => x.DefLine))
            foreach (var (def, ordinal) in lineGroup.Select((x, i) => (x, i)))
                byKey[(lineGroup.Key, ordinal)] = def.Children.ToList();
        return byKey;
    }

    /// <summary>
    /// <c>defs</c> 里第 <paramref name="index"/> 个 def 在**它自己那一行上**的序号（在同一个文件内计数）。
    /// 这是消费侧唯一的序数算式；与 <see cref="ReadElementsByDef"/> 的制造侧算式必须一致。
    /// </summary>
    public static int OrdinalWithinLine(IReadOnlyList<RawDef> defs, int index)
    {
        int ordinal = 0;
        for (int i = 0; i < index; i++)
            if (defs[i].Line == defs[index].Line) ordinal++;
        return ordinal;
    }

    /// <summary>
    /// 读某个 def 的一级子元素名/行号/抑制属性。只读该文件一次。
    /// ★ 第 3 期 Task 5c：必须同时给<b>同一行上的序号</b>（<paramref name="ordinalWithinLine"/>），
    ///   否则同一行有多个 def 时无法区分「这是谁的子元素」。
    /// </summary>
    public static List<(string Name, int Line, string? IgnoreIfNoMatchingField)> ReadTopLevelElements(
        string fileAbs, int startLine, int ordinalWithinLine = 0) =>
        ScanDefs(fileAbs)
            .Where(x => x.DefLine == startLine)
            .Skip(ordinalWithinLine)
            .Take(1)
            .SelectMany(x => x.Children)
            .ToList();

    /// <summary>
    /// ★★ 结构对账探针（第 3 期 Task 5c）：把「元素扫描实际数到的 def 行 + 逐行序号」与
    /// 「DefScanner 报出的 RawDef 行号」并列返回，供自测在真语料上断言两者相等。
    ///
    /// 存在意义：这是「两侧判据是否同一套」的**正面证据**。没有它，「(line, ordinal) 不会错位」
    /// 只是一句推断；有了它，全库任意文件都能当场验。
    /// </summary>
    public static IReadOnlyList<(int ElementLine, int OrdinalWithinLine, int ChildCount)> ProbeStructure(string fileAbs) =>
        ScanDefs(fileAbs)
            .GroupBy(x => x.DefLine)
            .SelectMany(g => g.Select((d, i) => (ElementLine: g.Key, OrdinalWithinLine: i, ChildCount: d.Children.Count)))
            .ToList();

    /// <summary>
    /// 底层单遍扫描：返回**每个 def** 的 (起始行, 该行上的序号, 它的一级子元素)。
    ///
    /// ★ 「什么算一个 def」在这里**只有一份判据**：<c>reader.Depth == 1</c>（根 <c>&lt;Defs&gt;</c> 的子元素），
    ///   与 <see cref="RimDev.Core.Defs.DefScanner.ScanFile"/> 完全同源 —— 后者也是 depth==1 即建一个 RawDef。
    ///   ★ 与「行号取桶」的旧实现的关键差别：这里**同时**产出身份与内容，两者不经过第二次独立遍历，
    ///     所以不可能出现「按行号取到邻居」的形态。
    ///
    /// ★★ 第 3 期 Task 2c 的采集侧改动也保留在这一层：原来的实现只取元素名与行号、**丢掉了属性**，
    ///   而 RD4010（抑制条件④）判的恰恰是元素属性 <c>IgnoreIfNoMatchingField</c>。
    ///     · def 元素自身（depth==1）用得到的 <c>Class=</c> 已由 DefScanner 收进 RawDef（Task 2b）；
    ///       而且引擎**从不**把 def 元素本身当 fieldNode 传给 DoFieldSearch（fieldNode 永远是
    ///       `xmlRoot.ChildNodes[i]`），所以 def 元素上的 IgnoreIfNoMatchingField 语义上根本无效。
    ///     · depth&gt;=3 的更深元素不在本规则范围内（多态节点内层字段是另一个 Task），不采集。
    /// </summary>
    public static List<ScannedDef> ScanDefs(string fileAbs)
    {
        var defs = new List<ScannedDef>();
        try
        {
            using var stream = File.OpenRead(fileAbs);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                IgnoreComments = true, IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Prohibit
            });
            var li = (IXmlLineInfo)reader;

            int currentLine = -1;
            int currentOrdinal = 0;
            List<(string Name, int Line, string? IgnoreIfNoMatchingField)>? currentChildren = null;

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    var line = li.HasLineInfo() ? li.LineNumber : 0;

                    if (reader.Depth == 1)
                    {
                        // 空元素（<ThingDef ... />）：同一遍里直接收尾，别等 EndElement
                        // （它也会来，但那时 currentChildren 已为 null，不会重复收）。
                        if (reader.IsEmptyElement)
                        {
                            var (_, emptyOrdinal) = NextOrdinal(defs, line);
                            defs.Add(new ScannedDef(line, emptyOrdinal, Array.Empty<(string, int, string?)>()));
                            continue;
                        }
                        currentLine = line;
                        (_, currentOrdinal) = NextOrdinal(defs, line);
                        currentChildren = new List<(string, int, string?)>();
                        continue;
                    }

                    if (reader.Depth == 2 && currentChildren is not null)
                    {
                        currentChildren.Add((reader.Name, line, reader.GetAttribute("IgnoreIfNoMatchingField")));
                        continue;
                    }

                    continue;
                }

                // 任何非元素节点都可能意味着 depth==1 的 def 已经结束（XmlReader 在 EndElement 上
                // 报的 Depth 已经是父层），所以判据写成「Depth < 2 且手里有正在收集的 def」，
                // 比逐 NodeType 枚举更稳 —— 与 DefScanner 的 Emit-on-EndElement 是同一套边界。
                if (currentChildren is not null && reader.Depth < 2)
                {
                    defs.Add(new ScannedDef(currentLine, currentOrdinal, currentChildren.ToArray()));
                    currentChildren = null;
                }
            }

            if (currentChildren is not null)
                defs.Add(new ScannedDef(currentLine, currentOrdinal, currentChildren.ToArray()));
        }
        catch (XmlException) { /* 解析失败由 RD2001 报告 */ }
        return defs;
    }

    /// <summary>下一个 def 在 <paramref name="line"/> 这一行上的序号（按已在 <paramref name="defs"/> 里的文档序数）。</summary>
    private static (int Line, int Ordinal) NextOrdinal(List<ScannedDef> defs, int line)
    {
        int ordinal = 0;
        foreach (var d in defs)
            if (d.DefLine == line) ordinal++;
        return (line, ordinal);
    }

    /// <summary>
    /// 元素扫描的一个 def：起始行 + **该行上的序号**（决定归属，见 <see cref="ReadElementsByDef"/>）
    /// + 它的一级子元素。
    /// </summary>
    public sealed record ScannedDef(
        int DefLine,
        int OrdinalWithinLine,
        IReadOnlyList<(string Name, int Line, string? IgnoreIfNoMatchingField)> Children);
    private static string ReadManagedDir()
    {
        var env = RimDev.Core.Environment.GameEnvProbe.Discover();
        return env.ManagedDir;
    }
}
