using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;

namespace RimDev.Core.Checks;

/// <summary>
/// ParentName 链校验：父类不存在（RD2005）、继承环（RD2006）。
///
/// ★ 引擎口径（第 2 期 Task 6c：反编译 + 装载顺序实证，不是推测）。
///   本节**明确推翻**旧注释里的两处错误说法，不要再按旧说法改回去。
///
/// 【推翻旧说法①】旧注释：「先在 defName 空间找，再在抽象名空间找」——**错的**。
///   引擎 `Verse.XmlInheritance`：
///     private static Dictionary&lt;string, List&lt;XmlInheritanceNode&gt;&gt; nodesByName;   // 单个全局字典
///     public static void TryRegister(XmlNode node, ModContentPack mod) {
///         XmlAttribute a  = node.Attributes["Name"];            // ★ 建表【只用 Name 属性】
///         XmlAttribute a2 = node.Attributes["ParentName"];
///         ...
///         if (a != null) nodesByName.Add(a.Value, value);       // ← 全类里唯一一处写入
///     }
///     private static XmlInheritanceNode GetBestParentFor(XmlInheritanceNode node, string parentName) {
///         if (nodesByName.TryGetValue(parentName, out var v)) { /* 按 loadOrder 选，不做类型检查 */ }
///         Log.Error("XML error: Could not find parent node named \"" + parentName + "\" ...");   // ← RD2005 的引擎措辞
///     }
///   `<defName>` **不是继承名**，引擎的父节点表根本不看它。所以本检查的解析顺序是
///   **Name 空间优先 → defName 空间兜底**。
///
///   ★ 兜底是【有意为之的补丁近似】，不是「引擎也这么干」，**不能删**：
///     引擎在 `Verse.LoadedModManager.LoadAllActiveMods` 里的真实顺序是
///       XmlInheritance.Clear() → InitializeMods() → LoadModContent() → CreateModClasses()
///       → LoadModXML() → CombineIntoUnifiedXML() → TKeySystem.Parse() → ErrorCheckPatches()
///       → ApplyPatches(xmlDocument, assetlookup)          ← 补丁在这里改 XML
///       → ParseAndProcessXML() → ★ XmlInheritance.TryRegister(每个顶层节点) → ★ XmlInheritance.Resolve()
///     即 **注册发生在打补丁【之后】**：补丁补上的 `Name` 属性引擎看得见，我们的静态索引看不见。
///     实证：CombatExtended(3147447844) `Patches\ThingDef_Guns.xml:904-908` 用
///       &lt;Operation Class="PatchOperationAttributeAdd"&gt;
///         &lt;xpath&gt;Defs/ThingDef[defName="OSNGun_HMG"]&lt;/xpath&gt;
///         &lt;attribute&gt;Name&lt;/attribute&gt;&lt;value&gt;OSNGun_HMG&lt;/value&gt;
///       &lt;/Operation&gt;
///     给这个 def 补上 `Name="OSNGun_HMG"`，它的 `Defs\MechGuns.xml:54` 的
///     `&lt;ThingDef ParentName="OSNGun_HMG"&gt;` 才解析得开。全库同形 **61 条**（11 个 mod）。
///     若静态只查 Name 空间（无兜底），这 61 条会全部变成假阳 —— 比不报更糟。
///     第 3 期实现 `PatchOperation*` 解析后，应改为「应用补丁之后的精确口径」。
///
/// 【推翻旧说法②】旧注释：「继承沿 C# 类型层次走」——**错的**，那只是巧合。
///   结论（ParentName 按名字【全局】解析、不按 DefType 过滤）不变，但理由要换成：
///   `nodesByName` 是**单个全局字典**，不按元素名/DefType 分区，`GetBestParentFor` 也不做类型检查。
///   引擎只保证「找得到」；字段能不能塞进那个 C# 类型是后面反序列化的事（巧合，不是机制）。
///
/// 【引擎如何判环 —— 按声明，不按名字】`XmlInheritance.Resolve()` 在节点对象上判：解析不出父节点的
///   那条链报 `Cyclic inheritance hierarchy detected`。**两个不同声明共用一个名字，引擎不认为成环**。
///   所以本检查的 `seen` 键是**声明身份**（= `RawDef` **对象引用**，见下），不是 `DefName`/`NameAttr` 字符串；
///   ★ 不是 `(FileAbs, Line)`：同一行可以写多个 def（本机 ThreatFleet `TF_MechWeapons.xml:28` 一行 18 个），
///     按行号做键会把同行的父与子当成同一个节点 ⇒ **假 RD2006**（第 3 期 Task 5a 修正）。
///   诊断里展示的名字用 `NameAttr ?? DefName ?? 文件名:行号`（`ParentName` 指的就是 `Name`），
///   另用 `Evidence["cycle"]` 带出**声明链**（`FileRelToMod:Line`）以便区分同名的两处声明。
///   官方反例（旧实现在这里报 5 条假自环，全是假阳；归档的真实游戏日志里没有对应
///   `Cyclic inheritance hierarchy detected`）：
///     · `Data\Core\Defs\PawnKindDefs_Humanlikes\PawnKinds_Mercenary.xml:198`
///       `&lt;PawnKindDef ParentName="MercenaryMidTierBase" Name="MercenarySlasherBase" Abstract="True"&gt;`
///       第 199 行还写着 `&lt;defName&gt;Mercenary_Slasher&lt;/defName&gt;` —— **抽象节点的 defName 只是装饰**
///       （抽象声明不会被建成 def，见 <see cref="DefIndex.Build"/>），而第 232 行的真 def 也叫
///       `Mercenary_Slasher`。两个不同声明的「可引用名」同串 → 旧实现按名字判环即报假自环。
///       同类另有 `Tribal_ChiefMelee`、`TradersGuild_Slasher`。
///     · `Data\Core\Defs\SoundDefs\UI_Oneshots_Designation.xml`：第 3 行
///       `&lt;SoundDef Name="Designate_DragStandard_Changed" Abstract="True"&gt;`（只有 Name），
///       第 19 行的真 def 又叫 `Designate_DragStandard_Changed` —— 按 defName 优先解析父节点会指到它自己。
///
/// 【MayRequire —— 引擎**两处**都跳，本检查两处都对齐】（第 3 期 Task 5a IL 实证）
///   ① 父节点表：`Verse.XmlInheritance.TryRegister` 在注册前过 `MayRequire*`，不满足就不注册
///      ⇒ 那个 `Name=` 引擎里不存在 ⇒ 引用它的 def 报 RD2005 是**对的**（InheritanceLayer 把它
///      从候选里排除）。
///   ② 建 def 循环：`Verse.LoadedModManager.ParseAndProcessXML` 的第二个循环**自己也过一遍**
///      `MayRequire*`（IL 见 `InheritanceLayer` 类注释【3b】），不满足就**不建 def、不注册**
///      ⇒ 引擎**绝不会**为它报 `Could not find parent node named`。
///   ⇒ 所以「带未满足 `MayRequire` 的声明自己」必须被本检查**跳过**（`ParentResolution.Evaluated == false`）：
///      它们既不报 RD2005 也不报 RD2006，只发 RD2008（info）。
///   ★ 旧注释写「我们不跳 → 方向是少报」是**错的**（那会让 RD2005 多报；全库实测多报 902 条，
///     正是 `01` §11.1 T17 禁止的「悬空引用误报」）。
///
/// 两个易误报点（项目历史坑）：
/// 1. 原版抽象基类用 **`Name="X"` 属性**定义（如 &lt;ThingDef ParentName="BuildingBase" Name="Fleshmass"&gt;），
///    不是 &lt;defName&gt;。只按 defName 建索引会把 BaseBullet / BaseGun / BuildingBase 全判成"缺失"。
/// 2. `defName` 的唯一性是按 DefType 的（RD2004 判重用 ByTypeAndDefName），
///    但 `ParentName` 是**按名字全局解析**的 —— 两个不同机制，不要互相外推。
///    实测（官方 6 个包 / 13808 def / 4553 个带 ParentName）：
///      「按类型未解析 = 26」而「全局(name-only)未解析 = 0」。
///    那 26 条全部是「父类跨 XML 元素名存在于原版」：
///      · 18 条 `BackstoryDef -> Solid`：`Data\Core\Defs\BackstoryDefs\Solid\Solid_Rare.xml:4`
///        是 `&lt;BackStoryDef Abstract="True" Name="Solid"&gt;`（**大写 S**），而第 10/21/44/57/74 行
///        子节点写 `&lt;BackstoryDef ParentName="Solid"&gt;`（**小写 s**）—— 同一份文件里两种拼写混用，
///        它们映射到同一个 C# 类型 `Verse.BackstoryDef`（引擎对元素名→类型是**大小写不敏感**的；
///        `rimdev api type BackStoryDef` 查不到只是本工具的类型查找大小写敏感）；
///      · 1 条 `CreepJoinerFormKindDef -> BasePlayerPawnKind`（Anomaly）：父在 Core 里是
///        `&lt;PawnKindDef Abstract="True" …&gt;`；
///      · 其余 7 条同类（`StructureLayoutDef -> OrbitalPlatformBase`、`ThingDef -> FleckBase_Thrown`、
///        `FleckDef -> FleckGlowDistorted`）。
///    反向推论：**官方内容（Core + 5 DLC）必须 0 个未解析父类**，这既是健康判据，也是本口径的反证。
/// </summary>
public sealed class ParentChainCheck : ICheck
{
    public string Id => Codes.ParentNotFound;

    /// <summary>
    /// 本次跑完的**实测统计**（由 <see cref="InheritanceLayer"/> 自己算，不是 CLI 另算一遍）。
    ///
    /// ★ 为什么要有它（同 <see cref="PatchCheck.LastRun"/> 的理由）：`RD2005 = 0` 必须能与
    ///   「解析层根本没跑」区分开 —— 否则就是项目纪律里最忌的**假 0**。`check` 把整块打进
    ///   `--json` 的 `parentResolution`，让「四个依据各多少条 / 补丁字面量救回几条」始终可见。
    ///   null = 本规则因「ctx.Defs 为空」直接返回（那也是一种必须报出来的状态）。
    /// </summary>
    public InheritanceLayer.Analysis? LastAnalysis { get; private set; }

    public IEnumerable<Diagnostic> Run(CheckContext ctx)
    {
        // ★★ 第 3 期 Task 5a：解析口径的**唯一真源**搬到了 InheritanceLayer。
        //   本规则只做两件事：① 把解析结果变成诊断；② 把统计块交给 CLI。
        //   过去这里内联写着 `ByAbstractName ?? ByDefName`，于是「解析」与「诊断」两套口径耦合在
        //   同一个类里，任何第二个消费方（环检测、统计、将来的 sim）都只能复制一遍 —— 必分叉。
        var swAnalyze = System.Diagnostics.Stopwatch.StartNew();
        var analysis = InheritanceLayer.Analyze(ctx);
        swAnalyze.Stop();
        AnalyzeMs = swAnalyze.ElapsedMilliseconds;
        var swBody = System.Diagnostics.Stopwatch.StartNew();
        s_suggestTicks = 0;
        LastAnalysis = analysis;

        // ★ 第 3 期 Task 2b：名字池惰性建【一次】（旧实现每条诊断重建一次）。
        //   注意必须在这里声明：Run 是迭代器，局部变量在两次 MoveNext 之间保持。
        NameSuggestionPool? suggestionPool = null;

        // ── ① RD2008：节点被 MayRequire* 挡掉（info，逐节点一条）────────────────────────────────
        //   ★ **不受 TargetModPackageId 过滤**：要求它把「哪些节点在本机不存在」全部报出来
        //     （--scope all 实测 **7,155** 条 info；这是可控噪音，不是 error）。
        //   ★ 码表去重：RD2016 与 RD2008 是同一个事件，只发 RD2008（见 Codes.MayRequireModMissing）。
        //   ★ 未解项：另一路探针在**原始 XML 文本**里数到约 11,580 次 `MayRequire*` 出现，与本计数差
        //     约 4,425。最可能的解释是本检查只看 **def 元素自己**的属性（子元素上的 `MayRequire` 不计），
        //     但**没有逐条核对** —— 不当结论用，已记入 `08` 的未解项。
        foreach (var d in ctx.Defs)
        {
            if (string.IsNullOrWhiteSpace(d.MayRequire) && string.IsNullOrWhiteSpace(d.MayRequireAnyOf)) continue;
            var missing = InheritanceLayer.MissingMods(d, analysis.ActivePackageIds);
            if (missing.Count == 0) continue;

            yield return new Diagnostic(
                Codes.MayRequireModMissing, Severity.Info, Scope.Offline,
                $"节点跳过：MayRequire* 指定的 mod 未安装（{string.Join(", ", missing)}）",
                "引擎 XmlInheritance.TryRegister 会跳过该节点；它不会被建成 def，其 Name= 也不会进父节点表"
                + "（RD2016 与 RD2008 是同一事件，已合并）。",
                FileAbs: d.FileAbs, Line: d.Line,
                ModPackageId: d.ModPackageId, DefName: d.DefName, DefType: d.DefType,
                Evidence: new Dictionary<string, string>
                {
                    ["mayRequire"] = d.MayRequire ?? "(none)",
                    ["mayRequireAnyOf"] = d.MayRequireAnyOf ?? "(none)",
                    ["missingMods"] = string.Join(" ", missing),
                    ["activeModIdSetSize"] = analysis.ActivePackageIds.Count.ToString(),
                });
        }

        // ── ② RD2005 / RD2006：按解析结果逐条报（这里是**报告过滤器**）─────────────────────────
        foreach (var res in analysis.ByDeclaration.Values)
        {
            var d = res.Node;

            // ★★ 第 3 期 Task 5a IL 实证修正：`MayRequire*` 未满足的声明，引擎
            //   `ParseAndProcessXML` 的建 def 循环**根本不处理它**（不建 def、不进父节点表）
            //   ⇒ 引擎绝不会为它报父类缺失/继承环，本检查也就不能报。
            //   不挡掉这一类的代价（全库实测 2026-10-15）：RD2005 1515 条里 **902** 条是这种假阳
            //   —— 正是 `01` §11.1 T17 说的「不产生悬空引用误报」。它只发 RD2008（info，见上面 ①）。
            if (!res.Evaluated) continue;

            // ★ 报告过滤器（修正 ④）：只报目标 mod 自己的 def。
            //   索引/父类解析仍然在全集上做 —— 否则指向 Core 基类的 def 会被全部误报 RD2005。
            if (ctx.TargetModPackageId is not null && d.ModPackageId != ctx.TargetModPackageId) continue;

            // 链解析成功（含 DefNameFallback / NameSpaceViaPatchLiteral）⇒ 不发 error。
            // ★ 只有 Unresolved 才报 RD2005：DefNameFallback 是「引擎可能解析不开、但我们无法证明」，
            //   计数与标记已经由统计块 + evidence 承担（96/497 会被补丁字面量救回，剩下 401 条要逐条证据）。
            if (res.ChainBasis != InheritanceLayer.ResolutionBasis.Unresolved) continue;

            // ── 环的判定键是【声明身份】= 声明对象引用，不是名字（见类注释与官方反例）──────
            var path = InheritanceLayer.Walk(analysis, d);
            if (HasCycle(path, out var chain, out var decls))
            {
                yield return new Diagnostic(
                    Codes.InheritanceCycle, Severity.Error, Scope.Offline,
                    $"ParentName 继承环：{string.Join(" → ", chain)}",
                    "打断环，让链最终落到一个不带 ParentName 的基类",
                    FileAbs: d.FileAbs, Line: d.Line,
                    ModPackageId: d.ModPackageId, DefName: d.DefName, DefType: d.DefType,
                    Evidence: new Dictionary<string, string>
                    {
                        // ★ 声明链（相对 mod 根，短且可读）：同名的两处不同声明靠它区分。
                        ["cycle"] = string.Join(" → ", decls.Select(x => $"{x.FileRelToMod}:{x.Line}"))
                    });
                continue;
            }

            // ★★ 断链要报在**真正断的那一跳**上（第 3 期 Task 5a 实测修正）：
            //   `DirectBasis != Unresolved` 说明直接父名**解析得开**，断的是更上游的一跳。
            //   旧文案一律写「ParentName=\"直接父名\" 指向不存在的 def」—— 全库 144 条属此类，
            //   而那句话在那 144 条上是**假的**（直接父类确实存在）。
            var brokenAt = res.DirectBasis != InheritanceLayer.ResolutionBasis.Unresolved
                ? (path.Count > 0 ? path[^1] : d)
                : d;
            var chainBroken = !ReferenceEquals(brokenAt, d);
            var culprit = brokenAt.ParentName ?? d.ParentName!;
            var suggestions = SuggestParent(ctx, culprit, ref suggestionPool);
            yield return new Diagnostic(
                Codes.ParentNotFound, Severity.Error, Scope.Offline,
                chainBroken
                    ? $"继承链在 \"{InheritanceLayer.DisplayName(brokenAt)}\" 处断开：它的 ParentName=\"{culprit}\" 指向不存在的 def/抽象基类"
                    : $"ParentName=\"{d.ParentName}\" 指向不存在的 def/抽象基类",
                chainBroken
                    ? "本条的 File/Line 是**引用者**；断点在 evidence.chainBrokenAt，先修那一跳（它的父类不存在）"
                    : "检查拼写；原版抽象基类用 Name=\"X\" 定义，只能在抽象名空间里找到",
                FileAbs: d.FileAbs, Line: d.Line,
                ModPackageId: d.ModPackageId, DefName: d.DefName, DefType: d.DefType,
                Suggestions: suggestions,
                Evidence: new Dictionary<string, string>
                {
                    // ★ 依据必须随诊断报出去：没有它，使用者分不清「引擎也解析不开」
                    //   与「只在我们看不见的地方解析得开」（本项目纪律里的「区分假 0」）。
                    ["basis"] = res.DirectBasis.ToString(),
                    ["chainBasis"] = res.ChainBasis.ToString(),
                    ["nameSpaceHit"] = ctx.Index.ByAbstractName.ContainsKey(d.ParentName!).ToString(),
                    ["defNameHit"] = ctx.Index.ByDefName.ContainsKey(d.ParentName!).ToString(),
                    ["patchLiteralHit"] = analysis.PatchNameLiterals.Contains(d.ParentName!).ToString(),
                    // ★ 链断点的声明定位（`FileRelToMod:Line`）与它缺的父名：144 条链式诊断靠它才能落地。
                    ["chainBrokenAt"] = chainBroken ? $"{brokenAt.FileRelToMod}:{brokenAt.Line}" : "(direct)",
                    ["chainBrokenParentName"] = culprit,
                });
        }

        BodyMs = swBody.ElapsedMilliseconds;
        SuggestMs = s_suggestTicks * 1000 / System.Diagnostics.Stopwatch.Frequency;
    }    /// <summary>
    /// 从 <paramref name="start"/> 向上走 ParentName 链；成环则返回 true。
    /// <paramref name="chain"/> = 面向继承的展示名链；<paramref name="declarations"/> = 对应的声明链。
    /// </summary>
    /// <summary>
    /// 在**已经走好的声明链**上判环；成环则返回 true。
    ///
    /// ★ 第 3 期 Task 5a：链的行走搬去了 <see cref="InheritanceLayer.Walk"/>（它用的是同一份解析口径），
    ///   本方法只保留【判环】与【展示】两件事 —— 判定键是**声明身份**（= `RawDef` 对象引用），不是名字，
    ///   **也不是** `(FileAbs, Line)`（同一行可以写多个 def ⇒ 按行号会把同行的父与子当成同一个 ⇒ 假环）：
    ///   引擎 `Resolve()` 也按节点对象判环，两个不同声明共用一个名字它不认为成环。
    ///   官方反例见类注释（`PawnKinds_Mercenary.xml:198` vs `:232`、`UI_Oneshots_Designation.xml:3` vs `:19`）。
    ///
    /// <paramref name="path"/> 的末项可能是「闭合点」（Walk 在遇到已访问声明时会把它追加一次并停下）。
    /// </summary>
    private static bool HasCycle(IReadOnlyList<RawDef> path, out List<string> chain, out List<RawDef> declarations)
    {
        chain = new List<string>();
        declarations = new List<RawDef>();
        var seen = new HashSet<RawDef>(ReferenceEqualityComparer.Instance);
        foreach (var cur in path)
        {
            if (!seen.Add(cur))
            {
                chain.Add(InheritanceLayer.DisplayName(cur));
                declarations.Add(cur);
                return true;
            }
            chain.Add(InheritanceLayer.DisplayName(cur));
            declarations.Add(cur);
        }
        return false;
    }

    // ★ 第 3 期 Task 5a：原地的 `DisplayName(RawDef)` 已删 —— 口径与
    //   `InheritanceLayer.DisplayName` 逐字相同，留着就是「两套并行实现」。
    //   本类改用后者（类注释里那条「同名不同声明靠展示名区分」的约束不变）。

    /// <summary>
    /// ParentName 未解析时给相近名（第 3 期 Task 2b：**提速 482 s → 秒级**，输出逐条不变）。
    ///
    /// ★ 旧实现（已删）每报一条就重建一次名字池并对**每个**名字调
    ///   <see cref="RimDev.Core.Metadata.MemberInfoIndex.Similarity"/>（全库 436 × 32 万 ≈ 1.4 亿次，
    ///   每次都 ToLowerInvariant + 两个 int[] 分配，实测 482 s = 全库 615 s 的 78%）。
    /// </summary>
    /// <summary>★ Task 8 Step 1：`InheritanceLayer.Analyze` 的墙钟（本检查是它的第一个调用方）。</summary>
    public long AnalyzeMs { get; private set; }
    /// <summary>★ Task 8 Step 1：Analyze 之后到报告结束的墙钟（两条诊断循环 + 候选建议）。</summary>
    public long BodyMs { get; private set; }
    /// <summary>★ Task 8 Step 1：候选名字建议的累计墙钟（惰性建池 + 全部 Suggest 调用）。</summary>
    public long SuggestMs { get; private set; }
    // 诊断用累加器（本检查单线程跑一次）。
    private static long s_suggestTicks;

    /// <summary>在**已经走好的声明链**上判环；成环则返回 true。</summary>
    private static List<string> SuggestParent(CheckContext ctx, string parentName, ref NameSuggestionPool? pool)
    {
        var t = System.Diagnostics.Stopwatch.GetTimestamp();
        if (pool is null) pool = NameSuggestionPool.FromIndex(ctx.Index);
        var r = pool.Suggest(parentName, 5);
        s_suggestTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t;
        return r;
    }
}
