using System.Text.Json;
using RimDev.Core.Catalog;
using RimDev.Core.Checks;
using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;

namespace RimDev.Cli.Commands;

/// <summary>
/// `rimdev check [&lt;mod&gt;] [--all] [--severity error|warning|info] [--json]`
///
/// 设计要点（与 Docs\05-实施计划-第2期.md 的 Task 7 预检修正一致）：
///  · 目标 mod：显式参数（packageId 或目录名）&gt; 工作区里含 `Source\*.csproj` 的 mod &gt; 报 RD1009 并退 5。
///  · `--severity` 未知取值【或缺值】一律退 3，**不静默降级为 info**（静默降级属「静默错答」类）。
///  · 规则集是 `CheckRunner.Default()` 的五条；索引/defs/files 必须建自【全集扫描】，
///    目标 mod 只作报告过滤器（见 CheckContext 的契约注释）。
/// </summary>
public static class CheckCommand
{
    public static int Run(string[] args)
    {
        // rimdev check [<mod>] [--all] [--severity error|warning|info] [--json]
        var modArg = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
        var all = args.Contains("--all");
        // ★ 预检修正 ③：未知取值/缺值必须报参数错（退出码 3），不能静默降级为 info。
        var sevArg = Option(args, "--severity")?.ToLowerInvariant();
        Severity minSeverity;
        switch (sevArg)
        {
            case null when !args.Contains("--severity"): minSeverity = Severity.Info; break;
            case "info": minSeverity = Severity.Info; break;
            case "warning": minSeverity = Severity.Warning; break;
            case "error": minSeverity = Severity.Error; break;
            default:
                Console.Error.WriteLine($"未知或缺值的 --severity: {sevArg ?? "(缺值)"}（可选 error|warning|info）");
                return 3;
        }
        var json = args.Contains("--json");

        GameEnv env;
        try { env = GameEnvProbe.Discover(); }
        catch (GameEnvProbeException ex) { Console.Error.WriteLine("RD1001: " + ex.Message); return 4; }

        var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
        var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;

        // 目标 mod：显式参数 > 工作区里含 Source\*.csproj 的目录 > 报错
        var target = modArg is null ? GuessTargetMod(env, cat) : cat.Get(modArg) ?? FindByFolder(cat, modArg);
        if (target is null && !all)
        {
            Console.Error.WriteLine($"{Codes.TargetModNotFound}: 无法确定目标 mod；请显式给出 <mod>（目录名或 packageId）或加 --all");
            return 5;
        }

        // ★★ 索引 / defs / files 必须建自【全集扫描】，TargetModPackageId 只作【报告过滤器】
        //     —— 这是 CheckContext 的契约（Task 6 修正 ④，本文件上方未改动任何判据）。
        //
        //   计划 Step 1 的原始代码在这里写的是 `all ? cat.Active : new List<ModInfo> { target! }`
        //   （即：指定目标 mod 时只扫它自己），**与契约直接矛盾**，实测后果就是契约预告的那一条：
        //   2026-10-14 跑 `rimdev check`（自动识别 ThreatFleet）得到 **error 100 / exit=2**，
        //   全部是 RD2005 假阳 —— ParentName="BenchBase"/"ResourceBase"/"BaseBullet" 这类父类
        //   都在 Core/DLC 里，只扫 ThreatFleet（178 个 def）自然一个也找不到。
        //   同一份代码在 --all 下反而正常（17 mod / 1649 XML / 14473 def 都在），说明问题只出在单目标分支。
        //
        //   修正后与计划自己的 Expected 一致：`rimdev check` → error 0 / exit=0；
        //   与 CheckCases 里「★ ThreatFleet 的静态体检：0 个 error」用的是同一口径（全集 + 过滤器）。
        //   报告范围【不变】：过滤器仍然只让「目标 mod 参与」的诊断出现（见 CheckContext 注释）。
        var mods = cat.Active;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var files = DefScanner.CollectFiles(
            mods.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)));
        var scan = DefScanner.Scan(files);
        var idx = DefIndex.Build(scan.Defs);
        var ctx = new CheckContext(idx, scan.Files, cat, scan.Defs, all ? null : target!.PackageId);

        var diags = CheckRunner.Default().Run(ctx)
            .Where(d => d.Severity >= minSeverity)
            .ToList();
        sw.Stop();

        var summary = new
        {
            error = diags.Count(d => d.Severity == Severity.Error),
            warning = diags.Count(d => d.Severity == Severity.Warning),
            info = diags.Count(d => d.Severity == Severity.Info)
        };

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = "1.0",
                tool = new { name = "rimdev", version = "0.2.0" },
                command = "check",
                target = all ? "(all active)" : target!.PackageId,
                scanned = new { mods = mods.Count, xmlFiles = files.Count, defs = idx.TotalDefs },
                summary,
                elapsedMs = sw.ElapsedMilliseconds,
                diagnostics = diags.Select(d => new
                {
                    code = d.Code,
                    severity = d.Severity.ToString().ToLowerInvariant(),
                    scope = d.Scope.ToString().ToLowerInvariant(),
                    message = d.Message,
                    hint = d.Hint,
                    file = d.FileAbs,
                    line = d.Line,
                    mod = d.ModPackageId,
                    defName = d.DefName,
                    defType = d.DefType,
                    suggestions = d.Suggestions,
                    evidence = d.Evidence,
                    fingerprint = d.Fingerprint
                })
            }, new JsonSerializerOptions { WriteIndented = true }));
            return summary.error > 0 ? 2 : 0;
        }

        Console.WriteLine($"目标            {(all ? "全部启用 mod" : target!.ShortName + " (" + target.PackageId + ")")}");
        // 扫描口径必须写明：永远是【全集】扫描，单目标时只是报告被过滤（避免「扫了 1 个 mod」的误读）
        Console.WriteLine($"扫描            {mods.Count} 个 mod / {files.Count} 个 XML / {idx.TotalDefs} 个 def"
                          + (all ? "" : "   （全集扫描；报告按目标过滤）"));
        Console.WriteLine($"结果            error {summary.error}   warning {summary.warning}   info {summary.info}");
        Console.WriteLine($"耗时            {sw.ElapsedMilliseconds} ms");
        Console.WriteLine();

        foreach (var g in diags.GroupBy(d => d.Code).OrderByDescending(g => g.First().Severity))
        {
            Console.WriteLine($"[{g.Key}] {g.Count()} 条");
            foreach (var d in g.Take(10))
                Console.WriteLine($"   {Path.GetFileName(d.FileAbs ?? "?")}:{d.Line}  {d.Message}");
            if (g.Count() > 10) Console.WriteLine($"   ...（还有 {g.Count() - 10} 条）");
        }

        return summary.error > 0 ? 2 : 0;
    }

    /// <summary>
    /// 自动识别「工作区里正在开发的那个 mod」：&lt;mod&gt;\Source\*.csproj + &lt;mod&gt;\About\About.xml 必须同时存在。
    /// 本机实测只有 Mods\ThreatFleet 满足（Twilight\Source 里 0 个 csproj），所以识别是确定性的。
    /// </summary>
    private static ModInfo? GuessTargetMod(GameEnv env, ModCatalog cat)
    {
        if (!Directory.Exists(env.ModsDir)) return null;
        foreach (var d in Directory.GetDirectories(env.ModsDir))
        {
            if (!Directory.Exists(Path.Combine(d, "Source"))) continue;
            if (!Directory.EnumerateFiles(Path.Combine(d, "Source"), "*.csproj").Any()) continue;
            var about = Path.Combine(d, "About", "About.xml");
            if (!File.Exists(about)) continue;
            var info = AboutXmlReader.Read(about);
            if (cat.TryGet(info.PackageId, out var m)) return m;
        }
        return null;
    }

    private static ModInfo? FindByFolder(ModCatalog cat, string folderName) =>
        cat.All.FirstOrDefault(m =>
            string.Equals(m.Locator, folderName, StringComparison.OrdinalIgnoreCase));

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
