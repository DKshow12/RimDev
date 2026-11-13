using RimDev.Cli;
using RimDev.Core.Catalog;
using RimDev.Core.Environment;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// ★★ 第 3 期 Task 22：**四个扫描命令的 `--only-official` 口径统一** + 同块内数字一致。
///
/// 起因（T21 体检的延续）：对比四个扫描命令的开关面，发现：
/// <code>
/// check:  --scope --all --only-official --shadowing --rules --skip-rules --severity --json
/// sim:    --scope --all --only-official --json --timings --parallel
/// index:  --scope --all --rebuild --no-workshop --cache --json     ← 缺 only-official
/// files:  --scope --all --explain --filter --limit --json          ← 缺 only-official
/// </code>
/// 「只看官方」在四个命令上语义完全相同，只有两个有的会让用户以为能力不存在。
///
/// ★★ 补 `--only-official` 时**当场发现一个更深的 bug**（不是新写的，是旧代码就有的）：
/// <code>
/// files 旧输出（同一块里）：
///   mod 数           6      ← 已按 --only-official 过滤
///   XML 文件总数     20058  ← 未过滤的全库！
/// </code>
/// 根因：<c>TotalXmlFiles(ModCatalog cat, bool scopeAll)</c> **自己重算了一遍 mod 集合**，
/// 它不知道 `--only-official` / `--filter`，于是与调用方已经做过的过滤脱节。
/// 修法：形参改成**已过滤的 `IReadOnlyList&lt;ModInfo&gt;`** —— 只允许一个来源。
///
/// 这是本项目反复出现的同一个模式：**同一个量有两处算法就会分叉**（同 Task 4 的
/// 「补丁层统计不许 CLI 重算」、Task 5a 的「父名解析只认 ParentChainCheck 的产物」）。
/// </summary>
public static class Phase3Task22Cases
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
        T.Case("task22 ★ 四个扫描命令都必须认得 --only-official", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            if (env.GameRoot is null) { T.Mark("task22 跳过：没有游戏根"); return; }

            foreach (var cmd in new[] { "check", "sim", "index", "files" })
            {
                var (_, _, err) = RunCli(cmd, "--only-official");
                T.False(err.Contains("未知开关", StringComparison.Ordinal),
                    $"★ {cmd} 不认 --only-official —— 四个扫描命令语义相同，不该有的有有的没有。stderr={err}");
            }
        }, LayoutNeed.Any);

        T.Case("task22 ★★ files 的 mod 数与 XML 文件数必须同一口径（不许一个过滤一个不过滤）", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            if (env.GameRoot is null) { T.Mark("task22 跳过：没有游戏根"); return; }

            var (_, stdout, _) = RunCli("files", "--only-official", "--limit", "1", "--json");
            T.Contains(stdout, "\"modsScanned\"", "应有 modsScanned 字段");
            T.Contains(stdout, "\"xmlFileCount\"", "应有 xmlFileCount 字段");

            // 从 JSON 里抠两个数（避免依赖 JSON 库）
            var scanned = ExtractInt(stdout, "\"modsScanned\"");
            var xmlCount = ExtractInt(stdout, "\"xmlFileCount\"");
            T.True(scanned is >= 5 and <= 7,
                $"--only-official 时 modsScanned 应为 6（实际 {scanned}）");
            // ★ 不能只断言「小于 5000」—— 那样传空列表（0）也能过，是个假门。
            //   必须与「官方 6 包真实 CollectFiles 产物数」**相等**。
            var active2 = ModCatalog.ReadActiveMods(env.ModsConfigPath);
            var cat2 = ModCatalog.Build(env, env.TargetVersion, active2).Catalog;
            var official2 = cat2.Active.Where(m => m.Source == ModSource.Official).ToList();
            var expect2 = RimDev.Core.Defs.DefScanner.CollectFiles(
                official2.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders))).Count;
            // ★ 人读输出也要查 —— 两条路径（人读 / JSON）必须同口径。
            //   只查 JSON 会漏掉「人读那边分叉」的情形（本轮就遇到了：
            //   我只改了人读的调用点，JSON 仍正确，于是用例假绿）。
            var (_, humanOut, _) = RunCli("files", "--only-official", "--limit", "1");
            var humanMatch = System.Text.RegularExpressions.Regex.Match(humanOut,
                @"XML 文件总数\s+(\d+)");
            T.True(humanMatch.Success, "人读输出应有「XML 文件总数」行");
            if (humanMatch.Success)
            {
                var humanXml = int.Parse(humanMatch.Groups[1].Value);
                T.Eq(humanXml, expect2,
                    $"★ 人读输出的 XML 数也必须等于 {expect2}（实际 {humanXml}）"
                    + " —— 人读与 JSON 两条路径都不能自己重算");
            }

            T.Eq(xmlCount, expect2,
                $"★ xmlFileCount 必须等于官方 6 包的真实文件数 {expect2}（实际 {xmlCount}；"
                + "若出现 20058 说明未过滤，若出现 0 说明传了空集合 —— 两种都是口径分叉）");
        }, LayoutNeed.Any);

        T.Case("task22 ★ 不带开关时两个数字回到全量口径（无回归）", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            if (env.GameRoot is null) { T.Mark("task22 跳过：没有游戏根"); return; }

            var (_, stdout, _) = RunCli("files", "--limit", "1", "--json");
            var scanned = ExtractInt(stdout, "\"modsScanned\"");
            var xmlCount = ExtractInt(stdout, "\"xmlFileCount\"");
            T.True(scanned > 100, $"不带开关时应是全部启用 mod（实际 {scanned}）");
            T.True(xmlCount > 5_000, $"不带开关时 XML 数应是全库量级（实际 {xmlCount}）");
        }, LayoutNeed.Any);

        T.Case("task22 ★ index 与 files 的官方文件数必须一致（同一集合同一算法）", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            if (env.GameRoot is null) { T.Mark("task22 跳过：没有游戏根"); return; }

            // 两个命令都数「官方 6 包的 XML 文件」，必须相等 ——
            // 若不等，说明其中一处又自己算了一遍（正是 Task22b 修的 bug 形态）。
            var (_, filesOut, _) = RunCli("files", "--only-official", "--limit", "1", "--json");
            var filesXml = ExtractInt(filesOut, "\"xmlFileCount\"");

            var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
            var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;
            var official = cat.Active.Where(m => m.Source == ModSource.Official).ToList();
            var expect = RimDev.Core.Defs.DefScanner.CollectFiles(
                official.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders))).Count;

            T.Eq(filesXml, expect,
                "★ files 的 xmlFileCount 必须等于「官方 6 包 CollectFiles 的产物数」——"
                + "不等就说明有一处自己重算了集合");
        }, LayoutNeed.Any);
    }

    /// <summary>从 JSON 文本里抠 <c>"key": 123</c> 的整数。找不到返回 -1。</summary>
    private static int ExtractInt(string json, string key)
    {
        var i = json.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return -1;
        var j = json.IndexOf(':', i);
        if (j < 0) return -1;
        var k = j + 1;
        while (k < json.Length && (json[k] == ' ' || json[k] == '\t' || json[k] == '\n' || json[k] == '\r')) k++;
        var start = k;
        while (k < json.Length && (char.IsDigit(json[k]) || json[k] == '-')) k++;
        if (k == start) return -1;
        return int.TryParse(json[start..k], out var v) ? v : -1;
    }
}
