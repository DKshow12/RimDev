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

    /// <summary>版本查询（命令位置的伪命令；不需要环境、也不需要开关白名单）。</summary>
    public const string VersionFlag = "--version";

    /// <summary>用法一行（`--help` 与未知命令共用）。</summary>
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

        var command = options.Args[0];

        // ★ 全局帮助 / 版本：不需要命令、也不需要环境（没装游戏时也得能看帮助）。
        if (CommandFlagSet.IsHelpFlag(command))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        if (command == VersionFlag) return PrintVersion();

        // ★★ 第 3 期 Task 9c：**每条命令先拿出它自己的开关白名单**（声明在各命令文件里，
        //   `CommandFlags` 只做登记）。拿不到 = 不认识的命令 ⇒ 走未知命令分支（含「开关写在命令位置」）。
        var flags = CommandFlags.For(command);
        if (flags is null) return Unknown(command);

        // ★★ 9c：**未知开关响亮失败，且绝不继续执行命令**。
        //   缺陷背景：`check --scope active --game-rot C:\__typo__`（把 --game-root 拼错）修前照常扫描并输出
        //   「9 个 mod / 561 个 XML / 6727 个 def / error 399」，与完全不带开关时一模一样 —— 用户无法从输出里
        //   察觉自己扫的不是他指定的那份安装。现在：退出码 3 + stderr 点名该开关 + 提示正确的全局开关名与位置语义。
        if (CommandFlags.FindUnknown(flags, options.Args) is { } unknownFlag)
        {
            Console.Error.WriteLine(CommandFlags.UnknownFlagMessage(flags, unknownFlag));
            return ExitArgs;
        }

        // 帮助走同一条路：`--help` / `-h` / `--?` 由白名单放行，这里统一打印并返回 0
        // （每个命令自己提供帮助正文，所以 check/sim/order 的详细帮助与 env/api/… 的简版帮助都在一处生效）。
        if (options.Args.Skip(1).Any(CommandFlagSet.IsHelpFlag))
        {
            flags.Help();
            return 0;
        }

        var ctx = new EnvironmentResolution(options);

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
            // 不可达（上面已按白名单登记表拦下）：留作兜底，新增命令忘登记时这里会报未知命令而不是编译错。
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
        // ★ 9c：命令位置的 token 长得像开关时，多一句归因 —— 否则用户会以为是「命令名写错了」而不是
        //   「全局开关拼写错了」（能被 GlobalOptions 摘掉的全局开关根本走不到这里）。
        if (CommandFlagSet.LooksLikeFlag(cmd))
            Console.Error.WriteLine("  （它看起来是个开关：全局开关会在解析阶段被摘掉，走到这里说明它既不是命令、也不是全局开关）");
        Console.Error.WriteLine($"全局开关（{GlobalOptions.PublicFlagList}）**可以放在命令前，也可以放在命令后**：");
        Console.Error.WriteLine("  rimdev --game-root <游戏根> check --scope active");
        Console.Error.WriteLine("  rimdev check --scope active --game-root <游戏根>        （两者等价）");
        Console.Error.WriteLine(Usage);
        return ExitArgs;
    }
}
