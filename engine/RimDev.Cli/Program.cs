using RimDev.Cli.Commands;
using RimDev.Core.Environment;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: rimdev <env|api|name|index|check|files|order|sim> [options]（`rimdev check --help` / `rimdev sim --help` 看 scan scope 语义）");
    return 3;
}

// ★ 第 3 期 Task 9：**产物 ↔ 布局**一致性闸门。
//   net8 产物服务 Classic16（1.6，net472 托管程序集），net10 产物服务 Waylith17（1.7.x 线，net10 托管程序集）。
//   跨用时不报错、只会**答错**：net8 反射载入 net10 的 Assembly-CSharp 得到的是空类型表，
//   于是 `api type ThingDef` 会答「类型不存在」—— 类型当然存在，是这份产物读不了它的元数据。
//   ⇒ 需要程序集元数据的命令在不匹配时**拒答**并退出 4（环境不满足），文案说明该用哪份产物。
//   不需要元数据的命令（env / files / index / order）照常跑：它们只用文件系统与 XML。
var needsAssembly = args[0] is "api" or "name" or "check" or "sim";
if (needsAssembly && TryMismatchMessage(args, out var mismatch))
{
    Console.Error.WriteLine(mismatch);
    return 4;
}

return args[0] switch
{
    "env" => EnvCommand.Run(args),
    "api" => ApiCommand.Run(args),
    "index" => IndexCommand.Run(args),
    "files" => FilesCommand.Run(args),
    "check" => CheckCommand.Run(args),
    // ★ 第 3 期 Task 3：mod 加载顺序 + 每个 mod 的文件夹优先级
    "order" => OrderCommand.Run(args),
    "name" => NameCommand.Run(args),
    // ★ 第 3 期 Task 6：加载结果摘要（有效 def / 遮蔽 / 悬空引用 / MayRequire / loader 边界）
    "sim" => SimCommand.Run(args),
    "--version" => PrintVersion(),
    _ => Unknown(args[0])
};

/// <summary>
/// 探测当前安装的布局，返回「产物/布局不匹配」的可读说明；自洽（或探测不出布局）时返回 false。
/// ★ 探测不出布局时**不插手**：让命令自己按既有路径报 RD1001（否则会把「没装游戏」说成「产物不匹配」）。
/// </summary>
static bool TryMismatchMessage(string[] argv, out string message)
{
    message = string.Empty;
    try
    {
        var explicitRoot = ArgValue(argv, "--game-root");
        var env = GameEnvProbe.Discover(startDir: Directory.GetCurrentDirectory(), explicitRoot: explicitRoot);
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

/// <summary>取 `--flag value` 或 `--flag=value` 的值（找不到返回 null）。</summary>
static string? ArgValue(string[] argv, string flag)
{
    for (var i = 1; i < argv.Length; i++)
    {
        if (argv[i] == flag && i + 1 < argv.Length) return argv[i + 1];
        if (argv[i].StartsWith(flag + "=", StringComparison.Ordinal))
            return argv[i][(flag.Length + 1)..];
    }
    return null;
}

static int PrintVersion()
{
    Console.WriteLine("rimdev 0.1.0");
    return 0;
}

static int Unknown(string cmd)
{
    Console.Error.WriteLine($"未知命令: {cmd}");
    Console.Error.WriteLine("usage: rimdev <env|api|name|index|check|files|order|sim> [options]（`rimdev check --help` / `rimdev sim --help` 看 scan scope 语义）");
    return 3;
}
