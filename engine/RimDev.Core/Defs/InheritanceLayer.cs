using RimDev.Core.Catalog;
using RimDev.Core.Checks;
using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;

namespace RimDev.Core.Defs;

/// <summary>
/// <c>ParentName</c> 解析口径的**唯一真源**（第 3 期 Task 5a）。
///
/// ★★ 为什么必须独立成一层：第 2 期把「Name 空间优先 → defName 兜底」写死在
///   <c>Checks/ParentChainCheck</c> 里，于是**解析口径**与**诊断口径**耦合在同一个类里 ——
///   任何消费方（环检测、统计块、将来的 sim/引用解析）想用一次解析，就只能复制一遍
///   `ByAbstractName ?? ByDefName`。本项目反复踩到的「两套并行实现必然分叉」就是这么来的。
///   现在：解析在这里，规则的职责只剩「把结果变成诊断」。
///
/// ── 引擎口径（三条，都有反编译/IL 实证，不要按直觉改） ──────────────────────────────
///
/// 【1】`nodesByName` 只按节点的 `Name` 属性建表（`Verse.XmlInheritance.TryRegister`：
///     `XmlAttribute a = node.Attributes["Name"]; … if (a != null) nodesByName.Add(a.Value, value);`），
///     是**单个全局字典**、不按元素名/DefType 分区，`GetBestParentFor` **也不做类型检查**。
///     ⇒ 解析顺序 = **Name 空间优先**；`<defName>` 不是继承名。
///
/// 【2】注册发生在**打补丁之后**（`LoadedModManager.LoadAllActiveMods` =
///     `LoadModXML → CombineIntoUnifiedXML → TKeySystem.Parse → ErrorCheckPatches →
///      ApplyPatches → ParseAndProcessXML`，而 `TryRegister`/`Resolve` 在 `ParseAndProcessXML` 内部）。
///     ⇒ 补丁用 `PatchOperationAttributeAdd/Set` 补上的 `Name` **引擎看得见**。
///     静态索引看不见它，所以本层额外收集「补丁字面量 Name 集」来近似 —— 这是**有意的过近似**，
///     方向是**少报**（把可能不存在的 Name 当存在），与项目「假阳比漏报更贵」一致。
///     实证量级（本机全库）：57 个 distinct 字面量，救回 96 条声明（59 靠 defName + 37 两空间皆无），
///     残余 401 条（= 497 − 96）—— 见 `Docs\07` Task 5a Step 7 的等式。
///
/// 【3】带 `MayRequire` / `MayRequireAnyOf` 且指定 mod **未安装**的节点，
///     `TryRegister` 会**跳过注册**（那个 `Name=` 不在父节点表里）。
///     ⇒ 本层把它们从「可作父类的声明」里排除。
///     ★ 方向说明（任务书 Step 4 要求实测确认）：跳过会让静态分析**多报** RD2005 ——
///       一个带着未满足 `MayRequire` 的父类会被当成不存在。这**不是**假阳：引擎里它确实不存在。
///       ★★ 但这条只对**会被加载的子节点**成立：子节点自己带未满足 `MayRequire` 时，引擎连它
///       都不加载，更不会为它报父类缺失 —— 那一类必须由 `ParentResolution.Evaluated` 挡掉（见【3b】）。
///
/// 【3b】`MayRequire*` 未满足的节点**连 def 都不会被建**（第 3 期 Task 5a IL 实证，本层据此修正）：
///     `Verse.LoadedModManager.ParseAndProcessXML`（767 字节 IL）里有两个循环 ——
///       · 第一个：`xmlDocument.DocumentElement.ChildNodes` →（`assetLookup.TryGetValue`）→ `TryRegister`；
///       · 中间 `XmlInheritance.Resolve()`；
///       · 第二个（**真正建 def** 的那个）：逐节点读 `node.Attributes["MayRequire"]` →
///         `ToLower().Split(',')` → `ModLister.AllModsActiveNoSuffix`；再读 `MayRequireAnyOf` →
///         `ModLister.AnyModActiveNoSuffix`（★ **不是** `AnyFromListActive`：那个方法存在，
///         但这两处调用点都不用它）→ 两个都过才 `DirectXmlLoader.DefFromNode` /
///         `DirectXmlToObjectNew.DefFromNodeNew`。
///     ⇒ 未满足的节点**既不建 def、也不进父节点表**，引擎**不会**为它报
///       `Could not find parent node named` —— 这正是 `01` §11.1 T17 的「不产生悬空引用误报」。
///     不建模这一步的代价（2026-10-15 全库实测）：RD2005 **1515 → 613** 条，
///     其中 **902** 条落在「自己就被 `MayRequire*` 挡掉」的声明上（`emitbreaker.MIM.WH40k.*`
///     等把整块 CE 兼容内容用 `MayRequire="CETeam.CombatExtended"` 包起来 —— 那是**正确写法**，
///     误报的是我们）。
///     ★ 判定集合 = **已启用（Active）** 的 packageId。
///       `Docs\01` §5.1.5 写的「判定集合 = 已安装的 packageId 全集（不是 activeMods）」**已被反编译证伪**：
///       `ModLister.TryAddMod` 确实把未启用的 mod 也放进 `modsByPackageId`，但查询入口
///       `GetActiveModWithIdentifier` 逐条要求 `item.Active`（见 `Analyze` 里那段逐行引文）。
///       以代码为准。
///
/// ── 与「声明身份」的关系 ────────────────────────────────────────────────────────
/// 环检测按**声明对象引用**而不是名字：两个不同声明可以合法共用同一个名字
/// ★ `(FileAbs, Line)` **不足以**标识一处声明 —— 同一行可以写多个 def（本机 ThreatFleet
///   `TF_MechWeapons.xml:28` 一行 18 个；active 作用域共 6 组 31 条）。按行号做键会
///   ① 折叠 `ByDeclaration`（`withParentName` 少 31）；② 让被折叠的声明**永远不被评估**（静默丢诊断）；
///   ③ 把同行的父与子当成同一个节点 ⇒ **假 RD2006**。因此本层的身份键一律是
///   `RawDef` + `ReferenceEqualityComparer`（`RawDef` 是 record，值相等不够）。
/// （原版 `PawnKinds_Mercenary.xml:198` 抽象模板的装饰 `<defName>` 与 `:232` 的真 def 同串；
/// `UI_Oneshots_Designation.xml` L3 的 `Name=` 与 L19 的 defName 同串）。本层的
/// <see cref="Walk"/> 因此返回**声明链**而不是名字链，环判定交给消费者在这条链上做。
/// </summary>
public sealed class InheritanceLayer
{
    // ────────────────────────────────────────────────────────────────────────────
    // 解析依据
    // ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 「这条 `ParentName` 是靠什么解析到的」——**必须**随每个解析结果一起报出去。
    /// 没有它，使用者分不清「引擎也解析不开」（<see cref="Unresolved"/>）与
    /// 「只在我们看不见的地方解析得开」（<see cref="NameSpaceViaPatchLiteral"/> /
    /// <see cref="DefNameFallback"/>）。这正是本项目纪律里的「区分假 0」。
    /// </summary>
    public enum ResolutionBasis
    {
        /// <summary>命中字面存在的 `Name` 属性 —— **引擎一定解析得开**。</summary>
        NameSpace,

        /// <summary>
        /// 只在「补丁字面量 Name 集」里 —— 引擎**大概率**解析得开（它确实给某个 def 补了该 `Name`），
        /// 但我们没有求值 XPath，所以不敢断言。**不报 error**。
        /// </summary>
        NameSpaceViaPatchLiteral,

        /// <summary>
        /// 只命中 `<defName>`。这是**有意保留的近似**：它可能对应「补丁给这个 def 补了 `Name`」
        /// （如 CE 给原版 `Bruise`/`Nerve` 补 `Name=`），也可能是本工具看不见的其它写法。
        /// **不报 error**，只在统计块里计数 + 在 evidence 里标出来。
        /// </summary>
        DefNameFallback,

        /// <summary>两个名字空间都不在，补丁字面量也没有 —— 引擎会报 `Could not find parent node named` ⇒ RD2005。</summary>
        Unresolved,
    }

    /// <summary>一条声明（`def` 或 `MayRequire` 节点）的解析结果。</summary>
    public sealed record ParentResolution(
        RawDef Node,
        /// <summary>**直接**父名的解析依据（第一跳）。</summary>
        ResolutionBasis DirectBasis,
        /// <summary>
        /// **直接**父声明（第一跳的目标；null = 第一跳就没解析开）。
        /// ★ 环检测必须沿它**逐跳**走，**不能**沿 <see cref="Terminal"/>：
        ///   `Terminal` 是「整条链的终点」，成环时它会退回环内某个节点
        ///   （实测 A↔B：Terminal 指回自己 ⇒ 声明链退化成 `A → A`，
        ///   两条诊断因此覆盖不到同一对声明 —— 自测 `★ RD2006 仍能抓真环` 抓到的就是这个）。
        /// </summary>
        RawDef? DirectTarget,
        /// <summary>整条链的最弱依据（链上任意一跳是 Unresolved ⇒ 本项也是 Unresolved）。</summary>
        ResolutionBasis ChainBasis,
        /// <summary>链末声明；null = 链没走通。</summary>
        RawDef? Terminal,
        /// <summary>链的长度（声明数，不含起点）。</summary>
        int ChainLength,
        /// <summary>
        /// 该声明在引擎里**是否会被加载**（`MayRequire*` 满足）。
        /// false ⇒ 引擎 `ParseAndProcessXML` 的建 def 循环自己就跳过这个节点（**不建 def、不进父节点表**），
        /// **因此它不能产生 RD2005/RD2006** —— 这正是 `01` §11.1 T17 要求的「不产生悬空引用误报」。
        /// </summary>
        bool Evaluated);

    /// <summary>一次分析的完整产物（`check` 的统计块与规则都从这里取，**单一口径**）。</summary>
    public sealed class Analysis
    {
        internal Analysis(
            IReadOnlyDictionary<RawDef, ParentResolution> byDeclaration,
            IReadOnlySet<string> patchNameLiterals,
            IReadOnlyList<(string Name, string FileAbs, string ModPackageId)> patchLiteralAdditions,
            IReadOnlySet<string> activePackageIds,
            int nameSpaceBlockedByMayRequire, int unresolvedMayRequireBlocked,
            long patchScanElapsedMs, int patchFilesScanned)
        {
            ByDeclaration = byDeclaration;
            PatchNameLiterals = patchNameLiterals;
            PatchLiteralAdditions = patchLiteralAdditions;
            ActivePackageIds = activePackageIds;
            NameSpaceBlockedByMayRequire = nameSpaceBlockedByMayRequire;
            UnresolvedMayRequireBlocked = unresolvedMayRequireBlocked;
            PatchScanElapsedMs = patchScanElapsedMs;
            PatchFilesScanned = patchFilesScanned;
        }


        /// <summary>
        /// 所有带 `ParentName` 的声明的解析结果。
        /// ★ 键是**声明对象本身**（`ReferenceEqualityComparer`）：`RawDef` 是 record，值相等会让
        ///   「同一行的两个不同声明」互相覆盖。同一行可以写多个 def（本机 ThreatFleet
        ///   `TF_MechWeapons.xml:28` 一行 18 个）—— 按 `(FileAbs, Line)` 做键会静默折叠它们。
        /// </summary>
        public IReadOnlyDictionary<RawDef, ParentResolution> ByDeclaration { get; }

        /// <summary>
        /// 补丁字面量 Name 集（`PatchOperationAttributeAdd` / `AttributeSet` 的 `attribute` 文本为
        /// `Name` 时的 `<value>`，加上嵌套进 `<operations><li Class=…>` / `<match>` / `<nomatch>` 的那些）。
        /// </summary>
        public IReadOnlySet<string> PatchNameLiterals { get; }

        /// <summary>带来源的补丁 Name 字面量（每条一次；供报告逐条回查，`distinct` 见上）。</summary>
        public IReadOnlyList<(string Name, string FileAbs, string ModPackageId)> PatchLiteralAdditions { get; }

        public IReadOnlySet<string> ActivePackageIds { get; }

        /// <summary>
        /// 直接父名**在 Name 空间里有候选、但候选全被 `MayRequire*` 挡掉**的声明数。
        ///
        /// ★ 为什么必须单独报出来（第 3 期 Task 5a 实测）：引擎在 `TryRegister` 里跳过
        /// `MayRequire*` 不满足的节点 ⇒ 那个 `Name=` 引擎里确实不存在 ⇒ 我们与引擎对齐地判它
        /// Unresolved。但这一步会让 `--scope all` 的 RD2005 比「只认字面 Name 空间」的口径
        /// **多出一大批**（本机实测 +970），而 `parentResolution` 原有四个数看不出来源，
        /// 使用者只会看到「RD2005 比预期多 3 倍」却不知道多在哪 —— 正是本项目最忌的**假 0 的镜像**：
        /// 数字对不上而无法归因。
        /// </summary>
        public int NameSpaceBlockedByMayRequire { get; }

        /// <summary>
        /// 上一条里**最终判成 Unresolved**的那些（即被挡、且补丁字面量/eligible 的 defName 都没有）。
        /// 它是 `--scope all` 上 RD2005 相对「497 − 96 = 401」多出来的主因。
        /// </summary>
        public int UnresolvedMayRequireBlocked { get; }

        /// <summary>本次遍历补丁文件花掉的时间（含在 `check` 总耗时里，必须报出来）。</summary>
        public long PatchScanElapsedMs { get; }

        public int PatchFilesScanned { get; }

        /// <summary>
        /// 按依据分桶（**声明数**）。★ **只统计「引擎会加载」的声明**（`ParentResolution.Evaluated`）——
        /// 被 `MayRequire*` 挡掉的声明引擎整节点跳过，其解析结果不产生任何诊断；
        /// 混进来会让「四个桶之和」与 RD2005 的实际条数对不上（第 3 期 Task 5a 的实测修正）。
        /// </summary>
        public int Count(ResolutionBasis basis) =>
            ByDeclaration.Values.Count(r => r.Evaluated && r.DirectBasis == basis);

        public int NameSpace => Count(ResolutionBasis.NameSpace);
        public int ViaPatchLiteral => Count(ResolutionBasis.NameSpaceViaPatchLiteral);
        public int DefNameFallback => Count(ResolutionBasis.DefNameFallback);
        public int Unresolved => Count(ResolutionBasis.Unresolved);

        /// <summary>
        /// **直接**父名解析得开、但整条链断在更上游的声明数
        /// （`Evaluated && DirectBasis != Unresolved && ChainBasis == Unresolved`）。
        ///
        /// ★ 为什么必须有这个数：`Unresolved` 与 RD2005 的**条数会不一样** ——
        ///   直接父名解析不开的声明发一条，而「父名解析得开、祖父名解析不开」的后代**也各发一条**
        ///   （引擎 `Resolve()` 对整条解析不出来的链同样报错）。全库实测 144 条属此类。
        ///   等式：<c>RD2005 == Unresolved + ChainBroken</c>（再减去成环的那些 —— 它们发 RD2006）。
        ///   少了这个数，使用者只会看到「RD2005 比 unresolved 多 144」而无法归因。
        /// </summary>
        public int ChainBroken =>
            ByDeclaration.Values.Count(r => r.Evaluated
                && r.DirectBasis != ResolutionBasis.Unresolved
                && r.ChainBasis == ResolutionBasis.Unresolved);

        /// <summary>
        /// 带 `ParentName`、但**引擎根本不会加载**（`MayRequire*` 不满足）的声明数。
        /// 引擎 `ParseAndProcessXML` 的建 def 循环自己也会过一遍 `MayRequire*`（IL 实证，见类注释【3b】），
        /// 这些节点既不建 def、也不进父节点表，因此**不产生 RD2005/RD2006**（只发 RD2008 info）。
        /// </summary>
        public int SkippedByMayRequire => ByDeclaration.Values.Count(r => !r.Evaluated);

        /// <summary>
        /// 带 `ParentName` 的声明总数。**不变式**：
        /// <c>WithParentName == SkippedByMayRequire + NameSpace + ViaPatchLiteral + DefNameFallback + Unresolved</c>。
        /// </summary>
        public int WithParentName => ByDeclaration.Count;

        /// <summary>
        /// 按**声明对象**取解析结果。
        /// ★ 键必须是对象引用而不是 `(FileAbs, Line)`：同一行可以写多个 def，按行号做键会把它们
        ///   折叠成一个，**静默丢掉诊断**（实测 active 有 6 组共 31 条）。
        /// </summary>
        public ParentResolution? Get(RawDef d) =>
            ByDeclaration.TryGetValue(d, out var r) ? r : null;
    }

    // ────────────────────────────────────────────────────────────────────────────
    // 入口
    // ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 在本次扫描集合上做一次完整解析。**只在规则里调一次**（`IEnumerable` 迭代器第二次 MoveNext
    /// 会重跑整段），结果放进 <see cref="Analysis"/> 复用。
    /// </summary>
    public static Analysis Analyze(CheckContext ctx)
    {
        // ── 1) 补丁字面量 Name 集：遍历【本次扫描集合】里根为 <Patch> 的文件 ──────────────
        //    口径与 DefScanner 一致（RootKind == "Patch" 就是「根是 <Patch> 且解析成功」），
        //    不自己 walk 磁盘 —— 否则 --scope active 与 --only-official 会各扫一套磁盘，
        //    「报告范围」与「解析输入」就会不一致。
        var patchScan = System.Diagnostics.Stopwatch.StartNew();
        var patchRoots = new List<(string FileAbs, string ModPackageId)>();
        foreach (var f in ctx.Files)
        {
            if (!string.Equals(f.RootKind, "Patch", StringComparison.Ordinal)) continue;
            patchRoots.Add((f.FileAbs, f.ModPackageId));
        }
        var literals = CollectPatchNameLiterals(patchRoots);
        patchScan.Stop();

        // ── 2) 已启用（Active）packageId 全集 —— MayRequire* 的真实判据（反编译实证，不是 §5.1.5）──
        //   集合元素统一 ToLowerInvariant()：引擎的 GetActiveModWithIdentifier 就是这么建键的
        //   （所以 Ludeon.Rimworld.Biotech 这种大小写写错的也能命中）。
        var activeModIds = ctx.ActivePackageIds is null
            ? BuildActiveModIdSet(ctx.Catalog)
            : NormalizeActiveModIds(ctx.ActivePackageIds);


        // ── 3) 「引擎会加载的声明」集合（eligible）与「声明 → 父名解析」表 ────────────────
        //   ★ 键必须是【声明对象引用】，不能是 (FileAbs, Line)：同一行可以写多个 def
        //     （本机 ThreatFleet `TF_MechWeapons.xml:28` 一行 18 个），按行号做键会把它们折叠成一个。
        //     实测 active 作用域有 6 组共 31 条被折叠（withParentName 少 31），且其中一部分声明
        //     **永远不会被评估** —— 这正是项目纪律里最忌的「不报错、只是答案错」。
        var eligible = new HashSet<RawDef>(ReferenceEqualityComparer.Instance);
        foreach (var d in ctx.Defs) if (IsEligible(d, activeModIds)) eligible.Add(d);

        // ── 3b) 「Name 空间有候选、但候选全被 MayRequire* 挡掉」的声明数（**只看会被加载的声明**）──
        //   引擎会跳这些候选节点，代价必须能看见：RD2005 会因此变多，而原来四个 basis 数看不出来源
        //   （详见 Analysis.NameSpaceBlockedByMayRequire）。被挡掉的**声明自己**不在统计范围：
        //   它引擎里根本不加载，也不会产生任何诊断。
        var nameSpaceBlocked = 0;
        var unresolvedMayRequireBlocked = 0;
        foreach (var d in ctx.Defs)
        {
            if (string.IsNullOrEmpty(d.ParentName)) continue;
            if (!eligible.Contains(d)) continue;
            if (!ctx.Index.ByAbstractName.TryGetValue(d.ParentName!, out var cands)) continue;
            if (PickEligible(cands, eligible) is not null) continue;      // 有可用候选 ⇒ 不受影响
            nameSpaceBlocked++;
            if (literals.Names.Contains(d.ParentName!)) continue;          // 被补丁字面量救回
            if (ctx.Index.ByDefName.TryGetValue(d.ParentName!, out var dn) && PickEligible(dn, eligible) is not null) continue;
            unresolvedMayRequireBlocked++;
        }

        var byDecl = new Dictionary<RawDef, ParentResolution>(ReferenceEqualityComparer.Instance);
        var memo = new Dictionary<RawDef, ResolutionBasis>(ReferenceEqualityComparer.Instance);
        var memoTerminal = new Dictionary<RawDef, RawDef?>(ReferenceEqualityComparer.Instance);
        var memoLen = new Dictionary<RawDef, int>(ReferenceEqualityComparer.Instance);
        foreach (var d in ctx.Defs)
        {
            if (string.IsNullOrEmpty(d.ParentName)) continue;
            var evaluated = eligible.Contains(d);
            var step = ResolveOneHop(d.ParentName!, d, ctx.Index, literals, eligible);
            var (chain, terminal, len) = ResolveChain(d, ctx.Index, literals, eligible, memo, memoTerminal, memoLen, step.Basis);
            var weakest = WeakestChainBasis(step.Basis, chain);
            byDecl[d] = new ParentResolution(d, step.Basis, step.Target, weakest, terminal, len, evaluated);
        }

        return new Analysis(byDecl, literals.Names, literals.Additions, activeModIds,
            nameSpaceBlocked, unresolvedMayRequireBlocked,
            patchScan.ElapsedMilliseconds, patchRoots.Count);
    }

    /// <summary>
    /// `CheckContext.ActivePackageIds` 缺省时的兜底：目录表里**已启用**的 mod（`Catalog.Active`）。
    /// ★ 旧注释写「`Catalog.All` 的全集 = 已安装（不是 `Active`）」是**错的** —— 代码本来就取 `Active`，
    ///   而 `Docs\01` §5.1.5 那句「判定集合 = 已安装」也已被反编译证伪（见类注释）。
    /// </summary>
    private static HashSet<string> BuildActiveModIdSet(ModCatalog catalog) =>
        NormalizeActiveModIds(catalog.Active.Select(m => m.PackageId));

    // ────────────────────────────────────────────────────────────────────────────
    // 单跳解析
    // ────────────────────────────────────────────────────────────────────────────

    private static (ResolutionBasis Basis, RawDef? Target) ResolveOneHop(
        string parentName, RawDef self, DefIndex index,
        (HashSet<string> Names, List<(string, string, string)> Additions) literals,
        HashSet<RawDef> eligible)
    {
        // Order（对齐引擎 + 本 Task 的解析顺序）：
        //   NameSpace → NameSpaceViaPatchLiteral → DefNameFallback → Unresolved
        if (index.ByAbstractName.TryGetValue(parentName, out var abs))
        {
            var t = PickEligible(abs, eligible);
            if (t is not null) return (ResolutionBasis.NameSpace, t);
        }

        if (literals.Names.Contains(parentName))
            return (ResolutionBasis.NameSpaceViaPatchLiteral, null);   // 补丁补在哪个 def 上我们不求值 XPath

        if (index.ByDefName.TryGetValue(parentName, out var defs))
        {
            var t = PickEligible(defs, eligible);
            if (t is not null) return (ResolutionBasis.DefNameFallback, t);
        }

        return (ResolutionBasis.Unresolved, null);

        // 引擎在**同名多声明**时按 mod 加载序挑；静态分析没有「引擎会挑哪个」的可靠判据，
        // 故只在「至少有一个候选可用」时取第一个（方向是**少报**缺失，与过近似一致）。
    }

    private static RawDef? PickEligible(List<RawDef> candidates, HashSet<RawDef> eligible)
    {
        // ★ 集合键是【声明对象引用】：`RawDef` 是 record，默认相等是**值相等**，
        //   同一行两个字段完全相同的声明会被当成同一个 —— 必须用引用相等区分。
        foreach (var c in candidates)
            if (eligible.Contains(c)) return c;
        return null;
    }

    // ────────────────────────────────────────────────────────────────────────────
    // 链解析（带 memo；不是为了性能，而是为了「同一节点只算一次」的可复现性）
    // ────────────────────────────────────────────────────────────────────────────

    private static (List<ResolutionBasis> Chain, RawDef? Terminal, int Len) ResolveChain(
        RawDef start, DefIndex index,
        (HashSet<string> Names, List<(string, string, string)> Additions) literals,
        HashSet<RawDef> eligible,
        Dictionary<RawDef, ResolutionBasis> memo,
        Dictionary<RawDef, RawDef?> memoTerminal,
        Dictionary<RawDef, int> memoLen,
        ResolutionBasis rootBasis)
    {
        // ★ visiting 必须是【本次链行走】自己的集合。做成外层共享会让「同一中间节点被两条链经过」
        //   在第二次被判成环（实测后果：withParent=5129 里 1480 条被误标 Unresolved → RD2005 暴涨到 1486）。
        //   ★ 键是【声明对象引用】—— 同一行可以写多个 def；用 (FileAbs, Line) 会把同行的父子
        //     当成同一个节点，从而报出**假 RD2006**。
        var visiting = new HashSet<RawDef>(ReferenceEqualityComparer.Instance);
        var chain = new List<ResolutionBasis>();
        var cur = start;
        RawDef? terminal = null;
        var len = 0;
        while (cur is not null && !string.IsNullOrEmpty(cur.ParentName))
        {
            if (visiting.Contains(cur)) { chain.Add(ResolutionBasis.Unresolved); break; }   // 环：链走不通

            var step = ResolveOneHop(cur.ParentName!, cur, index, literals, eligible);
            chain.Add(step.Basis);
            len++;
            terminal = step.Target;

            if (step.Target is null) break;

            if (memo.TryGetValue(step.Target, out var cached)) { chain.Add(cached); len += memoLen.GetValueOrDefault(step.Target); terminal = memoTerminal.GetValueOrDefault(step.Target); break; }

            visiting.Add(cur);
            cur = step.Target;
        }

        memo[start] = WeakestChainBasis(rootBasis, chain);
        memoTerminal[start] = terminal;
        memoLen[start] = len;
        return (chain, terminal, len);
    }

    /// <summary>
    /// 链上只要有一跳是 <see cref="ResolutionBasis.Unresolved"/>，整条链就是未解析 ——
    /// 因为链的终点必须是「不带 ParentName 的基类」才算真正解析成功。
    /// 依据的强弱：NameSpace &gt; ViaPatchLiteral &gt; DefNameFallback &gt; Unresolved。
    /// </summary>
    private static ResolutionBasis WeakestChainBasis(ResolutionBasis first, List<ResolutionBasis> chain)
    {
        var worst = first;
        foreach (var b in chain) worst = Weakest(worst, b);
        return worst;
    }

    private static ResolutionBasis Weakest(ResolutionBasis a, ResolutionBasis b) =>
        (int)b > (int)a ? b : a;

    // ────────────────────────────────────────────────────────────────────────────
    // 声明链（环检测的输入；**按声明身份**，不按名字）
    // ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 从 <paramref name="start"/> 沿 `ParentName` 往上走，返回**声明链**。
    /// 不在这里判环 —— 环的判定键是声明身份，交回消费者（`ParentChainCheck` 的既有实现不变）。
    /// </summary>
    public static IReadOnlyList<RawDef> Walk(Analysis a, RawDef start)
    {
        var path = new List<RawDef>();
        // ★ seen 的键是【声明对象引用】：同一行可以写多个 def，用 (FileAbs, Line) 会把同行的
        //   两个不同声明当成同一个 —— 那是**假 RD2006**（本轮新发现的一类，见 08 §8）。
        var seen = new HashSet<RawDef>(ReferenceEqualityComparer.Instance);
        var cur = start;
        while (cur is not null && !string.IsNullOrEmpty(cur.ParentName))
        {
            if (!seen.Add(cur)) { path.Add(cur); break; }     // 回到已访问的声明：把闭合点交出去
            path.Add(cur);
            var step = a.Get(cur);
            // ★ 必须走 **直接**父声明：Terminal 是链的终点，成环时会指回环内节点，
            //   于是声明链退化成 `A → A`（自测 RD2006 真环用例抓到的就是这个）。
            if (step?.DirectTarget is null) break;
            cur = step.DirectTarget;
        }
        return path;
    }

    /// <summary>
    /// 面向继承的展示名：`ParentName` 指的**就是 `Name` 属性**，其次 `defName`，
    /// 两者都没有就用「文件名:行号」（声明定位，永不与另一处声明混淆）。
    /// 与 `ParentChainCheck.DisplayName` 是同一口径 —— 放在这里，避免两处各写一遍。
    /// </summary>
    public static string DisplayName(RawDef d) =>
        !string.IsNullOrEmpty(d.NameAttr) ? d.NameAttr
        : !string.IsNullOrEmpty(d.DefName) ? d.DefName
        : System.IO.Path.GetFileName(d.FileAbs) + ":" + d.Line;

    // ────────────────────────────────────────────────────────────────────────────
    // MayRequire / MayRequireAnyOf
    // ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 拆分 `MayRequire*` 的值。**按逗号拆** —— 反编译实证（不是推测）：
    /// <code>
    /// // Verse.XmlInheritance.TryRegister
    /// XmlAttribute xmlAttribute3 = node.Attributes["MayRequire"];
    /// if ((xmlAttribute == null &amp;&amp; xmlAttribute2 == null)
    ///     || (xmlAttribute3 != null &amp;&amp; !ModLister.AllModsActiveNoSuffix(xmlAttribute3.Value.Split(','))))
    ///     return;   // ← 跳过注册
    /// </code>
    /// 拆出来的每一项再 `Trim()`（`GetActiveModWithIdentifier` 自己会 `ToLowerInvariant().Trim()`）。
    /// ★ 旧注释说「空格分隔」是**错的**（那会把 `"A,B"` 当成一个 id → 恒不命中 → 把本可解析的父类
    ///   当成不存在）。本机官方内容里就有这种写法：`Data\Core\Defs\Stats\Stats_Pawns_General.xml:653`
    ///   的 `MayRequireAnyOf="Ludeon.RimWorld.Biotech,Ludeon.RimWorld.Anomaly"`。
    /// </summary>
    private static string[] SplitModIds(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value!.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();

    /// <summary>
    /// 该节点在引擎里会不会被 `TryRegister` 注册（两个属性都满足才注册）。
    ///
    /// ★★ 两个属性的语义**不一样**（反编译实证，不许照「都取任一」写）：
    ///   · `MayRequire`      = `ModLister.AllModsActiveNoSuffix(list)` → 列表中**每一项都**必须存在（**与**）；
    ///   · `MayRequireAnyOf` = `ModLister.AnyModActiveNoSuffix(list)` → 列表中**任一项**存在即可（**或**）。
    ///     （IL 实证：`ParseAndProcessXML` 的两个循环分别调 `AllModsActiveNoSuffix` 与
    ///      `AnyModActiveNoSuffix`；`AnyFromListActive` 这个方法存在，但这两处都不用它。）
    ///   两个属性之间是**与**：`TryRegister` 只读了 `MayRequire`，`MayRequireAnyOf` 由调用方
    ///   （`LoadedModManager` 的建 def 循环）另行判定，两者任一不满足该节点都不注册、**也不建 def**。
    /// </summary>
    public static bool IsEligible(RawDef d, IReadOnlySet<string> activeModIds) =>
        MayRequireSatisfied(d.MayRequire, activeModIds, anyOf: false)
        && MayRequireSatisfied(d.MayRequireAnyOf, activeModIds, anyOf: true);

    /// <param name="anyOf">true = MayRequireAnyOf（任一命中）；false = MayRequire（全部命中）。</param>
    private static bool MayRequireSatisfied(string? value, IReadOnlySet<string> activeModIds, bool anyOf)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var parts = SplitModIds(value);

        // 值里**一个 id 都没解析出来**时不能当成「满足」：引擎会把整个字符串当一个 id 去查
        // （`Split(',')` 至少给出一个元素），查不到就跳过。这里对齐引擎 → 不满足。
        if (parts.Length == 0) return false;

        // `MayRequire` 走 `AllModsActiveNoSuffix`（全部命中）；`MayRequireAnyOf` 走
        // `AnyModActiveNoSuffix`（任一命中）—— 两者都是「查询侧只 lower+trim、后缀在表侧归一」，
        // 所以这里逐个 id lower 后查归一化集合即可，与引擎等价。
        return anyOf
            ? parts.Any(p => IsModActiveNoSuffix(p, activeModIds))
            : parts.All(p => IsModActiveNoSuffix(p, activeModIds));
    }

    /// <summary>
    /// 引擎的 packageId 后缀（IL 实证：`Verse.ModMetaData..cctor` 里
    /// `IL_0000 ldstr "_steam"` → `IL_0005 stsfld Verse.ModMetaData.SteamModPostfix`，token `0x060024DF`）。
    /// 工坊 mod 与本地 mod 撞 packageId 时 `TryAddMod` 把其中一份的
    /// `appendPackageIdSteamPostfix` 置真，于是 `PackageId` 属性 = `packageIdLowerCase + "_steam"`。
    /// </summary>
    private const string SteamModPostfix = "_steam";

    /// <summary>
    /// 把「活动 packageId 集合」归一到引擎**建表用的键**（`modsByPackageIdIgnorePostfix`）。
    ///
    /// 引擎真值（逐行反编译，不是推测）：
    ///   · **建表**：`ModLister.TryAddMod` 用 `mod.packageIdLowerCase` 作键，
    ///     而 `ModMetaData.Init` 里 `packageIdLowerCase = meta.packageId.ToLower()`
    ///     —— 就是 About.xml 里那个 id，**不含** `_steam`；
    ///   · **查询**：`GetActiveModWithIdentifier(x, ignorePostfix: true)` **只做**
    ///     `identifier.ToLowerInvariant().Trim()` 然后查表 —— **查询侧不剥任何后缀**。
    /// ⇒ 名字里的 `IgnorePostfix` 说的是「表忽略了后缀」，**不是**「查询会被剥后缀」。
    ///
    /// 所以归一化只能做在**集合**这一侧：`Trim()` + `ToLowerInvariant()`，若入参来自
    /// `ModsConfig` 的 `PackageId`（可能带 `_steam`）则剥掉它。
    ///
    /// ★ 第一版把剥离写在**查询侧**，两个地方都错：剥错的字符（`-` 而不是 `_steam`）、
    ///   剥错的方向（引擎剥表、不剥查询），于是「活动集里有 `syn.suffixed_steam`、
    ///   `MayRequire="syn.suffixed"`」查不中（自测 (e5) 抓到）。
    /// </summary>
    public static HashSet<string> NormalizeActiveModIds(IEnumerable<string> ids)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in ids)
        {
            var k = raw.Trim().ToLowerInvariant();
            if (k.Length == 0) continue;
            if (k.EndsWith(SteamModPostfix, StringComparison.Ordinal) && k.Length > SteamModPostfix.Length)
                k = k[..^SteamModPostfix.Length];
            keys.Add(k);
        }
        return keys;
    }

    /// <summary>`MayRequire*` 里的一个 id 是否命中（**精确匹配**；查询侧不剥后缀，见上）。</summary>
    private static bool IsModActiveNoSuffix(string id, IReadOnlySet<string> activeModIds) =>
        activeModIds.Contains(id.Trim().ToLowerInvariant());

    /// <summary>
    /// 「本节点被 `MayRequire*` 挡掉」时，返回**未启用/未安装**的那些 mod（供 RD2008 的 evidence 用）。
    /// 满足则返回空列表。两个属性按各自语义判定（`MayRequire` 全部、`MayRequireAnyOf` 任一），保持声明顺序。
    /// </summary>
    public static IReadOnlyList<string> MissingMods(RawDef d, IReadOnlySet<string> activeModIds)
    {
        var missing = new List<string>();
        CollectMissing(d.MayRequire, anyOf: false);
        CollectMissing(d.MayRequireAnyOf, anyOf: true);
        return missing;

        void CollectMissing(string? value, bool anyOf)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var parts = SplitModIds(value);
            if (parts.Length == 0) { missing.Add(value!.Trim()); return; }
            if (MayRequireSatisfied(value, activeModIds, anyOf)) return;   // 满足 ⇒ 不算被挡
            foreach (var p in parts) if (!missing.Contains(p)) missing.Add(p);
        }
    }

    // ────────────────────────────────────────────────────────────────────────────
    // 补丁字面量 Name 集
    // ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 从本次扫描集合里的补丁文件收集「补丁补上的 `Name` 字面量」。
    ///
    /// ★ 判据（任务书 Step 1 的 MK3，逐条都要能回查）：
    ///   · 只看根元素是 `<Patch>` 的文件（引擎 `ModContentPack.LoadPatches` 的硬要求）；
    ///   · 取 `Class=` 简单名为 `PatchOperationAttributeAdd` / `PatchOperationAttributeSet`
    ///     且子元素 `<attribute>` 文本等于 `Name` 的那些操作的**字面量 `<value>`**；
    ///   · 操作可能嵌在 `<operations><li Class="…">` 里（`PatchOperationSequence`），
    ///     也可能**本身就是** `<match>` / `<nomatch` 元素（`Conditional`/`FindMod` 的字段是
    ///     `private PatchOperation match`，由该元素自己的 `Class=` 反序列化）—— 两种都要下探；
    ///   · **不评估 XPath**（那是 PatchLayer 的事，且全库不可行）：这是有意的过近似，
    ///     方向是**少报**（把可能不存在的 Name 当存在）。
    ///   · `PatchOperationSetName` 的 `<name>` **不**计入：引擎那是「用新名字**重建**元素」
    ///     （`CreateElement(name) + InnerXml`），旧的 `Name` 属性随之消失，所以它不提供继承名，
    ///     反而**取消**一个名字。把它算进「救回」会凭空多报。
    /// </summary>
    public static (HashSet<string> Names, List<(string Name, string FileAbs, string ModPackageId)> Additions)
        CollectPatchNameLiterals(IReadOnlyList<(string FileAbs, string ModPackageId)> patchFiles)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var adds = new List<(string, string, string)>();

        foreach (var (fileAbs, mod) in patchFiles)
        {
            System.Xml.XmlDocument doc;
            try { doc = new System.Xml.XmlDocument(); doc.Load(fileAbs); }
            catch { continue; }                                  // 坏文件的 RD2001 归 DefScanner，本层不重复报
            if (doc.DocumentElement is null || doc.DocumentElement.Name != "Patch") continue;

            // ★ 一级子元素必须是 `Operation`（引擎 `ModContentPack.LoadPatches` 的硬要求：
            //   其它名字 Log.Error 后整份跳过）—— 这里也只认它，避免把非补丁内容当操作。
            foreach (System.Xml.XmlNode op in doc.DocumentElement.ChildNodes)
            {
                if (op.NodeType != System.Xml.XmlNodeType.Element) continue;
                var oel = (System.Xml.XmlElement)op;
                if (!string.Equals(oel.Name, "Operation", StringComparison.Ordinal)) continue;
                Visit(oel, fileAbs, mod, names, adds);
            }
        }
        return (names, adds);



        static void Visit(System.Xml.XmlElement el, string fileAbs, string mod,
            HashSet<string> names, List<(string, string, string)> adds)
        {
            var cls = PatchLayer.SimpleName(el.GetAttribute("Class"));

            if (cls is "PatchOperationAttributeAdd" or "PatchOperationAttributeSet")
            {
                var attr = el.SelectSingleNode("attribute")?.InnerText?.Trim();
                if (string.Equals(attr, "Name", StringComparison.Ordinal))
                {
                    var v = el.SelectSingleNode("value")?.InnerText?.Trim();
                    if (!string.IsNullOrEmpty(v))
                    {
                        names.Add(v!);
                        adds.Add((v!, fileAbs, mod));
                    }
                }
            }

            // ★ 下探规则（两个形态都要覆盖；第一版只写对了一半）：
            //   · `PatchOperationSequence` 的操作装在 `<operations><li Class=…>` 里 —— 元素名是 **li**，
            //     Class 在 li 自己身上；`<operations>` 只是无 Class 的容器；
            //   · `Conditional` / `FindMod` 的 `<match>` / `<nomatch>` 元素**本身就是一条操作**
            //     （引擎字段是 `private PatchOperation match`，按该元素自己的 `Class=` 反序列化）。
            //   ★ 所以递归的粒度必须是【元素】而不是【元素的子节点列表】：
            //     第一版写成 `Visit(ce.ChildNodes, …)`，`<match>` 自己永远走不到 ——
            //     只扫过它的子节点（xpath/attribute/value），字面量静默漏掉（自测 (i) 抓到）。
            //   ★ 判定用 Class 而不是元素名：`li` / `match` / `nomatch` 以及将来可能出现的容器，
            //     元素名各不相同，按名字枚举必然漏；「已知操作类 **或** 无 Class（容器）」覆盖全部形态。
            //     （无 Class 的元素也下探是**有意的过近似**，方向 = 少报，与本层整体口径一致。）
            foreach (System.Xml.XmlNode child in el.ChildNodes)
            {
                if (child.NodeType != System.Xml.XmlNodeType.Element) continue;
                var ce = (System.Xml.XmlElement)child;
                var ccls = PatchLayer.SimpleName(ce.GetAttribute("Class"));
                if (PatchLayer.KnownOperations.ContainsKey(ccls) || ccls.Length == 0) Visit(ce, fileAbs, mod, names, adds);
            }
        }
    }
}