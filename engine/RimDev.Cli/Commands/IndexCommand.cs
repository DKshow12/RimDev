using System.Text.Json;
using RimDev.Core.Catalog;
using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;

namespace RimDev.Cli.Commands;

public static class IndexCommand
{
    /// <summary>本命令认得的开关（★ Task 9c：白名单的唯一声明点，紧挨着读它们的代码）。</summary>
    public static readonly CommandFlagSet Flags = new("index", PrintHelp,
        new FlagSpec("--scope", true, "active|all"),
        new FlagSpec("--rebuild"),
        new FlagSpec("--no-workshop"),
        new FlagSpec("--cache", true, "目录"),
        new FlagSpec("--json"));

    /// <summary>`index --help`（帮助不依赖环境探测：没装游戏也能看）。</summary>
    public static void PrintHelp()
    {
        Console.WriteLine("rimdev index [options]");
        Console.WriteLine();
        Console.WriteLine("建立/复用 Def 索引缓存（defs.jsonl + 指纹表），并打印计数自洽三式的检查结果。");
        Console.WriteLine();
        Console.WriteLine("  --scope active|all   扫描范围（默认 active = 全部启用 mod；all = 全库）");
        Console.WriteLine("  --rebuild            忽略现有指纹，强制全量重扫");
        Console.WriteLine("  --no-workshop        扫描时排除工坊 mod");
        Console.WriteLine("  --cache <目录>       缓存目录（缺省 = DefIndexStore.DefaultCacheDir）");
        Console.WriteLine("  --json               机器可读输出");
        Console.WriteLine("  --help, -h           打印本帮助（退出码 0）");
        Console.WriteLine();
        Console.WriteLine("退出码：0 成功；3 参数错；4 环境不满足（找不到游戏根）");
    }

    public static int Run(EnvironmentResolution cli)
    {
        var args = cli.Args;
        // rimdev index [--scope active|all] [--rebuild] [--no-workshop] [--cache <dir>] [--json]
        // ★ 9c 收尾：`--scope` 的**取值域也必须响亮失败**。修前 `_ => ScanScope.Active` 会把任何拼错
        //   的值静默当成 active 并照常跑一次索引（`index --scope bogus` → exit 0、产出 1882 B 结果），
        //   而 `files`/`check`/`sim` 三条命令对同一个错值都是 exit 3 —— 同一类参数错在不同命令上
        //   一个响亮一个静默，是最容易被误读成「工具就是这么设计的」的那种不一致。
        //   （这是**参数层**与白名单同族的一环：9c 已让「开关名写错」响亮，这里让「开关值写错」也响亮。）
        var scopeArg = Option(args, "--scope")?.ToLowerInvariant();
        if (scopeArg is not (null or "active" or "all"))
        {
            Console.Error.WriteLine($"未知的 --scope: {scopeArg}（可选 active|all）");
            return 3;
        }
        var scope = scopeArg == "all" ? ScanScope.All : ScanScope.Active;
        var rebuild = args.Contains("--rebuild");
        var noWorkshop = args.Contains("--no-workshop");
        var json = args.Contains("--json");

        // ★★ 9b：环境**只有一个来源**（EnvironmentResolution）：不再自己 GameEnvProbe.Discover()。
        if (!cli.TryGetEnv(out var env)) return EnvironmentResolution.EnvExitCode;

        // ★ 单一真源（预检修正 ④）：缓存目录只在 DefIndexStore.DefaultCacheDir 里定义一次，
        //   索引命令与消费方（check / name --index）都调它 —— 各写一遍必然再次分叉。
        var cacheDir = Option(args, "--cache") ?? DefIndexStore.DefaultCacheDir(env);
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

        // ★★ 第 3 期 Task 2 Step 3：**启动断言** —— 在决定「能不能用缓存」之前先把三式核一遍。
        //   `xmlFiles == fingerprintsRecorded == filesParsedOk + filesSkippedUnchanged + filesParseFailed`
        //   不成立就报 RD1004 并【拒绝使用缓存】（走全量重扫），而不是拿自相矛盾的索引作答。
        var hadCache = store.Exists();
        var cacheCheck = store.ValidateCache(files.Count);

        ScanResult scan;
        // 自洽三元组的六个数字：描述【产出当前这份索引的那一次扫描】的真实动作
        int scFiles, scFingerprints, scParsedOk, scSkippedUnchanged, scFailed, scVanished;
        var usedCache = !rebuild && cacheCheck.Usable && changed.Count == 0 && diff.Removed.Count == 0;
        if (usedCache)
        {
            // ★ 不能把解析失败数硬编码成 0：缓存那一次真扫描的失败会被报成「解析失败 0」——
            //   一个假的「全清」信号（与「只删不改走缓存」同属一类静默错答）。从 meta 读回真实值。
            var meta = cacheCheck.Meta!;
            scan = new ScanResult(store.LoadDefs(), Array.Empty<DefFileResult>(), files.Count,
                meta.FilesParseFailed, 0);
            scFiles = meta.FilesEnumerated;
            scFingerprints = meta.FingerprintsRecorded;
            scParsedOk = meta.FilesParsedOk;
            scSkippedUnchanged = meta.FilesSkippedUnchanged;
            scFailed = meta.FilesParseFailed;
            scVanished = meta.FilesVanishedBeforeFingerprint;
        }
        else
        {
            // 简化增量：本期全量重扫，指纹用于判断"是否需要重扫"与统计
            scan = DefScanner.Scan(files);
            var fps = DefIndexStore.FingerprintAll(files);
            store.Save(scan, fps, scope.ToString().ToLowerInvariant());
            scFiles = files.Count;
            scFingerprints = fps.Count;
            scParsedOk = scan.Files.Count(f => f.ParseError is null);
            scSkippedUnchanged = 0;
            scFailed = scan.FilesFailed;
            // 枚举后被删、因而进不了指纹表的文件（活跃写入中的语料会偶发）
            scVanished = scan.Files.Count(f => !fps.ContainsKey(f.FileAbs));
        }
        sw.Stop();

        var selfConsistent = scFiles == scFingerprints
                             && scFiles == scParsedOk + scSkippedUnchanged + scFailed;
        var selfConsistencyText =
            $"files={scFiles} fingerprints={scFingerprints} "
            + $"parsedOk+skippedUnchanged+parseFailed={scParsedOk}+{scSkippedUnchanged}+{scFailed}"
            + $" (vanishedBeforeFingerprint={scVanished})";

        var idx = DefIndex.Build(scan.Defs);

        // ★ Task 5b / Task 2 统计字段说真话：下面两个数描述的是【本次实际做了什么】。
        //   修前的写法是 filesParsed = changed.Count / filesSkippedByCache = files.Count - changed.Count，
        //   在「仅因删除而拒绝缓存」时会严重误报：实测 removedFiles=71370（gate 正确拒绝缓存、
        //   确实全量重扫了 1649 个文件）同时 filesParsed=0——因为这种场景下 changed 恰好为 0。
        var filesParsedOkThisRun = usedCache ? 0 : scParsedOk;
        var filesSkippedUnchangedThisRun = usedCache ? files.Count : 0;
        var filesWithChangedFingerprint = changed.Count;
        var cachedFilesMissingFromDisk = diff.Removed.Count;

        // RD1004：三式不成立时优先报「本次/缓存扫描」的真实原因；否则报「缓存为何不可用」。
        var cacheDiags = new List<Diagnostic>();
        if (!selfConsistent)
            cacheDiags.Add(new Diagnostic(Codes.CacheSchemaMismatch, Severity.Warning, Scope.Offline,
                $"索引计数不自洽（RD1004）：{selfConsistencyText}",
                "缓存已标为不可信（下一次 index 会拒绝它并重扫）。常见原因：枚举与取指纹之间文件被删（活跃写入中的语料）",
                FileAbs: store.MetaPath,
                Evidence: new Dictionary<string, string>
                {
                    ["reason"] = "三式不成立",
                    ["files"] = scFiles.ToString(),
                    ["fingerprints"] = scFingerprints.ToString(),
                    ["parsedOk"] = scParsedOk.ToString(),
                    ["skippedUnchanged"] = scSkippedUnchanged.ToString(),
                    ["parseFailed"] = scFailed.ToString(),
                    ["vanishedBeforeFingerprint"] = scVanished.ToString(),
                }));
        else if (hadCache && !cacheCheck.Usable)
            cacheDiags.Add(new Diagnostic(Codes.CacheSchemaMismatch, Severity.Warning, Scope.Offline,
                $"索引缓存不可用，已拒绝使用并全量重扫（RD1004）：{cacheCheck.Reason}",
                "跑 `rimdev index --rebuild` 可显式重建；schemaVersion 升级后首次运行属预期行为",
                FileAbs: store.MetaPath,
                Evidence: new Dictionary<string, string> { ["reason"] = cacheCheck.Reason }));
        var allDiags = build.Diagnostics.Concat(cacheDiags).ToList();

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
                // ★ 第 3 期 Task 2（修 F3）：统计字段一律描述【实际动作】，不再用「判定输入」命名。
                //   · xmlFiles                     = 本次磁盘枚举到的 XML 文件数（= 计划里的 `files`）
                //   · filesParsedOk                = 本次真的重新读了并成功的文件数
                //   · filesSkippedUnchanged        = 本次因为指纹一致而**没读**的文件数
                //   · filesFailed                  = 产出当前索引的那一次扫描里，真的读了但失败的文件数
                //   · filesWithChangedFingerprint  = 指纹判定为「变了」的文件数（判定输入，不是工作量）
                //   · cachedFilesMissingFromDisk   = 缓存里有、本次输入已消失的文件数（检出，不是我们删的）
                xmlFiles = files.Count,
                filesParsedOk = filesParsedOkThisRun,
                filesSkippedUnchanged = filesSkippedUnchangedThisRun,
                filesWithChangedFingerprint,
                cachedFilesMissingFromDisk,
                fingerprintsRecorded = scFingerprints,
                filesVanishedBeforeFingerprint = scVanished,
                filesFailed = scFailed,
                // ★★ 自洽三式（Task 2 Step 3）：三个数必须相等，否则报 RD1004 并拒绝用缓存。
                selfConsistency = new
                {
                    rule = "xmlFiles == fingerprintsRecorded == filesParsedOk + filesSkippedUnchanged + filesFailed",
                    describes = usedCache ? "cache-producing-scan" : "this-scan",
                    files = scFiles,
                    fingerprints = scFingerprints,
                    parsedOk = scParsedOk,
                    skippedUnchanged = scSkippedUnchanged,
                    parseFailed = scFailed,
                    vanishedBeforeFingerprint = scVanished,
                    holds = selfConsistent,
                    detail = selfConsistencyText,
                },
                defsIndexed = idx.TotalDefs,
                abstractDefs = idx.AbstractDefs,
                typeCounts = idx.CountByType.OrderByDescending(kv => kv.Value).Take(20)
                    .ToDictionary(kv => kv.Key, kv => kv.Value),
                // ★ 必须按 (DefType, defName) 判重：用 ByDefName 会报出 1202 条跨类型假重名（真值 1）
                // ★ excludesAbstract（Task 2 Step 7 / F6 口径标注）：DefIndex.ByTypeAndDefName 只收录
                //   **非抽象**声明（Abstract="True" 的节点不会被引擎建成 def）。必须随这个数字一起输出，
                //   否则会与「把 defs.jsonl 全部行都算进去」的探针口径（全库实测 13195）差出几千条 ——
                //   那个差不是缺陷，只是口径（分母在 Task 1 后也变了）。
                duplicateDefNames = idx.ByTypeAndDefName.Count(kv => kv.Value.Count > 1),
                excludesAbstract = true,
                elapsedMs = sw.ElapsedMilliseconds,
                peakWorkingSetMb = Math.Round(
                    System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0, 1),
                diagnostics = allDiags.Select(d => new
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
        Console.WriteLine($"XML 文件         {files.Count}   （本次真读成功 {filesParsedOkThisRun} / 指纹一致跳过 {filesSkippedUnchangedThisRun}）");
        Console.WriteLine($"指纹判定变更     {filesWithChangedFingerprint}   （缓存里有、本次输入已消失 {cachedFilesMissingFromDisk}）");
        Console.WriteLine($"解析失败         {scFailed}");
        Console.WriteLine($"索引 def 总数    {idx.TotalDefs}   （抽象基类 {idx.AbstractDefs}）");
        // ★ 计数自洽（Task 2 Step 3）：三式相等才说明「指纹表 / 计数器 / 磁盘集合」互不矛盾
        Console.WriteLine($"计数自洽         {selfConsistent}   {selfConsistencyText}");
        if (!usedCache && scVanished > 0)
            Console.WriteLine($"   ★ 有 {scVanished} 个文件在「枚举」与「取指纹」之间消失（活跃写入中的语料），已计入不自洽");
        // ★ 必须按 (DefType, defName) 判重：用 ByDefName 会报出 1202 条跨类型假重名（真值 1）
        Console.WriteLine($"defName 重名     {idx.ByTypeAndDefName.Count(kv => kv.Value.Count > 1)}   "
                          + "（excludesAbstract=true：与「把全部声明都算上」的探针口径之差不是缺陷）");
        Console.WriteLine($"缓存目录         {cacheDir}");
        Console.WriteLine($"耗时             {sw.ElapsedMilliseconds} ms   峰值工作集 "
                          + $"{Math.Round(System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0, 1)} MB");
        foreach (var d in allDiags)
            Console.WriteLine($"[{d.Code}] {d.Message}");
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
