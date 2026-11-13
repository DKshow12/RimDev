using RimDev.Core.Diagnostics;

namespace RimDev.Core.Checks;

public sealed class CheckRunner
{
    private readonly List<ICheck> _checks = new();
    public CheckRunner Add(ICheck check) { _checks.Add(check); return this; }

    /// <summary>★ 排障开关：`RIMDEV_CHECK_TRACE=1` 时逐条规则打开始/耗时到 stderr（默认关闭，不产生任何输出）。</summary>

    /// <summary>
    /// ★ Task 17：慢规则阈值（ms）。超过就向 stderr 打一条耗时。
    /// <para>为什么是 3 秒：本工具常规操作都在毫秒到几百毫秒级，
    /// 超 3 秒就不是「慢一点」而是「不正常」，值得告诉用户。</para>
    /// </summary>
    public const int SlowRuleMs = 3000;

    /// <summary>
    /// Task 17：做「可能较慢」预告的 **def 数门槛**。
    /// <para>实测：官方 6 包 14,084 def ⇒ RD2009 约 0.7 s；629 mod 100,236 def ⇒ 13.7 min。
    /// 两者差 7 倍 def，却差 1,170 倍耗时（超线性：DOM 变大后单次 XPath 也变慢）。</para>
    /// <para>50,000 作分界：下方（官方/小集合）不告知，上方才告知。</para>
    /// </summary>
    public const int SlowRuleDefThreshold = 50_000;

    /// <summary>
    /// ★ Task 17：已知会较慢的规则（开始前先告知用户）。
    /// <para>实测依据（本机 629 mod / 16,607 def 文件 / 3,364 补丁文件 / 24,838 操作）：
    /// RD2009 要在 100,236 节点的 DOM 上求值 2.4 万条 XPath ⇒ 13.7 分钟。</para>
    /// <para>这不是 bug，是该规则的固有成本（Docs/10 §2.1 记过同一笔账）；
    /// 本集合只用于「提前告知」，不改任何计算。</para>
    /// </summary>
    public static readonly IReadOnlySet<string> SlowRules = new HashSet<string>(StringComparer.Ordinal)
    {
        "RD2009",   // 补丁层（PatchCheck）
        "RD3001",   // 引用解析（ReferenceCheck）—— 实测 29 s
    };
    private static readonly bool _trace =
        System.Environment.GetEnvironmentVariable("RIMDEV_CHECK_TRACE") == "1";


    /// <summary>
    /// ★ Task 19：**只跑选中的规则**（黑/白名单）。
    ///
    /// <para>为什么不能用 <see cref="Run"/> 的 <c>filter</c>：那个参数是**结果过滤** ——
    /// 诊断仍然全部算完了才剔除，耗时一分不少。而实测（本机 629 mod / 1.7.5005）
    /// RD2009 单条就占全部 14.3 分钟里的 13.7 分钟 —— 想只看引用问题却要陪跑补丁层。</para>
    ///
    /// <para>本方法在**执行前**就把不选的规则从清单里去掉。</para>
    ///
    /// <para>★ 语义：两个集合都空 ⇒ 全跑（保持原行为）。
    /// <c>only</c> 非空 ⇒ 只跑它里面的；<c>skip</c> 非空 ⇒ 跑除它之外的；
    /// 两者同时给 ⇒ 先取交集再剔除（最保守）。两条都**大小写不敏感**。</para>
    ///
    /// <returns>实际被保留的规则 id（按注册顺序）。</returns>
    /// </summary>
    public IReadOnlyList<string> SelectRules(
        IReadOnlyCollection<string>? only, IReadOnlyCollection<string>? skip)
    {
        if ((only is null || only.Count == 0) && (skip is null || skip.Count == 0))
            return _checks.Select(c => c.Id).ToList();

        var onlySet = only is null || only.Count == 0
            ? null
            : new HashSet<string>(only.Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase);
        var skipSet = skip is null || skip.Count == 0
            ? null
            : new HashSet<string>(skip.Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase);

        var keep = _checks
            .Where(c => onlySet is null || onlySet.Contains(c.Id))
            .Where(c => skipSet is null || !skipSet.Contains(c.Id))
            .ToList();

        _checks.Clear();
        _checks.AddRange(keep);
        return keep.Select(c => c.Id).ToList();
    }
    /// <summary>
    /// 已注册的规则（只读）。
    /// ★ 存在意义：`check --json` 要把 <see cref="PatchCheck"/> **自己算出来的**补丁层统计报出去
    ///   （求值了几个 def/补丁文件、几条操作、补了几条 Name），否则「RD2009 = 0」无法与
    ///   「补丁层没跑」区分。CLI 就地重算一遍会形成第二套口径 —— 这里让调用方直接读规则的产物。
    /// </summary>
    public IReadOnlyList<ICheck> Checks => _checks;

    /// <summary>★ Task 8 Step 1：上一次 <see cref="Run"/> 里**每条规则**的墙钟（ms，按注册顺序）。
    ///   「引用解析之外的时间」必须也能归因到具体规则 —— 只报一个总数等于没有结论。</summary>
    public IReadOnlyList<(string Id, long Ms)> LastCheckMs { get; private set; } = Array.Empty<(string, long)>();

    public static CheckRunner Default()
    {
        return new CheckRunner()
            // ★ 第 3 期 Task 1：loadFolders 规则族（RD2012/RD2018–RD2024）。放最前，因为「哪些目录/文件会被加载」
            //   是其它所有规则的前提（加载目录算错，后面的 def 级诊断也就跟着错）。
            .Add(new LoadFolderCheck())
            .Add(new XmlWellFormedCheck())
            .Add(new CommentDashCheck())
            .Add(new DuplicateDefNameCheck())
            .Add(new ParentChainCheck())
            // ★ 第 3 期 Task 3：遮蔽（RD2025/RD2026）与 loadAfter/loadBefore 顺序一致性（RD2011）。
            //   放在 ParentChainCheck 之后：它不依赖继承链，但同一份索引上跑完更省一次遍历（ctx 是共享的）。
            //   级别：RD2026=error / RD2025=info / RD2011=info（详见 ShadowingCheck 类注释）。
            .Add(new ShadowingCheck())
            .Add(new FieldNameCheck())
            // ★ 第 3 期 Task 4：补丁层（RD2009「补丁打空了」/ RD2014 自定义补丁类跳过）。
            //   放最后：它要自己建一遍「合并后的统一 XML」DOM（引擎时序：补丁在继承解析之前），
            //   与前面几条只吃 RawDef 头的规则没有共享计算，放哪里都不影响结果。
            //   求值范围 = 扫描集合 ∩ 启用集合（理由见 PatchCheck 类注释）。
            .Add(new PatchCheck())
            // ★ 第 3 期 Task 6：引用解析（RD3001）。放最后 —— 它要自己流式读一遍全部 def 文件
            //   （在**同一次**读取里同时产出 def 归属与引用值），并建一次有效 def 集合
            //   （SimulatedDefSet，来源是 Task 5b 的 InheritanceLayer）。
            .Add(new ReferenceCheck())
            // ★★ 第 3 期 Task 26：About.xml 三个附加特性的消费侧
            //   （实测 302 / 107 / 2 个 mod 在用；长期被忽略）。
            .Add(new AboutFeatureCheck());
    }

    public IReadOnlyList<Diagnostic> Run(CheckContext ctx, Func<Diagnostic, bool>? filter = null)
    {
        var all = new List<Diagnostic>();
        var timings = new List<(string Id, long Ms)>(_checks.Count);
        foreach (var c in _checks)
        {
            // ★ Task 17：只对【已知慢规则】做预告。
            //   为什么不是每条都告：其余规则都在毫秒级，逐条打反而是噪音。
            //   为什么要告：实测（本机 629 mod）RD2009 单条要 13.7 分钟，
            //   用户看到的是「命令没反应」⇒ Ctrl+C 以为挂了。
            //   只写 stderr：保住 --json 的 stdout 逐字节稳定。
            //   只在**规模足够大**时告知：官方 6 包跑 RD2009 只要几百毫秒，
            //   对它说「请等待」是误导（用户会以为真要等很久）。
            //   判据用 def 数（实测：官方 14,084 ⇒ 约 0.7 s；629 mod 100,236 ⇒ 13.7 min）。
            if (!_trace && SlowRules.Contains(c.Id) && ctx.Defs.Count >= SlowRuleDefThreshold)
                Console.Error.WriteLine($"[check] {c.Id} 可能较慢（{ctx.Defs.Count} 个 def），请等待…");
            var t = System.Diagnostics.Stopwatch.StartNew();
            // ★ 排障用（默认关闭）：`RIMDEV_CHECK_TRACE=1` 时逐条规则打开始/耗时到 stderr。
            //   存在理由：某条规则**挂起**时，`run` 不返回、也没有任何输出，只看诊断结果无从归因。
            var trace = _trace;
            if (trace) Console.Error.WriteLine($"[check] {c.Id} start");
            try { all.AddRange(c.Run(ctx)); }
            catch (Exception ex)
            {
                all.Add(new Diagnostic("RD9999", Severity.Error, Scope.Offline,
                    $"规则 {c.Id} 自身抛异常：{ex.GetType().Name}: {ex.Message}"));
            }
            t.Stop();
            if (trace) Console.Error.WriteLine($"[check] {c.Id} done {t.ElapsedMilliseconds} ms");
            // ★ Task 17：慢规则事后补一条耗时（超阈值才打，避免刷屏）。
            if (!_trace && t.ElapsedMilliseconds >= SlowRuleMs)
                Console.Error.WriteLine($"[check] {c.Id} 耗时 {t.ElapsedMilliseconds / 1000.0:0.0} s（超 {SlowRuleMs / 1000.0:0} s 阈值）");
            timings.Add((c.Id, t.ElapsedMilliseconds));
        }
        LastCheckMs = timings;
        if (filter is not null) all = all.Where(filter).ToList();

        // ★ 按稳定指纹去重：同一处问题只报一条，附出现次数
        // （第 1 期实测：一次启动会加载两遍 Def，同一错误重复出现）
        //   注意指纹里含 FileAbs/Line —— 不含位置会把【不同文件】的同一条 XML 错误合并成一条。
        return all
            .GroupBy(d => d.Fingerprint, StringComparer.Ordinal)
            .Select(g =>
            {
                var first = g.First();
                if (g.Count() == 1) return first;
                return first with
                {
                    Evidence = (first.Evidence ?? new Dictionary<string, string>())
                        .Concat(new[] { new KeyValuePair<string, string>("occurrences", g.Count().ToString()) })
                        .ToDictionary(kv => kv.Key, kv => kv.Value)
                };
            })
            .OrderByDescending(d => d.Severity)
            .ToList();
    }
}
