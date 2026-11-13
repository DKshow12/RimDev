using System.Text;

namespace RimDev.SelfTest;

/// <summary>
/// 极简断言台。设计约束：本机 nuget.org 不可达、无 xunit / MSTest 可用，
/// 因此测试以"注册用例 + 抛异常即失败"的方式手写。
/// </summary>
public static class T
{
    private sealed record Result(string Name, bool Ok, string? Message, long Ms);

    private static readonly List<Result> Results = new();
    private static string _group = "default";

    public static void Group(string name) => _group = name;

    public static void Case(string name, Action body)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            body();
            Results.Add(new Result($"{_group}/{name}", true, null, sw.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            Results.Add(new Result($"{_group}/{name}", false, ex.Message, sw.ElapsedMilliseconds));
        }
    }

    public static void Eq<T>(T actual, T expected, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            throw new InvalidOperationException($"{what}: 期望 [{expected}]，实际 [{actual}]");
    }

    public static void True(bool value, string what)
    {
        if (!value) throw new InvalidOperationException($"{what}: 期望 true，实际 false");
    }

    public static void False(bool value, string what) => True(!value, what);

    public static void NotNull(object? value, string what)
    {
        if (value is null) throw new InvalidOperationException($"{what}: 期望非 null，实际 null");
    }

    public static void Null(object? value, string what)
    {
        if (value is not null) throw new InvalidOperationException($"{what}: 期望 null，实际 [{value}]");
    }

    public static void Contains(string haystack, string needle, string what)
    {
        if (haystack is null || !haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{what}: [{haystack ?? "null"}] 中未包含 [{needle}]");
    }

    /// <summary>打印汇总，返回进程退出码（0=全过，1=有失败）。</summary>
    public static int Report()
    {
        var sb = new StringBuilder();
        int passed = 0, failed = 0;

        string? lastGroup = null;
        foreach (var r in Results)
        {
            var group = r.Name.Split('/')[0];
            if (group != lastGroup)
            {
                sb.AppendLine($"--- {group} ---");
                lastGroup = group;
            }

            if (r.Ok)
            {
                passed++;
                sb.AppendLine($"  PASS {r.Name.Split('/')[1]} ({r.Ms} ms)");
            }
            else
            {
                failed++;
                sb.AppendLine($"  FAIL {r.Name.Split('/')[1]} ({r.Ms} ms)");
                sb.AppendLine($"       {r.Message}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"合计 {Results.Count} 个用例：{passed} 通过 / {failed} 失败");
        Console.WriteLine(sb.ToString());

        if (failed > 0)
        {
            Console.WriteLine("有失败用例。失败清单：");
            foreach (var r in Results.Where(x => !x.Ok))
                Console.WriteLine($"  - {r.Name}: {r.Message}");
        }

        return failed > 0 ? 1 : 0;
    }
}
