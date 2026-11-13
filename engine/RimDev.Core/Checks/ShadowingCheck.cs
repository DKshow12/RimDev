using RimDev.Core.Catalog;
using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;

namespace RimDev.Core.Checks;

/// <summary>
/// ★ 一次 scope 内「重复声明」的分组统计。<b>全是顺序无关的量</b> —— 它们只回答
/// 「有多少组 <c>(DefType, defName)</c> 被声明了多次」，不掺入胜者判定，所以可以与历史探针直接对账。
///
/// ★★ <b>口径（第 3 期 Task 4b 改正）：这里所有量都是【已折叠跨安装副本之后】的口径。</b>
/// 不写这句，下一个会话看到全库 RD2026 = 14（修复前 3,892）会把它当成回归
/// （详见 <see cref="InstallReplicaCollapse"/>）。
///
/// ★ 口径必须写清楚（本项目历史上多次因口径混淆得出错误结论）：
/// <list type="bullet">
/// <item><see cref="DuplicateGroups"/> = 声明数 ≥ 2 的 <c>(DefType, defName)</c> 组数（**不** 计 <c>Abstract="True"</c>）；
///   计数前先做一次 <see cref="InstallReplicaCollapse"/>，折叠后 <c>Kept.Count &lt; 2</c> 的组**根本不是重复组**。</item>
/// <item><see cref="GroupsInSingleMod"/> / <see cref="GroupsAcrossMods"/> = 上面这些组按「贡献声明的 mod 个数」
///   分成两类：1 个 mod ⇒ 前者；≥2 个 mod ⇒ 后者。二者之和**恒等于** <see cref="DuplicateGroups"/>。</item>
/// <item><see cref="GroupsWithAnyWithinModDup"/> = 组内**某个 mod 自己**就声明了 ≥ 2 次（与上一对不是同一个划分：
///   一个组可以既跨 mod、又有 mod 内部重复）。这是任务书说的「同 mod 内」最自然的口径。</item>
/// <item><see cref="WithinModGroups"/> = <c>(mod, DefType, defName)</c> 三元组里声明数 ≥ 2 的个数 = **RD2026 的条数**。</item>
/// <item><see cref="CrossModLosingDeclarations"/> = 在跨 mod 竞争里落败的声明数 = **RD2025 的条数**。</item>
/// <item><see cref="TotalDeclarations"/> / <see cref="DeclarationsInDuplicateGroups"/> 同样是折叠**之后**的数
///   （与上面各量保持同一个口径；“索引里的原始行数”是另一回事，要去问 <c>DefIndex.TotalDefs</c>）。</item>
/// <item><see cref="InstallReplicaDeclarationsDropped"/> / <see cref="InstallReplicaPackages"/> = 本次折叠掉多少条声明、
///   涉及哪些 packageId。★ 它们**必须恒可读**（不折叠时是 0 / 空表），因为 `check --json` 的
///   <c>installReplicas</c> 块要靠它们防「字段缺失 = 假 0」。</item>
/// </list>
/// </summary>
public sealed record ShadowingStats(
    int DuplicateGroups,
    int GroupsInSingleMod,
    int GroupsAcrossMods,
    int GroupsWithAnyWithinModDup,
    int WithinModGroups,
    int SameModLosingDeclarations,
    int CrossModLosingDeclarations,
    int TotalDeclarations,
    int DeclarationsInDuplicateGroups,
    // ★ 第 3 期 Task 4b 追加（加在末尾 = 既有位置调用不受影响）：跨安装副本折叠的产物。
    int InstallReplicaDeclarationsDropped = 0,
    IReadOnlyList<string>? InstallReplicaPackages = null);

/// <summary>
/// 遮蔽与加载顺序规则族（第 3 期 Task 3）：
/// <list type="bullet">
/// <item><b>RD2026</b>（error）—— 同一个 mod 内同一 <c>(DefType, defName)</c> 被声明多次；</item>
/// <item><b>RD2025</b>（info）—— 跨 mod 落败的声明（大多数是合法的覆盖写法）；</item>
/// <item><b>RD2011</b>（info）—— <c>loadAfter</c>/<c>loadBefore</c> 与实际加载顺序矛盾（实现在
///   <see cref="LoadOrder.CheckOrderConsistency"/>，本类只负责按报告过滤器转发）。</item>
/// </list>
///
/// <para>
/// ★★ 胜者判定键 = <c>(modLoadOrder, folderIndexInDescendingOrder)</c> 的**字典序，小者赢**。
/// 依据：引擎枚举时用 <c>dict.TryAdd(相对键, file)</c> **先到先得**，而文件夹列表按优先级**降序**排，
/// 所以 index 0 赢；跨 mod 时先加载的 mod 先 <c>TryAdd</c>，所以 mod 序位小的赢。
/// </para>
///
/// <para>
/// ★★ 去重键 = **相对该文件夹根**的路径，且**每个 mod 一份字典**
/// （<see cref="DefFileEnumerator.Enumerate"/> 已经是这个语义，本规则直接复用它的产物 ——
/// <see cref="RawDef"/> 经由同一条 <c>CollectFiles</c> 链路产生）。
/// <b>因此同一 mod 内 <c>&lt;mod&gt;\1.6\Defs\A.xml</c> 与 <c>&lt;mod&gt;\Defs\A.xml</c>
/// 的相对键都是 <c>Defs\A.xml</c>，是同一个键、不会同时成为候选</b>
/// —— 任务书 §2.2 的结论句「是两个不同的键、会同时进入候选」与它自己上一句给的去重键**互相矛盾**，
/// 且与已被用例钉住的 <see cref="DefFileEnumerator"/> 行为冲突（见 LoadFolderCases 的
/// 「相对键去重（list[0] 先到先得）」用例）。本实现按**去重键那一句 + 已有实测行为**走，
/// 差异记在交付报告里。
/// 真正会同时进入候选的是「相对键**不同**」的同名 def，例如
/// <c>&lt;mod&gt;\1.6\Defs\A.xml</c> 与 <c>&lt;mod&gt;\1.6\Defs\B.xml</c>（两条都声明 X）——
/// 这正是 RD2026 的主要来源。
/// </para>
///
/// <para>
/// ★★ <b>Task 4b：统计与诊断的口径都是「已折叠跨安装副本之后」的重复组。</b>
/// 同一 packageId 可以在本机装两份（工坊目录不同、内容相同 —— 实测 Combat Extended ×2），
/// 它们会被 <see cref="ModCatalog.Build"/> 归并成「同一个 mod」，于是同一份内容被当成
/// 「同 mod 内重复声明」报了两次（全库实测 3,878 条假 error）。折叠规则与判别键见
/// <see cref="InstallReplicaCollapse"/>（—— 判别键必须是 **安装根不同**，不能只看相对路径）。
/// 本折叠**只改报告口径**，不改扫描集合（决策 (b)；「只评估一份安装」本期明确不做）。
/// </para>
/// <para>
/// ★ 为什么 RD2026 是 error、RD2025 是 info：同一个 mod 自己重复声明是**作者失误**
/// （引擎只会用其中一个，另一份静默消失）；跨 mod 的遮蔽是**生态常态**（故意覆盖原版内容），
/// 把后者报成 error 会让 `--scope all` 一次刷出近万条噪音，使用者会直接不看报告。
/// </para>
/// </summary>
public sealed class ShadowingCheck : ICheck
{
    public string Id => Codes.DefShadowed;

    public IEnumerable<Diagnostic> Run(CheckContext ctx)
    {
        // ① loadAfter/loadBefore 与启用顺序矛盾（RD2011 info）。
        foreach (var d in RunLoadOrderConsistency(ctx)) yield return d;

        // ② 同名 (DefType, defName) 的胜者/落败者（RD2025 info / RD2026 error）。
        //    ★ Task 4b：这里把分析结果【留在实例上】供 CLI 读（与 PatchCheck.LastRun 同一先例）。
        //    为什么不能让 CLI 自己再算一遍「折叠了多少条」：两套口径必然分叉，而
        //    `check --json` 的 installReplicas 块正是要防「字段缺失 = 假 0」的那个数字。
        var analysis = Analyze(ctx);
        LastAnalysis = analysis;
        foreach (var d in analysis.Diagnostics) yield return d;
    }

    /// <summary>
    /// 本次跑完后遮蔽分析的**完整产物**（诊断 + 折叠统计），由 <see cref="Analyze"/> 自己算出。
    ///
    /// ★ 为什么要有它（与 <c>PatchCheck.LastRun</c> 同一个理由）：`check` 要把
    /// 「跨安装副本折叠了多少条声明 / 涉及哪些 packageId」打进报告，而这个数字必须与规则内部
    /// 实际用的口径**逐字一致**。CLI 自己重算一套 = 第二套口径（本项目历史上因此分叉过）。
    /// nil = 本规则还没跑过（不是「折叠了 0 条」）。
    /// </summary>
    public ShadowingAnalysis? LastAnalysis { get; private set; }

    private static IEnumerable<Diagnostic> RunLoadOrderConsistency(CheckContext ctx)
    {
        var order = ctx.LoadOrder;
        if (order is null) yield break;   // 没给加载序：RD2011 的命题不成立，不猜

        foreach (var d in order.CheckOrderConsistency(ctx.TargetModPackageId))
        {
            // ★ 报告范围必须与【本次扫描的 mod 集合】一致（与 LoadFolderCheck 用 ctx.Files 定界同理）。
            //   漏了这条会在 `check --scope active --only-official` 上泄漏：只扫了官方 6 包，
            //   却把 3 条关于 brrainz.harmony 等第三方 mod 的 RD2011 也报出来（实测就是这么发现的）。
            //   ModRank 就是本次 scope 内 packageId → 序位的映射，拿它当“在本次扫描范围内”的判据。
            var declaredBy = d.ModPackageId;
            if (ctx.ModRank is not null
                && (declaredBy is null || !ctx.ModRank.ContainsKey(declaredBy))) continue;

            // 报告过滤器：目标 mod 必须参与（它自己声明、或它就是被指向的那一方）。
            if (ctx.TargetModPackageId is not null
                && !string.Equals(declaredBy, ctx.TargetModPackageId, StringComparison.OrdinalIgnoreCase)
                && !(d.Evidence is not null
                     && d.Evidence.TryGetValue("declaredTarget", out var t)
                     && string.Equals(t, ctx.TargetModPackageId, StringComparison.OrdinalIgnoreCase)))
                continue;
            yield return d;
        }
    }

    /// <summary>分析结果（诊断 + 顺序无关的分组统计）。</summary>
    public sealed record ShadowingAnalysis(IReadOnlyList<Diagnostic> Diagnostics, ShadowingStats Stats);

    private sealed record Ranked(RawDef Def, ModInfo? Mod, int Rank, int FolderIndex, string TieBreak);

    /// <summary>
    /// 对 <c>(DefType, defName)</c> 求胜者与全部落败者并产出诊断。
    ///
    /// ★ 统计口径：<see cref="ShadowingStats"/> 永远对**整个 scope** 计算（不按目标 mod 过滤），
    ///   而诊断按 <see cref="CheckContext.TargetModPackageId"/> 过滤 —— 「量到的数字」与「报出来的条目」
    ///   是两件事，混在一起就会像本项目历史上的 `filesParsed` 一样给出假 0。
    /// </summary>
    public static ShadowingAnalysis Analyze(CheckContext ctx)
    {
        var rank = ctx.ModRank ?? FallbackRank(ctx.Catalog.All);
        int RankOf(string pkg) => rank.TryGetValue(pkg, out var r) ? r : int.MaxValue;

        var folderCache = new Dictionary<(string, string), int>();
        var diags = new List<Diagnostic>();

        int duplicateGroups = 0, singleModGroups = 0, acrossModsGroups = 0, anyWithinModDup = 0;
        int withinModGroups = 0, sameModLosers = 0, crossModLosers = 0;
        int totalDecls = 0, declsInDupGroups = 0;
        var replicaDropped = 0;
        var replicaPackages = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ((defType, defName), allDecls) in ctx.Index.ByTypeAndDefName)
        {
            // ★★ Task 4b：**先折叠跨安装副本**，再拿 Kept 算胜者/落败者/分组统计。
            //   同一 packageId 装了两份（本机 CE 实测）时，两份安装的相对路径完全相同，
            //   于是“(mod, type, defName) 三元组里声明 ≥ 2 次”会在**同一份内容**上各算一次
            //   → 一次无害双装被放大成数千条 error。折叠后 Kept.Count < 2 的组根本不是重复组。
            //   （为什么不能只按 (pkg, rel) 判：详见 InstallReplicaCollapse 的类注释。）
            var collapse = InstallReplicaCollapse.Collapse(allDecls, ctx.Catalog);
            if (collapse.DeclarationsDropped > 0)
            {
                replicaDropped += collapse.DeclarationsDropped;
                foreach (var p in collapse.Packages) replicaPackages.Add(p);
            }

            var defs = collapse.Kept;

            totalDecls += defs.Count;
            if (defs.Count < 2) continue;

            duplicateGroups++;
            declsInDupGroups += defs.Count;

            // 每个声明算出判定键：(modLoadOrder, folderIndex) 字典序 ⇒ Rank 小者赢，同 Rank 时 FolderIndex 小者赢。
            var ranked = new List<Ranked>(defs.Count);
            foreach (var d in defs)
            {
                ctx.Catalog.TryGet(d.ModPackageId, out var mod);
                var fi = mod is null ? int.MaxValue : FolderIndexOf(mod, d.FileAbs, folderCache);
                ranked.Add(new Ranked(d, mod, RankOf(d.ModPackageId), fi, $"{d.FileRelToMod}:{d.Line}"));
            }

            var winner = ranked
                .OrderBy(r => r.Rank)
                .ThenBy(r => r.FolderIndex)
                .ThenBy(r => r.TieBreak, StringComparer.Ordinal)
                .First();

            var distinctMods = ranked.Select(r => r.Def.ModPackageId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (distinctMods == 1) singleModGroups++; else acrossModsGroups++;

            // 「某个 mod 自己声明了 ≥2 次」
            var withinModDup = ranked
                .GroupBy(r => r.Def.ModPackageId, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= 2)
                .ToList();
            if (withinModDup.Count > 0) anyWithinModDup++;
            withinModGroups += withinModDup.Count;

            // 报告过滤器：目标 mod 不参与这一组 ⇒ 既不报 RD2025 也不报 RD2026。
            var involved = ctx.TargetModPackageId is null
                || ranked.Any(r => string.Equals(r.Def.ModPackageId, ctx.TargetModPackageId, StringComparison.OrdinalIgnoreCase));

            // ── RD2026：同一个 mod 内重复声明（每个 (mod, type, defName) 一条，列出全部声明点）──
            foreach (var g in withinModDup)
            {
                var sites = g.OrderBy(r => r.Rank).ThenBy(r => r.FolderIndex)
                    .ThenBy(r => r.TieBreak, StringComparer.Ordinal).ToList();
                var head = sites[0];

                if (involved)
                {
                    diags.Add(new Diagnostic(
                        Codes.DefDuplicateWithinMod, Severity.Error, Scope.Offline,
                        $"mod `{head.Def.ModPackageId}` 内 {defType} 的 defName `{defName}` 被声明 {sites.Count} 次"
                        + $"（同一个 mod 里只会生效一份，其余的**静默消失**）",
                        "删掉多余的那份，或让其中一份用 ParentName 继承 —— 同一个 mod 内不要重复声明同名 def",
                        FileAbs: head.Def.FileAbs, Line: head.Def.Line,
                        ModPackageId: head.Def.ModPackageId, DefName: defName, DefType: head.Def.DefType,
                        Suggestions: sites.Skip(1)
                            .Select(r => $"{r.Def.ModPackageId}:{r.Def.FileRelToMod}:{r.Def.Line}").ToList(),
                        Evidence: new Dictionary<string, string>
                        {
                            ["kind"] = "withinModDuplicate",
                            ["declarations"] = string.Join(" | ",
                                sites.Select(r => $"{r.Def.ModPackageId}/{r.Def.FileRelToMod}:{r.Def.Line}")),
                            ["winningSite"] = $"{head.Def.FileRelToMod}:{head.Def.Line}",
                            ["rank"] = $"{head.Rank}",
                            ["folderIndex"] = head.FolderIndex == int.MaxValue ? "-" : head.FolderIndex.ToString()
                        }));
                }
            }

            // ── RD2025：跨 mod 落败（每个落败声明一条，与胜者成对）──
            foreach (var r in ranked)
            {
                if (ReferenceEquals(r.Def, winner.Def)) continue;
                var sameMod = string.Equals(r.Def.ModPackageId, winner.Def.ModPackageId, StringComparison.OrdinalIgnoreCase);
                if (sameMod) { sameModLosers++; continue; }
                crossModLosers++;

                if (!involved) continue;

                var winnerName = winner.Mod?.ShortName ?? winner.Def.ModPackageId;
                var sameName = string.Equals(winner.Def.DefName, r.Def.DefName, StringComparison.Ordinal);
                diags.Add(new Diagnostic(
                    Codes.DefShadowed, Severity.Info, Scope.Offline,
                    $"{defType} `{defName}` 被遮蔽：胜者 {winner.Def.ModPackageId}"
                    + $"（来自 mod `{winnerName}`）@ {winner.Def.FileRelToMod}:{winner.Def.Line}；"
                    + $"败者 {r.Def.ModPackageId} @ {r.Def.FileRelToMod}:{r.Def.Line}"
                    + $"；两处 def 名 {(sameName ? "相同" : "不同")}（`{winner.Def.DefName}` vs `{r.Def.DefName}`）",
                    "跨 mod 的同名 def 遮蔽通常是**合法**的（后加载者覆盖先加载者）。"
                    + "只有当败者含有独占内容、且这些内容被别处引用时才是缺陷；必要时调整 ModsConfig.xml 的顺序",
                    FileAbs: r.Def.FileAbs, Line: r.Def.Line,
                    ModPackageId: r.Def.ModPackageId, DefName: r.Def.DefName, DefType: r.Def.DefType,
                    Suggestions: new[] { $"胜者：{winner.Def.ModPackageId} @ {winner.Def.FileAbs}:{winner.Def.Line}" },
                    Evidence: new Dictionary<string, string>
                    {
                        ["kind"] = "crossModShadowing",
                        ["winnerFile"] = winner.Def.FileAbs,
                        ["winnerLine"] = winner.Def.Line.ToString(),
                        ["winnerMod"] = winner.Def.ModPackageId,
                        ["winnerModName"] = winnerName,
                        ["winnerSource"] = winner.Mod?.Source.ToString() ?? "(unknown)",
                        ["winnerOrigin"] = (ctx.LoadOrder?.RankOf(winner.Def.ModPackageId) is int wr
                            ? ctx.LoadOrder.Entries[wr].Origin.ToString() : "NotInLoadOrder"),
                        ["loserFile"] = r.Def.FileAbs,
                        ["loserLine"] = r.Def.Line.ToString(),
                        ["loserMod"] = r.Def.ModPackageId,
                        ["defType"] = defType,
                        ["defName"] = defName,
                        ["winnerKey"] = $"(rank={winner.Rank}, folder={winner.FolderIndex})",
                        ["loserKey"] = $"(rank={r.Rank}, folder={r.FolderIndex})"
                    }));
            }
        }

        var stats = new ShadowingStats(
            DuplicateGroups: duplicateGroups,
            GroupsInSingleMod: singleModGroups,
            GroupsAcrossMods: acrossModsGroups,
            GroupsWithAnyWithinModDup: anyWithinModDup,
            WithinModGroups: withinModGroups,
            SameModLosingDeclarations: sameModLosers,
            CrossModLosingDeclarations: crossModLosers,
            TotalDeclarations: totalDecls,
            DeclarationsInDuplicateGroups: declsInDupGroups,
            // ★ Task 4b：跨安装副本的折叠统计（恒可读，不折叠时 0 / 空表）。
            InstallReplicaDeclarationsDropped: replicaDropped,
            InstallReplicaPackages: replicaPackages.ToList());

        return new ShadowingAnalysis(diags, stats);
    }

    /// <summary>没有 <c>ModRank</c>（用例/外部调用）时的退化序位：按目录表自身的顺序。不静默失效，只是口径更粗。</summary>
    private static Dictionary<string, int> FallbackRank(IReadOnlyList<ModInfo> all)
    {
        var map = new Dictionary<string, int>(all.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < all.Count; i++) map[all[i].PackageId] = i;
        return map;
    }

    /// <summary>
    /// 该文件来自哪个加载文件夹（= 引擎 <c>foldersToLoadDescendingOrder</c> 的下标）。
    /// ★ 必须取**最靠前**的那个：相对键去重是「先到先得」，一个文件只可能从它的胜者文件夹进来。
    /// </summary>
    private static int FolderIndexOf(ModInfo mod, string fileAbs, Dictionary<(string, string), int> cache)
    {
        var key = (mod.PackageId, fileAbs);
        if (cache.TryGetValue(key, out var cached)) return cached;

        var folders = mod.Resolution?.DescendingOrder ?? mod.LoadFolders;
        var result = int.MaxValue;
        for (var i = 0; i < folders.Count; i++)
        {
            var f = folders[i];
            if (string.IsNullOrEmpty(f)) continue;
            if (fileAbs.Length > f.Length
                && fileAbs.StartsWith(f, StringComparison.OrdinalIgnoreCase)
                && (fileAbs[f.Length] == Path.DirectorySeparatorChar || fileAbs[f.Length] == Path.AltDirectorySeparatorChar))
            {
                result = i;
                break;
            }
        }

        cache[key] = result;
        return result;
    }
}
