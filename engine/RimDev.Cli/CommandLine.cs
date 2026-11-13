using RimDev.Cli.Commands;
using RimDev.Core.Environment;

namespace RimDev.Cli;

/// <summary>
/// ★★ 第 3 期 Task 9b：**可直调的 CLI 入口**。
///
/// 存在的两个理由：
/// <list type="number">
/// <item><b>可测</b>：自测工程直接 <c>CommandLine.Run(args, stdout, stderr)</c> 断言退出码与输出，
///   不必起子进程（子进程会引入「路径/引号/cwd/超时」这类与被测行为无关的失败模式）。
///   判别性用例要能断言「**没有产出扫描结果**」，所以输出必须可捕获 —— 这里用 TextWriter 包住
///   <see cref="Console"/> 的重定向（命令体一律写 Console.Out/Error，没有第二套写出口）。</item>
/// <item><b>一处分发 + 一处解析</b>：全局开关解析（<see cref="GlobalOptions"/>）与环境构造
///   （<see cref="EnvironmentResolution"/>）都只在这里发生一次，命令只接收解析结果。</item>
/// </list>
///
/// ★ 可重入：自测里会连续调用很多次。每次进入时保存 <see cref="Console"/> 的写出口与
///   <see cref="GameEnvProbe.Resolved"/>（进程内单一来源），退出时**原样还原** ——
///   否则前一条用例的 <c>--game-root</c> 会污染后一条用例的环境探测。
/// </summary>
public static class CommandLine
{
    /// <summary>参数错（与 <c>01</c> §4 一致）。</summary>
    public const int ExitArgs = 3;

    /// <summary>用法一行（--help 与未知命令共用）。</summary>
    public const string Usage =
        "usage: rimdev <env|api|name|index|check|files|order|sim> [options]（`rimdev check --help` / `rimdev sim --help` 看 scan scope 语义）";

    /// <summary>
    /// 唯一入口：解析全局开关 → 构造成唯一的环境解析结果 → 分发。
    /// </summary>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        var prevResolved = GameEnvProbe.Resolved;

        var swapOut = !ReferenceEquals(stdout, prevOut);
        var swapErr = !ReferenceEquals(stderr, prevErr);
        if (swapOut) Console.SetOut(stdout);
        if (swapErr) Console.SetError(stderr);

        try
        {
            return RunCore(args);
        }
        finally
        {
            // ★ 还原进程内单一来源：不然一条用例指定的 --game-root 会泄漏给后续用例。
            GameEnvProbe.SetResolved(prevResolved);
            if (swapOut) Console.SetOut(prevOut);
            if (swapErr) Console.SetError(prevErr);
        }
    }

    private static int RunCore(string[] argv)
    {
        // ★★ 全局开关**只在这里解析一次**（放命令前/后都行）。解析失败 ⇒ 参数错（退出码 3）。
        var options = GlobalOptions.Parse(argv);
        if (options.HasError)
        {
            Console.Error.WriteLine(options.Error);
            Console.Error.WriteLine(Usage);
            return ExitArgs;
        }

        if (options.Args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return ExitArgs;
        }

        var ctx = new EnvironmentResolution(options);
        var command = options.Args[0];

        // ★ 第 3 期 Task 9：**产物 ↔ 布局**一致性闸门。
        //   net8 产物服务 Classic16（1.6，net472 托管程序集），net10 产物服务 Waylith17（1.7.x 线，net10 托管程序集）。
        //   跨用时不报错、只会**答错**：net8 反射载入 net10 的 Assembly-CSharp 得到的是空类型表，
        //   于是 `api type ThingDef` 会答「类型不存在」—— 类型当然存在，是这份产物读不了它的元数据。
        //   ⇒ 需要程序集元数据的命令在不匹配时**拒答**并退出 4（环境不满足），文案说明该用哪份产物。
        //   不需要元数据的命令（env / files / index / order）照常跑：它们只用文件系统与 XML。
        //   ★ 9b：这个闸门也必须用**同一份**全局选项（旧实现自己 ArgValue(argv, "--game-root") 扫一遍）。
        var needsAssembly = command is "api" or "name" or "check" or "sim";
        if (needsAssembly && TryMismatchMessage(ctx, out var mismatch))
        {
            Console.Error.WriteLine(mismatch);
            return EnvironmentResolution.EnvExitCode;
        }

        return command switch
        {
            "env" => EnvCommand.Run(ctx),
            "api" => ApiCommand.Run(ctx),
            "index" => IndexCommand.Run(ctx),
            "files" => FilesCommand.Run(ctx),
            "check" => CheckCommand.Run(ctx),
            // ★ 第 3 期 Task 3：mod 加载顺序 + 每个 mod 的文件夹优先级
            "order" => OrderCommand.Run(ctx),
            "name" => NameCommand.Run(ctx),
            // ★ 第 3 期 Task 6：加载结果摘要（有效 def / 遮蔽 / 悬空引用 / MayRequire / loader 边界）
            "sim" => SimCommand.Run(ctx),
            "--version" => PrintVersion(),
            _ => Unknown(command)
        };
    }

    /// <summary>
    /// 探测当前安装的布局，返回「产物/布局不匹配」的可读说明；自洽（或探测不出布局）时返回 false。
    /// ★ 探测不出布局时**不插手**：让命令自己按既有路径报 RD1001（否则会把「没装游戏」说成「产物不匹配」）。
    /// ★ 9b：`--game-root` 指向无效目录时这里也走 catch 分支（返回 false），RD1001 由命令层统一报。
    /// </summary>
    private static bool TryMismatchMessage(EnvironmentResolution ctx, out string message)
    {
        message = string.Empty;
        try
        {
            var env = GameEnvProbe.Discover(
                explicitRoot: ctx.Options.GameRoot,
                explicitWorkshopRoot: ctx.Options.WorkshopRoot);
            if (!RimDevProducts.IsRuntimeCompatibleWith(env.Layout))
            {
                message = RimDevProducts.MismatchMessage(env.Layout) ?? string.Empty;
                return message.Length > 0;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static int PrintVersion()
    {
        Console.WriteLine("rimdev 0.1.0");
        return 0;
    }

    /// <summary>
    /// 未知命令 / 未知开关。
    /// ★ 9b：文案必须点明「全局开关可放在命令前或命令后」——
    ///   否则 `rimdev --game-root X env` 报「未知命令: --game-root」时，用户只会以为是位置写错了。
    /// </summary>
    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"未知命令: {cmd}");
        Console.Error.WriteLine("全局开关（--game-root / --workshop-root / --profile）**可以放在命令前，也可以放在命令后**：");
        Console.Error.WriteLine("  rimdev --game-root <游戏根> check --scope active");
        Console.Error.WriteLine("  rimdev check --scope active --game-root <游戏根>        （两者等价）");
        Console.Error.WriteLine(Usage);
        return ExitArgs;
    }
}
