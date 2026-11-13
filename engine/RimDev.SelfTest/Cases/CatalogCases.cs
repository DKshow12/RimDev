using RimDev.Core.Catalog;
using RimDev.Core.Environment;

namespace RimDev.SelfTest.Cases;

public static class CatalogCases
{
    public static void Run()
    {
        T.Group("catalog");
        var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);

        T.Case("★ VisualExceptions 的 packageId 不能被依赖块污染", () =>
        {
            var about = Path.Combine(Facts.WorkshopDir, "2538411704", "About", "About.xml");
            T.True(File.Exists(about), "样例 About.xml 存在");

            var info = AboutXmlReader.Read(about);
            T.Null(info.Error, "不应有解析错误");
            T.Eq(info.PackageId, "brrainz.visualexceptions", "packageId（真实值）");
            T.Eq(info.DisplayName, "Visual Exceptions", "displayName");

            // 反面证据：正则取第一个 <packageId> 会得到依赖的 id
            var raw = File.ReadAllText(about);
            var naive = System.Text.RegularExpressions.Regex.Match(raw, "<packageId>\\s*([^<]+?)\\s*</packageId>").Groups[1].Value;
            T.Eq(naive, "brrainz.harmony", "正则确实会取到依赖 id（这就是必须用 XML 感知的原因）");
        });

        T.Case("supportedVersions / loadAfter 解析", () =>
        {
            var about = Path.Combine(Facts.WorkshopDir, "2538411704", "About", "About.xml");
            var info = AboutXmlReader.Read(about);
            T.True(info.SupportedVersions.Contains("1.6"), "supportedVersions 含 1.6，实际: " + string.Join(",", info.SupportedVersions));
            T.True(info.LoadAfter.Contains("brrainz.harmony"), "loadAfter 含 brrainz.harmony");
            T.True(info.LoadBefore.Contains("Ludeon.RimWorld"), "loadBefore 含 Ludeon.RimWorld");
            T.True(info.Dependencies.Contains("brrainz.harmony"), "modDependencies 含 brrainz.harmony");
        });

        T.Case("ThreatFleet 自己的 About 解析", () =>
        {
            var about = Path.Combine(env.ModsDir, "ThreatFleet", "About", "About.xml");
            var info = AboutXmlReader.Read(about);
            T.Null(info.Error, "不应有解析错误");
            T.Eq(info.PackageId, "dsh.threatfleet.fragments", "packageId");
            T.True(info.SupportedVersions.Contains("1.6"), "supportedVersions 含 1.6");
        });

        T.Case("loadFolders 读 <v1.6>（ThreatFleet 用 / 与 1.6 两个目录）", () =>
        {
            var root = Path.Combine(env.ModsDir, "ThreatFleet");
            var dirs = LoadFoldersReader.Read(root, "1.6");
            T.NotNull(dirs, "应读到 loadFolders");
            T.Eq(dirs!.Length, 2, "目录数（/ 与 1.6）");
            T.True(dirs.Any(d => d.TrimEnd('\\') == root.TrimEnd('\\')), "含 mod 根");
            T.True(dirs.Any(d => d.EndsWith("1.6", StringComparison.Ordinal)), "含 1.6 子目录");
        });

        T.Case("loadFolders 不存在时 Read 返回 null", () =>
        {
            var tmp = Path.Combine(Path.GetTempPath(), "rimdev-nolf-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tmp);
                T.Null(LoadFoldersReader.Read(tmp, "1.6"), "无 loadFolders 应返回 null");
                Directory.CreateDirectory(Path.Combine(tmp, "1.6"));
                var fb = LoadFoldersReader.Fallback(tmp, "1.6");
                T.Eq(fb.Length, 2, "回落应含根 + 版本目录");
            }
            finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
        });

        T.Case("★ 目录表：3971 个真 mod、0 个重复、候选目录数在基线附近", () =>
        {
            var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
            T.Eq(active.Length, Facts.ActiveModCount, "activeMods 条数（★ 不能把 knownExpansions 混进来）");

            var result = ModCatalog.Build(env, env.TargetVersion, active);
            var cat = result.Catalog;

            T.Eq(cat.All.Count, Facts.ModsWithPackageId, "解析出 packageId 的 mod 数");
            T.Eq(result.Diagnostics.Count(d => d.Code == "RD1005"), Facts.RealDuplicatePackageIdCount,
                "真实 packageId 重复数");

            var candidates = ModCatalog.EnumerateCandidates(env).Count();
            // ★ 不用精确等值：候选目录数会随任何工具往 Mods\ 丢文件夹而漂移（实测 2026-10-14 因
            //   vision-toolkit 建了 Mods\.dsh-vision-toolkit，由 3984 → 3985，那是环境变动而非缺陷）。
            //   下界 + 小容差仍能抓住真正的结构性漂移：递归枚举会爆到几万、丢掉工坊会掉到几十。
            T.True(candidates >= Facts.CandidateModDirCount,
                $"候选目录数 {candidates} 应 ≥ 基线 {Facts.CandidateModDirCount}");
            T.True(candidates <= Facts.CandidateModDirCount + 16,
                $"候选目录数 {candidates} 超出基线上限（{Facts.CandidateModDirCount} +16）——枚举口径可能变了");
            var nonMod = candidates - cat.All.Count;
            T.True(nonMod >= 1 && nonMod <= 20,
                $"无 About.xml 的候选目录数 {nonMod} 应在 1..20（真 mod {cat.All.Count} / 候选 {candidates}）");
        });

        T.Case("启用 mod 全部定位到目录，且互相不重叠", () =>
        {
            var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
            var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;

            T.Eq(cat.Active.Count, Facts.ActiveModCount, "Active 集合大小");
            var missing = active.Where(p => !cat.TryGet(p, out _)).ToArray();
            T.True(missing.Length == 0, "未定位到的启用 mod: " + string.Join(", ", missing));
            T.True(cat.Active.All(m => m.IsActive), "Active 集合里 IsActive 都应为 true");
        });

        T.Case("来源分类正确（本地/官方/工坊）", () =>
        {
            var cat = ModCatalog.Build(env, env.TargetVersion,
                ModCatalog.ReadActiveMods(env.ModsConfigPath)).Catalog;

            T.True(cat.Get("dsh.threatfleet.fragments")?.Source == ModSource.Local, "ThreatFleet 是本地");
            T.True(cat.Get("Ludeon.RimWorld")?.Source == ModSource.Official, "Core 是官方");
            T.True(cat.Get("brrainz.visualexceptions")?.Source == ModSource.Workshop, "VisualExceptions 是工坊");
            T.True(cat.Get("brrainz.visualexceptions")?.PublishedFileId == "2538411704", "工坊目录名即 publishedFileId");
        });

        T.Case("_rimdev 不会进目录表（它就在 Mods 下但没有 About.xml）", () =>
        {
            var cat = ModCatalog.Build(env, env.TargetVersion, Array.Empty<string>()).Catalog;
            T.True(cat.All.All(m => !m.RootPath.Contains("_rimdev", StringComparison.OrdinalIgnoreCase)),
                "目录表里不应出现 _rimdev");
        });

        T.Case("扫描性能：目录表构建应远低于 10 秒", () =>
        {
            var r = ModCatalog.Build(env, env.TargetVersion, Array.Empty<string>());
            T.True(r.ElapsedMs < 10_000, $"实测 {r.ElapsedMs} ms（基线 1375 ms）");
        });
    }
}
