using System.Diagnostics.CodeAnalysis;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;

namespace RimDev.Cli;

/// <summary>
/// ★★ 第 3 期 Task 9b：**环境解析的唯一来源**。
///
/// 每个命令都从它取环境（<see cref="TryGetEnv"/>），**不允许**再自己 <c>GameEnvProbe.Discover()</c> 现场探测 ——
/// 那正是「<c>--game-root</c> / <c>--workshop-root</c> 被静默忽略」的入口（各命令各探一次，谁也不读开关）。
///
/// 三层职责，一层一处：
/// <list type="number">
/// <item><see cref="GlobalOptions"/> —— 参数层：把全局开关从 argv 里摘出来（放命令前/后都行）。</item>
/// <item><b>本类</b> —— 解析层：把选项变成唯一的 <see cref="GameEnv"/>；失败即 RD1001 + 退出码 4。</item>
/// <item><see cref="GameEnvProbe.Resolved"/> —— 进程内单一来源：Core 内部历史遗留的
///   <c>Discover()</c> 也拿到同一份，避免「命令层对了、检查器层又探一遍」的二次分叉。</item>
/// </list>
///
/// ★ 为什么解析是**惰性**的：<c>check --help</c> / <c>order --help</c> 在探测环境**之前**就该正常退出 0。
///   若在分发前强制解析，一次「没有游戏安装」就会把帮助也打成退出码 4。
/// </summary>
public sealed class EnvironmentResolution
{
    /// <summary>环境不满足的统一退出码（<c>01</c> §4：0 成功 / 2 有 error / 3 参数错 / 4 环境不满足 / 5 目标 mod 找不到）。</summary>
    public const int EnvExitCode = 4;

    private GameEnv? _env;
    private string? _failure;

    public EnvironmentResolution(GlobalOptions options) => Options = options;

    public GlobalOptions Options { get; }

    /// <summary>摘掉全局开关后的参数（<c>Args[0]</c> = 子命令）。</summary>
    public string[] Args => Options.Args;

    /// <summary>已经解析过的环境（未解析时 null；仅供诊断输出用，不要用它代替 <see cref="TryGetEnv"/>）。</summary>
    public GameEnv? Resolved => _env;

    /// <summary>
    /// 解析（并缓存）唯一的环境。失败时**已经把 <c>RD1001: …</c> 写到 stderr**，调用方只需
    /// <c>return EnvironmentResolution.EnvExitCode</c> —— 退出码与文案各只有一处定义（否则必然分叉）。
    /// </summary>
    public bool TryGetEnv([NotNullWhen(true)] out GameEnv? env)
    {
        if (_env is not null)
        {
            env = _env;
            return true;
        }

        if (_failure is not null)
        {
            // 已经报过一次：不再重复刷屏（同一命令里可能有多处取环境）。
            env = null;
            return false;
        }

        try
        {
            var resolved = GameEnvProbe.Discover(
                explicitRoot: Options.GameRoot,
                // ★ 传 null 而不是 cwd：显式根 / 环境变量 / 向上找 / 注册表 / 默认路径的既有优先级不变。
                startDir: null,
                explicitWorkshopRoot: Options.WorkshopRoot);

            GameEnvProbe.SetResolved(resolved);
            _env = resolved;
            env = resolved;
            return true;
        }
        catch (GameEnvProbeException ex)
        {
            _failure = ex.Message;
            Console.Error.WriteLine(Codes.GameRootNotFound + ": " + ex.Message);
            env = null;
            return false;
        }
    }

    /// <summary>
    /// `env` 的「这个值从哪来」回显（<c>--game-root</c> / 环境变量 / auto）—— 单一来源就在 <see cref="Options"/>。
    /// </summary>
    public string GameRootSource =>
        Options.GameRoot is not null ? GlobalOptions.GameRootFlag
        : !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("RIMWORLD_DIR")) ? "RIMWORLD_DIR"
        : "auto";

    /// <inheritdoc cref="GameRootSource"/>
    public string WorkshopRootSource =>
        Options.WorkshopRoot is not null ? GlobalOptions.WorkshopRootFlag
        : !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("RIMWORLD_WORKSHOP_DIR")) ? "RIMWORLD_WORKSHOP_DIR"
        : "auto";
}
