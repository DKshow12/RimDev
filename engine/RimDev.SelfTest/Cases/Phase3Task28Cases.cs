using RimDev.Cli;
using RimDev.Core.Catalog;
using RimDev.Core.Environment;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// ★★ 第 3 期 Task 28：<c>rimdev deps</c> —— 依赖关系查询。
///
/// <para>起因：对照 <c>RimDevTools 1.5.9.0</c>（另一套环世界 mod 开发工具）时发现的缺口。
/// 它有 <c>mod dependencies</c> / <c>mod dependents</c> 两个命令，
/// 而我们的依赖数据**一直在 <see cref="ModInfo"/> 里**（Task 26 刚补全
/// <c>DependenciesByVersion</c>），却**从未暴露给用户** ——
/// <c>order</c> 的输出里一个依赖字段都没有。</para>
///
/// <para>于是两个最常见的问题只能手工 grep：</para>
/// <list type="bullet">
/// <item>「我这个 mod 到底缺哪个前置」</item>
/// <item>「我改了 X 的 def，哪些 mod 会受影响」（= 安全改动的前提）</item>
/// </list>
///
/// <para>★ 与 RimDevTools 的差异（我们更强的地方）：</para>
/// <list type="number">
/// <item>不限定本地 mod —— 支持 <c>--scope all</c> 查未启用的工坊 mod。</item>
/// <item>依赖状态分四档：<b>已启用 / 已启用（经备选名）/ 未启用 / 缺失</b>。
///   RimDevTools 会笼统报「解析不到」，而「磁盘上有但没启用」与「根本没装」
///   修法完全不同 —— 混在一起会让人白找。</item>
/// <item>支持 <c>--reverse</c>（反向查询）。</item>
/// </list>
///
/// <para>★ 版本语义（本命令最关键的判据）：<c>modDependencies</c> 是所有版本通用的基线，
/// 任何版本都该带上；<c>modDependenciesByVersion</c> 的不同块是**互斥**的
/// （v1.6 的依赖不该出现在 v1.7 里）。实测 <c>Torann.ARimworldOfMagic</c>：
/// 不带 <c>--version</c> 出 18 条（6 个块全列），<c>--version v1.1</c> 出 3 条，
/// <c>--version v1.6</c> 出 3 条且内容不同（<c>jecrell.jecstools</c> 换成了
/// <c>zal.jecsliterwom</c>）。</para>
/// </summary>
public static class Phase3Task28Cases
{
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
        T.Case("task28 ★★ deps 命令必须能被分发（Sets 白名单漏登记 = 静默变「未知命令」）", () =>
        {
            // 这条守的是本轮实测撞到的坑：
            //   命令分派里写了 "deps" => ...，但 CommandFlags.Sets 没登记 ⇒
            //   走到 CommandLines.For() 拿不到白名单 ⇒ 直接报「未知命令: deps」。
            //   CommandFlags 的注释里明确写了「新增命令若忘登记，自测会当场变红」——
            //   本用例就是那条防线。
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            if (env.GameRoot is null) { T.Mark("task28 跳过：没有游戏根"); return; }

            var (_, _, err) = RunCli("deps", "--mod", "harmony");
            T.False(err.Contains("未知命令", StringComparison.Ordinal),
                $"★ deps 不该是「未知命令」—— CommandFlags.Sets 里必须登记 DepsCommand.Flags。stderr={err}");
        }, LayoutNeed.Any);

        T.Case("task28 ★ 正向：harmony 自己没有依赖（依赖数 0）", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            if (env.GameRoot is null) { T.Mark("task28 跳过：没有游戏根"); return; }

            var (code, stdout, _) = RunCli("deps", "--mod", "brrainz.harmony");
            T.Eq(code, 0, "应正常退出");
            T.Contains(stdout, "依赖数", "应有依赖数行");
            T.Contains(stdout, "0 个", "Harmony 是底层库，自己没有前置依赖");
        }, LayoutNeed.Any);

        T.Case("task28 ★★ 反向：谁依赖 harmony —— 数量必须 >100（实测 264）", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            if (env.GameRoot is null) { T.Mark("task28 跳过：没有游戏根"); return; }

            var (code, stdout, _) = RunCli("deps", "--mod", "brrainz.harmony", "--reverse");
            T.Eq(code, 0, "应正常退出");
            T.Contains(stdout, "依赖它的 mod", "应有反向计数行");

            // 从输出里抠出数量（"- 依赖它的 mod    264 个"）
            var m = System.Text.RegularExpressions.Regex.Match(stdout, @"依赖它的 mod\s+(\d+)");
            T.True(m.Success, "必须能解析出反向依赖数");
            if (m.Success)
            {
                var n = int.Parse(m.Groups[1].Value);
                T.True(n > 100, $"★ Harmony 实测被 264 个 mod 依赖，读到 {n} —— 太少说明反向查询有漏");
            }
        }, LayoutNeed.Any);

        T.Case("task28 ★★ 版本过滤真的生效（不是摆设）", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            if (env.GameRoot is null) { T.Mark("task28 跳过：没有游戏根"); return; }

            // Torann.ARimworldOfMagic 有 v1.1…v1.6 六个版本块（实测）
            const string target = "Torann.ARimworldOfMagic";

            var (_, allOut, _) = RunCli("deps", "--mod", target, "--scope", "all");
            var (_, v11Out, _) = RunCli("deps", "--mod", target, "--scope", "all", "--version", "v1.1");
            var (_, v16Out, _) = RunCli("deps", "--mod", target, "--scope", "all", "--version", "v1.6");

            int Count(string s)
            {
                var mm = System.Text.RegularExpressions.Regex.Match(s, @"依赖数\s+(\d+)");
                return mm.Success ? int.Parse(mm.Groups[1].Value) : -1;
            }

            var nAll = Count(allOut);
            var n11 = Count(v11Out);
            var n16 = Count(v16Out);

            T.True(nAll > 0, $"不带 --version 应列出全部版本块（读到 {nAll}）");
            T.True(n11 > 0, $"v1.1 块应有依赖（读到 {n11}）");
            T.True(n11 < nAll, $"★ 带 --version 必须少于全部（v1.1={n11} vs 全部={nAll}）—— 否则过滤没生效");
            T.True(n16 > 0, $"v1.6 块应有依赖（读到 {n16}）");

            // v1.1 与 v1.6 的内容不同 —— 这是「版本化依赖」的意义所在
            T.True(!string.Equals(v11Out, v16Out, StringComparison.Ordinal),
                "★ v1.1 与 v1.6 的依赖内容必须不同（实测 jecrell.jecstools 换成了 zal.jecsliterwom）");
        }, LayoutNeed.Any);

        T.Case("task28 ★★ 依赖状态分四档（已启用 / 备选名 / 未启用 / 缺失）", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            if (env.GameRoot is null) { T.Mark("task28 跳过：没有游戏根"); return; }

            // 实测 Torann.ARimworldOfMagic v1.1 块里 jecrell.jecstools 是「缺失」
            var (_, stdout, _) = RunCli("deps", "--mod", "Torann.ARimworldOfMagic", "--scope", "all", "--version", "v1.1");
            T.Contains(stdout, "缺失",
                "★ 该 mod 的 jecrell.jecstools 确实不在磁盘上，应报「缺失」而不是笼统的「解析不到」");
            T.Contains(stdout, "已启用",
                "同时应有已启用的依赖（harmony / HugsLib）");
        }, LayoutNeed.Any);

        T.Case("task28 没找到 mod 时退出码 2（不是 3，也不是 0）", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            if (env.GameRoot is null) { T.Mark("task28 跳过：没有游戏根"); return; }

            var (code, _, err) = RunCli("deps", "--mod", "definitely.no.such.mod.zzz");
            T.Eq(code, 2, "★ 2 = 没查到（不是参数错）；这是与「工具坏了」的区分");
            T.Contains(err, "--scope all", "提示里要给出扩大范围的办法");
        }, LayoutNeed.Any);

        T.Case("task28 缺 --mod 时退出码 3（参数错）", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            if (env.GameRoot is null) { T.Mark("task28 跳过：没有游戏根"); return; }

            var (code, _, err) = RunCli("deps");
            T.Eq(code, 3, "缺必需参数 = 3");
            T.Contains(err, "--mod", "错误信息要指出缺什么");
        }, LayoutNeed.Any);

        T.Case("task28 --json 输出可解析且字段完整", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            if (env.GameRoot is null) { T.Mark("task28 跳过：没有游戏根"); return; }

            var (code, stdout, _) = RunCli("deps", "--mod", "brrainz.harmony", "--reverse", "--json");
            T.Eq(code, 0, "应正常退出");
            T.Contains(stdout, "\"direction\"", "应有 direction 字段（区分正向/反向）");
            T.Contains(stdout, "\"dependents\"", "反向查询应有 dependents 数组");
            T.Contains(stdout, "\"count\"", "应有 count");
        }, LayoutNeed.Any);
    }
}
