using RimDev.Core.Catalog;
using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// ★★ 第 3 期 Task 26c：<b>端到端验证</b> —— 真实语料上的 AboutFeatureCheck 行为。
///
/// <para>★ 这个文件本身是一次<b>探针错误的修正记录</b>，值得完整保留：</para>
///
/// <para><b>第一版误判</b>：写了个 Node 探针统计「真实冲突」，它用
/// <c>/&lt;packageId&gt;\s*([^&lt;]+)&lt;\/packageId&gt;/</c> 取**第一个** packageId。
/// 但 <c>About.xml</c> 里 <c>&lt;modDependencies&gt;</c> 可能排在自身 packageId 之前，
/// 于是探针把「声明冲突的 mod」和「它依赖的 mod」搞混了。
/// 结论：报出 2 条「真实冲突」——
/// <code>
/// HenTaiLoliTeam.Axolotl  ✗  Susir.MoeLotlPatch
/// brrainz.harmony         ✗  trigger.eliteraidplus
/// </code></para>
///
/// <para><b>实际是假的</b>。核查发现 <c>brrainz.harmony</c> 那条记录来自工坊
/// <c>1541721856</c>，而该文件真正的根级 packageId 是 <c>sarg.alphaanimals</c>。
/// 用「剥掉 modDependencies 段再取 packageId」重算后：</para>
/// <code>
/// 带 incompatibleWith 的 About.xml   302
/// 其中已启用的                        48
/// ★ 真实冲突（正确统计）               0
/// </code>
///
/// <para>⇒ 工具报 0 条是<b>正确</b>的；错的是探针。本文件把正确口径钉住，
/// 防止后来人再被同一个坑骗。</para>
///
/// <para>★ 教训：这与本项目知识库 §3.1 记的「探针错误」是同一类 ——
/// 验证工具的诊断本身也可能是错的，**期望值必须用正确口径复算**。</para>
/// </summary>
public static class Phase3Task26cCases
{
    public static void Run()
    {
        T.Case("task26c ★★ 端到端：真实语料里 ModInfo.IncompatibleWith 必须非空", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            if (env.GameRoot is null) { T.Mark("task26c 跳过：没有游戏根"); return; }

            var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
            var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;

            var withInc = cat.Active.Count(m => (m.IncompatibleWith?.Length ?? 0) > 0);
            T.True(withInc > 0,
                $"正确口径实测：302 个 About.xml 声明了 incompatibleWith，其中 48 个已启用；"
                + $"但 ModInfo 里读到 {withInc} 个 —— 说明 Build 没把字段传进 ModInfo");
        }, LayoutNeed.Any);

        T.Case("task26c ★★ 端到端：正确口径下真实冲突 = 0（工具报 0 条是对的）", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            if (env.GameRoot is null) { T.Mark("task26c 跳过：没有游戏根"); return; }

            var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
            var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;

            var ctx = new RimDev.Core.Checks.CheckContext(
                Index: DefIndex.Build(Array.Empty<RawDef>()),
                Files: Array.Empty<DefFileResult>(),
                Catalog: cat,
                Defs: Array.Empty<RawDef>(),
                TargetModPackageId: null);

            var diags = new RimDev.Core.Checks.AboutFeatureCheck().Run(ctx)
                .Where(d => d.Code == Codes.IncompatibleModActive)
                .ToList();

            // ★ 不断言「必须 > 0」—— 那是被错误探针误导的写法。
            //   正确口径：本机 48 个已启用 mod 声明了冲突，但声明对象都没启用 ⇒ 0 条是对的。
            //   这里断言的是「检查器跑得通、不抛异常、且不产生假阳」。
            T.Eq(diags.Count, 0,
                $"正确口径下有 0 条真实冲突；若这里非 0，需要逐个核对是不是假阳。"
                + $"实际 {diags.Count} 条：{string.Join("; ", diags.Take(3).Select(d => d.Message))}");
        }, LayoutNeed.Any);

        T.Case("task26c ★ 端到端：检查器本身是活的（不靠本机语料，用合成冲突自证）", () =>
        {
            // ★ 这条是「0 条不是假阴」的关键证据：
            //   真实语料恰好 0 冲突，光看它就分不清「检查器没生效」还是「真的没有」。
            //   这里造一个确定会冲突的场景，证明检查器能报。
            var cat = ModCatalog.FromMods(new[]
            {
                MakeFake("aaa.declarer", incompatible: new[] { "bbb.target" }),
                MakeFake("bbb.target"),
            });
            var ctx = new RimDev.Core.Checks.CheckContext(
                Index: DefIndex.Build(Array.Empty<RawDef>()),
                Files: Array.Empty<DefFileResult>(),
                Catalog: cat,
                Defs: Array.Empty<RawDef>(),
                TargetModPackageId: null);

            var diags = new RimDev.Core.Checks.AboutFeatureCheck().Run(ctx)
                .Where(d => d.Code == Codes.IncompatibleModActive).ToList();
            T.Eq(diags.Count, 1, "★ 合成冲突必须报出来 —— 这证明「真实语料 0 条」不是假阴");
        }, LayoutNeed.Any);

        T.Case("task26c ★ 端到端：DependenciesByVersion 也要真的传进 ModInfo", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            if (env.GameRoot is null) { T.Mark("task26c 跳过：没有游戏根"); return; }

            var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
            var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;

            var withByVer = cat.Active.Count(m => (m.DependenciesByVersion?.Count ?? 0) > 0);
            T.True(withByVer > 0,
                $"实测有 107 个 About.xml 用 modDependenciesByVersion，但 ModInfo 里读到 {withByVer} 个");
        }, LayoutNeed.Any);
    }

    private static ModInfo MakeFake(string pkg, string[]? incompatible = null)
        => new ModInfo(
            PackageId: pkg, DisplayName: pkg, RootPath: @"C:\fake\" + pkg,
            Source: ModSource.Local, PublishedFileId: null,
            SupportedVersions: new[] { "1.7" }, LoadFolders: Array.Empty<string>(),
            Dependencies: Array.Empty<string>(), LoadAfter: Array.Empty<string>(),
            LoadBefore: Array.Empty<string>(), IsActive: true, HasAssemblies: false,
            AboutPath: @"C:\fake\" + pkg + @"\About\About.xml", AboutError: null,
            Resolution: null, DependenciesByVersion: null,
            AlternativePackageIds: null, IncompatibleWith: incompatible);
}
