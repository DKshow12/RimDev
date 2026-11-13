using System.Text.Json;
using RimDev.Core.Catalog;
using RimDev.Core.Diagnostics;
using RimDev.Core.Logs;

namespace RimDev.Cli.Commands;

/// <summary>
/// ★★ 第 3 期 Task 15：<c>rimdev log</c> —— 读 Player.log 做诊断。
///
/// 这是本工具第一次具备「真机视角」：前面所有命令都是**离线预测**（读 XML 猜引擎会怎样），
/// 而日志是**引擎自己说的话**。两者互补：
/// <list type="bullet">
/// <item>离线 <c>RD3001</c>（预测悬空）↔ 日志 <c>RD7020</c>（引擎真的报 not defined）</item>
/// <item>离线 <c>RD4001</c>（字段不存在）↔ 日志 <c>RD7021</c>（引擎真的报 XML error）</item>
/// </list>
///
/// ★ 默认路径走 <see cref="GameEnv"/> 的二级回退（Docs/10 §6.2 的裁决）：
///   1.7 的日志在 <c>&lt;localLow&gt;\Logs\Player.log</c>，不是根目录。
/// </summary>
public static class LogCommand
{
    public static readonly CommandFlagSet Flags = new("log", PrintHelp,
        new FlagSpec("--path", true, "文件"),
        new FlagSpec("--json"),
        new FlagSpec("--top", true, "N"),
        new FlagSpec("--max-lines", true, "N"),
        new FlagSpec("--all-rules"));

    public static int Run(EnvironmentResolution cli, string[] args)
    {
        var help = false;
        var json = false;
        var top = 5;
        var maxLines = 0;
        var allRules = false;
        string? pathArg = null;

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--help" or "-h" or "--?": help = true; break;
                case "--json": json = true; break;
                case "--all-rules": allRules = true; break;
                case "--path":
                    if (++i >= args.Length) { Console.Error.WriteLine("--path 缺少值"); return 3; }
                    pathArg = args[i];
                    break;
                case "--top":
                    if (++i >= args.Length || !int.TryParse(args[i], out top) || top < 1)
                    { Console.Error.WriteLine("--top 需要正整数"); return 3; }
                    break;
                case "--max-lines":
                    if (++i >= args.Length || !int.TryParse(args[i], out maxLines) || maxLines < 0)
                    { Console.Error.WriteLine("--max-lines 需要非负整数"); return 3; }
                    break;
                default:
                    Console.Error.WriteLine($"未知开关: {a}（log 不认识它）");
                    return 3;
            }
        }

        if (help) { PrintHelp(); return 0; }

        // ── 解析日志路径 ──
        string logPath;
        IReadOnlySet<string>? activeMods = null;

        if (pathArg is not null)
        {
            logPath = pathArg;
        }
        else
        {
            if (!cli.TryGetEnv(out var env)) return EnvironmentResolution.EnvExitCode;
            logPath = env.PlayerLogPath;

            // 活跃集（供归因交叉比对）；失败不影响主流程。
            try
            {
                var actives = ModCatalog.ReadActiveMods(env.ModsConfigPath);
                activeMods = actives.Select(x => x.Trim().ToLowerInvariant())
                                    .Where(x => x.Length > 0)
                                    .ToHashSet(StringComparer.Ordinal);
            }
            catch { /* 没有 ModsConfig 也能分析日志，只是归因弱一点 */ }
        }

        if (!File.Exists(logPath))
        {
            Console.Error.WriteLine($"日志文件不存在: {logPath}");
            Console.Error.WriteLine("（可用 --path 指定；1.7 的默认位置是 <localLow>\\Logs\\Player.log）");
            return 4;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = LogAnalyzer.Analyze(logPath, activeMods, maxLines);
        sw.Stop();

        if (json) WriteJson(r, sw.ElapsedMilliseconds);
        else WriteHuman(r, top, allRules, sw.ElapsedMilliseconds);

        // 退出码：有 error 级 ⇒ 2（与 check 一致）
        return r.ErrorCount > 0 ? 2 : 0;
    }

    private static void WriteJson(LogAnalysisResult r, long elapsedMs)
    {
        var payload = new
        {
            schemaVersion = "1.0",
            tool = new { name = "rimdev", version = "0.1.0" },
            command = "log",
            path = r.Path,
            bytes = r.Bytes,
            linesScanned = r.LinesScanned,
            blocksScanned = r.BlocksScanned,
            elapsedMs,
            summary = new { error = r.ErrorCount, warning = r.WarningCount, info = r.InfoCount },
            byRule = r.ByRule,
            byAttribution = r.ByAttribution.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            findings = r.Findings.Select(f => new
            {
                code = f.Code,
                severity = f.Severity.ToString().ToLowerInvariant(),
                line = f.LineNumber,
                timestamp = f.Timestamp,
                level = f.Level,
                rule = f.RuleId,
                message = f.Message,
                explanation = f.Explanation,
                attribution = f.Attribution.ToString(),
                modPackageId = f.ModPackageId,
                relatedFiles = f.RelatedFiles,
                relatedModIds = f.RelatedModIds,
                relatedNamespaces = f.RelatedNamespaces,
            }),
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }

    private static void WriteHuman(LogAnalysisResult r, int top, bool allRules, long elapsedMs)
    {
        string Mb(long b) => (b / 1048576.0).ToString("0.0");
        Console.WriteLine($"日志            {r.Path}");
        Console.WriteLine($"规模            {Mb(r.Bytes)} MB / 扫描 {r.LinesScanned} 行 / {r.BlocksScanned} 块");
        Console.WriteLine($"结果            error {r.ErrorCount}   warning {r.WarningCount}   info {r.InfoCount}");
        Console.WriteLine($"归因            "
            + string.Join(" / ", r.ByAttribution.OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key} {kv.Value}")));
        Console.WriteLine($"耗时            {elapsedMs} ms");
        Console.WriteLine();

        if (r.Findings.Count == 0)
        {
            Console.WriteLine("没有任何 ERROR / WARNING / EXCEPTION 级日志。");
            return;
        }

        // 按规则分组（命中多的在前）
        var groups = r.Findings.GroupBy(f => f.RuleId)
            .OrderByDescending(g => g.Count())
            .ToList();

        foreach (var g in groups)
        {
            var first = g.First();
            Console.WriteLine($"[{first.Code}] {first.RuleId} —— {g.Count()} 条");
            Console.WriteLine($"   {first.Explanation}");
            foreach (var f in g.Take(top))
            {
                var where = f.Attribution == Attribution.Unattributed ? "（未归因）"
                    : f.ModPackageId is not null ? $"（{f.ModPackageId}）" : $"（{f.Attribution}）";
                Console.WriteLine($"   L{f.LineNumber} {where} {Truncate(f.Message, 110)}");
            }
            if (g.Count() > top) Console.WriteLine($"   ...（还有 {g.Count() - top} 条）");
            Console.WriteLine();
        }

        if (allRules)
        {
            var missed = LogDiagnosisRules.All.Where(rule => !r.ByRule.ContainsKey(rule.Id)).ToList();
            if (missed.Count > 0)
            {
                Console.WriteLine($"未命中的规则（{missed.Count} 条）：");
                foreach (var m in missed) Console.WriteLine($"   {m.Id}");
            }
        }

        Console.WriteLine("提示：未归类（uncategorized）不参与「比基线新增」判定 —— 否则游戏版本一变，");
        Console.WriteLine("      通用报错会被算成新增缺陷，门禁变成噪音源。");
    }

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s[..n] + "…";

    public static void PrintHelp()
    {
        Console.WriteLine("rimdev log [options]");
        Console.WriteLine();
        Console.WriteLine("读 Player.log 做诊断（第 3 期 Task 15）。规则库移植自 RimCrow 的 LogAnalyzer。");
        Console.WriteLine();
        Console.WriteLine("  --path <文件>      指定日志（默认用环境探测到的 Player.log）");
        Console.WriteLine("  --json             机器可读输出");
        Console.WriteLine("  --top <N>          每个规则最多列几条样本（默认 5）");
        Console.WriteLine("  --max-lines <N>    最多读多少行（0 = 全部；用于快速采样）");
        Console.WriteLine("  --all-rules        连未命中的规则也打印");
        Console.WriteLine("  --help, -h         打印本帮助");
        Console.WriteLine();
        Console.WriteLine("默认路径：<localLow>\\Logs\\Player.log（1.7）；找不到时回退 <localLow>\\Player.log。");
        Console.WriteLine();
        Console.WriteLine("退出码：0 无 error；2 有 error；3 参数错；4 日志文件不存在");
    }
}
