using RimDev.Core.Defs;
using RimDev.Core.Metadata;

namespace RimDev.Core.Checks;

/// <summary>
/// 「ParentName 没解析到 → 给相近名字」的**加速版**名字池（第 3 期 Task 2b）。
///
/// ★ 背景（实测）：「全库 check」615 s 里有 **482 s（78%）** 花在 `ParentChainCheck.SuggestParent`：
///   436 条未解析的 ParentName，每条都把「全库名字池」（约 32 万个名字）逐个打分 ⇒ 约 1.4 亿次
///   `MemberInfoIndex.Similarity`；而那个实现每次调用都要 `ToLowerInvariant()` 两次（每次都在分配
///   字符串）并为 Levenshtein 分配两个 `int[]`。全是可避免的开销。
///
/// ★★ 硬约束：**输出必须逐条与旧实现相同**（同样的分数、同样的排序、同样的 tie-break、同样的顺序），
///   只许变快。为了做到这一点：
///   <list type="number">
///   <item>池子的**顺序**与旧实现逐字相同（`ByDefName.Keys` 再 `ByAbstractName.Keys`，
///     `Distinct()` 保留首次出现顺序）；任何预筛都必须**保持相对顺序**（只做过滤，不做重排）。</item>
///   <item>评分函数逐分支等价于 <see cref="MemberInfoIndex.Similarity"/>：全等 1000 / 仅大小写差异 900 /
///     前缀 500-长度差 / 编辑距离 `1 - d/maxLen`（≥0.6 才算）。</item>
///   <item>所有预筛（长度窗、首字符捷径、公共前后缀剥离、带状 Levenshtein 截断）只剪掉
///     **必然 score ≤ 0** 的候选 —— 判据写在每一处注释里。</item>
///   <item>同一个 ParentName 只算一次（<c>_memo</c>）：同一条未解析名字出现多次时结果本来就相同。</item>
///   </list>
///
/// 线程安全：**否**（复用 DP 缓冲）。check 是单线程跑规则，够用。
/// </summary>
public sealed class NameSuggestionPool
{
    /// <summary>池中名字（保持旧实现的顺序）。</summary>
    private readonly List<string> _names;
    /// <summary>与 <see cref="_names"/> 同下标的 <c>ToLowerInvariant()</c>（预计算，消掉 1.4 亿次分配）。</summary>
    private readonly List<string> _lower;
    private readonly Dictionary<string, List<string>> _memo = new(StringComparer.Ordinal);

    // 复用的 Levenshtein 行缓冲（按需增长），避免每次打分分配两个 int[]。
    private int[] _prev = new int[64];
    private int[] _cur = new int[64];

    /// <summary>池大小（报告/自测用）。</summary>
    public int PoolSize => _names.Count;
    /// <summary>被完整打分的候选数（预筛通过的数量，报告/自测用）。</summary>
    public long SimilarityCalls { get; private set; }
    /// <summary>被长度窗/首字符捷径剪掉的候选数（报告/自测用）。</summary>
    public long CandidatesSkipped { get; private set; }
    /// <summary>命中 memo 的名字数（报告/自测用）。</summary>
    public int MemoHits { get; private set; }
    /// <summary>未命中 memo、真正计算过的名字数（报告/自测用）。</summary>
    public int MemoMisses { get; private set; }

    public NameSuggestionPool(IEnumerable<string> orderedNames)
    {
        _names = new List<string>();
        _lower = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in orderedNames)
        {
            if (n is null || !seen.Add(n)) continue;   // 与旧实现的 Distinct() 等价（保序去重）
            _names.Add(n);
            _lower.Add(n.ToLowerInvariant());
        }
    }

    /// <summary>
    /// 按 <see cref="DefIndex"/> 建池，**顺序与旧实现的
    /// <c>ByDefName.Keys.Concat(ByAbstractName.Keys).Distinct()</c> 逐字相同**。
    /// </summary>
    public static NameSuggestionPool FromIndex(DefIndex index)
    {
        var names = new List<string>(index.ByDefName.Count + index.ByAbstractName.Count);
        names.AddRange(index.ByDefName.Keys);
        names.AddRange(index.ByAbstractName.Keys);
        return new NameSuggestionPool(names);
    }

    /// <summary>与 <c>ParentChainCheck.SuggestParent</c> 的旧语义完全一致：升序取前 <paramref name="max"/> 个。</summary>
    public List<string> Suggest(string name, int max = 5)
    {
        if (_memo.TryGetValue(name, out var cached))
        {
            MemoHits++;
            return cached;
        }
        MemoMisses++;
        var result = Compute(name, max);
        _memo[name] = result;
        return result;
    }

    private List<string> Compute(string name, int max)
    {
        var scored = new List<(string Name, double Score)>();
        var nl = name.ToLowerInvariant();
        var la = nl.Length;

        // 旧实现：a 或 b 为空 → Similarity 返回 0；a 为空时整个池都是 0。
        if (la > 0)
        {
            for (var i = 0; i < _names.Count; i++)
            {
                var candidate = _names[i];
                var cl = _lower[i];
                var lb = cl.Length;
                if (lb == 0) { CandidatesSkipped++; continue; }

                double score;
                if (cl[0] != nl[0])
                {
                    // ★ 首字符捷径：全等/仅大小写相等/前缀三种分支都要求 b[0] == a[0]
                    //   （前缀：b 以 a 开头 ⇒ 同首字符；a 以 b 开头 ⇒ 同首字符），
                    //   所以首字符不同的候选**只可能**靠编辑距离拿分。
                    score = LevenshteinScore(nl, cl, out var skipped);
                    if (skipped) CandidatesSkipped++;
                }
                else
                {
                    if (lb == la && string.Equals(candidate, name, StringComparison.Ordinal))
                    {
                        score = 1000;   // 与旧实现第一分支一致（同串最像）
                    }
                    else if (lb == la && string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                    {
                        score = 900;    // 仅大小写差异
                    }
                    else if (cl.StartsWith(nl, StringComparison.Ordinal)
                             || nl.StartsWith(cl, StringComparison.Ordinal))
                    {
                        // ★ 注意长度差用【原始】长度（与旧实现的 Math.Abs(a.Length - b.Length) 一致）
                        score = 500 - Math.Abs(candidate.Length - name.Length);
                    }
                    else
                    {
                        score = LevenshteinScore(nl, cl, out var skipped);
                        if (skipped) CandidatesSkipped++;
                    }
                }

                if (score > 0) scored.Add((candidate, score));
            }
        }

        // ★ 排序必须与旧实现逐字相同：**只有** score 降序（旧实现没有 ThenBy ——
        //   同分时靠 LINQ 稳定排序保持【池内原有顺序】。千万别拿 MemberInfoIndex.Suggest 的
        //   「再按长度升序」当成这里的口径：那是另一条路径，加上去会让同分建议的顺序变样）。
        return scored
            .OrderByDescending(x => x.Score)
            .Take(max)
            .Select(x => x.Name)
            .ToList();
    }

    /// <summary>
    /// 编辑距离分支：等价于旧实现的
    /// <c>var d = Levenshtein(al, bl); var sim = 1.0 - (double)d / Math.Max(al.Length, bl.Length);
    ///  return sim >= 0.6 ? sim * 100 : 0;</c>。
    /// </summary>
    /// <param name="skipped">true = 被长度窗或截断直接剪掉（没有做完整 DP），用于统计。</param>
    private double LevenshteinScore(string al, string bl, out bool skipped)
    {
        skipped = false;
        var la = al.Length;
        var lb = bl.Length;
        var maxLen = la > lb ? la : lb;

        // ★ 长度窗（必要条件，不是近似）：d >= |la-lb|，而 score>0 需要 d <= 0.4*maxLen。
        //   若 lb >= la：需要 lb-la <= 0.4*lb ⟺ 6*lb <= 10*la；
        //   若 lb <  la：需要 la-lb <= 0.4*la ⟺ 10*lb >= 6*la。
        //   两式合起来就是下面的两条整数比较（对整数与实数等价）。
        if (10L * lb < 6L * la || 6L * lb > 10L * la) { skipped = true; return 0; }

        // ★ 截断上界：cut = floor(0.4*maxLen) + 1。
        //   若真实 d > cut（即 d >= floor(0.4*maxLen)+2 > 0.4*maxLen），则 sim = 1 - d/maxLen < 0.6 → 必定 0。
        //   所以只要 DP 报「> cut」就可以安全返回 0；报 <= cut 时必须给出**精确**的 d（带状 DP 保证）。
        var cut = maxLen * 2 / 5 + 1;
        if (!TryLevenshtein(al, bl, cut, out var d)) { skipped = true; return 0; }

        SimilarityCalls++;
        var similarity = 1.0 - (double)d / maxLen;
        return similarity >= 0.6 ? similarity * 100 : 0;
    }

    /// <summary>
    /// 带状 Levenshtein：只保证「结果 ≤ <paramref name="cut"/> 时返回精确距离」，否则返回 false。
    /// 先剥离公共前后缀（不改变编辑距离的标准优化），再用带内 DP + 逐行提前退出。
    /// </summary>
    private bool TryLevenshtein(string a, string b, int cut, out int dist)
    {
        dist = 0;
        var la = a.Length;
        var lb = b.Length;

        var minLen = la < lb ? la : lb;
        var p = 0;
        while (p < minLen && a[p] == b[p]) p++;

        var s = 0;
        while (s < la - p && s < lb - p && a[la - 1 - s] == b[lb - 1 - s]) s++;

        var alen = la - p - s;
        var blen = lb - p - s;

        if (alen == 0) { dist = blen; return blen <= cut; }
        if (blen == 0) { dist = alen; return alen <= cut; }

        if (alen - blen > cut || blen - alen > cut) return false;

        EnsureBuffers(blen + 1);
        var prev = _prev;
        var cur = _cur;
        var inf = cut + 1;

        for (var j = 0; j <= blen; j++) prev[j] = j <= cut ? j : inf;

        for (var i = 1; i <= alen; i++)
        {
            var jlo = i - cut > 1 ? i - cut : 1;
            var jhi = i + cut < blen ? i + cut : blen;

            var rowMin = i <= cut ? i : inf;
            // 带外清成 inf：这两个数组是复用的，上一行的残留值会被下一次 prev/cur 交换带进来。
            for (var j = 0; j <= blen; j++) cur[j] = inf;
            cur[0] = i <= cut ? i : inf;

            for (var j = jlo; j <= jhi; j++)
            {
                var cost = a[p + i - 1] == b[p + j - 1] ? 0 : 1;
                var v = prev[j - 1] + cost;
                var del = prev[j] + 1;
                if (del < v) v = del;
                var ins = cur[j - 1] + 1;
                if (ins < v) v = ins;
                cur[j] = v;
                if (v < rowMin) rowMin = v;
            }

            // 整行都 > cut ⇒ 任何代价 ≤ cut 的路径都已不可能（标准截断），直接判「> cut」。
            if (rowMin > cut) return false;

            (prev, cur) = (cur, prev);
        }

        dist = prev[blen];
        return dist <= cut;
    }

    private void EnsureBuffers(int size)
    {
        if (_prev.Length >= size && _cur.Length >= size) return;
        var n = _prev.Length;
        while (n < size) n *= 2;
        _prev = new int[n];
        _cur = new int[n];
    }
}
