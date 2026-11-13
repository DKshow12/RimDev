using System.Text;
using RimDev.Core.Environment;
using RimDev.SelfTest.Cases;

namespace RimDev.SelfTest;

/// <summary>
/// 用例对**布局能力**的要求。
///
/// · <see cref="Any"/>：布局无关（夹具 XML、loadFolders 逻辑、补丁操作、解析器往返…）
///   ⇒ 任何布局都必须跑、必须绿。
/// · <see cref="Classic16"/>：绑定 1.6 语料 / 1.6 托管程序集
///   （断言 `Managed dll = 108`、`GetTypes() = 16157`、`defs`/诊断计数、`activeMods = 17`、
///   官方 6 包、需要 `UnityEngine.CoreModule.dll` 的引擎对照台…）
///   ⇒ 当前布局不是 Classic16 时**显式 SKIP 并逐条打印用例名**，绝不静默跳过、也不放宽断言。
/// </summary>
public enum LayoutNeed
{
    Any = 0,
    Classic16 = 1,
}

/// <summary>
/// 极简断言台。设计约束：本机 nuget.org 不可达、无 xunit / MSTest 可用，
/// 因此测试以"注册用例 + 抛异常即失败"的方式手写。
///
/// ★ 第 3 期 Task 9 新增三件事：
///   ① **按布局分级**（<see cref="LayoutNeed"/>）+ **显式 SKIP**（记名字、计数 `skipped-by-layout`）；
///   ② **身份快照**：把「本次实际加载的 Assembly-CSharp 身份 + 跑在哪个 .NET 运行时 + 布局」打进摘要。
///      1.7.x 小版本会继续改元数据；没有这一行，锚点漂移会表现成「某个用例莫名红了」而无法归因。
///   ③ `--progress`：逐用例打印开始行（排障用；结论只在最后打印，卡住时否则无从判断）。
/// </summary>
public static class T
{
    // ★ Group 与 Name 分开存：用例名里可能含 " / "（如「supportedVersions / loadAfter 解析」），
    //   早期版本把两者拼成 "group/name" 再用 Split('/')[1] 取显示名，会把这类用例名截断。
    // ★ Ok 三态：true=通过 / false=失败 / null=SKIP（按布局跳过）。
    private sealed record Result(string Group, string Name, bool? Ok, string? Message, long Ms);

    private static readonly List<Result> Results = new();
    private static string _group = "default";

    /// <summary>文件级默认要求（<see cref="Scope"/> 设置；单个用例可用 <see cref="Case(string,Action,LayoutNeed)"/> 覆盖）。</summary>
    private static LayoutNeed _scope = LayoutNeed.Any;

    // ── 环境身份（Program 启动时探测一次） ─────────────────────────────────────
    public static RimWorldLayout Layout { get; private set; } = RimWorldLayout.Unknown;
    public static string IdentityLine { get; private set; } = "(未探测)";
    public static string? IdentityError { get; private set; }
    public static string Runtime { get; private set; } = ManagedAssemblyIdentity.RuntimeDescription;

    /// <summary>逐用例进度（`--progress`）：排障时能看出「卡在哪一条」——结论在最后才打印。</summary>
    public static bool Progress { get; set; }

    /// <summary>
    /// 细粒度进度标记（只在 `--progress` 下打印）：用于定位「卡在某个 RunFile 的**初始化**里」的挂起 ——
    /// 那种挂起不会打印任何 `&gt;&gt; group / case`，只看用例级进度会把整段初始化当成一条用例。
    /// 只影响排障输出，不参与任何断言。
    /// </summary>
    public static void Mark(string message)
    {
        if (Progress) Console.WriteLine($"  .. {message}");
    }

    /// <summary>探测一次布局与身份；**任何失败都降级成 Unknown + 记录原因**，不抛（否则整个自测跑不起来）。</summary>
    public static void Init(string? startDir = null)
    {
        try
        {
            var env = GameEnvProbe.Discover(startDir: startDir ?? Directory.GetCurrentDirectory());
            Layout = env.Layout;
            var id = env.Identity();
            Runtime = id.Runtime;
            IdentityLine = id.ToOneLine();
            IdentityError = env.Layout == RimWorldLayout.Classic16
                ? null
                : $"当前布局是 {env.Layout.ToName()}，不是 Classic16 ⇒ 绑定 1.6 语料的用例将被显式跳过";
        }
        catch (Exception ex)
        {
            Layout = RimWorldLayout.Unknown;
            IdentityLine = $"(环境探测失败：{ex.GetType().Name}: {ex.Message})";
            IdentityError = ex.Message;
        }
    }

    /// <summary>当前布局是否满足该要求。Classic16 只在布局**确实**是 Classic16 时成立（Unknown 也不算）。</summary>
    public static bool Allows(LayoutNeed need) =>
        need == LayoutNeed.Any || Layout == RimWorldLayout.Classic16;

    /// <summary>文件级默认要求（同一文件里布局无关的用例用 <see cref="Case(string,Action,LayoutNeed)"/> 覆盖）。</summary>
    public static IDisposable Scope(LayoutNeed need) => new ScopeGuard(need);

    private sealed class ScopeGuard : IDisposable
    {
        private readonly LayoutNeed _prev;
        public ScopeGuard(LayoutNeed need) { _prev = _scope; _scope = need; }
        public void Dispose() => _scope = _prev;
    }

    public static void Group(string name) => _group = name;

    public static void Case(string name, Action body) => Case(name, body, _scope);

    public static void Case(string name, Action body, LayoutNeed need)
    {
        // ★ 绑定 1.6 语料的用例由**清单**声明（LayoutBoundInventory 一处维护，
        //   避免 31 处散落的参数遗漏）；命中清单 ⇒ 要求提升为 Classic16。
        var bound = LayoutBoundInventory.TryGet(group: _group, name: name, out var why);
        if (bound)
        {
            LayoutBoundInventory.MarkMatched(_group, name);
            if (need == LayoutNeed.Any) need = LayoutNeed.Classic16;
        }

        if (!Allows(need))
        {
            // ★ 显式 SKIP：名字照样进结果集（所以「总数 266」不变），但不执行用例体。
            Results.Add(new Result(_group, name, null,
                $"需 {need} 布局（实测 {Layout.ToName()}）" + (bound ? "；" + why : ""), 0));
            return;
        }

        if (Progress) Console.WriteLine($"  >> {_group} / {name}");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            body();
            Results.Add(new Result(_group, name, true, null, sw.ElapsedMilliseconds));
        }
        catch (Exception ex)
        {
            Results.Add(new Result(_group, name, false, ex.Message, sw.ElapsedMilliseconds));
        }
    }

    /// <summary>
    /// 跑一个用例文件的注册+执行；文件级初始化异常（比如顶层 Discover/Load 抛了）
    /// 记成一条**失败**用例而不是让整个自测崩掉 —— 否则会得到「0 个用例、exit 1」这种无法归因的结果。
    /// </summary>
    public static void RunFile(string label, Action body)
    {
        Mark($"file: {label}");
        try
        {
            body();
        }
        catch (Exception ex)
        {
            Results.Add(new Result(label, "用例文件初始化异常", false,
                $"{ex.GetType().Name}: {ex.Message}", 0));
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

    /// <summary>打印摘要前的自检：绑定清单必须全部命中已注册用例（防「改名后静默不跳过」）。</summary>
    private static void EmitInventoryGuard()
    {
        foreach (var miss in LayoutBoundInventory.Unmatched())
            Results.Add(new Result("布局分级清单", "清单条目未命中已注册用例: " + miss, false,
                "LayoutBoundInventory 的名字必须与已注册用例逐字一致", 0));
    }

    /// <summary>打印摘要，返回进程退出码（0=全过/仅有按布局的 SKIP，1=有失败）。</summary>
    public static int Report()
    {
        EmitInventoryGuard();

        var sb = new StringBuilder();
        int passed = 0, failed = 0, skipped = 0;

        sb.AppendLine("================ 身份快照（本次实际加载的对象）================");
        sb.AppendLine($"layout      = {Layout.ToName()}");
        sb.AppendLine($"runtime     = {Runtime}");
        sb.AppendLine($"assembly    = {IdentityLine}");
        if (IdentityError is not null)
            sb.AppendLine($"注意        = {IdentityError}");
        sb.AppendLine("（这一行是 1.7.x 小版本之间做归因的唯一依据：MVID/版本一变，锚点就可能漂）");
        sb.AppendLine();

        string? lastGroup = null;
        foreach (var r in Results)
        {
            if (r.Group != lastGroup)
            {
                sb.AppendLine($"--- {r.Group} ---");
                lastGroup = r.Group;
            }

            if (r.Ok == true)
            {
                passed++;
                sb.AppendLine($"  PASS {r.Name} ({r.Ms} ms)");
            }
            else if (r.Ok == null)
            {
                skipped++;
                sb.AppendLine($"  SKIP {r.Name}");
                sb.AppendLine($"       （{r.Message}）");
            }
            else
            {
                failed++;
                sb.AppendLine($"  FAIL {r.Name} ({r.Ms} ms)");
                sb.AppendLine($"       {r.Message}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"合计 {Results.Count} 个用例：{passed} 通过 / {failed} 失败 / {skipped} 跳过（需 Classic16 布局）");
        // ★ 稳定可 grep 的机器可读行（build-all.ps1 与门禁都靠它）。
        sb.AppendLine($"skipped-by-layout = {skipped}");
        if (skipped > 0)
        {
            sb.AppendLine("被跳过的用例（需 Classic16 布局的 1.6 语料/托管程序集）：");
            foreach (var r in Results.Where(x => x.Ok == null))
                sb.AppendLine($"  - {r.Group}/{r.Name}");
            sb.AppendLine($"（跳过原因：当前布局 {Layout.ToName()} 上这些断言针对的是 1.6 安装的具体值；");
            sb.AppendLine("  装上 1.6 或 --game-root 指向 1.6 后重跑，它们必须原样全部回绿。）");
        }

        Console.WriteLine(sb.ToString());

        if (failed > 0)
        {
            Console.WriteLine("有失败用例。失败清单：");
            foreach (var r in Results.Where(x => x.Ok == false))
                Console.WriteLine($"  - {r.Group}/{r.Name}: {r.Message}");
        }

        return failed > 0 ? 1 : 0;
    }
}
