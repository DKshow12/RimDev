using System.Collections.Concurrent;
using System.Reflection;
using System.Xml;
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
    /// <summary>★ Task 8 Step 2：运行期生成的 `(类型\0名字)` 对 + 名字集合（用于「生成名命中数」的独立计数）。</summary>
    private readonly HashSet<string> _generatedPairs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _generatedNames = new(StringComparer.Ordinal);

    /// <summary>
    /// ★ 第 3 期 Task 6b：引擎**运行期生成 def** 的模型（12 个 `DefGenerator` 入口）。
    /// null = 没建模（旧行为）；非 null 时 <see cref="Names"/> 已经把生成名并进集合。
    /// </summary>
    public DefGeneratorModel? GeneratorModel { get; private set; }
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

    /// <summary>
    /// ★ 第 3 期 Task 6b：引擎**运行期生成**、并进本集合的 def 名数（不参与「声明」类计数）。
    /// </summary>
    public int GeneratedDefNames { get; private init; }

    /// <summary>★ Task 8 Step 1：`Build` 的三段耗时（只由 `sim --timings` 打印，不进 --json）。</summary>
    public long BuildDeclLoopMs { get; private init; }
    public long BuildGeneratorModelMs { get; private init; }
    public long BuildPostLoopMs { get; private init; }

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

    /// <summary>
    /// ★ Task 8 Step 2：这条引用是不是**靠「引擎运行期生成 def」才命中的**。
    /// 必须能单独计数：生成名不是 XML 声明，若优化时把它「顺手漏掉」，RD3001 会凭空变多，
    /// 而总量类计数（悬空数）只有在它恰好被别的名字抵消时才会看起来正常 —— 单看一个数不够。
    /// </summary>
    public bool AnyGeneratedHit(IReadOnlyList<string> candidateTypes, string defName)
    {
        if (!_generatedNames.Contains(defName)) return false;
        foreach (var t in candidateTypes)
            if (_generatedPairs.Contains(t + "\u0000" + defName)) return true;
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

    // ════════════════════════════════════════════════════════════════════════════════
    // ★ 第 3 期 Task 8：`Suggest` 的**精确等价加速路径**
    // ════════════════════════════════════════════════════════════════════════════════
    // 旧路径（上面的 `Suggest`）实测成本：全库 3532 条悬空引用、平均 **195.9 ms/条**
    //（`sim --all` 的 691.8 s 候选判定里几乎全部来自这里）。它按 bound 序列 4 → 12 → 28…
    // **整窗重扫**，且 bound 变大后 `LevenshteinBounded` 的早退条件（行最小 > bound）几乎不触发
    // ⇒ 同一个名字被算 2~3 次全表 DP，每次还分配两个 int[]。
    //
    // 本路径结果**逐项相同**，只扫一遍：
    //   ① 候选池按「目标类型」合并去重一次并跨调用复用（顺带消掉每次调用的 HashSet `seen`）；
    //   ② 按 |lenDiff| **升序**扫描，运行期 cutoff = 当前第 max 名的距离；
    //      一旦 `|lenDiff| > cutoff`，剩余候选满足 `dist ≥ |lenDiff| > cutoff`，不可能进前 max ⇒ 停；
    //   ③ 编辑距离走线程静态缓冲（热路径零分配）。
    //
    // 等价性推导（为什么「一遍扫 + 全局前 max + D 过滤」≡ 旧实现的输出）：
    //   · 旧实现第 k 轮的命中集 = { n : dist(n) ≤ b_k }（长度窗 [L-b_k, L+b_k] 恰好包含全体
    //     dist ≤ b_k 的名字，因为 dist ≥ |lenDiff|）；命中数对 b_k 单调不减。
    //   · 它停在序列 {4,12,28,…} 里第一个满足「命中 ≥ max」或「b_k ≥ upper」的 b_k（记作 D）；
    //     输出 = {dist ≤ D} 按 (dist, name) 升序取前 max。
    //     而「命中 ≥ max」⟺「第 max 小的距离 d_max ≤ b_k」⟹ D = 序列里第一个 ≥ min(d_max, upper) 的值。
    //   · 因此输出 =「全局 (dist, name) 升序前 max 名」再按 `dist ≤ D` 过滤 —— 就是本实现的做法。
    //     ★ 过滤不能省：d_max > upper 时 D 可能 < d_max，旧实现会**返回不足 max 个**，必须同样少返回。

    /// <summary>候选池：按长度分桶、已去重；桶内按名字 Ordinal 升序（结果与桶内顺序无关，排序只为可复现）。</summary>
    private sealed class SuggestPool
    {
        internal SuggestPool(Dictionary<int, string[]> byLen, int minLen, int maxLen)
        {
            ByLen = byLen;
            MinLen = minLen;
            MaxLen = maxLen;
        }

        internal Dictionary<int, string[]> ByLen { get; }
        internal int MinLen { get; }

        internal int MaxLen { get; }
        /// <summary>同一 (目标类型, 值) 的候选结果（同一悬空名常被多处引用；实测 3532 条里 2343 个 distinct）。</summary>
        internal ConcurrentDictionary<string, IReadOnlyList<string>> Cache { get; } = new(StringComparer.Ordinal);
    }

    private readonly ConcurrentDictionary<Type, SuggestPool> _suggestPools = new();
    private readonly object _poolBuildLock = new();

    /// <summary>
    /// 建议候选（加速路径）。语义与 <see cref="Suggest"/> **逐项相同**（推导见上方注释），
    /// 调用方额外给一个「目标类型」作为池与缓存的键。
    /// </summary>
    public IReadOnlyList<string> SuggestFor(Type targetType, IReadOnlyList<string> candidateTypes,
        string defName, int max = 5)
    {
        var pool = PoolFor(targetType, candidateTypes);
        if (pool.Cache.TryGetValue(defName, out var cached)) return cached;
        var res = ComputeSuggest(pool, defName, max);
        pool.Cache[defName] = res;
        return res;
    }

    private SuggestPool PoolFor(Type targetType, IReadOnlyList<string> candidateTypes)
    {
        if (_suggestPools.TryGetValue(targetType, out var hit)) return hit;
        lock (_poolBuildLock)
        {
            if (_suggestPools.TryGetValue(targetType, out hit)) return hit;
            var merged = new Dictionary<int, HashSet<string>>();
            foreach (var t in candidateTypes)
            {
                if (!_byType.TryGetValue(t, out var names)) continue;
                foreach (var n in names)
                {
                    if (!merged.TryGetValue(n.Length, out var set))
                        merged[n.Length] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(n);
                }
            }
            var byLen = new Dictionary<int, string[]>(merged.Count);
            var minLen = int.MaxValue;
            var maxLen = 0;
            foreach (var (bucketLen, set) in merged)
            {
                var arr = set.ToArray();
                Array.Sort(arr, StringComparer.Ordinal);
                byLen[bucketLen] = arr;
                if (bucketLen < minLen) minLen = bucketLen;
                if (bucketLen > maxLen) maxLen = bucketLen;
            }
            if (minLen == int.MaxValue) minLen = 0;
            hit = new SuggestPool(byLen, minLen, maxLen);
            _suggestPools[targetType] = hit;
            return hit;
        }
    }

    private static IReadOnlyList<string> ComputeSuggest(SuggestPool pool, string defName, int max)
    {
        if (max <= 0 || pool.ByLen.Count == 0) return Array.Empty<string>();

        var len = defName.Length;
        var upper = Math.Max(len, 1) + 1;
        var best = new List<(int Distance, string Name)>(max + 1);

        // ★ 单遍扫描（一遍 = 旧实现全部 bound 轮的并集，且**不重扫**）：
        //   ① 按 |lenDiff| 升序扫 —— 最像的候选先被看到，cutoff（当前第 max 名的距离）迅速收紧；
        //   ② 桶内用 `bound = min(dMax, cutoff)` 调 `LevenshteinBounded`：早退（行最小 > bound）
        //      让绝大多数候选只算几行；
        //   ③ 只扫到 |lenDiff| ≤ dMax 即可 —— 旧实现的 D 必 ≤ 第一个 ≥ upper 的 bound 值（= dMax），
        //      而 dist ≥ |lenDiff|，所以 {dist ≤ D} 全落在该窗口内（实测这一步把整池重扫从 3 遍缩成 1 遍）。
        var dMax = 4;
        while (dMax < upper && dMax < int.MaxValue / 2) dMax = dMax * 2 + 4;

        for (var lenDiff = 0; lenDiff <= dMax; lenDiff++)
        {
            if (best.Count >= max && lenDiff > best[max - 1].Distance) break;   // 精确提前终止
            var bound = best.Count >= max ? best[max - 1].Distance : dMax;
            ScanBucket(pool, len - lenDiff, defName, bound, best, max);
            if (lenDiff != 0) ScanBucket(pool, len + lenDiff, defName, bound, best, max);
            if (len - lenDiff < pool.MinLen && len + lenDiff > pool.MaxLen) break;
        }

        // ★ 旧实现的 D 过滤（必须保留，否则少数条目会多返回）：
        //   D = 序列 {4,12,28,…} 里第一个 ≥ min(d_max, upper) 的值（d_max = 第 max 小的距离；
        //   不足 max 个时视作 +∞ ⇒ D = dMax）。输出 = {dist ≤ D} 里最近的 max 个。
        //   只有 d_max > upper 时 D < d_max —— 此时旧实现**故意少返回**，这里同样按 D 截尾。
        if (best.Count > 0)
        {
            var dTarget = best.Count >= max ? Math.Min(best[max - 1].Distance, upper) : upper;
            var dOld = 4;
            while (dOld < dTarget && dOld < int.MaxValue / 2) dOld = dOld * 2 + 4;
            while (best.Count > 0 && best[best.Count - 1].Distance > dOld) best.RemoveAt(best.Count - 1);
        }

        var res = new List<string>(best.Count);
        foreach (var (_, n) in best) res.Add(n);
        return res;
    }

    private static void ScanBucket(SuggestPool pool, int bucketLen, string defName, int bound,
        List<(int Distance, string Name)> best, int max)
    {
        if (bucketLen < 0 || !pool.ByLen.TryGetValue(bucketLen, out var names)) return;
        foreach (var n in names)
        {
            var d = LevenshteinBoundedPooled(defName, n, bound);
            if (d > bound) continue;                      // bound+1 = 「超过上界」，丢弃
            var item = (d, n);
            var i = best.Count;
            while (i > 0 && CompareCandidate(best[i - 1], item) > 0) i--;
            if (i >= max) continue;                       // 比当前第 max 名还差
            best.Insert(i, item);
            if (best.Count > max) best.RemoveAt(best.Count - 1);
        }
    }

    /// <summary>与旧实现完全相同的排序口径：先距离、**同分按名字 Ordinal 升序**（`--json` 字节稳定的前提）。</summary>
    private static int CompareCandidate((int Distance, string Name) a, (int Distance, string Name) b)
    {
        var c = a.Distance.CompareTo(b.Distance);
        return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
    }

    [ThreadStatic] private static int[]? _levPrev;
    [ThreadStatic] private static int[]? _levCur;

    /// <summary>
    /// 与公开的 <see cref="LevenshteinBounded"/> **逐位相同**，只是复用线程静态缓冲。
    /// 热路径上每次调用省掉两个 `int[]` 分配（实测候选判定 681,581 次调用里绝大多数是这个函数）。
    /// </summary>
    private static int LevenshteinBoundedPooled(string a, string b, int bound)
    {
        if (Math.Abs(a.Length - b.Length) > bound) return bound + 1;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var need = b.Length + 1;
        var prev = _levPrev;
        var cur = _levCur;
        if (prev is null || prev.Length < need)
        {
            prev = _levPrev = new int[need];
            cur = _levCur = new int[need];
        }
        else if (cur is null || cur.Length < need)
        {
            cur = _levCur = new int[need];
        }
        var curArr = cur!;

        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curArr[0] = i;
            var rowMin = curArr[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curArr[j] = Math.Min(Math.Min(curArr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                if (curArr[j] < rowMin) rowMin = curArr[j];
            }
            if (rowMin > bound) return bound + 1;
            (_levPrev, _levCur) = (curArr, prev);
            prev = _levPrev!;
            curArr = _levCur!;
        }
        return prev[b.Length];
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
        ManagedAssemblyCatalog catalog, bool modelGenerators = true)
    {
        var byType = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var seenNames = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var byTypeLen = new Dictionary<string, Dictionary<int, List<string>>>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        int declarations = 0, abstractCount = 0, skipped = 0, effective = 0;
        int inherited = 0, unnamed = 0, unresolvedType = 0;

        // ★ Task 8 Step 1：Build 的三段耗时（声明循环 / 生成器建模 / 收尾排序）。
        //   必须有分段：`SimulatedDefSet.Build` 一次性涵盖了「遍历 31 万个声明」
        //   「重跑一遍全部 XML 收事实」「给每个名字建分桶」三件性质完全不同的事。
        var swDecl = System.Diagnostics.Stopwatch.StartNew();
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
        swDecl.Stop();

        // ★ 声明口径的 distinct 计数必须在并生成名**之前**取（生成名不是声明）。
        var declaredDistinct = byType.Values.Sum(x => x.Count);

        // ★★ 第 3 期 Task 6b：把「引擎运行期生成的 def」并进集合。
        //   唯一来源 = DefGeneratorModel（12 个生成器入口由 IL 实测得到，规则可证伪）。
        //   ★ 必须在 byType 上直接并（而不是事后加）——否则排序与长度分桶会与生成名不一致。
        //   ★ 生成名**不**进 counts（它们不是声明，DeclarationCount 应只数 XML 声明）。
        DefGeneratorModel? genModel = null;
        var genPairsLocal = new HashSet<string>(StringComparer.Ordinal);
        var genNamesLocal = new HashSet<string>(StringComparer.Ordinal);
        var swGen = System.Diagnostics.Stopwatch.StartNew();
        if (modelGenerators)
        {
            genModel = DefGeneratorModel.Build(catalog, ctx, analysis, byType);
            foreach (var (gt, gn) in genModel.Names)
            {
                if (!byType.TryGetValue(gt, out var glist))
                {
                    byType[gt] = glist = new List<string>();
                    seenNames[gt] = new HashSet<string>(StringComparer.Ordinal);
                    // ★ 必须同时建长度分桶：下面排序段用的是 `byTypeLen[type]` 直接索引。
                    //   漏了这一行 ⇒「某类型只有运行期生成名、没有任何 XML 声明」时抛
                    //   `KeyNotFoundException: The given key 'TerrainDef' was not present`。
                    //   官方语料恰好每个生成类型都有 XML 声明，所以只有夹具能抓到它
                    //   （Task 6b 的 TerrainDef 探针夹具第一次跑就命中了）。
                    byTypeLen[gt] = new Dictionary<int, List<string>>();
                }
                if (seenNames[gt].Add(gn)) glist.Add(gn);
                genPairsLocal.Add(gt + "\u0000" + gn);
                genNamesLocal.Add(gn);
            }
        }
        swGen.Stop();

        // 排序（二分查找 + 建议列表的稳定序）
        var swPost = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (type, list) in byType)
        {
            list.Sort(StringComparer.Ordinal);
            if (!byTypeLen.TryGetValue(type, out var byLen))
                byTypeLen[type] = byLen = new Dictionary<int, List<string>>();
            foreach (var n in list)
            {
                if (!byLen.TryGetValue(n.Length, out var bucket)) byLen[n.Length] = bucket = new List<string>();
                bucket.Add(n);
            }
        }
        swPost.Stop();

        var set = new SimulatedDefSet
        {
            Declarations = declarations,
            AbstractDeclarations = abstractCount,
            SkippedByMayRequire = skipped,
            EffectiveDeclarations = effective,
            EffectiveDefNames = declaredDistinct,
            ShadowedDeclarations = effective - declaredDistinct,
            GeneratedDefNames = genModel?.Names.Count ?? 0,
            BuildDeclLoopMs = swDecl.ElapsedMilliseconds,
            BuildGeneratorModelMs = swGen.ElapsedMilliseconds,
            BuildPostLoopMs = swPost.ElapsedMilliseconds,
            InheritedDefNameDeclarations = inherited,
            UnnamedDeclarations = unnamed,
            UnresolvedTypeDeclarations = unresolvedType,
            ClassAttrDeclarations = ctx.Defs.Count(x => x.ClassAttr is not null),
        };
        set.GeneratorModel = genModel;
        foreach (var p in genPairsLocal) set._generatedPairs.Add(p);
        foreach (var n in genNamesLocal) set._generatedNames.Add(n);
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

// ════════════════════════════════════════════════════════════════════════════════
// 第 3 期 Task 6b：引擎**运行期生成 def** 的模型
//
// 背景（`08` §4f.1）：引擎在 `DefGenerator.GenerateImpliedDefs_PreResolve/PostResolve`
// 里跑 12 个静态生成器，造出 XML 里**根本不存在**的 def；XML 对它们的引用是合法的。
// 静态检查不建模这一整块 ⇒ 官方 6 包上 95 条 RD3001 假阳。
//
// ★ 为什么是「规则 + IL 证据」而不是「前缀白名单」：
//   每条规则都声明了它必须能在**对应生成器的 IL 里找到**的字面量/字段引用（`lit:` / `field:`）。
//   找不到 ⇒ 该规则**被丢弃并计数**（`EvidenceFailures`），而不是照用；
//   于是「换个引擎版本前缀变了」会**响亮地失败**，而不是静默放行。
// ★ 每条规则的方向都是**有界**的：名字必须 = 字面量 + **某个真实存在的基底 def** 的 defName，
//   且基底还要满足该生成器的谓词（如 `Corpse_<X>` 要求 X 是**带 `<race>` 的 ThingDef**）。
//   所以 `Corpse_Gun`（基底 def 不存在）与「基底存在但不是带 race 的 ThingDef」都**仍会报 RD3001**。
// ════════════════════════════════════════════════════════════════════════════════

/// <summary>一条生成规则跑完后的**可审计**结果（`sim --json` 的 `generatedDefs.rules`）。</summary>
public sealed record GeneratedRuleReport(
    string RuleId,
    string Generator,
    string Method,
    string ProducedDefType,
    string NameShape,
    string BaseSpec,
    bool IlEvidenceOk,
    string Evidence,
    int BaseDefsMatched,
    int BaseBDefsMatched,
    int NamesGenerated);

/// <summary>
/// 一次「运行期生成 def」建模的实测统计。
/// ★ 所有计数都是**实测**（不是「应该有」）：`UnmodeledGenerators` 明确列出**没有规则覆盖**的
///   生成器入口 —— 防「没建模」被读成「没问题」。
/// </summary>
public sealed class DefGeneratorModel
{
    public IReadOnlyList<(string DefType, string Name)> Names { get; init; } = Array.Empty<(string, string)>();
    public IReadOnlyList<GeneratedRuleReport> Rules { get; init; } = Array.Empty<GeneratedRuleReport>();
    public IReadOnlyList<string> UnmodeledGenerators { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DiscoveredGenerators { get; init; } = Array.Empty<string>();
    public int EvidenceFailures { get; init; }

    /// <summary>
    /// ★ Task 8 Step 1：其中的「事实表扫描」耗时（`DefGenFactScanner.Scan`）——
    /// 它要**把全部 XML 再读一遍**，与 `ElapsedMs` 的差就是 15 条生成规则的求值时间。
    /// </summary>
    public long FactScanElapsedMs { get; init; }
    public long ElapsedMs { get; init; }

    // ── 规则表（每条的 `Evidence` 都必须在 IL 里找到，见类注释）────────────────────
    private sealed record BaseReq(string DefType, string Predicate);

    private sealed record Rule(
        string Id, string GeneratorType, string Method, string ProducedDefType,
        string[] NameShapes, string[] Evidence, BaseReq A, BaseReq? B);

    private static readonly Rule[] Table =
    {
        // 基底谓词 `child:<元素名>` = 该 def 有同名一级子元素；`any` = 只要求基底该 DefType 存在。
        new("Corpses", "RimWorld.ThingDefGenerator_Corpses", "GenerateCorpseDef", "ThingDef",
            new[] { "Corpse_{0}" }, new[] { "lit:Corpse_" }, new("ThingDef", "child:race"), null),
        new("UnnaturalCorpses", "RimWorld.ThingDefGenerator_Corpses", "GenerateUnnaturalCorpseDef", "ThingDef",
            new[] { "UnnaturalCorpse_{0}" }, new[] { "lit:UnnaturalCorpse_" }, new("ThingDef", "child:race"), null),
        new("Meat", "RimWorld.ThingDefGenerator_Meat", "GenerateMeatDef", "ThingDef",
            new[] { "Meat_{0}" }, new[] { "lit:Meat_" }, new("ThingDef", "child:race"), null),
        new("Neurotrainer", "RimWorld.ThingDefGenerator_Neurotrainer", "ImpliedThingDefs", "ThingDef",
            new[] { "Neurotrainer_{0}" }, new[] { "lit:_", "field:NeurotrainerDefPrefix" }, new("SkillDef", "any"), null),
        new("Psytrainer", "RimWorld.ThingDefGenerator_Neurotrainer", "ImpliedThingDefs", "ThingDef",
            new[] { "Psytrainer_{0}" }, new[] { "lit:_", "field:PsytrainerDefPrefix" }, new("AbilityDef", "any"), null),
        // ★ 谓词是 **IL 实证**的 `ResearchProjectDef.techprintCount > 0`（不是某个 XML 子元素）：
        //   `<ImpliedTechprintDefs>d__1.MoveNext` 里 `call get_TechprintCount` → `ldc.i4.0` → `ble.s`（跳过）。
        //   改前写的是 `child:techprint` —— 那个字段**根本不存在**（`api member` 实测：只有 techprintCount），
        //   于是该规则恒匹配 0 个基底 ⇒ `Techprint_X` 引用会被假报悬空（一类潜在假阳）。
        new("Techprints", "RimWorld.ThingDefGenerator_Techprints", "ImpliedTechprintDefs", "ThingDef",
            new[] { "Techprint_{0}" }, new[] { "lit:Techprint_", "call:ResearchProjectDef.get_TechprintCount" },
            new("ResearchProjectDef", "intGt:techprintCount:0"), null),
        new("Blueprint", "RimWorld.ThingDefGenerator_Buildings", "NewBlueprintDef_Thing", "ThingDef",
            new[] { "Blueprint_{0}" }, new[] { "field:BlueprintDefNamePrefix" },
            new("ThingDef", "child:designationCategory"), null),
        new("InstallBlueprint", "RimWorld.ThingDefGenerator_Buildings", "NewBlueprintDef_Thing", "ThingDef",
            new[] { "Blueprint_Install_{0}" }, new[] { "field:BlueprintDefNamePrefix", "field:InstallBlueprintDefNamePrefix" },
            new("ThingDef", "child:designationCategory"), null),
        new("Frame", "RimWorld.ThingDefGenerator_Buildings", "NewFrameDef_Thing", "ThingDef",
            new[] { "Frame_{0}" }, new[] { "field:BuildingFrameDefNamePrefix" },
            new("ThingDef", "child:designationCategory"), null),
        new("Stone", "RimWorld.TerrainDefGenerator_Stone", "ImpliedTerrainDefs", "TerrainDef",
            new[] { "{0}_Rough", "{0}_RoughHewn", "{0}_Smooth" },
            new[] { "lit:_Rough", "lit:_RoughHewn", "lit:_Smooth" }, new("ThingDef", "naturalRock"), null),
        new("Carpet", "RimWorld.TerrainDefGenerator_Carpet", "CarpetFromBlueprint", "TerrainDef",
            new[] { "{0}{1}" }, new[] { "lit:Structure_" },
            new("TerrainTemplateDef", "any"), new("ColorDef", "colorType:Structure")),
        new("Recipe", "RimWorld.RecipeDefGenerator", "CreateRecipeDefFromMaker", "RecipeDef",
            new[] { "Make_{0}" }, new[] { "lit:Make_", "call:RecipeDefGenerator.CreateRecipeDefFromMaker" },
            new("ThingDef", "child:recipeMaker"), null),
        // ★ Bulk 变体（IL：`ldstr "Bulk"` + `String.Concat`，门是 `recipeMaker.bulkRecipeCount > 0`）。
        //   不建模它，则第三方内容里的 `Make_XBulk` 引用会被假报悬空（官方内容的 Make_*Bulk 全是显式声明，
        //   所以官方门禁看不见这个洞）。
        new("RecipeBulk", "RimWorld.RecipeDefGenerator", "CreateRecipeDefFromMaker", "RecipeDef",
            new[] { "Make_{0}Bulk" }, new[] { "lit:Bulk", "field:bulkRecipeCount" },
            new("ThingDef", "intGt:recipeMaker.bulkRecipeCount:0"), null),
        new("Gene", "RimWorld.GeneDefGenerator", "ImpliedGeneDefs", "GeneDef",
            new[] { "{0}_{1}" }, new[] { "lit:_" }, new("GeneTemplateDef", "any"), new("", "geneSource")),
    };

    internal static DefGeneratorModel Build(ManagedAssemblyCatalog catalog, CheckContext ctx,
        InheritanceLayer.Analysis analysis, IReadOnlyDictionary<string, List<string>> declaredNames)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var names = new List<(string, string)>();
        var reports = new List<GeneratedRuleReport>();
        var failures = 0;

        // ① IL 实测「生成器清单」：不硬编码，读 `RimWorld.DefGenerator` 两个入口的调用目标。
        var discovered = DiscoverGenerators(catalog);

        // ② 事实表（XML 一级/二级子元素与少量标量）—— 谓词需要它。
        //   ★ Task 8 Step 1：单独计时 —— 这一步会**把本次扫描集合的每个 XML 再读一遍**。
        var swFacts = System.Diagnostics.Stopwatch.StartNew();
        var facts = DefGenFactScanner.Scan(ctx, catalog, analysis);
        swFacts.Stop();

        // ③ 逐条规则：先验 IL 证据，再算名字。
        foreach (var rule in Table)
        {
            var genType = catalog.Find(rule.GeneratorType);
            if (genType is null)
            {
                failures++;
                reports.Add(new GeneratedRuleReport(rule.Id, rule.GeneratorType, rule.Method, rule.ProducedDefType,
                    string.Join("|", rule.NameShapes), Describe(rule.A, rule.B), false,
                    "生成器类型在本程序集里找不到", 0, 0, 0));
                continue;
            }

            var (literals, fieldRefs, callRefs) = IlEvidence(genType);
            var missing = rule.Evidence.Where(e =>
                e.StartsWith("lit:", StringComparison.Ordinal) ? !literals.Contains(e[4..])
                : e.StartsWith("call:", StringComparison.Ordinal) ? !callRefs.Contains(e[5..])
                : !fieldRefs.Contains(e["field:".Length..])).ToList();
            var ok = missing.Count == 0;
            if (!ok) failures++;

            var producedKey = TypeKey(catalog, rule.ProducedDefType);
            var aNames = BaseNames(declaredNames, catalog, rule.A, facts, out var aCount);
            var bNames = rule.B is null
                ? new List<string>()
                : BaseNames(declaredNames, catalog, rule.B, facts, out _);
            // ★ Gene 的第二基底是「SkillDef ∪ ChemicalDef，按模板类型二选一」，单独算。
            if (rule.Id == "Gene")
                bNames = GeneSources(declaredNames, catalog);

            var generated = 0;
            if (ok)
            {
                foreach (var a in aNames)
                {
                    // ★ 单基底规则（B == null）用 [""] 占位（格式串只有 {0}）；
                    //   双基底规则在第二基底为空时**不生成任何名字**（用 a+"" 会造出垃圾名）。
                    var bs = rule.Id == "Gene"
                        ? GeneSourcesFor(facts, a, declaredNames, catalog)
                        : rule.B is null ? new List<string> { "" } : bNames;
                    foreach (var b in bs)
                        foreach (var shape in rule.NameShapes)
                        {
                            var nm = string.Format(shape, a, rule.Id == "Carpet" ? b.Replace("Structure_", "") : b);
                            names.Add((producedKey, nm));
                            generated++;
                        }
                }
            }

            reports.Add(new GeneratedRuleReport(rule.Id, rule.GeneratorType, rule.Method, rule.ProducedDefType,
                string.Join("|", rule.NameShapes), Describe(rule.A, rule.B), ok,
                string.Join(" ", rule.Evidence) + (ok ? "" : "  缺失: " + string.Join(",", missing)),
                aCount, bNames.Count, generated));
        }

        // ④ 未建模的生成器入口（响亮地报出来，绝不静默）
        var modeled = new HashSet<string>(Table.Select(r => r.GeneratorType + "::" + r.Method), StringComparer.Ordinal);
        var unmodeled = discovered.Where(d => !modeled.Contains(d)).ToList();

        sw.Stop();
        return new DefGeneratorModel
        {
            Names = names,
            Rules = reports,
            UnmodeledGenerators = unmodeled,
            DiscoveredGenerators = discovered,
            EvidenceFailures = failures,
            FactScanElapsedMs = swFacts.ElapsedMilliseconds,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    /// <summary>Gene 生成的第二基底 = `DefDatabase&lt;SkillDef&gt; ∪ DefDatabase&lt;ChemicalDef&gt;`。</summary>
    private static List<string> GeneSources(IReadOnlyDictionary<string, List<string>> declared,
        ManagedAssemblyCatalog catalog)
    {
        var res = new List<string>();
        foreach (var t in new[] { "SkillDef", "ChemicalDef" })
        {
            var k = TypeKey(catalog, t);
            if (declared.TryGetValue(k, out var l)) res.AddRange(l);
        }
        return res;
    }

    /// <summary>按模板的 `geneTemplateType` 选第二基底（Skill 模板配 SkillDef，Chemical 模板配 ChemicalDef）。</summary>
    private static List<string> GeneSourcesFor(DefGenFacts facts, string template,
        IReadOnlyDictionary<string, List<string>> declared, ManagedAssemblyCatalog catalog)
    {
        var t = facts.GeneKind(template) == "Chemical" ? "ChemicalDef" : "SkillDef";
        var k = TypeKey(catalog, t);
        return declared.TryGetValue(k, out var l) ? l : new List<string>();
    }

    private static string Describe(BaseReq a, BaseReq? b) =>
        $"{a.DefType}({a.Predicate})" + (b is null ? "" : $" × {b.DefType}({b.Predicate})");

    private static string TypeKey(ManagedAssemblyCatalog catalog, string elementName) =>
        catalog.ResolveXmlTypeName(elementName)?.Name ?? elementName;

    private static List<string> BaseNames(IReadOnlyDictionary<string, List<string>> declared,
        ManagedAssemblyCatalog catalog, BaseReq req, DefGenFacts facts, out int matched)
    {
        var key = TypeKey(catalog, req.DefType);
        var all = declared.TryGetValue(key, out var l) ? l : new List<string>();
        var res = new List<string>();
        foreach (var n in all)
        {
            if (req.Predicate == "any")
            {
                // Gene 的第二基底由第一基底的类型决定，这里只做「该名字是不是通行的第二基底」粗筛。
                if (req.Predicate == "any" && req.DefType.Length == 0) { res.Add(n); continue; }
            }
            switch (req.Predicate)
            {
                case "any": res.Add(n); break;
                case "geneSource": res.Add(n); break;
                case "naturalRock":
                    if (facts.IsNaturalRock(key, n)) res.Add(n);
                    break;
                default:
                    if (req.Predicate.StartsWith("child:", StringComparison.Ordinal))
                    {
                        if (facts.HasChild(key, n, req.Predicate[6..])) res.Add(n);
                    }
                    else if (req.Predicate.StartsWith("colorType:", StringComparison.Ordinal))
                    {
                        if (facts.Scalar(key, n, "colorType") == req.Predicate[10..]) res.Add(n);
                    }
                    else if (req.Predicate.StartsWith("intGt:", StringComparison.Ordinal))
                    {
                        // `intGt:<标量名>:<下界>` —— IL 实证的**数值条件**谓词（如 techprintCount > 0）。
                        var parts = req.Predicate.Split(':');
                        var raw = facts.Scalar(key, n, parts[1]);
                        if (raw is not null && int.TryParse(raw, out var v) && v > int.Parse(parts[2])) res.Add(n);
                    }
                    break;
            }
        }
        matched = res.Count;
        return res;
    }

    /// <summary>
    /// IL 实测：读 `RimWorld.DefGenerator.GenerateImpliedDefs_PreResolve / _PostResolve` 的
    /// 调用目标（`call <生成器>::Implied*`）—— 这就是「引擎会跑哪些生成器」的权威清单。
    /// </summary>
    private static List<string> DiscoverGenerators(ManagedAssemblyCatalog catalog)
    {
        var res = new List<string>();
        // ★ Task 9：候选回退（1.6 = Verse.DefGenerator，1.7 = RimWorld.DefGenerator）。
        var t = TypeNameResolver.ResolveType(catalog, KnownTypeNames.DefGenerator);
        if (t is null) return res;
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (!m.Name.StartsWith("GenerateImpliedDefs", StringComparison.Ordinal)) continue;
            foreach (var called in CalledMethods(m))
                if (called.Name.StartsWith("Implied", StringComparison.Ordinal) && called.DeclaringType is { } d)
                {
                    var entry = $"{d.FullName}::{called.Name}";
                    if (!res.Contains(entry, StringComparer.Ordinal)) res.Add(entry);
                }
        }
        res.Sort(StringComparer.Ordinal);
        return res;
    }

    private static IEnumerable<MethodBase> CalledMethods(MethodBase m)
    {
        var res = new List<MethodBase>();
        var module = m.Module;
        var il = m.GetMethodBody()?.GetILAsByteArray();
        if (il is null) return res;
        for (var i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] is not (0x28 or 0x6F or 0xFE)) continue;
            try
            {
                var tok = BitConverter.ToInt32(il, i + 1);
                if (module.ResolveMember(tok) is MethodBase mb) res.Add(mb);
            }
            catch { /* 非成员 token */ }
        }
        return res;
    }

    /// <summary>
    /// 该生成器（含其嵌套状态机）IL 里的全部字符串字面量 + 引用到的字段名 + 调用的方法名。
    /// ★ `calls` 用来给**谓词本身**钉证据：例如 Techprints 的谓词是 `techprintCount &gt; 0`（不是某个
    ///   XML 子元素），它的依据就是 IL 里那句 `call ResearchProjectDef.get_TechprintCount`。
    /// </summary>
    private static (HashSet<string> Literals, HashSet<string> FieldRefs, HashSet<string> CallRefs) IlEvidence(Type genType)
    {
        var lits = new HashSet<string>(StringComparer.Ordinal);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        var calls = new HashSet<string>(StringComparer.Ordinal);
        var types = new List<Type> { genType };
        types.AddRange(genType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
        foreach (var t in types)
        {
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                           BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var module = m.Module;
                var il = m.GetMethodBody()?.GetILAsByteArray();
                if (il is null) continue;
                for (var i = 0; i + 4 < il.Length; i++)
                {
                    if (il[i] == 0x72)
                    {
                        try { lits.Add(module.ResolveString(BitConverter.ToInt32(il, i + 1))); } catch { }
                    }
                    else if (il[i] is 0x7B or 0x7C or 0x7D or 0x7E or 0x80)   // ldfld/ldflda/stfld/ldsfld/stsfld
                    {
                        try { var fi = module.ResolveField(BitConverter.ToInt32(il, i + 1)); if (fi is not null) fields.Add(fi.Name); } catch { }
                    }
                    else if (il[i] is 0x28 or 0x6F)   // call / callvirt
                    {
                        try
                        {
                            var mi = module.ResolveMethod(BitConverter.ToInt32(il, i + 1));
                            if (mi?.DeclaringType is { } dt) calls.Add(dt.Name + "." + mi.Name);
                        }
                        catch { }
                    }
                }
            }
        }
        return (lits, fields, calls);
    }
}

// ── 第 3 期 Task 6b：事实表 + 扫描器 ──────────────────────────────────────────
/// <summary>
/// def 的**一级/二级子元素事实**（生成规则谓词用）。
/// ★ 继承：引擎在继承之后才建 def，所以谓词必须按**解析后的内容**求值；
///   <see cref="DefGenFactScanner"/> 会把祖先的同一份事实并进来（否则「子 def 从父类继承
///   `&lt;race&gt;`」会被漏掉，那会让 `Corpse_&lt;子&gt;` 变成假阳）。
/// </summary>
public sealed class DefGenFacts
{
    private sealed class Decl
    {
        public HashSet<string> Children { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Scalar { get; } = new(StringComparer.Ordinal);
        public bool NaturalRock;
        public string GeneKind = "Skill";
    }

    private readonly Dictionary<string, Decl> _byKey = new(StringComparer.Ordinal);

    private static string K(string type, string name) => type + "\u0000" + name;

    private Decl Get(string type, string name)
    {
        var k = K(type, name);
        if (!_byKey.TryGetValue(k, out var d)) _byKey[k] = d = new Decl();
        return d;
    }

    internal void AddChild(string type, string name, string child) => Get(type, name).Children.Add(child);

    internal void SetScalar(string type, string name, string key, string value) =>
        Get(type, name).Scalar[key] = value;

    internal void SetGeneKind(string type, string name, string kind) => Get(type, name).GeneKind = kind;

    internal void SetNaturalRock(string type, string name, bool natural, bool resource) =>
        Get(type, name).NaturalRock = natural && !resource;

    /// <summary>把 (srcType, srcName) 的事实并进 (dstType, dstName)（继承链用）。</summary>
    internal void MergeInto(string dstType, string dstName, DefGenFacts source, string srcType, string srcName)
    {
        if (!source._byKey.TryGetValue(K(srcType, srcName), out var src)) return;
        var dst = Get(dstType, dstName);
        foreach (var c in src.Children) dst.Children.Add(c);
        foreach (var kv in src.Scalar) dst.Scalar[kv.Key] = kv.Value;
        if (src.NaturalRock) dst.NaturalRock = true;
        if (src.GeneKind != "Skill") dst.GeneKind = src.GeneKind;
    }

    public bool HasChild(string type, string name, string child) =>
        _byKey.TryGetValue(K(type, name), out var d) && d.Children.Contains(child);

    public string? Scalar(string type, string name, string key) =>
        _byKey.TryGetValue(K(type, name), out var d) && d.Scalar.TryGetValue(key, out var v) ? v : null;

    public bool IsNaturalRock(string type, string name) =>
        _byKey.TryGetValue(K(type, name), out var d) && d.NaturalRock;

    public string GeneKind(string defName)
    {
        foreach (var kv in _byKey)
        {
            var i = kv.Key.IndexOf('\u0000');
            if (i < 0) continue;
            if (kv.Key[(i + 1)..] == defName) return kv.Value.GeneKind;
        }
        return "Skill";
    }
}

/// <summary>
/// 把每个 def 的一级/二级子元素读成 <see cref="DefGenFacts"/>（一次额外流式扫描）。
/// 归属键与 <see cref="ReferenceCheck"/> 同源：**按文件内 depth==1 的出现顺序**对应
/// `ctx.Defs`（同一物理行多个 def 也不会串味 —— 与 Task 5c 的教训一致）。
/// </summary>
internal static class DefGenFactScanner
{
    internal static DefGenFacts Scan(CheckContext ctx, ManagedAssemblyCatalog catalog, InheritanceLayer.Analysis analysis)
    {
        var raw = new DefGenFacts();
        foreach (var group in ctx.Defs.GroupBy(d => d.FileAbs, StringComparer.OrdinalIgnoreCase))
        {
            var defsInFile = group as IList<RawDef> ?? group.ToList();
            if (defsInFile.Count == 0) continue;
            var defIdx = 0;
            var text = new System.Text.StringBuilder();
            var natural = false;
            var resource = false;
            try
            {
                using var stream = File.OpenRead(group.Key);
                using var reader = XmlReader.Create(stream, new XmlReaderSettings
                {
                    IgnoreComments = true,
                    IgnoreWhitespace = true,
                    DtdProcessing = DtdProcessing.Prohibit
                });
                var stack = new List<string>(8);
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        var depth = reader.Depth;
                        if (depth == 0) continue;
                        if (depth == 1)
                        {
                            if (defIdx >= defsInFile.Count) break;
                            var d = defsInFile[defIdx++];
                            stack.Clear();
                            stack.Add(d.DefType);
                            natural = false;
                            resource = false;
                            continue;
                        }
                        if (defIdx == 0) continue;
                        // stack[d-1] = 深度 d 的元素名（stack[0] = 深度 1 的 def 元素名）
                        while (stack.Count > depth) stack.RemoveAt(stack.Count - 1);
                        while (stack.Count < depth) stack.Add(string.Empty);
                        stack.Add(reader.Name);
                        if (depth <= 4) text.Clear();
                        continue;
                    }

                    if (reader.NodeType == XmlNodeType.EndElement)
                    {
                        var depth = reader.Depth;
                        if (depth < 2 || defIdx == 0) continue;
                        var d = defsInFile[defIdx - 1];
                        var typeKey = catalog.ResolveXmlTypeName(d.ResolvedTypeName)?.Name ?? d.ResolvedTypeName;
                        var name = d.ReferableName;
                        if (string.IsNullOrEmpty(name)) continue;
                        var txt = text.ToString().Trim();
                        if (depth == 2)
                        {
                            raw.AddChild(typeKey, name, reader.Name);
                            if (txt.Length > 0)
                            {
                                raw.SetScalar(typeKey, name, reader.Name, txt);
                                if (reader.Name == "geneTemplateType") raw.SetGeneKind(typeKey, name, txt);
                            }
                        }
                        else if (depth == 3 && stack.Count > 2 && stack[2] == "building")
                        {
                            if (reader.Name == "isNaturalRock") natural = txt == "true";
                            else if (reader.Name == "isResourceRock") resource = txt == "true";
                            raw.SetNaturalRock(typeKey, name, natural, resource);
                        }
                        else if (depth == 3 && stack.Count > 2 && stack[2] == "recipeMaker" && txt.Length > 0)
                        {
                            // RecipeDefGenerator 的 Bulk 变体门是 `recipeMaker.bulkRecipeCount > 0`
                            //（IL：`ldfld RecipeMakerProperties.bulkRecipeCount` + `ldc.i4.0` + `ble.s`）。
                            raw.SetScalar(typeKey, name, "recipeMaker." + reader.Name, txt);
                        }
                        continue;
                    }

                    if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
                        if (reader.Depth - 1 <= 4) text.Append(reader.Value);
                }
            }
            catch { /* 解析失败由 RD2001 报；本扫描只做尽力而为的事实收集 */ }
        }

        // ★ 继承：把祖先链的事实并进本声明（引擎在继承之后才建 def —— 谓词必须按解析后的内容求值）。
        //   ★ 为什么自己走 ParentName 而不用 InheritanceLayer.Walk：本类的键是「名字」，
        //     而 Name 空间优先 + defName 兜底正是 Task 5a 定下的解析口径；这里只需**祖先的 XML 事实**，
        //     不需要链的解析依据，所以就地做一次带环保护的窄遍历即可（不复制第二套合并语义）。
        var byName = new Dictionary<string, RawDef>(StringComparer.Ordinal);
        foreach (var d in ctx.Defs)
        {
            foreach (var nm in new[] { d.NameAttr, d.DefName })
                if (!string.IsNullOrEmpty(nm)) byName.TryAdd(nm!, d);
        }
        var effective = new DefGenFacts();
        foreach (var d in ctx.Defs)
        {
            var typeKey = catalog.ResolveXmlTypeName(d.ResolvedTypeName)?.Name ?? d.ResolvedTypeName;
            var name = d.ReferableName;
            if (string.IsNullOrEmpty(name)) continue;
            effective.MergeInto(typeKey, name, raw, typeKey, name);
            var visited = new HashSet<string>(StringComparer.Ordinal) { name! };
            var cur = d;
            for (var hop = 0; hop < 64; hop++)
            {
                if (cur.ParentName is not { Length: > 0 } pn || !visited.Add(pn)) break;
                if (!byName.TryGetValue(pn, out var parent)) break;
                var pn2 = parent.ReferableName;
                if (string.IsNullOrEmpty(pn2)) break;
                var pk = catalog.ResolveXmlTypeName(parent.ResolvedTypeName)?.Name ?? parent.ResolvedTypeName;
                effective.MergeInto(typeKey, name!, raw, pk, pn2!);
                cur = parent;
            }
        }        return effective;
    }
}
