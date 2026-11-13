using System.Text.Json;
using RimDev.Core.Catalog;
using RimDev.Core.Checks;
using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;

namespace RimDev.Cli.Commands;

/// <summary>
/// `rimdev sim [&lt;mod&gt;] [--scope target|active|all] [--all] [--json] [--help]`
/// —— 「加载结果摘要」最小版（第 3 期 Task 6 Step 5）。
///
/// ★★ 作用域语义（**与 `check` 的 `--all` 刻意不同，必须记住**）：
///   <list type="bullet">
///   <item><b>默认</b> = `--scope active`（任务书要求「默认作用域 active」）；</item>
///   <item><b>`--all`（sim）</b> = **全库**（`--scope all`）。`check` 里 `--all` 是 `--scope active`
///     的兼容别名，<b>sim 不是</b> —— 任务书要求「`--all` 时**必须显式标注已求值/未求值范围**」，
///     而那个边界（补丁只在「扫描集合 ∩ 启用集合」求值）**只有在全库时才存在**；
///     把 `--all` 也做成 active 的别名，那条要求就永远触发不到（等于把它静默掉）。</item>
///   <item>`&lt;mod&gt;` = `--scope target` + 该 mod 作报告过滤器（诊断只留它参与的）。</item>
///   </list>
///
/// ★★ 摘要的每一项都能定位到 `文件:行`（正文给总数 + 前 8 条；`--json` 给全量诊断）。
///
/// ★★ `--json` 的**字节稳定性**（Step 6）：严格按 `01` §4.8 的 schema，且
///   **除 `run.id` / `run.startedAt` / `run.durationMs` 外不含任何时间字段** ——
///   各统计块的 `elapsedMs` 只出现在**人读输出**里（`--json` 里刻意剔除）。
///   理由：任务书要求「同一份输入跑两次，两份 `--json` 逐字节相同」，
///   而任何 `Stopwatch` 取值都必然不同；把它们留在 `--json` 里，那条要求就不可能成立。
/// </summary>
public static class SimCommand
{
    public static int Run(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h") || args.Contains("--?"))
        {
            PrintHelp();
            return 0;
        }

        var modArg = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
        var scope = ParseScope(args, out var scopeError);
        if (scopeError is not null) { Console.Error.WriteLine(scopeError); return 3; }
        var json = args.Contains("--json");

        GameEnv env;
        try { env = GameEnvProbe.Discover(); }
        catch (GameEnvProbeException ex) { Console.Error.WriteLine("RD1001: " + ex.Message); return 4; }

        var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
        var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;
        var loadOrder = LoadOrder.Build(env, cat, active, LoadOrder.ReadKnownExpansions(env.ModsConfigPath));

        ModInfo? target = null;
        if (modArg is not null)
        {
            target = cat.Get(modArg) ?? cat.All.FirstOrDefault(m =>
                string.Equals(m.Locator, modArg, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                Console.Error.WriteLine($"{Codes.TargetModNotFound}: 找不到 mod {modArg}（目录名或 packageId）");
                return 5;
            }
        }

        var mods = scope == CheckScope.All ? cat.All.ToList() : cat.Active.ToList();
        var targetFilter = target?.PackageId;
        if (scope == CheckScope.Target && target is null)
        {
            // 无 <mod> 参数又指定了 target：退化成 active（**显式说明**，不静默）
            scope = CheckScope.Active;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ── 阶段 1：枚举 ─────────────────────────────────────────────────────────────
        var tEnum = System.Diagnostics.Stopwatch.StartNew();
        var files = DefScanner.CollectFiles(
            mods.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)));
        tEnum.Stop();

        // ── 阶段 2：解析（def 头） ───────────────────────────────────────────────────
        var tParse = System.Diagnostics.Stopwatch.StartNew();
        var scan = DefScanner.Scan(files);
        var idx = DefIndex.Build(scan.Defs);
        tParse.Stop();

        var ctx = new CheckContext(idx, scan.Files, cat, scan.Defs, targetFilter,
            LoadOrder: loadOrder, ModRank: loadOrder.RankFor(mods),
            ActivePackageIds: new HashSet<string>(active.Select(p => p.Trim().ToLowerInvariant()),
                StringComparer.Ordinal));

        // ── 阶段 3–5：补丁 / 合并 / 引用解析（由各自的规则承载，CLI 不重算） ──────────
        var runner = new CheckRunner()
            .Add(new PatchCheck())
            .Add(new ParentChainCheck())
            .Add(new ShadowingCheck())
            .Add(new ReferenceCheck());
        var diags = runner.Run(ctx).ToList();
        var patch = runner.Checks.OfType<PatchCheck>().FirstOrDefault();
        var parent = runner.Checks.OfType<ParentChainCheck>().FirstOrDefault();
        var shadow = runner.Checks.OfType<ShadowingCheck>().FirstOrDefault();
        var reference = runner.Checks.OfType<ReferenceCheck>().FirstOrDefault();
        sw.Stop();

        var byCode = diags.GroupBy(d => d.Code, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var load = new
        {
            declarations = reference?.LastSet?.Declarations ?? 0,
            effectiveDefNames = reference?.LastSet?.EffectiveDefNames ?? 0,
            effectiveDeclarations = reference?.LastSet?.EffectiveDeclarations ?? 0,
            abstractDeclarations = reference?.LastSet?.AbstractDeclarations ?? 0,
            inheritedDefNameDeclarations = reference?.LastSet?.InheritedDefNameDeclarations ?? 0,
            unnamedDeclarations = reference?.LastSet?.UnnamedDeclarations ?? 0,
            skippedByMayRequire = reference?.LastSet?.SkippedByMayRequire ?? 0,
            shadowedDeclarations = reference?.LastSet?.ShadowedDeclarations ?? 0,
            classAttrDeclarations = reference?.LastSet?.ClassAttrDeclarations ?? 0,
            unresolvedTypeDeclarations = reference?.LastSet?.UnresolvedTypeDeclarations ?? 0,
        };

        var parents = parent?.LastAnalysis;
        var refs = reference?.LastStats;
        var shadowStats = shadow?.LastAnalysis?.Stats;
        var patchStats = patch?.LastRun;

        static List<SampleRow> Sample(IEnumerable<Diagnostic> ds, int n) => ds.Take(n)
            .Select(d => new SampleRow(d.FileAbs, d.Line, d.ModPackageId, d.DefName, d.DefType, d.Message))
            .ToList();

        var samples = new
        {
            parentUnresolved = Sample(diags.Where(d => d.Code == Codes.ParentNotFound), 8),
            danglingReferences = Sample(diags.Where(d => d.Code == Codes.DefNameNotFound), 8),
            skippedByMayRequire = Sample(diags.Where(d => d.Code == Codes.MayRequireModMissing), 8),
            shadowed = Sample(diags.Where(d => d.Code is Codes.DefShadowed or Codes.DefDuplicateWithinMod), 8),
            customLoaderBoundary = (refs?.CustomLoaderSamples ?? (IReadOnlyList<string>)Array.Empty<string>())
                .Take(8).ToList(),
        };

        var runId = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss") + "-" +
                    Guid.NewGuid().ToString("N")[..4];
        var startedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ssK");

        var error = diags.Count(d => d.Severity == Severity.Error);
        var warning = diags.Count(d => d.Severity == Severity.Warning);
        var info = diags.Count(d => d.Severity == Severity.Info);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = "1.0",
                tool = new { name = "rimdev", version = "0.2.0" },
                run = new
                {
                    command = "sim",
                    id = runId,
                    startedAt,
                    durationMs = sw.ElapsedMilliseconds,
                    args = new
                    {
                        mod = modArg,
                        scope = CheckScopeSelector.Name(scope),
                        all = args.Contains("--all"),
                    },
                },
                env = new
                {
                    gameRoot = env.GameRoot,
                    targetVersion = env.TargetVersion,
                    activeModCount = active.Length,
                },
                targets = target is null
                    ? Array.Empty<object>()
                    : new object[] { new { packageId = target.PackageId, name = target.DisplayName, path = target.RootPath } },
                summary = new
                {
                    error,
                    warning,
                    info,
                    byCode,
                    byScope = new
                    {
                        offline = diags.Count(d => d.Scope == Scope.Offline),
                        needsGame = diags.Count(d => d.Scope == Scope.NeedsGame),
                    },
                    indexStats = new
                    {
                        modsScanned = mods.Count,
                        xmlFiles = files.Count,
                        defsIndexed = idx.TotalDefs,
                        filesFailed = scan.FilesFailed,
                        filesSkippedByCache = 0,
                    },
                    loadResult = load,
                    // ★ 第 3 期 Task 6b：运行期生成 def 的**建模自报**（口径本身要能被审计）。
                    //   `unmodeledGenerators` 非空 = 有生成器入口没被规则覆盖（绝不静默）。
                    generatedDefs = reference?.LastSet is null ? null : new
                    {
                        generatedNames = reference.LastSet.GeneratedDefNames,
                        evidenceFailures = reference.LastSet.GeneratorModel?.EvidenceFailures ?? 0,
                        discoveredGenerators = reference.LastSet.GeneratorModel?.DiscoveredGenerators
                            ?? (IReadOnlyList<string>)Array.Empty<string>(),
                        unmodeledGenerators = reference.LastSet.GeneratorModel?.UnmodeledGenerators
                            ?? (IReadOnlyList<string>)Array.Empty<string>(),
                        rules = (reference.LastSet.GeneratorModel?.Rules ?? (IReadOnlyList<GeneratedRuleReport>)Array.Empty<GeneratedRuleReport>())
                            .Select(r => new
                            {
                                rule = r.RuleId,
                                generator = r.Generator,
                                method = r.Method,
                                producedDefType = r.ProducedDefType,
                                nameShape = r.NameShape,
                                baseSpec = r.BaseSpec,
                                ilEvidenceOk = r.IlEvidenceOk,
                                evidence = r.Evidence,
                                baseDefsMatched = r.BaseDefsMatched,
                                baseBDefsMatched = r.BaseBDefsMatched,
                                namesGenerated = r.NamesGenerated,
                            }),
                    },
                    referenceResolution = refs is null ? null : new
                    {
                        declarationsTraversed = refs.DeclarationsTraversed,
                        declarationsSkippedByMayRequire = refs.DeclarationsSkippedByMayRequire,
                        declarationsUnknownType = refs.DeclarationsUnknownType,
                        referenceSites = refs.ReferenceSites,
                        resolvedExact = refs.ResolvedExact,
                        resolvedViaFullNameKey = refs.ResolvedViaFullNameKey,
                        resolvedViaAssignableTypes = refs.ResolvedViaAssignableTypes,
                        danglingReferences = refs.MissingInScope,
                        reported = refs.Reported,
                        customLoaderSubtreesSkipped = refs.CustomLoaderSubtreesSkipped,
                        customLoaderSamples = refs.CustomLoaderSamples,
                        unknownFieldElements = refs.UnknownFieldElements,
                        abstractElementWithoutClass = refs.AbstractElementWithoutClass,
                        classDispatchHits = refs.ClassDispatchHits,
                        classDispatchMisses = refs.ClassDispatchMisses,
                        parityFailures = refs.ParityFailures,
                    },
                    parentResolution = parents is null ? null : new
                    {
                        withParentName = parents.WithParentName,
                        skippedByMayRequire = parents.SkippedByMayRequire,
                        nameSpace = parents.NameSpace,
                        viaPatchLiteral = parents.ViaPatchLiteral,
                        defNameFallback = parents.DefNameFallback,
                        unresolved = parents.Unresolved,
                        chainBroken = parents.ChainBroken,
                    },
                    shadowing = shadowStats is null ? null : new
                    {
                        totalDeclarations = shadowStats.TotalDeclarations,
                        duplicateGroups = shadowStats.DuplicateGroups,
                        sameModLosingDeclarations = shadowStats.SameModLosingDeclarations,
                        crossModLosingDeclarations = shadowStats.CrossModLosingDeclarations,
                        installReplicaDeclarationsDropped = shadowStats.InstallReplicaDeclarationsDropped,
                    },
                    // ★ 补丁求值边界（防「没跑 ⇒ 报 0」的假 0）：与 Task 4 的 patchEvaluation 同一口径。
                    patchEvaluation = new
                    {
                        configuration = "scan ∩ active",
                        activeMods = active.Length,
                        defFiles = patch?.LastInputs?.DefFiles ?? 0,
                        patchFiles = patch?.LastInputs?.PatchFiles ?? 0,
                        ran = patchStats is not null,
                        topLevelDefNodesBefore = patchStats?.DefNodesBeforePatches ?? 0,
                        topLevelDefNodesAfter = patchStats?.DefNodesAfterPatches ?? 0,
                        // ★ 这个差值就是「SimulatedDefSet（未打补丁的声明集合）看不到的东西」——
                        //   为 0 时该边界可证为空。
                        topLevelDefNodesAddedByPatches =
                            (patchStats?.DefNodesAfterPatches ?? 0) - (patchStats?.DefNodesBeforePatches ?? 0),
                        nameAttributesAdded = patchStats?.NameAttributesAdded ?? 0,
                        customClassSkipped = patchStats?.SkippedCustomClass ?? 0,
                        note = "补丁只在【扫描集合 ∩ 启用集合】求值（引擎 ApplyPatches 只跑 runningMods）；"
                             + "`--all` 时其余 mod 的补丁是引擎永不执行的假想配置，未求值。"
                             + "SimulatedDefSet 吃的是**未打补丁**的声明集合，差值见 topLevelDefNodesAddedByPatches。",
                    },
                    // ★ 时刻字段刻意不进 --json（见类注释：Step 6 要求两次运行逐字节相同）
                    timingNote = "elapsedMs 只在人读输出里给出；--json 刻意剔除所有 Stopwatch 取值以保证"
                               + "「同一份输入跑两次、除 run.* 外逐字节相同」。",
                },
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
                    fingerprint = d.Fingerprint,
                }),
            }, new JsonSerializerOptions { WriteIndented = true }));
            return error > 0 ? 2 : 0;
        }

        // ── 人读输出（含分阶段计时） ─────────────────────────────────────────────────
        Console.WriteLine($"scope           {CheckScopeSelector.Name(scope)}（{SimScopeExplanation(scope)}）"
                          + (modArg is not null ? $"   mod={modArg}" : ""));
        // ★ 刻意**不用** CheckScopeSelector.Explanation：那句话写的是 `check` 的别名语义（"--all 是它的兼容别名"），
        //   而 sim 的 `--all` 是**全库** —— 照抄会把使用者直接引到错的语义上。
        Console.WriteLine($"扫描            {mods.Count} 个 mod / {files.Count} 个 XML / {idx.TotalDefs} 个 def"
                          + $"（解析失败 {scan.FilesFailed}）");
        Console.WriteLine("加载结果摘要");
        Console.WriteLine($"  有效 def 名           {load.effectiveDefNames}"
                          + $"（会成为 def 的声明 {load.effectiveDeclarations} / 声明总数 {load.declarations}）");
        Console.WriteLine($"  ├ 抽象声明            {load.abstractDeclarations}");
        Console.WriteLine($"  ├ 继承来的 defName    {load.inheritedDefNameDeclarations}");
        Console.WriteLine($"  ├ 无名声明            {load.unnamedDeclarations}");
        Console.WriteLine($"  ├ 类型不可解析（mod 自定义 Def 类）{load.unresolvedTypeDeclarations}");
        Console.WriteLine($"  └ Class= 声明         {load.classAttrDeclarations}");
        Console.WriteLine($"  被遮蔽声明            {load.shadowedDeclarations}"
                          + $"（ShadowingStats：同 mod 落败 {shadowStats?.SameModLosingDeclarations ?? 0}"
                          + $" / 跨 mod 落败 {shadowStats?.CrossModLosingDeclarations ?? 0}"
                          + $" / 副本折叠 {shadowStats?.InstallReplicaDeclarationsDropped ?? 0}）");
        Console.WriteLine($"  未解析 ParentName    {parents?.Unresolved ?? 0} + 链断 {parents?.ChainBroken ?? 0}（= RD2005）");
        Console.WriteLine($"  悬空引用（RD3001）    {refs?.MissingInScope ?? 0}（报告 {refs?.Reported ?? 0}）");
        Console.WriteLine($"  被 MayRequire 跳过    {load.skippedByMayRequire}（声明层）"
                          + $" / {diags.Count(d => d.Code == Codes.MayRequireModMissing)} 条 RD2008（info）");
        Console.WriteLine($"  自定义 loader 边界    跳过子树 {refs?.CustomLoaderSubtreesSkipped ?? 0}"
                          + $"（loader 类型 {reference?.LastIndex?.CustomLoaderTypeCount ?? 0} 个，"
                          + $"其自身引用槽 {reference?.LastIndex?.CustomLoaderOwnRefSlotCount ?? 0} 个）");
        Console.WriteLine($"  引用解析              {refs?.Describe() ?? "(未运行)"}");
        var gm = reference?.LastSet?.GeneratorModel;
        Console.WriteLine($"  运行期生成 def        生成 {reference?.LastSet?.GeneratedDefNames ?? 0} 个名"
                          + $"（生成器入口 {gm?.DiscoveredGenerators.Count ?? 0} 个 / 规则 {gm?.Rules.Count ?? 0} 条"
                          + $" / IL 证据失败 {gm?.EvidenceFailures ?? 0} / 未建模入口 {gm?.UnmodeledGenerators.Count ?? 0}）");
        if (gm is not null)
            foreach (var r in gm.Rules)
                Console.WriteLine($"    [{r.RuleId}] {r.ProducedDefType} {r.NameShape} ← {r.BaseSpec}"
                                  + $"  基底 A{r.BaseDefsMatched}/B{r.BaseBDefsMatched} → 名字 {r.NamesGenerated}"
                                  + (r.IlEvidenceOk ? "" : $"  ★ IL 证据失败：{r.Evidence}"));
        if (gm is not null)
            foreach (var u in gm.UnmodeledGenerators) Console.WriteLine($"    ★ 未建模生成器入口：{u}");
        Console.WriteLine($"  补丁求值              scan ∩ active：defFiles={patch?.LastInputs?.DefFiles ?? 0}"
                          + $" patchFiles={patch?.LastInputs?.PatchFiles ?? 0}"
                          + $" 顶层 def 节点 {patchStats?.DefNodesBeforePatches ?? 0}→{patchStats?.DefNodesAfterPatches ?? 0}"
                          + $"（补丁新增 {(patchStats?.DefNodesAfterPatches ?? 0) - (patchStats?.DefNodesBeforePatches ?? 0)}）"
                          + $" NameAdded={patchStats?.NameAttributesAdded ?? 0}");
        Console.WriteLine();
        Console.WriteLine("定位（前 8 条/项）");
        PrintSamples("未解析 ParentName", samples.parentUnresolved);
        PrintSamples("悬空引用", samples.danglingReferences);
        PrintSamples("MayRequire 跳过", samples.skippedByMayRequire);
        PrintSamples("被遮蔽", samples.shadowed);
        Console.WriteLine($"  自定义 loader 边界（无逐条诊断，只有计数 + 样本）：");
        foreach (var s in samples.customLoaderBoundary) Console.WriteLine($"     {s}");
        Console.WriteLine();
        Console.WriteLine($"计时            枚举 {tEnum.ElapsedMilliseconds} ms / 解析 {tParse.ElapsedMilliseconds} ms"
                          + $" / 补丁+合并+引用解析+其它规则 {sw.ElapsedMilliseconds - tEnum.ElapsedMilliseconds - tParse.ElapsedMilliseconds} ms"
                          + $" / 合计 {sw.ElapsedMilliseconds + tEnum.ElapsedMilliseconds + tParse.ElapsedMilliseconds} ms");
        Console.WriteLine($"                引用解析自身 {refs?.ElapsedMs ?? 0} ms，补丁扫描 "
                          + $"{parents?.PatchScanElapsedMs ?? 0} ms，峰值工作集 "
                          + $"{Math.Round(System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0, 1)} MB");
        Console.WriteLine($"结果            error {error}   warning {warning}   info {info}");
        foreach (var kv in byCode) Console.WriteLine($"    [{kv.Key}] {kv.Value} 条");
        return error > 0 ? 2 : 0;
    }

    /// <summary>sim 自己的作用域说明（`--all` 在这里是**全库**，与 `check` 的别名语义不同）。</summary>
    private static string SimScopeExplanation(CheckScope scope) => scope switch
    {
        CheckScope.All => "全库（本地 Mods + 官方 Data + 全部工坊 mod）—— 注意：sim 的 --all 就是这个，不是 check 里的别名",
        CheckScope.Target => "目标 mod（扫描 active 全集 + 只报该 mod 参与的诊断）",
        _ => "全部启用 mod",
    };

    /// <summary>摘要里每一项的「文件:行」样本（Step 5 要求每一项可定位）。</summary>
    private sealed record SampleRow(string? File, int? Line, string? Mod, string? DefName, string? DefType, string Message);

    private static void PrintSamples(string title, List<SampleRow> items)
    {
        Console.WriteLine($"  {title}：{(items.Count == 0 ? "(无)" : "")}");
        foreach (var x in items)
            Console.WriteLine($"     {x.File}:{x.Line}  {x.Message}");
    }

    private static CheckScope ParseScope(string[] args, out string? error)
    {
        // `--all`（sim）= 全库；与 check 的兼容别名语义**刻意不同**（见类注释）。
        var hasAll = args.Contains("--all");
        var hasScope = args.Contains("--scope");
        var v = Option(args, "--scope")?.ToLowerInvariant();

        if (hasScope && v is null)
        {
            error = "未知或缺值的 --scope（可选 target|active|all）";
            return CheckScope.Active;
        }
        switch (v)
        {
            case null:
                error = null;
                return hasAll ? CheckScope.All : CheckScope.Active;
            case "target": error = null; return CheckScope.Target;
            case "active": error = null; return CheckScope.Active;
            case "all": error = null; return CheckScope.All;
            default:
                error = $"未知的 --scope: {v}（可选 target|active|all）";
                return CheckScope.Active;
        }
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("rimdev sim [<mod>] [options]");
        Console.WriteLine();
        Console.WriteLine("「加载结果摘要」：有效 def 数 / 被遮蔽数 / 未解析 ParentName / 悬空引用（RD3001）/"
                          + "MayRequire 跳过 / 自定义 loader 边界，每项给总数 + 前 8 条 文件:行。");
        Console.WriteLine();
        Console.WriteLine("扫描范围（★ `--all` 在 sim 里是【全库】，与 `check` 的 `--all`（= --scope active 的别名）不同）：");
        Console.WriteLine("  （默认，无 --scope）   --scope active：整个【启用】集合");
        Console.WriteLine("  --scope active         同上");
        Console.WriteLine("  --scope all / --all    整个【全库】（本地 Mods + 官方 Data + 全部工坊 mod）");
        Console.WriteLine("  --scope target         目标 mod（默认作用域；诊断只留该 mod 参与的）");
        Console.WriteLine("  <mod>                  等价于 --scope target + 该 mod 作过滤器");
        Console.WriteLine();
        Console.WriteLine("  --json                 按 01 §4.8 schema 输出；同一输入两次运行除 run.* 外逐字节相同");
        Console.WriteLine("  --help, -h             打印本帮助（退出码 0）");
        Console.WriteLine();
        Console.WriteLine("退出码：0 无 error；2 有 error；3 参数错；4 环境不满足；5 目标 mod 找不到");
    }
}
