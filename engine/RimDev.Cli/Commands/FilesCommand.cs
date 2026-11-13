using System.Text.Json;
using RimDev.Core.Catalog;
using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;

namespace RimDev.Cli.Commands;

/// <summary>
/// <c>rimdev files [--explain &lt;mod&gt;|all] [--scope active|all] [--filter &lt;substr&gt;] [--limit N] [--json]</c>
///
/// 用途：把「每个 mod 到底会加载哪些目录」摊开给人看 —— 这是第 2 期 <c>LoadFoldersReader</c> 与引擎差得最远的地方
/// （F7），也是第 3 期 Task 2「全库体检」的输入。
/// 每个 mod 输出：<c>Origin / MatchedVersionKey / DescendingOrder / XmlFileCount / Issues</c>。
///
/// 退出码：0 成功；3 参数错；4 环境不满足；5 <c>--explain</c> 指定的 mod 找不到（<c>01</c> §4 退出码表）。
/// </summary>
public static class FilesCommand
{
    /// <summary>本命令认得的开关（★ Task 9c：白名单的唯一声明点，紧挨着读它们的代码）。</summary>
    public static readonly CommandFlagSet Flags = new("files", PrintHelp,
        new FlagSpec("--scope", true, "active|all"),
        new FlagSpec("--explain", true, "mod|all"),
        new FlagSpec("--filter", true, "子串"),
        new FlagSpec("--limit", true, "N"),
        new FlagSpec("--json"));

    /// <summary>`files --help`（帮助不依赖环境探测：没装游戏也能看）。</summary>
    public static void PrintHelp()
    {
        Console.WriteLine("rimdev files [options]");
        Console.WriteLine();
        Console.WriteLine("把「每个 mod 到底会加载哪些目录」摊开：Origin / MatchedVersionKey / DescendingOrder / XmlFileCount / Issues。");
        Console.WriteLine();
        Console.WriteLine("  --scope active|all   扫描范围（默认 active）");
        Console.WriteLine("  --explain <mod|all>  额外打印每个 mod 的目录优先级明细（索引 0 = 优先级最高）");
        Console.WriteLine("  --filter <子串>      只保留 packageId/locator/显示名含该子串的 mod");
        Console.WriteLine("  --limit <N>          最多打印 N 个 mod（0 = 不限；--scope all 默认 200）");
        Console.WriteLine("  --json               机器可读输出");
        Console.WriteLine("  --help, -h           打印本帮助（退出码 0）");
        Console.WriteLine();
        Console.WriteLine("退出码：0 成功；3 参数错；4 环境不满足；5 --explain 指定的 mod 找不到");
    }

    public static int Run(EnvironmentResolution cli)
    {
        var args = cli.Args;
        var json = args.Contains("--json");
        var scopeArg = Option(args, "--scope")?.ToLowerInvariant();
        bool scopeAll;
        switch (scopeArg)
        {
            case "all": scopeAll = true; break;
            case "active" or null: scopeAll = false; break;
            default:
                Console.Error.WriteLine($"未知的 --scope: {scopeArg}（可选 active|all）");
                return 3;
        }
        var explain = Option(args, "--explain");
        var filter = Option(args, "--filter");
        var limitArg = Option(args, "--limit");
        int limit = scopeAll ? 200 : int.MaxValue;
        if (limitArg is not null && (!int.TryParse(limitArg, out limit) || limit < 0))
        {
            Console.Error.WriteLine($"未知或缺值的 --limit: {limitArg}");
            return 3;
        }

        // ★★ 9b：环境**只有一个来源**（EnvironmentResolution）：不再自己 GameEnvProbe.Discover()。
        if (!cli.TryGetEnv(out var env)) return EnvironmentResolution.EnvExitCode;

        var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
        var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;
        var version = EngineVersionProbe.Probe(env);

        var mods = (scopeAll ? cat.All : cat.Active).ToList();
        if (filter is not null)
            mods = mods.Where(m => m.PackageId.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                || m.Locator.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                || m.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        mods = mods.OrderBy(m => m.PackageId, StringComparer.OrdinalIgnoreCase).ToList();

        // --explain <mod> 只影响「打印粒度」，不影响范围判定。
        ModInfo? one = null;
        if (explain is not null && !explain.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            one = mods.FirstOrDefault(m => m.PackageId.Equals(explain, StringComparison.OrdinalIgnoreCase))
                  ?? mods.FirstOrDefault(m => m.Locator.Equals(explain, StringComparison.OrdinalIgnoreCase))
                  ?? mods.FirstOrDefault(m => m.Locator.StartsWith(explain, StringComparison.OrdinalIgnoreCase));
            if (one is null)
            {
                Console.Error.WriteLine($"{Codes.TargetModNotFound}: 在 scope={(scopeAll ? "all" : "active")} 里找不到 mod `{explain}`");
                return 5;
            }
        }
        var detail = one is not null || explain?.Equals("all", StringComparison.OrdinalIgnoreCase) == true;
        if (one is not null) mods = new List<ModInfo> { one };

        if (json)
        {
            var payload = new
            {
                schemaVersion = "1.0",
                command = "files",
                scope = scopeAll ? "all" : "active",
                engine = new
                {
                    // ★ Q1：引擎的真实版本口径（反射取；拿不到时会标 env-fallback）
                    source = version.Source,
                    currentVersionString = version.CurrentVersionString,
                    currentVersionStringWithoutBuild = version.CurrentVersionStringWithoutBuild,
                    currentVersion = version.CurrentVersion.ToString(),
                    error = version.Error,
                },
                modsScanned = mods.Count,
                byOrigin = mods.GroupBy(m => OriginOf(m).ToString())
                    .OrderByDescending(g => g.Count())
                    .ToDictionary(g => g.Key, g => g.Count()),
                xmlFileCount = TotalXmlFiles(cat, scopeAll),
                mods = mods.Select(m => Describe(m, version)).ToList(),
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Console.WriteLine($"scope            {(scopeAll ? "all" : "active")}");
        Console.WriteLine($"引擎版本口径      {version.CurrentVersionString}（CurrentVersionStringWithoutBuild={version.CurrentVersionStringWithoutBuild}，来源={version.Source}）");
        Console.WriteLine($"mod 数           {mods.Count}   （目录表 {cat.All.Count} / 启用 {cat.Active.Count}）");
        Console.WriteLine($"XML 文件总数     {TotalXmlFiles(cat, scopeAll)}   （按引擎口径去重后）");
        Console.WriteLine();

        var shown = 0;
        foreach (var m in mods)
        {
            if (limit != 0 && shown >= limit)
            {
                Console.WriteLine($"...（还有 {mods.Count - shown} 个 mod；用 --limit 0 或 --json 看全部）");
                break;
            }
            PrintMod(m, detail || !scopeAll);
            shown++;
        }
        return 0;
    }

    private static void PrintMod(ModInfo m, bool withFolders)
    {
        var r = m.Resolution;
        var issues = r?.Issues ?? Array.Empty<Diagnostic>();
        var origin = OriginOf(m);
        Console.WriteLine($"{m.PackageId}  ({m.Locator})");
        Console.WriteLine($"   Origin={origin}  MatchedVersionKey={r?.MatchedVersionKey ?? "-"}  "
                          + $"Folders={r?.DescendingOrder.Count ?? 0}  XmlFileCount={XmlFileCount(m)}  Issues={issues.Count}");
        if (issues.Count > 0)
            Console.WriteLine($"   Issues: {string.Join(", ", issues.GroupBy(d => d.Code).Select(g => $"{g.Key}×{g.Count()}"))}");
        if (withFolders && r is not null)
        {
            for (var i = 0; i < r.DescendingOrder.Count; i++)
            {
                var exists = Directory.Exists(r.DescendingOrder[i]) ? " " : "✗";
                Console.WriteLine($"   [{i}] {exists} {r.DescendingOrder[i]}");
            }
        }
    }

    private static ResolutionOrigin OriginOf(ModInfo m) =>
        m.Resolution?.Origin ?? ResolutionOrigin.Empty;

    private static object Describe(ModInfo m, EngineVersionInfo version)
    {
        var r = m.Resolution;
        return new
        {
            packageId = m.PackageId,
            locator = m.Locator,
            displayName = m.DisplayName,
            root = m.RootPath,
            isActive = m.IsActive,
            hasLoadFolders = LoadFolderResolver.FindLoadFoldersFile(m.RootPath) is not null,
            definedVersions = ReadDefinedVersions(m),
            origin = OriginOf(m).ToString(),
            matchedVersionKey = r?.MatchedVersionKey,
            descendingOrder = r?.DescendingOrder ?? (IReadOnlyList<string>)Array.Empty<string>(),
            folderExists = (r?.DescendingOrder ?? Array.Empty<string>())
                .Select(f => new { folder = f, exists = Directory.Exists(f) }).ToList(),
            xmlFileCount = XmlFileCount(m),
            issues = (r?.Issues ?? Array.Empty<Diagnostic>()).Select(d => new
            {
                code = d.Code,
                severity = d.Severity.ToString().ToLowerInvariant(),
                message = d.Message,
                line = d.Line,
                evidence = d.Evidence,
            }).ToList(),
            engineVersion = version.CurrentVersionString,
        };
    }

    /// <summary>已定义的版本键（不重复解析文档：<c>loadFolders.xml</c> 很小，且这里只用于 JSON 输出）。
    /// 失败时返回空数组（= 引擎会走回退分支的情形）。</summary>
    private static IReadOnlyList<string> ReadDefinedVersions(ModInfo m)
    {
        try { return LoadFolderResolver.Load(m.RootPath).DefinedVersions; }
        catch { return Array.Empty<string>(); }
    }

    private static int XmlFileCount(ModInfo m)
    {
        var folders = m.Resolution?.DescendingOrder;
        return folders is null || folders.Count == 0 ? 0 : DefFileEnumerator.Enumerate(folders).Count;
    }

    private static int TotalXmlFiles(ModCatalog cat, bool scopeAll)
    {
        var mods = scopeAll ? cat.All : cat.Active;
        return DefScanner.CollectFiles(
            mods.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders))).Count;
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
