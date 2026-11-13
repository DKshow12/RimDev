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
        // 全量扫描只做一次（1649 文件，实测约 200 ms），供后续多条用例复用
        var fullScan = DefScanner.Scan(files);

        T.Case("★ 启用集合的文件数与实测锚点一致（mod 列表金丝雀）", () =>
            T.Eq(files.Count, Facts.ActiveModXmlFileCount, "XML 文件数"));

        T.Case("流式扫描全部文件、耗时可控、失败数为 0", () =>
        {
            T.Eq(fullScan.FilesScanned, files.Count, "扫描文件数");
            T.Eq(fullScan.FilesFailed, 0, "解析失败文件数（期望 0）");
            T.True(fullScan.ElapsedMs < 120_000, $"扫描耗时 {fullScan.ElapsedMs} ms");
        });

        T.Case("★ 扫描器产出与 DOM 真值逐文件一致（防静默丢 def）", () =>
        {
            // 这条取代了原先的「def 数 ≈ 233」魔法数字。它有两个好处：
            //  ① 直接盯住最贵的那类错——**静默丢 def**（不报错、不变红，但索引里少东西）；
            //  ② 不会因为 ThreatFleet 日常增删内容而误报。
            // 实测背景：初版 DefScanner 用 ReadElementContentAsString 读 <defName> 时，
            //   会把 def 自己的结束标签一起消费掉；当 <defName> 是最后一个子元素时（原版大量 def 正是这个形状），
            //   该 def 被静默丢弃。修后用本对账验证：domTotal=14480 / mismatchedFiles=0。
            int domTotal = 0, mismatched = 0;
            var details = new List<string>();
            foreach (var f in files)
            {
                int dom = 0;
                try
                {
                    var doc = new System.Xml.XmlDocument { XmlResolver = null };
                    doc.Load(f.FileAbs);
                    var root = doc.DocumentElement;
                    if (root?.Name == "Defs")
                        foreach (System.Xml.XmlNode n in root.ChildNodes)
                            if (n.NodeType == System.Xml.XmlNodeType.Element) dom++;
                }
                catch { continue; }   // 解析失败的文件由 RD2001 处理，这里不重复计入

                domTotal += dom;
                var got = fullScan.Defs.Count(x => x.FileAbs == f.FileAbs);
                if (dom != got)
                {
                    mismatched++;
                    if (details.Count < 5) details.Add($"{Path.GetFileName(f.FileAbs)}: dom={dom} scan={got}");
                }
            }

            T.True(mismatched == 0,
                $"有 {mismatched} 个文件的 def 数与 DOM 真值不一致（domTotal={domTotal}）：{string.Join("; ", details)}");
            T.Eq(fullScan.Defs.Count, domTotal, "扫描器 def 总数 vs DOM 真值总数");
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
