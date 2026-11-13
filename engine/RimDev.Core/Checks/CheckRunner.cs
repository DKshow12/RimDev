using RimDev.Core.Diagnostics;

namespace RimDev.Core.Checks;

public sealed class CheckRunner
{
    private readonly List<ICheck> _checks = new();

    public CheckRunner Add(ICheck check) { _checks.Add(check); return this; }

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
            .Add(new FieldNameCheck());
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
