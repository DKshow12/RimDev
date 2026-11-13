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
        {
            T.Eq(files.Count, Facts.ActiveModXmlFileCount, "XML 文件数");
            var totalBytes = files.Sum(f => new FileInfo(f.FileAbs).Length);
            T.Eq(totalBytes, Facts.ActiveModXmlTotalBytes, "XML 总字节数");
        });

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

        T.Case("★ 索引落盘后可读回，def 数一致", () =>
        {
            var tf = cat.Get("dsh.threatfleet.fragments")!;
            var tfFiles = DefScanner.CollectFiles(
                new[] { (tf.PackageId, tf.RootPath, (IReadOnlyList<string>)tf.LoadFolders) });
            var scan = DefScanner.Scan(tfFiles);

            var tmp = Path.Combine(Path.GetTempPath(), "rimdev-idx-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new DefIndexStore(tmp);
                store.Save(scan, DefIndexStore.FingerprintAll(tfFiles), "test");
                T.True(store.Exists(), "索引文件应存在");

                var back = store.LoadDefs();
                T.Eq(back.Count, scan.Defs.Count, "读回的 def 数");
                var a = scan.Defs.First(x => x.DefName == "TF_ThreatHive");
                var b = back.First(x => x.DefName == "TF_ThreatHive");
                T.Eq(b.DefType, a.DefType, "DefType 往返一致");
                T.Eq(b.Line, a.Line, "行号往返一致");
                T.Eq(b.ModPackageId, a.ModPackageId, "mod 往返一致");
            }
            finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
        });

        T.Case("★ 指纹增量：全未变时 SelectChanged 返回 0", () =>
        {
            var tf = cat.Get("dsh.threatfleet.fragments")!;
            var tfFiles = DefScanner.CollectFiles(
                new[] { (tf.PackageId, tf.RootPath, (IReadOnlyList<string>)tf.LoadFolders) });
            var fp = DefIndexStore.FingerprintAll(tfFiles);
            var changed = DefIndexStore.SelectChanged(tfFiles, fp);
            T.Eq(changed.Count, 0, "未变文件数应为 0");
        });

        T.Case("指纹增量：改一个文件后只有它被选中", () =>
        {
            var tf = cat.Get("dsh.threatfleet.fragments")!;
            var tfFiles = DefScanner.CollectFiles(
                new[] { (tf.PackageId, tf.RootPath, (IReadOnlyList<string>)tf.LoadFolders) });
            var fp = DefIndexStore.FingerprintAll(tfFiles);
            var victim = tfFiles[0].FileAbs;
            fp[victim] = new FileFingerprint(-1, -1);      // 伪造"变了"
            var changed = DefIndexStore.SelectChanged(tfFiles, fp);
            T.Eq(changed.Count, 1, "只有被改的文件应被选中");
            T.Eq(changed[0].FileAbs, victim, "选中的应是那个文件");
        });

        T.Case("★ 指纹增量：LoadFingerprints 往返一致（落盘 → 读回 → 闭环判定）", () =>
        {
            var tf = cat.Get("dsh.threatfleet.fragments")!;
            var tfFiles = DefScanner.CollectFiles(
                new[] { (tf.PackageId, tf.RootPath, (IReadOnlyList<string>)tf.LoadFolders) });
            var scan = DefScanner.Scan(tfFiles);

            var tmp = Path.Combine(Path.GetTempPath(), "rimdev-fp-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new DefIndexStore(tmp);
                store.Save(scan, DefIndexStore.FingerprintAll(tfFiles), "test");

                var back = store.LoadFingerprints();
                T.Eq(back.Count, tfFiles.Count, "读回的指纹条数");

                var sample = tfFiles[0].FileAbs;
                T.True(back.ContainsKey(sample), "读回的指纹表应含样例文件");
                T.Eq(back[sample], DefIndexStore.Fingerprint(sample), "指纹值往返一致（record 值相等）");

                // 闭环：落盘 → 读回 → 增量判定必须为 0（这条覆盖此前完全未测的反序列化路径）
                T.Eq(DefIndexStore.SelectChanged(tfFiles, back).Count, 0,
                    "闭环：读回的指纹应使 SelectChanged 返回 0");
                var d = DefIndexStore.Diff(tfFiles, back);
                T.Eq(d.Changed.Count, 0, "闭环：Diff.Changed 应为 0");
                T.Eq(d.Removed.Count, 0, "闭环：Diff.Removed 应为 0");
            }
            finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
        });

        T.Case("★ 指纹增量：只删文件时 Diff 必须报出 Removed（否则会静默用陈旧索引）", () =>
        {
            var tmp = Path.Combine(Path.GetTempPath(), "rimdev-del-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tmp);
                var a = Path.Combine(tmp, "A.xml");
                var b = Path.Combine(tmp, "B.xml");
                File.WriteAllText(a, "<Defs><ThingDef><defName>RDDel_A</defName></ThingDef></Defs>");
                File.WriteAllText(b, "<Defs><ThingDef><defName>RDDel_B</defName></ThingDef></Defs>");

                var all = new List<(string, string, string)> { (a, "pkg", tmp), (b, "pkg", tmp) };
                var fp = DefIndexStore.FingerprintAll(all);

                var d0 = DefIndexStore.Diff(all, fp);
                T.Eq(d0.Changed.Count, 0, "全未变：Changed 应为 0");
                T.Eq(d0.Removed.Count, 0, "全未变：Removed 应为 0（不得扫出假删除）");

                File.Delete(b);                                     // 只删，不改
                var remaining = all.Where(f => File.Exists(f.Item1)).ToList();
                var d = DefIndexStore.Diff(remaining, fp);

                T.Eq(d.Changed.Count, 0, "只删不改：Changed 仍为 0（这正是单看它会出错的场景）");
                T.Eq(d.Removed.Count, 1, "只删不改：应报出 1 个已删除文件");
                T.Eq(d.Removed[0], b, "报出的应是 B.xml");
            }
            finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
        });

        T.Case("指纹表缺失或损坏时 LoadFingerprints 返回空（安全降级 → 全量重扫）", () =>
        {
            var tmp = Path.Combine(Path.GetTempPath(), "rimdev-bad-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tmp);
                var store = new DefIndexStore(tmp);
                T.Eq(store.LoadFingerprints().Count, 0, "指纹文件不存在时应返回空表");

                File.WriteAllText(store.FingerprintsPath, "{ this is not json");
                T.Eq(store.LoadFingerprints().Count, 0, "指纹文件损坏时应返回空表（降级为全量重扫，不得抛异常）");
            }
            finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
        });
    }
}
