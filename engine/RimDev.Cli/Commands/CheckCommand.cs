using System.Text.Json;
using RimDev.Core.Catalog;
using RimDev.Core.Checks;
using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;

namespace RimDev.Cli.Commands;

/// <summary>
/// `rimdev check [&lt;mod&gt;] [--scope target|active|all] [--all] [--only-official]
///  [--severity error|warning|info] [--json] [--help]`
///
/// ★★ `--scope` 的三种语义（第 3 期 Task 2 Step 1，修 F1）—— **必须无歧义**：
/// <list type="bullet">
/// <item><b>默认</b>（无 `--scope`、无 `--all`）= **目标 mod**：全集扫描 + 按目标过滤。
///   沿用第 2 期行为；**不能**退回「只扫目标 mod」—— 那会让 ParentName 指向 Core/DLC 基类的 def
///   全部误报 RD2005（实测 `check ThreatFleet` 会得到 error 100）。</item>
/// <item><b>`--scope active`</b> = 整个**启用**集合（本机 17 个 mod / 1654 XML / 14495 def）。</item>
/// <item><b>`--all`</b> = `--scope active` 的**兼容别名**（第 2 期的写法继续可用，语义完全等价；
///   自测里有一条用例直接断言这一点）。</item>
/// <item><b>`--scope all`</b> = **全库**（3971 个 mod / 31 万 def）—— 这正是第 2 期缺的能力（F1）。</item>
/// </list>
///
/// 设计要点（与 Docs\05-实施计划-第2期.md 的 Task 7 预检修正一致）：
///  · 目标 mod：显式参数（packageId 或目录名）&gt; 工作区里含 `Source\*.csproj` 的 mod &gt; 报 RD1009 并退 5。
///  · `--severity` 未知取值【或缺值】一律退 3，**不静默降级为 info**（静默降级属「静默错答」类）。
///  · 规则集是 `CheckRunner.Default()` 的六条；索引/defs/files 必须建自【本次 scope 的全集扫描】，
///    目标 mod 只作报告过滤器（见 CheckContext 的契约注释）。
///  · `--only-official` 只保留官方 6 个包（`Data\Core` + 5 个 DLC）—— 这是「假阳比漏报更贵」的
///    机械保障：官方内容上出现任何 error 一律视为**我们的规则错了**（Task 2 Step 8）。
/// </summary>
public static class CheckCommand
{
    // ★ 扫描范围（CheckScope）与其解析（CheckScopeSelector）的**唯一实现**在
    //   RimDev.Core/Checks/CheckScope.cs —— 放在 Core 是为了让自测**不必引用 CLI 工程**
    //   就能钉住「`--all` 等价于 `--scope active`」。本类只保留一层转发（见下方三个转发函数）。

    /// <summary>本命令认得的开关（★ Task 9c：白名单的唯一声明点，紧挨着读它们的代码）。</summary>
    public static readonly CommandFlagSet Flags = new("check", PrintHelp,
        new FlagSpec("--scope", true, "target|active|all"),
        new FlagSpec("--all"),
        new FlagSpec("--only-official"),
        new FlagSpec("--shadowing"),
        new FlagSpec("--severity", true, "error|warning|info"),
        new FlagSpec("--json"));

    public static int Run(EnvironmentResolution cli)
    {
        var args = cli.Args;
        // rimdev check [<mod>] [--scope target|active|all] [--all] [--only-official] [--severity ...] [--json]

        var modArg = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
        var scope = ParseScope(args, out var scopeError);
        if (scopeError is not null)
        {
            Console.Error.WriteLine(scopeError);
            return 3;
        }
        var onlyOfficial = args.Contains("--only-official");
        // ★ 第 3 期 Task 3：`--shadowing` = 只看遮蔽/顺序族（RD2011 / RD2025 / RD2026）。
        var shadowingOnly = args.Contains("--shadowing");
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

        // ★★ 9b：环境**只有一个来源**（EnvironmentResolution）。这里不再 GameEnvProbe.Discover() ——
        //   那是「--game-root / --workshop-root 被静默忽略」的入口（旧实现就是把它们丢掉、照常自动探测）。
        if (!cli.TryGetEnv(out var env)) return EnvironmentResolution.EnvExitCode;
        var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
        var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;

        // ★ 第 3 期 Task 3：引擎语义的加载顺序（Core → knownExpansions → activeMods）。
        //   RD2011 用它比较 loadAfter/loadBefore；遮蔽胜者判定用 RankFor(mods)（= 本次扫描子集内的序位）。
        var loadOrder = LoadOrder.Build(env, cat, active, LoadOrder.ReadKnownExpansions(env.ModsConfigPath));

        // 目标 mod：显式参数 > 工作区里含 Source\*.csproj 的目录 > 报错（只在 Target scope 下需要）
        ModInfo? target = null;
        if (scope == CheckScope.Target)
        {
            target = modArg is null ? GuessTargetMod(env, cat) : cat.Get(modArg) ?? FindByFolder(cat, modArg);
            if (target is null)
            {
                Console.Error.WriteLine($"{Codes.TargetModNotFound}: 无法确定目标 mod；请显式给出 <mod>（目录名或 packageId），"
                                        + "或改用 `--scope active`（全部启用）/ `--scope all`（全库，3971 个 mod）");
                return 5;
            }
        }

        // ★★ 索引 / defs / files 必须建自【全集扫描】，TargetModPackageId 只作【报告过滤器】
        //     —— 这是 CheckContext 的契约（Task 6 修正 ④）。
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
        //
        //   ★ 第 3 期 Task 2：Target scope 仍扫【active 全集】；Active scope 也扫 active 全集（差别在过滤器）；
        //     All scope 扫 cat.All（3971 个 mod）。
        var mods = scope == CheckScope.All ? cat.All.ToList() : cat.Active.ToList();
        if (onlyOfficial)
            mods = mods.Where(m => m.Source == ModSource.Official).ToList();

        var targetFilter = scope == CheckScope.Target ? target!.PackageId : null;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var files = DefScanner.CollectFiles(
            mods.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)));
        var scan = DefScanner.Scan(files);
        var idx = DefIndex.Build(scan.Defs);
        var ctx = new CheckContext(idx, scan.Files, cat, scan.Defs, targetFilter,
            LoadOrder: loadOrder, ModRank: loadOrder.RankFor(mods),
            // ★ Task 5a：`MayRequire` 的判据是**已启用**的 packageId（反编译实证：引擎走
            //   `ModLister.GetActiveModWithIdentifier`，硬条件是 `item.Active`）。取 `active` 而不是
            //   `mods`：`--only-official` / `--scope all` 只改**扫描**集合，与「本机启用了什么」无关。
            ActivePackageIds: new HashSet<string>(active.Select(p => p.Trim().ToLowerInvariant()), StringComparer.Ordinal));

        // ★ 第 3 期 Task 4：把 runner 持有住 —— 补丁层的实测统计要从 PatchCheck 自己身上读，
        //   不能在 CLI 里重算一遍（两套口径必然分叉）。
        var runner = CheckRunner.Default();
        var diags = runner.Run(ctx)
            .Where(d => d.Severity >= minSeverity)
            // ★ 第 3 期 Task 3：`--shadowing` 只保留「遮蔽 / 加载顺序」族（RD2011 / RD2025 / RD2026）。
            //   默认（不加开关）这族的诊断也会照常出现 —— 开关是**聚焦**而不是「打开」：
            //   否则「RD2025/RD2026 到底报了多少条」就取决于有没有记得加开关，成了假 0。
            .Where(d => !shadowingOnly || IsShadowingCode(d.Code))
            .ToList();
        // ★ Task 4：补丁层统计的唯一来源（null = 该规则因「扫描集合 ∩ 启用集合里没有补丁文件」提前返回）
        var patchCheck = runner.Checks.OfType<PatchCheck>().FirstOrDefault();
        // ★ Task 4b：跨安装副本折叠统计的唯一来源（同上，不许 CLI 重算）。
        var shadowCheck = runner.Checks.OfType<ShadowingCheck>().FirstOrDefault();
        // ★ Task 5a：父名解析统计块的唯一来源 —— 从 ParentChainCheck 自己身上读（它就是从
        //   InheritanceLayer 拿的），**CLI 绝不重算一遍**（同 Task 4/4b 的理由）。
        //   null = 该规则没跑（ctx.Defs 为空）。
        var parentCheck = runner.Checks.OfType<ParentChainCheck>().FirstOrDefault();
        var parentStats = parentCheck?.LastAnalysis;
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
                // ★ Task 2 Step 1：`--json` 必须带 scope 字段，否则消费方分不清「那是启用集还是全库」
                scope = ScopeName(scope),
                scopeExplanation = ScopeExplanation(scope),
                onlyOfficial,
                shadowingOnly,
                target = targetFilter ?? "(all in scope)",
                scanned = new { mods = mods.Count, xmlFiles = files.Count, defs = idx.TotalDefs },
                // ★ 第 3 期 Task 4：补丁求值范围。**必须显式报出来**，否则「RD2009/RD2014 = 0」
                //   到底是「没打空」还是「根本没求值」分不清 —— 这正是项目纪律里的「假 0」。
                //   求值集 = 扫描集合 ∩ 启用集合（引擎只会为具体的启用配置打补丁；见 PatchCheck 类注释）。
                patchEvaluation = new
                {
                    configuration = "scan ∩ active",
                    activeMods = active.Length,
                    note = "RD2009/RD2014 只对【启用 mod】的补丁求值（引擎 ApplyPatches 只跑 runningMods）；"
                         + "`--scope all` 的其余 3900+ 个 mod 的补丁是引擎永不执行的假想配置，未求值。"
                },
                // ★ Task 4 实测块：由 PatchCheck 自己填（null = 规则因「无补丁」提前返回）
                patchLayer = patchCheck?.LastRun is { } ps
                    ? new
                    {
                        defFiles = patchCheck.LastInputs?.DefFiles,
                        patchFiles = patchCheck.LastInputs?.PatchFiles,
                        unifiedDefNodes = ps.UnifiedDefNodes,
                        topLevelOperations = ps.TopLevelOperations,
                        defNodesBeforePatches = ps.DefNodesBeforePatches,
                        defNodesAfterPatches = ps.DefNodesAfterPatches,
                        registeredNodes = ps.RegisteredNodes,
                        tKeyRefs = ps.TKeyRefs,
                        nameAttributesAdded = ps.NameAttributesAdded,
                        customClassSkipped = ps.SkippedCustomClass,
                        elapsedMs = ps.ElapsedMs
                    }
                    : null,
                // ★ Task 4b：跨安装副本的折叠统计。**恒存在**（0 也报）——与 installReplicas 同属「防假 0」：
                //   字段缺失会让消费方分不出「没折叠」与「工具没实现」。
                //   数字全部来自 ShadowingCheck.LastAnalysis（单一口径，CLI 不重算）。
                installReplicas = new
                {
                    declarationsDropped = shadowCheck?.LastAnalysis?.Stats.InstallReplicaDeclarationsDropped ?? 0,
                    packages = shadowCheck?.LastAnalysis?.Stats.InstallReplicaPackages ?? (IReadOnlyList<string>)Array.Empty<string>(),
                    note = "同一 packageId 的多份安装（同一 mod 装两份）会各自贡献一条声明；"
                         + "跨安装目录但相对路径相同、安装根不同的声明不再计作「同 mod 内重复」（RD2026）。"
                         + "本折叠只改报告口径，不改扫描集合。"
                },
                // ★ Task 5a：`ParentName` 解析统计。**恒存在**（解析层没跑则各数为 0，
                //   且 `ran=false`）—— 字段缺失会让消费方分不出「0 条未解析」与「根本没解析」。
                //   数字全部来自 ParentChainCheck.LastAnalysis（单一口径，CLI 不重算）。
                //   口径：每条**带 ParentName、且引擎会加载**的声明（`Evaluated`）按其**直接**父名的
                //   依据分桶一次 ⇒ `withParentName == skippedByMayRequire + 四数之和`。
                //   ★ 被 `MayRequire*` 挡掉的声明引擎**连 def 都不建**（第 3 期 Task 5a IL 实证），
                //     所以它既不进四桶、也不发 RD2005 —— 旧口径把全库 902 条这类声明算成了 error。
                parentResolution = new
                {
                    ran = parentStats is not null,
                    withParentName = parentStats?.WithParentName ?? 0,
                    skippedByMayRequire = parentStats?.SkippedByMayRequire ?? 0,
                    nameSpace = parentStats?.NameSpace ?? 0,
                    viaPatchLiteral = parentStats?.ViaPatchLiteral ?? 0,
                    defNameFallback = parentStats?.DefNameFallback ?? 0,
                    unresolved = parentStats?.Unresolved ?? 0,
                    chainBroken = parentStats?.ChainBroken ?? 0,
                    // ★ Task 5a：区分「字面 Name 空间里本来就没有」与「有候选但被 MayRequire* 挡掉」。
                    //   少了这两个数，`--scope all` 的 RD2005 会比「497 − 96 = 401」多出一大批而无法归因。
                    nameSpaceBlockedByMayRequire = parentStats?.NameSpaceBlockedByMayRequire ?? 0,
                    unresolvedMayRequireBlocked = parentStats?.UnresolvedMayRequireBlocked ?? 0,
                    patchNameLiterals = parentStats?.PatchNameLiterals.Count ?? 0,
                    patchLiteralAdditions = parentStats?.PatchLiteralAdditions.Count ?? 0,
                    patchFilesScanned = parentStats?.PatchFilesScanned ?? 0,
                    patchScanElapsedMs = parentStats?.PatchScanElapsedMs ?? 0,
                    note = "依据枚举 = NameSpace | NameSpaceViaPatchLiteral | DefNameFallback | Unresolved；"
                         + "四桶只统计 Evaluated（引擎会加载）的声明：withParentName == skippedByMayRequire + 四数之和。"
                         + "RD2005 == unresolved + chainBroken（再减去成环的，环发 RD2006）。"
                         + "补丁字面量集是【有意的过近似】：只取字面量、不评估 XPath，方向是少报。"
                },
                summary,
                elapsedMs = sw.ElapsedMilliseconds,
                peakWorkingSetMb = Math.Round(
                    System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0, 1),
                diagnostics = diags.Select(d => new
                {
                    code = d.Code,
                    severity = d.Severity.ToString().ToLowerInvariant(),
                    scope = d.Scope.ToString().ToLowerInvariant(),
                    // ★ Task 2 Step 6：RD2001 的归因（Encoding | Structure）直接作为一等字段报出，
                    //   否则消费方必须去翻 evidence 才知道「是我的文件坏了还是工具读不了」
                    cause = d.Evidence is not null && d.Evidence.TryGetValue("cause", out var c) ? c : null,
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

        Console.WriteLine($"scope           {ScopeName(scope)}   （{ScopeExplanation(scope)}）");
        Console.WriteLine($"目标            {(targetFilter is null ? "(scope 内全部 mod)" : target!.ShortName + " (" + targetFilter + ")")}");
        // 扫描口径必须写明：Target scope 下永远是【全集】扫描，单目标时只是报告被过滤（避免「扫了 1 个 mod」的误读）
        Console.WriteLine($"扫描            {mods.Count} 个 mod / {files.Count} 个 XML / {idx.TotalDefs} 个 def"
                          + (targetFilter is not null ? "   （全集扫描；报告按目标过滤）" : ""));
        Console.WriteLine($"结果            error {summary.error}   warning {summary.warning}   info {summary.info}");
        // ★ Task 11：被降级的 RD3001（引用方 mod 未启用 ⇒ info）单独可见。
        //   没这一行，使用者只能看到 error 数和 info 数同时变大，分不清哪些是「未启用 mod 的噪音」、哪些是「真缺陷」。
        var downgradedRd3001 = diags.Count(d => d.Code == Codes.DefNameNotFound && d.Severity == Severity.Info);
        if (downgradedRd3001 > 0)
            Console.WriteLine($"引用降级        RD3001 中 {downgradedRd3001} 条是「引用方 mod 未启用」" 
                              + $"，已降为 info（不影响当前游戏；启用前需先补齐前置）");
        // ★ Task 4：RD2009/RD2014 的求值范围（默认 scope=active 时两者等价，`--scope all` 时不同）
        // ★ Task 4：RD2009/RD2014 的求值范围与【实测】规模 —— 数字来自补丁层自己，不是 CLI 另算的。
        //   没有这一行，「RD2009 = 0」就分不清是「没打空」还是「根本没求值」。
        Console.WriteLine(patchCheck?.LastRun is { } pl
            ? $"补丁求值        defFiles={patchCheck.LastInputs?.DefFiles} patchFiles={patchCheck.LastInputs?.PatchFiles}"
              + $" topOps={pl.TopLevelOperations} defNodes {pl.DefNodesBeforePatches}→{pl.DefNodesAfterPatches}"
              + $" NameAdded={pl.NameAttributesAdded} skipped={pl.SkippedCustomClass}（scan ∩ active）"
            : "补丁求值        未运行（scan ∩ active 里没有补丁文件）");
        // ★ Task 4b：跨安装副本折叠的可见性（防「静默消失」）。
        //   仅当真的折叠了才打（否则这一行会变成负信息）；数字一律来自 ShadowingCheck 自己。
        //   不写这行的话，使用者看到全库 RD2026 从 3,892 跌到 14 时无法知道为什么。
        var replicas = shadowCheck?.LastAnalysis?.Stats;
        if (replicas is { InstallReplicaDeclarationsDropped: > 0 })
            Console.WriteLine($"安装副本        折叠 {replicas.InstallReplicaDeclarationsDropped} 条声明"
                              + $"（{replicas.InstallReplicaPackages?.Count ?? 0} 个 packageId："
                              + string.Join(", ", replicas.InstallReplicaPackages ?? (IReadOnlyList<string>)Array.Empty<string>())
                              + "）—— 同一 packageId 的多份安装提供相同相对路径，不作为「同 mod 内重复」报告");
        // ★ Task 5a：父名解析依据的可见性（防「静默无依据」）。
        //   不写这一行，使用者就分不清「RD2005 只有 401 条」是「解析准」还是「解析层没跑」。
        //   数字一律来自 ParentChainCheck.LastAnalysis（单一口径）。
        Console.WriteLine(parentStats is { } pr
            ? $"父名解析        withParent={pr.WithParentName}（其中引擎不加载 {pr.SkippedByMayRequire} 条）"
              + $" nameSpace={pr.NameSpace} viaPatchLiteral={pr.ViaPatchLiteral} defNameFallback={pr.DefNameFallback}"
              + $" unresolved={pr.Unresolved} chainBroken={pr.ChainBroken}（RD2005 = 两者之和减环；其中 {pr.UnresolvedMayRequireBlocked} 条是"
              + $"父名有候选却被 MayRequire* 挡掉，Name 空间被挡候选共 {pr.NameSpaceBlockedByMayRequire} 条）"
              + $"  补丁字面量 {pr.PatchNameLiterals.Count} 个 distinct / {pr.PatchLiteralAdditions.Count} 处"
              + $"（扫过 {pr.PatchFilesScanned} 个补丁文件，{pr.PatchScanElapsedMs} ms）"
            : "父名解析        未运行（ctx.Defs 为空）");
        Console.WriteLine($"耗时            {sw.ElapsedMilliseconds} ms   峰值工作集 "
                          + $"{Math.Round(System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0, 1)} MB");
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
    /// ★ 语义的**唯一实现**在 RimDev.Core.Checks.CheckScopeSelector.Parse；这里只是转发，
    ///   好让 CLI 的调用点与自测用的是同一份代码（两套实现必然分叉）。
    ///   `--all` 是 `--scope active` 的兼容别名；`--scope target` 等价于默认；参数错必须报退出码 3。
    /// </summary>
    public static CheckScope ParseScope(string[] args, out string? error) =>
        CheckScopeSelector.Parse(args, out error);

    /// <summary>`--json` 里 scope 字段的唯一来源（转发）。</summary>
    public static string ScopeName(CheckScope scope) => CheckScopeSelector.Name(scope);

    /// <summary>给人看的解释（转发）。</summary>
    public static string ScopeExplanation(CheckScope scope) => CheckScopeSelector.Explanation(scope);

    /// <summary>「遮蔽/加载顺序」族的码号 —— 与 <c>--shadowing</c> 开关共用同一处定义（避免两处分叉）。</summary>
    private static bool IsShadowingCode(string code) =>
        code is Codes.DefShadowed or Codes.DefDuplicateWithinMod or Codes.LoadOrderViolation;

    public static void PrintHelp()
    {
        Console.WriteLine("rimdev check [<mod>] [options]");
        Console.WriteLine();
        Console.WriteLine("扫描范围（★ 三者语义不同，`--all` 不等于 `--scope all`）：");
        Console.WriteLine("  （默认，无 --scope）   目标 mod：扫【active 全集】但报告只保留该 mod 参与的诊断");
        Console.WriteLine("                        目标 mod 的判定：显式 <mod> > 工作区里含 Source\\*.csproj 的 mod > RD1009/退出码 5");
        Console.WriteLine("  --scope active         整个【启用】集合（全部启用 mod，报告不过滤）");
        Console.WriteLine("  --all                  【兼容别名】= `--scope active`，两者完全等价（第 2 期的写法继续可用）");
        Console.WriteLine("  --scope all            整个【全库】= 本地 Mods + 官方 Data + 全部工坊 mod（第 2 期缺失的能力）");
        Console.WriteLine("  --scope target         同「默认」");
        Console.WriteLine();
        Console.WriteLine("其它选项：");
        Console.WriteLine("  --only-official        只保留官方 6 个包（Data\\Core + 5 个 DLC）；与上面任一 scope 组合");
        Console.WriteLine("  --shadowing            只保留「遮蔽/加载顺序」族（RD2011 / RD2025 / RD2026）；默认不开也已经包含它们");
        Console.WriteLine("  --severity <级别>      error | warning | info（默认 info）。未知或缺值 → 退出码 3，不静默降级");
        Console.WriteLine("  --json                 机器可读输出（含 scope / summary / 每条诊断的 cause 与 location）");
        Console.WriteLine("  --help, -h             打印本帮助（退出码 0）");
        Console.WriteLine();
        Console.WriteLine("退出码：0 无 error；2 有 error；3 参数错；4 环境不满足（缺游戏根/Managed）；5 目标 mod 找不到");
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
