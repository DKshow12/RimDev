using RimDev.Core.Checks;
using RimDev.Core.Metadata;

namespace RimDev.Core.Defs;

/// <summary>
/// 离线「引擎加载完之后会存在的 def 集合」的最小模型（第 3 期 Task 6）。
///
/// 它的**唯一消费者**是 <see cref="Checks.ReferenceCheck"/> 的 RD3001 存在性判定：
/// 「这条引用指向的 `(DefType, defName)` 到底存不存在」。
///
/// ── 口径（每一项都对应一条引擎事实，不是方便起见）────────────────────────────────
/// <list type="number">
/// <item><b>哪个声明会成为 def</b>：非 `Abstract="True"` **且** `MayRequire*` 满足。
///   被挡掉的节点引擎**连 def 都不建**（`Verse.LoadedModManager.ParseAndProcessXML` 的建 def 循环
///   自己过一遍 `MayRequire*`，见 <see cref="InheritanceLayer"/> 类注释【3b】）⇒ 它不能作为引用目标。</item>
/// <item><b>defName 可以是继承来的</b>：子声明自己不写 `&lt;defName&gt;` 时，`Verse.XmlInheritance`
///   的合并结果里留下的是**父节点**的 `&lt;defName&gt;`（第 3 期 Task 5b 的 `RecursiveNodeCopyOverwriteElements`
///   逐行复刻：子没有该元素 ⇒ 父的同名块原样保留）。
///   ★ 本类**没有**为此另写一套合并：它沿 <see cref="InheritanceLayer.Walk"/> 取链上**第一个**
///   非空 `DefName`，而这条等价关系由自测用例在**真语料**上与 `InheritanceLayer.Merge` 的实际输出
///   逐声明对账（`Phase3Task6Cases` 的「继承 defName ≡ Merge 结果」用例）—— 口径只有一处，且被证伪过。</item>
/// <item><b>链断了就不继承</b>：`InheritanceLayer.ResolveCore` 在任何一跳解析不开时向上返回 null，
///   引擎的 `GetResolvedNodeFor` 于是**退回原始节点** ⇒ 该声明只有自己的 `&lt;defName&gt;`。
///   本类照此：`ChainBasis == Unresolved` 时不做链式继承。</item>
/// <item><b>键用「解析后的类型名 + defName」</b>：元素名有两种合法写法（`BackstoryDef` / `BackStoryDef`，
///   引擎对元素名→C# 类型的大小写不敏感），所以键取 `catalog.ResolveXmlTypeName(DefType)`（**元素名**：RD2004/RD2026 的 (DefType, defName) 键也是它）
///   的 `Type.Name`；解析不到（mod 自定义 Def 类型，本进程没加载它的程序集）才退回**字面**名字。
///   引用侧的候选键同时给 `Type.Name` 与 `Type.FullName`（自定义 Def 的 XML 元素名惯例是**全限定名**）。</item>
/// </list>
///
/// ── 明确不在本类范围内的边界（**必须计数并打进输出**，不许静默）────────────────────
/// <list type="bullet">
/// <item><b>补丁</b>：本类吃的是**未打补丁**的声明集合。<see cref="Checks.PatchCheck"/> /
///   `sim` 会在 `扫描集合 ∩ 启用集合` 上求值补丁并报出「顶层 def 节点 before→after」——
///   两者之差就是本类看不到的东西（往往是 0，那时边界可证为空）。</item>
/// <item><b>mod 自定义 Def 类型的字段</b>：`RimDev.Core` 只加载官方程序集，所以 mod 自定义 Def 类里
///   的引用字段**不在** <see cref="ReferenceFieldIndex"/> 里（引用侧覆盖不到）。由
///   <see cref="ReferenceCheck"/> 的统计块显式报出「未知类型声明数」。</item>
/// <item><b>遮蔽</b>：本集合只回答「这个名字有没有声明」，不回答「哪一个赢」。胜者判定在
///   <see cref="Checks.ShadowingCheck"/>（RD2025/RD2026）。因此 <see cref="ShadowedDeclarations"/>
///   只是「同名声明里落败的那些」的计数，供 `sim` 摘要使用。</item>
/// </list>
/// </summary>
public sealed class SimulatedDefSet
{
    private readonly Dictionary<string, List<string>> _byType = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<int, List<string>>> _byTypeLen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _countByTypeAndName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _typesByName = new(StringComparer.Ordinal);

    private SimulatedDefSet() { }

    // ── 摘要数字（`rimdev sim` 的「加载结果摘要」从这里取，CLI 不另算一遍）─────────
    /// <summary>扫描集合里的声明总数（含抽象节点与被 `MayRequire*` 挡掉的节点）。</summary>
    public int Declarations { get; private init; }
    /// <summary>`Abstract="True"` 的声明数（引擎不会把它们建成 def）。</summary>
    public int AbstractDeclarations { get; private init; }
    /// <summary>被 `MayRequire*` 挡掉的声明数（引擎连 def 都不建）。</summary>
    public int SkippedByMayRequire { get; private init; }
    /// <summary>会成为 def 的声明数 = Declarations − Abstract − SkippedByMayRequire。</summary>
    public int EffectiveDeclarations { get; private init; }
    /// <summary>有效 def 名（distinct 的 `(类型, defName)` 对）数。</summary>
    public int EffectiveDefNames { get; private init; }
    /// <summary>落败的同名声明数 = EffectiveDeclarations − EffectiveDefNames（含跨安装副本）。</summary>
    public int ShadowedDeclarations { get; private init; }
    /// <summary>自己不写 `&lt;defName&gt;`、靠继承链拿到名字的声明数。</summary>
    public int InheritedDefNameDeclarations { get; private init; }
    /// <summary>既没有自己的 `&lt;defName&gt;`、链上也拿不到的声明数（引擎里等于无名 def ⇒ 不可被引用）。</summary>
    public int UnnamedDeclarations { get; private init; }
    /// <summary>解析不到 CLR 类型、只能用字面元素名做键的声明数（mod 自定义 Def 类型）。</summary>
    public int UnresolvedTypeDeclarations { get; private init; }
    /// <summary>带 Class= 的声明数（它必须报出来：键取元素名这条口径正是为了它们）。</summary>
    public int ClassAttrDeclarations { get; private init; }

    /// <summary>某个 `(DefType, defName)` 是否存在于有效集合里。</summary>
    public bool Contains(string defType, string defName) =>
        _byType.TryGetValue(defType, out var names) && ContainsName(names, defName);

    /// <summary>
    /// 候选键里**任一** `(类型, defName)` 命中即算存在。
    /// 用于两条口径：（a）引用槽类型给 `Type.Name` + `Type.FullName`；
    /// （b）槽类型是抽象 Def（或基类 `Def`）时，目标 DefType 未知 ⇒ 传「可赋值的全部具体 Def 类型名」。
    /// </summary>
    public bool ContainsAnyOf(IReadOnlyList<string> candidateTypes, string defName)
    {
        foreach (var t in candidateTypes)
            if (Contains(t, defName)) return true;
        return false;
    }

    /// <summary>该 DefType 下的全部 defName（只读，供统计/建议）。</summary>
    public IReadOnlyList<string> NamesOf(string defType) =>
        _byType.TryGetValue(defType, out var names) ? names : Array.Empty<string>();

    /// <summary>
    /// 这个名字在**哪些别的 DefType** 下存在（升序，最多 <paramref name="max"/> 项）。
    /// RD3001 的归因线索：字段声明的 Def 类型与实际 def 的类型不一致时，
    /// 「名字在别的类型下存在」能把一条看似悬空的引用立刻变成可解释的假阳/真缺陷。
    /// </summary>
    public IReadOnlyList<string> TypesContaining(string defName, int max = 5) =>
        _typesByName.TryGetValue(defName, out var types)
            ? types.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).Take(max).ToList()
            : Array.Empty<string>();

    /// <summary>同名声明数（= 这个 `(类型, defName)` 被声明了几次）。</summary>
    public int DeclarationCount(string defType, string defName) =>
        _countByTypeAndName.GetValueOrDefault(defType + "\u0000" + defName);

    /// <summary>
    /// 最相近的 <paramref name="max"/> 个候选（Levenshtein；**同分按名字的 Ordinal 升序**，
    /// 保证 `--json` 两次运行逐字节相同）。只在候选键各自的 DefType 名字池里找。
    ///
    /// 性能：用「长度分桶」预筛（距离 ≤ <paramref name="maxDistance"/> 的候选必然长度差 ≤ maxDistance），
    /// 避免对 ThingDef 这种几万个名字的池子逐个算编辑距离。
    /// </summary>
    public IReadOnlyList<string> Suggest(IReadOnlyList<string> candidateTypes, string defName, int max = 5)
    {
        // ★ 口径：取**整个候选池**里最近的 max 个（任务书：「最相近的 5 个候选」）。
        //   上界不能直接取 max —— 否则「最近的第 5 名距离 > 5」时会把候选截成空
        //   （实测：官方池里 Shot_MachineGun → Shot_MachinePistol 的距离 > 5，只报 Shot_Minigun）。
        //   做法：用「长度分桶」预筛，bound 逐步放宽（4 → 12 → 28 …）直到拿到 ≥ max 个命中
        //   或 bound 超过两串长度的上界（此时距离一定 ≤ bound，结果必然正确）。
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hits = new List<(int Distance, string Name)>();
        var upper = Math.Max(defName.Length, 1) + 1;
        for (var bound = 4; ; bound = bound * 2 + 4)
        {
            hits.Clear(); seen.Clear();
            foreach (var t in candidateTypes)
            {
                if (!_byTypeLen.TryGetValue(t, out var byLen)) continue;
                for (var len = Math.Max(0, defName.Length - bound); len <= defName.Length + bound; len++)
                {
                    if (!byLen.TryGetValue(len, out var names)) continue;
                    foreach (var n in names)
                    {
                        if (!seen.Add(n)) continue;
                        var d = LevenshteinBounded(defName, n, bound);
                        if (d <= bound) hits.Add((d, n));
                    }
                }
            }
            if (hits.Count >= max || bound >= upper) break;
        }

        return hits
            .OrderBy(x => x.Distance)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Take(max)
            .Select(x => x.Name)
            .ToList();
    }

    private static bool ContainsName(List<string> names, string defName)
    {
        // 池子通常很小（几十~几百），但 ThingDef 可达几万 ⇒ 排序后二分。
        if (names.Count > 32) return names.BinarySearch(defName, StringComparer.Ordinal) >= 0;
        foreach (var n in names) if (string.Equals(n, defName, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>带上界的编辑距离：超过上界立刻返回上界+1（避免整表计算）。</summary>
    public static int LevenshteinBounded(string a, string b, int bound)
    {
        if (Math.Abs(a.Length - b.Length) > bound) return bound + 1;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var rowMin = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                if (cur[j] < rowMin) rowMin = cur[j];
            }
            if (rowMin > bound) return bound + 1;
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    // ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 从一次扫描的声明集合 + `ParentName` 解析结果建集合。
    /// </summary>
    /// <param name="ctx">本次扫描（<c>Defs</c> 必须来自全 scope 扫描）。</param>
    /// <param name="analysis">
    /// `InheritanceLayer.Analyze(ctx)` 的结果 —— 唯一来源，本类不重新解析 ParentName。
    /// </param>
    /// <param name="catalog">用于把 `ResolvedTypeName` 归一到 CLR 类型名（大小写/全限定名归一）。</param>
    public static SimulatedDefSet Build(CheckContext ctx, InheritanceLayer.Analysis analysis,
        ManagedAssemblyCatalog catalog)
    {
        var byType = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var seenNames = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var byTypeLen = new Dictionary<string, Dictionary<int, List<string>>>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        int declarations = 0, abstractCount = 0, skipped = 0, effective = 0;
        int inherited = 0, unnamed = 0, unresolvedType = 0;

        foreach (var d in ctx.Defs)
        {
            declarations++;
            if (d.IsAbstract) { abstractCount++; continue; }

            // ★★ 资格判定必须走 InheritanceLayer.IsEligible（单一真源），**不能**用 analysis.Get(d)：
            //   后者的键集只覆盖「带 ParentName 的声明」，拿它当资格判据会让「无 ParentName 但带
            //   未满足 MayRequire」的声明被当成有效 def —— 自测「MayRequire 未满足的声明不进集合」
            //   第一版实测 SkippedByMayRequire=0，抓到的就是这条。
            if (!InheritanceLayer.IsEligible(d, analysis.ActivePackageIds)) { skipped++; continue; }
            var res = analysis.Get(d);

            var defName = d.DefName;
            if (string.IsNullOrEmpty(defName))
            {
                // 链断（ChainBasis == Unresolved）⇒ 引擎退回原始节点 ⇒ 不继承。
                if (res is not null && res.ChainBasis != InheritanceLayer.ResolutionBasis.Unresolved)
                {
                    foreach (var ancestor in InheritanceLayer.Walk(analysis, d))
                        if (!string.IsNullOrEmpty(ancestor.DefName)) { defName = ancestor.DefName; break; }
                    // ★★ Walk 只在「当前节点还有 ParentName」时才把它收进链，所以**链末那个没有
                    //   ParentName 的祖先不会被收进去** —— 而它恰恰就是写 <defName> 的那一级。
                    //   漏掉它会把「子 def 的 defName 来自父 def」整类判成无名（自测用例抓到）。
                    if (string.IsNullOrEmpty(defName) && !string.IsNullOrEmpty(res.Terminal?.DefName))
                        defName = res.Terminal!.DefName;
                    if (!string.IsNullOrEmpty(defName)) inherited++;
                }
            }

            if (string.IsNullOrEmpty(defName)) { unnamed++; continue; }

            effective++;
            var key = TypeKey(d, catalog, ref unresolvedType);
            if (!byType.TryGetValue(key, out var list))
            {
                byType[key] = list = new List<string>();
                seenNames[key] = new HashSet<string>(StringComparer.Ordinal);
                byTypeLen[key] = new Dictionary<int, List<string>>();
            }
            if (seenNames[key].Add(defName)) list.Add(defName);
            var countKey = key + "\u0000" + defName;
            counts[countKey] = counts.GetValueOrDefault(countKey) + 1;
        }

        // 排序（二分查找 + 建议列表的稳定序）
        foreach (var (type, list) in byType)
        {
            list.Sort(StringComparer.Ordinal);
            var byLen = byTypeLen[type];
            foreach (var n in list)
            {
                if (!byLen.TryGetValue(n.Length, out var bucket)) byLen[n.Length] = bucket = new List<string>();
                bucket.Add(n);
            }
        }

        var set = new SimulatedDefSet
        {
            Declarations = declarations,
            AbstractDeclarations = abstractCount,
            SkippedByMayRequire = skipped,
            EffectiveDeclarations = effective,
            EffectiveDefNames = byType.Values.Sum(x => x.Count),
            ShadowedDeclarations = effective - byType.Values.Sum(x => x.Count),
            InheritedDefNameDeclarations = inherited,
            UnnamedDeclarations = unnamed,
            UnresolvedTypeDeclarations = unresolvedType,
            ClassAttrDeclarations = ctx.Defs.Count(x => x.ClassAttr is not null),
        };
        foreach (var (k, v) in byType) set._byType[k] = v;
        foreach (var (type, names) in byType)
            foreach (var n in names)
            {
                if (!set._typesByName.TryGetValue(n, out var tl)) set._typesByName[n] = tl = new List<string>();
                tl.Add(type);
            }
        foreach (var (k, v) in byTypeLen) set._byTypeLen[k] = v;
        foreach (var (k, v) in counts) set._countByTypeAndName[k] = v;
        return set;
    }

    /// <summary>
    /// 声明的类型键：优先用**解析后的 CLR 类型简单名**（元素名大小写两种写法映射到同一个 C# 类型，
    /// 引擎对元素名→类型是大小写不敏感的）；解析不到才退回字面 `ResolvedTypeName`。
    /// </summary>
    private static string TypeKey(RawDef d, ManagedAssemblyCatalog catalog, ref int unresolvedType)
    {
        // ★ 键用【元素名】而不是 Class=：RD2004/RD2026 的 (DefType, defName) 键就是元素名
        //   （DefIndex.ByTypeAndDefName 用的正是 RawDef.DefType）。若改用 ResolvedTypeName，
        //   <ThingDef Class="CombatExtended.AmmoDef"> 会登记在一个**没有任何引用能写得出来**的键下
        //   （mod 程序集不在本进程里 ⇒ 引用侧不可能存在 CombatExtended.AmmoDef 类型的字段），
        //   于是所有指向它的 ThingDef 引用都会变成假悬空 —— 正是本项目最忌的成批假阳。
        //   这里只做一次**大小写归一**（引擎对元素名→C# 类型是大小写不敏感的，BackStoryDef/BackstoryDef
        //   是同一个类型）；归一失败（mod 自定义 Def 类型）就保留字面元素名。
        var resolved = catalog.ResolveXmlTypeName(d.DefType);
        if (resolved is null) { unresolvedType++; return d.ResolvedTypeName; }
        return resolved.Name;
    }

    /// <summary>
    /// 引用侧的候选键：`Type.Name`（原版元素名写法）与 `Type.FullName`（自定义 Def 的 XML 元素名
    /// 惯例是**全限定名**，本项目 `&lt;ThreatFleet.TF_MechModuleDef&gt;` 就是）。两者都给 ⇒ 判定方向是
    /// **少报**（宁可漏报、不可假阳）。
    /// </summary>
    public static IReadOnlyList<string> CandidateTypeKeys(Type slotType)
    {
        var name = slotType.Name;
        var full = slotType.FullName;
        if (string.IsNullOrEmpty(full) || full == name) return new[] { name };
        return new[] { name, full };
    }
}
