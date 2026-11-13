using RimDev.Core.Catalog;
using RimDev.Core.Defs;
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

        T.Case("loadFolders 读 <v1.6>（ThreatFleet 用 / 与 1.6 两个目录；1.6 优先级更高）", () =>
        {
            var root = Path.Combine(env.ModsDir, "ThreatFleet");
            // ★ 第 3 期 Task 1 起：ResolutionOrigin 走【向下取版本】而不是「精确版本」——
            //   引擎的 CurrentVersionString 是 "1.6.4871"，mod 写的是 <v1.6> ⇒ 精确分支永不命中（Q1）。
            var r = LoadFoldersReader.Resolve(root, "dsh.threatfleet.fragments",
                EngineVersionProbe.Probe(env), ModCatalog.ReadActiveMods(env.ModsConfigPath));

            T.Eq(r.Origin, ResolutionOrigin.LowerVersion, "分支（精确串 1.6.4871 不命中）");
            T.Eq(r.MatchedVersionKey, "1.6", "命中键");
            T.Eq(r.DescendingOrder.Count, 2, "目录数（/ 与 1.6）");
            T.True(r.DescendingOrder.Any(d => d.TrimEnd('\\') == root.TrimEnd('\\')), "含 mod 根");
            T.Eq(Path.GetFileName(r.DescendingOrder[0]), "1.6", "1.6 必须排在根之前（后声明的 <li> 优先）");
        });

        T.Case("loadFolders 不存在 → 回退分支 [1.6, root]（旧 Fallback 的 root 在前是错的）", () =>
        {
            var tmp = Path.Combine(Path.GetTempPath(), "rimdev-nolf-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tmp);
                T.False(LoadFolderResolver.Load(tmp).HasFile, "无 loadFolders 时 HasFile 应为 false");
                Directory.CreateDirectory(Path.Combine(tmp, "1.6"));
                var r = LoadFoldersReader.Resolve(tmp, "tmp.mod", EngineVersionProbe.Probe(env), Array.Empty<string>());
                T.Eq(r.Origin, ResolutionOrigin.FallbackDir, "回退分支");
                T.Eq(string.Join("|", r.DescendingOrder.Select(Path.GetFileName)), "1.6|" + Path.GetFileName(tmp),
                    "顺序必须是 1.6 → root（root 最后加 = 优先级最低）");
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

        T.Case("★ loadFolders 里的重复 <li> 只返回一次目录", () =>
        {
            var tmp = Path.Combine(Path.GetTempPath(), "rimdev-lfdup-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tmp);
                var sub = Path.Combine(tmp, "1.6");
                Directory.CreateDirectory(sub);
                File.WriteAllText(Path.Combine(tmp, "loadFolders.xml"),
                    "<loadFolders><v1.6>"
                    + "<li>.</li><li>1.6</li><li>1.6</li><li>.</li><li>1.6</li>"
                    + "</v1.6></loadFolders>");

                // ★ 第 3 期 Task 1：引擎 **不**对目录去重（AddFolders 逐条追加），所以这里 5 条 <li> → 5 个目录项。
                //   去重发生在文件层（DefFileEnumerator 按「相对文件夹根」的键 TryAdd 先到先得）。
                var dirs = LoadFolderResolver.Resolve(tmp, "tmp.mod", LoadFolderResolver.Load(tmp),
                    new EngineVersionInfo("1.6.4871", "1.6", new Version(1, 6, 4871, 591), "test", null),
                    LoadFolderResolver.ActiveMatcher(Array.Empty<string>())).DescendingOrder;
                T.Eq(dirs.Count, 5, $"引擎不去重目录，5 条 <li> 应得 5 项，实际 {dirs.Count}");
                T.Eq(dirs.Distinct(StringComparer.OrdinalIgnoreCase).Count(), 2, "但不同目录只有 2 个（根 + 1.6）");
                var files = DefFileEnumerator.Enumerate(dirs);
                T.Eq(files.Count, files.Select(f => f.FileAbs).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    "文件层必须去重（同一份 XML 不得被枚举两次）");
            }
            finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
        });
    }
}
