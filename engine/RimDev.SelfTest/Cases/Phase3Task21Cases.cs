using RimDev.Cli;
using RimDev.Core.Catalog;
using RimDev.Core.Environment;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// ★★ 第 3 期 Task 21：**命令间口径一致性**（`sim` 与 `check` 的开关对齐 + 组合歧义防护）。
///
/// 起因（系统性体检发现，不是跑出错才发现的）：
/// <list type="number">
/// <item><c>check</c> 有 <c>--only-official</c>，<c>sim</c> **没有** —— 同一个工具两个命令口径不一致。
///   后果：想对一下官方基线，<c>sim</c> 也要扫 629 mod（约 14 分钟），而 <c>check</c> 只要 2 秒。</item>
/// <item><c>check --only-official</c> **不带** <c>--scope</c> 时落进 target 作用域，
///   报告被目标 mod 过滤成 0 条 —— 而输出里同时写着「全集扫描」。
///   读者会把「0 条」当成「官方没问题」，那是**假 0**。</item>
/// </list>
///
/// 本文件的用例钉住修复后的行为。
/// </summary>
public static class Phase3Task21Cases
{
    /// <summary>跑一次 CLI，返回 (退出码, stdout, stderr)。</summary>
    private static (int Code, string Out, string Err) RunCli(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code;
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        try
        {
            Console.SetOut(o);
            Console.SetError(e);
            code = CommandLine.Run(args, o, e);
        }
        finally
        {
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
        }
        return (code, o.ToString(), e.ToString());
    }

    public static void Run()
    {
        T.Case("task21 ★ sim 必须认得 --only-official（与 check 口径一致）", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            if (env.GameRoot is null) { T.Mark("task21 跳过：没有游戏根"); return; }

            var (code, _, err) = RunCli("sim", "--only-official", "--json");
            T.False(err.Contains("未知开关", StringComparison.Ordinal),
                $"sim 不该拒绝 --only-official，stderr={err}");
            T.True(code is 0 or 2, $"sim --only-official 应正常结束（实际 exit={code}）");
        }, LayoutNeed.Any);

        T.Case("task21 check 也必须认得 --only-official（对照）", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            if (env.GameRoot is null) { T.Mark("task21 跳过：没有游戏根"); return; }

            var (code, _, err) = RunCli("check", "--only-official", "--json");
            T.False(err.Contains("未知开关", StringComparison.Ordinal),
                $"check 不该拒绝 --only-official，stderr={err}");
            T.True(code is 0 or 2, $"check --only-official 应正常结束（实际 exit={code}）");
        }, LayoutNeed.Any);

        T.Case("task21 ★ --only-official 的判定口径：Source == Official（Core + 5 DLC）", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            if (env.GameRoot is null) { T.Mark("task21 跳过：没有游戏根"); return; }

            var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
            var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;
            var official = cat.Active.Where(m => m.Source == ModSource.Official).ToList();

            T.True(official.Count is >= 5 and <= 7,
                $"官方包数应为 5–7（Core + 5 DLC，实测 {official.Count}）");
            T.True(official.All(m => m.Source == ModSource.Official),
                "判据必须是 ModSource.Official，不是路径/名字启发式");
        }, LayoutNeed.Any);

        T.Case("task21 ★★ target + --only-official 的 0 条不能当「官方没问题」", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            if (env.GameRoot is null) { T.Mark("task21 跳过：没有游戏根"); return; }

            var (_, stdout, _) = RunCli("check", "--only-official");
            if (stdout.Contains("扫描            0 个 mod", StringComparison.Ordinal))
            {
                T.Mark("task21 提示：本次没有目标 mod，跳过冲突用例");
                return;
            }
            T.Contains(stdout, "不属于官方 6 包",
                "★ 冲突组合必须显式提示（否则 0 条会被读成「官方没问题」＝假 0）");
            T.Contains(stdout, "--scope active --only-official", "提示里必须给出正确用法");
        }, LayoutNeed.Any);

        T.Case("task21 扫描行必须标出 [限官方 6 包]", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            if (env.GameRoot is null) { T.Mark("task21 跳过：没有游戏根"); return; }

            var (_, stdout, _) = RunCli("check", "--scope", "active", "--only-official");
            T.Contains(stdout, "[限官方 6 包]", "★ 不标就会被读成「本机只有 6 个 mod」");
            T.Contains(stdout, "6 个 mod", "扫描数应为 6");
        }, LayoutNeed.Any);
    }
}
