using RimDev.Core.Diagnostics;

namespace RimDev.Core.Checks;

public sealed class CheckRunner
{
    private readonly List<ICheck> _checks = new();
    public CheckRunner Add(ICheck check) { _checks.Add(check); return this; }

    /// <summary>
    /// 已注册的规则（只读）。
    /// ★ 存在意义：`check --json` 要把 <see cref="PatchCheck"/> **自己算出来的**补丁层统计报出去
    ///   （求值了几个 def/补丁文件、几条操作、补了几条 Name），否则「RD2009 = 0」无法与
    ///   「补丁层没跑」区分。CLI 就地重算一遍会形成第二套口径 —— 这里让调用方直接读规则的产物。
    /// </summary>
    public IReadOnlyList<ICheck> Checks => _checks;

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
            .Add(new PatchCheck());
    }

    public IReadOnlyList<Diagnostic> Run(CheckContext ctx, Func<Diagnostic, bool>? filter = null)
    {
        var all = new List<Diagnostic>();
        foreach (var c in _checks)
        {
            try { all.AddRange(c.Run(ctx)); }
            catch (Exception ex)
            {
                all.Add(new Diagnostic("RD9999", Severity.Error, Scope.Offline,
                    $"规则 {c.Id} 自身抛异常：{ex.GetType().Name}: {ex.Message}"));
            }
        }
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
