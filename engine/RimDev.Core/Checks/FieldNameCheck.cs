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
    //   元素属性由本类的元素扫描层采集（ScanTopLevelElements —— 原来只取元素名与行号、丢掉属性）。
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

            var elementsByDefLine = ReadElementsByDef(g.Key);

            foreach (var d in defsInFile)
            {
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

                if (!elementsByDefLine.TryGetValue(d.Line, out var elements)) continue;
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
                                ["suppressedDetail"] = suppressedDetail,
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
    /// ★ 一次流式读入某文件里**所有** def 的一级子元素，按 def 起始行分组。
    /// 这是「每个文件只读一次」的落点：文件打开一次，元素按行号归属到对应 def。
    /// 归属依据是「depth==1 的元素起始行 == RawDef.Line」，与 DefScanner 的行号口径完全一致。
    /// </summary>
    public static Dictionary<int, List<(string Name, int Line, string? IgnoreIfNoMatchingField)>> ReadElementsByDef(string fileAbs)
    {
        var byDefLine = new Dictionary<int, List<(string Name, int Line, string? IgnoreIfNoMatchingField)>>();
        foreach (var (defLine, name, line, ignoreAttr) in ScanTopLevelElements(fileAbs))
        {
            if (!byDefLine.TryGetValue(defLine, out var l))
                byDefLine[defLine] = l = new List<(string, int, string?)>();
            l.Add((name, line, ignoreAttr));
        }
        return byDefLine;
    }

    /// <summary>读某个 def（从 startLine 起）的一级子元素名/行号/抑制属性。只读该文件一次。</summary>
    public static List<(string Name, int Line, string? IgnoreIfNoMatchingField)> ReadTopLevelElements(string fileAbs, int startLine) =>
        ScanTopLevelElements(fileAbs)
            .Where(x => x.DefLine == startLine)
            .Select(x => (x.Name, x.Line, x.IgnoreIfNoMatchingField))
            .ToList();

    /// <summary>
    /// 底层单遍扫描：返回 (def 起始行, 一级子元素名, 元素行号, IgnoreIfNoMatchingField 属性值)。
    ///
    /// ★★ 第 3 期 Task 2c 的**采集侧改动就在这一层**：原实现只取元素名与行号、**丢掉了属性**，
    ///   而 RD4010（抑制条件④）判的恰恰是元素属性 <c>IgnoreIfNoMatchingField</c>。
    ///   只补这一层用得到的属性，不动其它层：
    ///     · def 元素自身（depth==1）用得到的 <c>Class=</c> 已由 DefScanner 收进 RawDef（Task 2b）；
    ///       而且引擎**从不**把 def 元素本身当 fieldNode 传给 DoFieldSearch（fieldNode 永远是
    ///       `xmlRoot.ChildNodes[i]`），所以 def 元素上的 IgnoreIfNoMatchingField 语义上根本无效。
    ///     · depth&gt;=3 的更深元素不在本规则范围内（多态节点内层字段是另一个 Task），不采集。
    /// </summary>
    private static List<(int DefLine, string Name, int Line, string? IgnoreIfNoMatchingField)> ScanTopLevelElements(string fileAbs)
    {
        var list = new List<(int, string, int, string?)>();
        try
        {
            using var stream = File.OpenRead(fileAbs);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                IgnoreComments = true, IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Prohibit
            });
            var li = (IXmlLineInfo)reader;
            var defLine = -1;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                var line = li.HasLineInfo() ? li.LineNumber : 0;
                if (reader.Depth == 1)
                {
                    defLine = line;                       // 一个 def 的开始
                    continue;
                }
                if (reader.Depth == 2 && defLine >= 0)
                    list.Add((defLine, reader.Name, line, reader.GetAttribute("IgnoreIfNoMatchingField")));
            }
        }
        catch { /* 解析失败由 RD2001 报告 */ }
        return list;
    }

    private static string ReadManagedDir()
    {
        var env = RimDev.Core.Environment.GameEnvProbe.Discover();
        return env.ManagedDir;
    }
}
