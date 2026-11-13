using RimDev.Core.Catalog;
using RimDev.Core.Checks;
using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// ★★ 第 3 期 Task 26b：<b>AboutFeatureCheck 的行为验证</b>（不只是解析，是整条链路）。
///
/// <para>起因：实现完 <see cref="AboutFeatureCheck"/> 后，在真实语料上跑
/// <c>check --scope active --rules aboutFeatures</c> 得到 <b>0 条诊断</b>。</para>
///
/// <para>而独立探针（Node 正则直接读 ModsConfig + 302 个 About.xml）算出：</para>
/// <code>
/// 带 incompatibleWith 的 mod        302
/// 其中已启用的                        63
/// ★ 真实冲突（声明方与冲突方都启用）   2
///     HenTaiLoliTeam.Axolotl  ✗  Susir.MoeLotlPatch
///     brrainz.harmony         ✗  trigger.eliteraidplus
/// </code>
///
/// <para>⇒ <b>0 条是假 0</b>，检查器没生效。本文件用**合成 mod**复现最小场景，
/// 把它钉住 —— 这样即使本机语料变化，也能保证检查器本身是活的。</para>
/// </summary>
public static class Phase3Task26bCases
{
    /// <summary>造一个 ModInfo（直接调检查器，不经过磁盘）。</summary>
    private static ModInfo MakeMod(
        string pkg,
        string[]? deps = null,
        IReadOnlyDictionary<string, string[]>? depsByVer = null,
        string[]? altIds = null,
        string[]? incompatible = null,
        bool active = true)
        => new ModInfo(
            PackageId: pkg,
            DisplayName: pkg,
            RootPath: @"C:\fake\" + pkg,
            Source: ModSource.Local,
            PublishedFileId: null,
            SupportedVersions: new[] { "1.7" },
            LoadFolders: Array.Empty<string>(),
            Dependencies: deps ?? Array.Empty<string>(),
            LoadAfter: Array.Empty<string>(),
            LoadBefore: Array.Empty<string>(),
            IsActive: active,
            HasAssemblies: false,
            AboutPath: @"C:\fake\" + pkg + @"\About\About.xml",
            AboutError: null,
            Resolution: null,
            DependenciesByVersion: depsByVer,
            AlternativePackageIds: altIds,
            IncompatibleWith: incompatible);

    /// <summary>用合成目录表造一个最小 CheckContext（DefIndex/Files/Defs 都给空）。</summary>
    private static CheckContext MakeCtx(ModCatalog cat)
        => new(
            Index: DefIndex.Build(Array.Empty<RawDef>()),
            Files: Array.Empty<DefFileResult>(),
            Catalog: cat,
            Defs: Array.Empty<RawDef>(),
            TargetModPackageId: null);

    public static void Run()
    {
        T.Case("task26b ★★ 冲突：声明的冲突方已启用 ⇒ 必须报 RD3003（本轮实测漏报的正是这个）", () =>
        {
            // 复刻真实案例：brrainz.harmony 声明与 trigger.eliteraidplus 不兼容，两者都启用
            var cat = ModCatalog.FromMods(new[]
            {
                MakeMod("brrainz.harmony", incompatible: new[] { "trigger.eliteraidplus" }),
                MakeMod("trigger.eliteraidplus"),
            });
            var ctx = MakeCtx(cat);
            var diags = new AboutFeatureCheck().Run(ctx)
                .Where(d => d.Code == Codes.IncompatibleModActive).ToList();

            T.Eq(diags.Count, 1, "★ 必须报 1 条冲突 —— 0 条就是假阴");
            T.Eq(diags[0].ModPackageId, "brrainz.harmony", "归因到声明方");
        }, LayoutNeed.Any);

        T.Case("task26b ★ 冲突：冲突方未启用 ⇒ 不报（无假阳）", () =>
        {
            var cat = ModCatalog.FromMods(new[]
            {
                MakeMod("a.declares", incompatible: new[] { "b.notloaded" }),
                MakeMod("b.notloaded", active: false),
            });
            var ctx = MakeCtx(cat);
            var diags = new AboutFeatureCheck().Run(ctx)
                .Where(d => d.Code == Codes.IncompatibleModActive).ToList();
            T.Eq(diags.Count, 0, "★ 冲突方没启用就不该报");
        }, LayoutNeed.Any);

        T.Case("task26b 冲突：大小写差异也要对上（About.xml 保留原大小写，ModsConfig 全小写）", () =>
        {
            var cat = ModCatalog.FromMods(new[]
            {
                MakeMod("a.declares", incompatible: new[] { "Susir.MoeLotlPatch" }),
                MakeMod("susir.moelotlpatch"),
            });
            var ctx = MakeCtx(cat);
            var diags = new AboutFeatureCheck().Run(ctx)
                .Where(d => d.Code == Codes.IncompatibleModActive).ToList();
            T.Eq(diags.Count, 1, "★ 大小写不敏感 —— 否则真实的冲突会漏");
        }, LayoutNeed.Any);

        T.Case("task26b ★ 依赖：声明的前置没启用 ⇒ 报 RD3002", () =>
        {
            var cat = ModCatalog.FromMods(new[]
            {
                MakeMod("a.needs", deps: new[] { "b.missing" }),
            });
            var ctx = MakeCtx(cat);
            var diags = new AboutFeatureCheck().Run(ctx)
                .Where(d => d.Code == Codes.DependencyMissing).ToList();
            T.Eq(diags.Count, 1, "必须报缺前置");
            T.Contains(diags[0].Evidence!["source"], "modDependencies", "证据要标来源");
        }, LayoutNeed.Any);

        T.Case("task26b ★★ 依赖：写在 modDependenciesByVersion 里也要查（107 个 mod 在用）", () =>
        {
            var byVer = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["v1.7"] = new[] { "c.versioned" },
            };
            var cat = ModCatalog.FromMods(new[]
            {
                MakeMod("a.versioned", depsByVer: byVer),
            });
            var ctx = MakeCtx(cat);
            var diags = new AboutFeatureCheck().Run(ctx)
                .Where(d => d.Code == Codes.DependencyMissing).ToList();

            T.Eq(diags.Count, 1, "★ 只读 modDependencies 会漏掉这条 —— 实测 107 个 mod 受影响");
            T.Contains(diags[0].Evidence!["source"], "v1.7", "证据要标出是哪个版本块");
        }, LayoutNeed.Any);

        T.Case("task26b ★★ 依赖走备选名 ⇒ 报 RD3004（Info）而不是 RD3002（缺前置）", () =>
        {
            var cat = ModCatalog.FromMods(new[]
            {
                MakeMod("a.needs", deps: new[] { "b.altname" }),
                MakeMod("b.real", altIds: new[] { "b.altname" }),
            });
            var ctx = MakeCtx(cat);
            var all = new AboutFeatureCheck().Run(ctx).ToList();

            var viaAlt = all.Where(d => d.Code == Codes.DependencyViaAlternativeId).ToList();
            var missing = all.Where(d => d.Code == Codes.DependencyMissing).ToList();

            T.Eq(viaAlt.Count, 1, "★ 应认出「依赖已由备选名满足」");
            T.Eq(missing.Count, 0, "★ 不能同时报缺前置 —— 那是假阳，会让人去装已经装了的");
            T.Eq(viaAlt[0].Severity, Severity.Info, "级别必须是 Info（这是正常情况）");
        }, LayoutNeed.Any);

        T.Case("task26b ★ core 别名：依赖写 core 应视为 ludeon.rimworld（已启用就不报）", () =>
        {
            var cat = ModCatalog.FromMods(new[]
            {
                MakeMod("Ludeon.RimWorld"),
                MakeMod("a.usescore", deps: new[] { "core" }),
            });
            var ctx = MakeCtx(cat);
            var diags = new AboutFeatureCheck().Run(ctx)
                .Where(d => d.Code == Codes.DependencyMissing).ToList();
            T.Eq(diags.Count, 0, "★ core 是 ludeon.rimworld 的别名，不该报缺前置");
        }, LayoutNeed.Any);

        T.Case("task26b 自我声明不兼容自己 ⇒ 不报（避免无意义噪音）", () =>
        {
            var cat = ModCatalog.FromMods(new[]
            {
                MakeMod("a.self", incompatible: new[] { "a.self" }),
            });
            var ctx = MakeCtx(cat);
            var diags = new AboutFeatureCheck().Run(ctx)
                .Where(d => d.Code == Codes.IncompatibleModActive).ToList();
            T.Eq(diags.Count, 0, "自己 vs 自己不是真冲突");
        }, LayoutNeed.Any);
    }
}
