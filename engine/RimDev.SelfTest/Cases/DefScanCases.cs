using RimDev.Core.Catalog;
using RimDev.Core.Defs;
using RimDev.Core.Environment;

namespace RimDev.SelfTest.Cases;

public static class DefScanCases
{
    public static void Run()
    {
        T.Group("defscan");
        var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
        var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
        var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;

        var inputs = cat.Active
            .Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders))
            .ToList();
        var files = DefScanner.CollectFiles(inputs);

        T.Case("★ 启用集合的文件数与实测锚点一致（6340）", () =>
            T.Eq(files.Count, Facts.ActiveModXmlFileCount, "XML 文件数"));

        T.Case("流式扫描全部文件、耗时可控、失败数为 0", () =>
        {
            var result = DefScanner.Scan(files);
            T.Eq(result.FilesScanned, files.Count, "扫描文件数");
            T.Eq(result.FilesFailed, 0, "解析失败文件数（期望 0）");
            T.True(result.ElapsedMs < 120_000, $"扫描耗时 {result.ElapsedMs} ms");
        });

        T.Case("★ ThreatFleet 的 def 数约为 233（历史实测 233 个 Def 节点）", () =>
        {
            var tf = cat.Get("dsh.threatfleet.fragments")!;
            var tfFiles = DefScanner.CollectFiles(
                new[] { (tf.PackageId, tf.RootPath, (IReadOnlyList<string>)tf.LoadFolders) });
            var r = DefScanner.Scan(tfFiles);
            T.Eq(r.FilesFailed, 0, "ThreatFleet 解析失败数");
            // 历史记录：17 个 XML / 233 个 Def 节点。留 ±5% 余量以容忍文档与磁盘不同步。
            var n = r.Defs.Count;
            T.True(n >= 221 && n <= 245, $"ThreatFleet def 数 = {n}（历史锚点 233，允许 ±5%）");
        });

        T.Case("def 头带行号与来源文件", () =>
        {
            var tf = cat.Get("dsh.threatfleet.fragments")!;
            var tfFiles = DefScanner.CollectFiles(
                new[] { (tf.PackageId, tf.RootPath, (IReadOnlyList<string>)tf.LoadFolders) });
            var r = DefScanner.Scan(tfFiles);
            var d = r.Defs.First(x => x.DefName is not null);
            T.True(d.Line > 0, "行号应 > 0");
            T.True(d.FileAbs.Length > 0, "应有来源文件");
            T.True(d.FileRelToMod.Length > 0 && !Path.IsPathRooted(d.FileRelToMod), "相对路径应为相对 mod 根");
        });

        T.Case("ThreatFleet 已知 def 出现在索引里", () =>
        {
            var tf = cat.Get("dsh.threatfleet.fragments")!;
            var tfFiles = DefScanner.CollectFiles(
                new[] { (tf.PackageId, tf.RootPath, (IReadOnlyList<string>)tf.LoadFolders) });
            var idx = DefIndex.Build(DefScanner.Scan(tfFiles).Defs);
            foreach (var name in new[] { "TF_ThreatHive", "TF_Wreck_OnslaughtMkI", "TF_Drone_Construction" })
                T.True(idx.IsDefNameTaken(name), $"索引里应有 {name}");
            T.True(idx.CountByType.ContainsKey("ThingDef"), "应有 ThingDef 计数");
            T.True(idx.CountByType["ThingDef"] > 0, "ThingDef 数应 > 0");
        });
    }
}
