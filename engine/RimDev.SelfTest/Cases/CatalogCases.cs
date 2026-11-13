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
    }
}
