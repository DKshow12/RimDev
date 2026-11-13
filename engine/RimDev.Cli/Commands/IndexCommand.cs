using System.Text.Json;
using RimDev.Core.Catalog;
using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;

namespace RimDev.Cli.Commands;

public static class IndexCommand
{
    public static int Run(string[] args)
    {
        // rimdev index [--scope active|all] [--rebuild] [--no-workshop] [--cache <dir>] [--json]
        var scope = Option(args, "--scope")?.ToLowerInvariant() switch
        {
            "all" => ScanScope.All,
            _ => ScanScope.Active
        };
        var rebuild = args.Contains("--rebuild");
        var noWorkshop = args.Contains("--no-workshop");
        var json = args.Contains("--json");

        GameEnv env;
        try { env = GameEnvProbe.Discover(); }
        catch (GameEnvProbeException ex) { Console.Error.WriteLine("RD1001: " + ex.Message); return 4; }

        var cacheDir = Option(args, "--cache")
                       // ★ 必须从 env.ModsDir（= 游戏根\Mods）再往下取 _rimdev：
                       //   _rimdev 就在 Mods\ 里，而 Path.GetDirectoryName(env.ModsDir) 得到的是
                       //   游戏根，会拼出 <游戏根>\_rimdev\cache —— 那既不存在，也在 DSH 工作区
                       //   (...\RimWorld\Mods) 之外，写入会被沙箱直接拒绝。
                       ?? Path.Combine(env.ModsDir, "_rimdev", "cache");
        var store = new DefIndexStore(cacheDir);

        var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
        var build = ModCatalog.Build(env, env.TargetVersion, active);
        var cat = build.Catalog;

        var mods = (scope == ScanScope.All ? cat.All : cat.Active).AsEnumerable();
        if (noWorkshop) mods = mods.Where(m => m.Source != ModSource.Workshop);

        var modList = mods
            .Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders))
            .ToList();
        var files = DefScanner.CollectFiles(modList);

        var cached = rebuild ? new Dictionary<string, FileFingerprint>() : store.LoadFingerprints();
        var diff = DefIndexStore.Diff(files, cached);
        // ★ 必须同时要求「无变更」与「无删除」：只删不改时 changed==0，单看它会直接走缓存快路径，
        //   把已删除文件的 def 当最新结果返回。实测复现（2026-10-14）：缓存 2 / 磁盘 1 /
        //   SelectChanged 0 / Diff.Removed 1。这就是 Task 4b 加 Diff 的原因。
        var changed = diff.Changed;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        ScanResult scan;
        // 是否走了缓存快路径——filesParsed / filesSkippedByCache 要靠它区分「实际动作」
        var usedCache = changed.Count == 0 && diff.Removed.Count == 0 && store.Exists() && !rebuild;
        if (usedCache)
        {
            // ★ 不能把 FilesFailed 硬编码成 0：上一次真扫描若有解析失败，这里会把
            //   「解析失败 0」报出去——一个假的「全清」信号（与「只删不改走缓存」同属一类静默错答）。
            //   从 index-meta.json 读回真实值（因此本 Task 要给 DefIndexStore 加 LoadMeta()）。
            var meta = store.LoadMeta();
            scan = new ScanResult(store.LoadDefs(), Array.Empty<DefFileResult>(), files.Count,
                meta?.Failed ?? 0, 0);
        }
        else
        {
            // 简化增量：本期全量重扫，指纹用于判断"是否需要重扫"与统计
            scan = DefScanner.Scan(files);
            store.Save(scan, DefIndexStore.FingerprintAll(files), scope.ToString().ToLowerInvariant());
        }
        sw.Stop();

        // ★ Task 5b：统计字段说真话。下面两个数描述的是「**本次实际做了什么**」，
        //   不是「指纹 diff 判定了什么」——后者单独用 changedByFingerprint 报出。
        //   修前的写法是 filesParsed = changed.Count / filesSkippedByCache = files.Count - changed.Count，
        //   在「仅因删除而拒绝缓存」时会严重误报：实测 removedFiles=71370（gate 正确拒绝缓存、
        //   确实全量重扫了 1649 个文件）同时 filesParsed=0——因为这种场景下 changed 恰好为 0。
        var changedByFingerprint = changed.Count;
        var filesParsed = usedCache ? 0 : files.Count;
        var filesSkippedByCache = usedCache ? files.Count : 0;
        var idx = DefIndex.Build(scan.Defs);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = "1.0",
                command = "index",
                scope = scope.ToString().ToLowerInvariant(),
                cacheDir,
                modsScanned = modList.Count,
                candidateDirs = ModCatalog.EnumerateCandidates(env).Count(),
                xmlFiles = files.Count,
                filesParsed,
                filesSkippedByCache,
                // ★ 诊断口径：本次有多少文件被「指纹」判定为变更。它与 filesParsed 是两件事，
                //   不可互相替代（仅因删除而拒绝缓存时 changed==0 但实际全量重扫）。
                changedByFingerprint,
                removedFiles = diff.Removed.Count,
                filesFailed = scan.FilesFailed,
                defsIndexed = idx.TotalDefs,
                abstractDefs = idx.AbstractDefs,
                typeCounts = idx.CountByType.OrderByDescending(kv => kv.Value).Take(20)
                    .ToDictionary(kv => kv.Key, kv => kv.Value),
                // ★ 必须按 (DefType, defName) 判重：用 ByDefName 会报出 1202 条跨类型假重名（真值 1）
                duplicateDefNames = idx.ByTypeAndDefName.Count(kv => kv.Value.Count > 1),
                elapsedMs = sw.ElapsedMilliseconds,
                diagnostics = build.Diagnostics.Select(d => new
                {
                    code = d.Code, severity = d.Severity.ToString().ToLowerInvariant(), message = d.Message
                })
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Console.WriteLine($"scope            {scope.ToString().ToLowerInvariant()}");
        Console.WriteLine($"候选目录         {ModCatalog.EnumerateCandidates(env).Count()}");
        Console.WriteLine($"目录表 mod       {cat.All.Count}   （启用 {cat.Active.Count}）");
        Console.WriteLine($"本次扫描 mod     {modList.Count}");
        Console.WriteLine($"XML 文件         {files.Count}   （本次解析 {filesParsed} / 缓存跳过 {filesSkippedByCache}）");
        Console.WriteLine($"指纹判定变更     {changedByFingerprint}   （已删除 {diff.Removed.Count}）");
        Console.WriteLine($"解析失败         {scan.FilesFailed}");
        Console.WriteLine($"索引 def 总数    {idx.TotalDefs}   （抽象基类 {idx.AbstractDefs}）");
        // ★ 必须按 (DefType, defName) 判重：用 ByDefName 会报出 1202 条跨类型假重名（真值 1）
        Console.WriteLine($"defName 重名     {idx.ByTypeAndDefName.Count(kv => kv.Value.Count > 1)}");
        Console.WriteLine($"缓存目录         {cacheDir}");
        Console.WriteLine($"耗时             {sw.ElapsedMilliseconds} ms");
        Console.WriteLine();
        Console.WriteLine("Def 类型分布（前 12）：");
        foreach (var kv in idx.CountByType.OrderByDescending(k => k.Value).Take(12))
            Console.WriteLine($"   {kv.Key,-28} {kv.Value}");
        return 0;
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
