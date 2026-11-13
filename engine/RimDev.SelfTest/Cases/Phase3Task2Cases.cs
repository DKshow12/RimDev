using System.Text;
using System.Xml;
using RimDev.Core.Checks;
using RimDev.Core.Catalog;
using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// 第 3 期 Task 2：`check --scope` 语义、索引计数自洽（F3）、RD2001 归因（F4/F6 口径）。
///
/// ★ 这里**不**断言任何全库数字（那些会随语料漂移，见 Facts 的漂移说明）；
///   所有断言都用合成语料或结构性不变量，只有官方内容那一条走真实语料（它是「零假阳」的机械保障）。
/// </summary>
public static class Phase3Task2Cases
{
    public static void Run()
    {
        T.Group("phase3-task2");

        // ───────────────────────── Step 1：check 的 --scope 语义 ─────────────────────────
        T.Case("★ check --scope：`--all` 与 `--scope active` **等价**；三种语义互不混同（F1）", () =>
        {
            // ★ 这条就是「把三者语义钉死」的用例：`--all` 是**兼容别名**，不是「全库」。
            //   若有人把 `--all` 改成「全库」，这里会立刻变红（这正是第 2 期的语义陷阱）。
            var byAllAlias = CheckScopeSelector.Parse(new[] { "check", "--all" }, out var e1);
            var byScopeActive = CheckScopeSelector.Parse(new[] { "check", "--scope", "active" }, out var e2);
            T.Null(e1, "--all 不应报参数错");
            T.Null(e2, "--scope active 不应报参数错");
            T.Eq(byAllAlias, byScopeActive, "`--all` 必须与 `--scope active` 完全等价");
            T.Eq(byAllAlias, CheckScope.Active, "--all 解析为 Active");

            T.Eq(CheckScopeSelector.Parse(new[] { "check" }, out _), CheckScope.Target,
                "默认（无参数）= 目标 mod");
            T.Eq(CheckScopeSelector.Parse(new[] { "check", "ThreatFleet" }, out _), CheckScope.Target,
                "显式 <mod> = 目标 mod");
            T.Eq(CheckScopeSelector.Parse(new[] { "check", "--scope", "target" }, out _), CheckScope.Target,
                "--scope target = 目标 mod");
            T.Eq(CheckScopeSelector.Parse(new[] { "check", "--scope", "all" }, out _), CheckScope.All,
                "--scope all = 全库（3971 个 mod）");
            T.Eq(CheckScopeSelector.Parse(new[] { "check", "--scope", "ALL" }, out _), CheckScope.All,
                "--scope 取值大小写不敏感");

            // 三种名字必须不同（防「两个枚举值撞成同一个」这类静默错答）
            T.Eq(CheckScopeSelector.Name(CheckScope.Target), "target", "scope 名 target");
            T.Eq(CheckScopeSelector.Name(CheckScope.Active), "active", "scope 名 active");
            T.Eq(CheckScopeSelector.Name(CheckScope.All), "all", "scope 名 all");

            // 参数错必须报错（退出码 3），不能静默降级
            CheckScopeSelector.Parse(new[] { "check", "--scope" }, out var miss);
            T.NotNull(miss, "--scope 缺值必须报参数错");
            CheckScopeSelector.Parse(new[] { "check", "--scope", "bogus" }, out var bogus);
            T.NotNull(bogus, "--scope 未知取值必须报参数错");
            T.Contains(bogus!, "bogus", "错误信息里必须带上实际取值");
            CheckScopeSelector.Parse(new[] { "check", "--all", "--scope", "all" }, out var conflict);
            T.NotNull(conflict, "--all（=active）与 --scope all 互相矛盾，必须报参数错而不是猜一个");
            Console.WriteLine($"       [scope] --all => {byAllAlias} / --scope active => {byScopeActive} / "
                              + $"冲突信息：{conflict}");
        });

        T.Case("★ check --json 的 scope 字段来自同一处（CLI 与 ScopeName 不会分叉）", () =>
        {
            // 结构性钉子：ScopeName 是 --json 里 scope 字段的**唯一**来源（CheckCommand 里只调它）。
            //   这里把「三个名字都不为空且互不相同」钉住，防止有人改成内联字符串。
            var names = new[]
            {
                CheckScopeSelector.Name(CheckScope.Target),
                CheckScopeSelector.Name(CheckScope.Active),
                CheckScopeSelector.Name(CheckScope.All)
            };
            T.Eq(names.Distinct().Count(), 3, "三个 scope 名必须互不相同");
            T.True(names.All(n => n.Length > 0), "scope 名不得为空");
            foreach (var s in new[] { CheckScope.Target, CheckScope.Active, CheckScope.All })
                T.True(CheckScopeSelector.Explanation(s).Length > 0, $"scope={s} 必须有解释文本");
        });

        // ───────────────────────── Step 3：索引计数自洽（F3） ─────────────────────────
        T.Case("★ 计数自洽三式：Save → ValidateCache 成立（files == fingerprints == parsedOk+skipped+failed）", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "rimdev-t2-self-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var (store, files, scan) = SaveSynthetic(dir, okFiles: 3, brokenFiles: 1);
                Console.WriteLine($"       [自洽] files={files.Count} parsedOk={scan.Files.Count(f => f.ParseError is null)} "
                                  + $"failed={scan.FilesFailed} fingerprints={DefIndexStore.FingerprintAll(files).Count}");

                var check = store.ValidateCache(files.Count);
                T.True(check.Usable, "自洽的缓存必须可用；实际理由：" + check.Reason);
                T.Eq(check.FingerprintCount, files.Count, "指纹表条数 == 文件数");
                T.NotNull(check.Meta, "meta 必须读回");
                T.True(check.Meta!.SelfConsistent, "SelfConsistent 必须为 true：" + check.Meta.SelfConsistencyText);
                T.Contains(check.Meta!.SelfConsistencyText, $"={scan.Files.Sum(f => f.ParseError is null ? 1 : 0)}",
                    "三式里的 parsedOk 必须等于真实解析成功数");

                // ★ 计划里写的是 `files == fingerprints == parsed + skipped + failed`。
                //   若把 parsed 读成「读过的全部文件」（含失败），等式不成立：会**重复计一次失败**。
                //   这条把差异算清楚，证明我们采用的是「parsed = parsedOk」这一种读法（否则要改的是计划而不是代码）。
                var parsedAll = scan.Files.Count;
                var planLiteral = parsedAll + scan.FilesFailed;      // 计划字面量（parsed 含失败）+ failed
                T.Eq(parsedAll, files.Count, "读过的文件数 == 枚举到的文件数");
                T.Eq(planLiteral, files.Count + scan.FilesFailed,
                    "计划字面量在 failed>0 时会**多出 failed 个**（这正是必须把 parsed 定义为 parsedOk 的原因）");
                T.True(planLiteral != files.Count, "有失败文件时两者必须不同（否则本用例的前提不成立）");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        T.Case("★ 计数自洽：三式任一项被破坏 → ValidateCache 拒绝并给得出具体数字（RD1004）", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "rimdev-t2-bad-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var (store, files, _) = SaveSynthetic(dir, okFiles: 3, brokenFiles: 1);
                T.True(store.ValidateCache(files.Count).Usable, "基准：自洽时可用");

                // (a) files != fingerprints：把 fingerprintsRecorded 改掉
                var good = File.ReadAllText(store.MetaPath);
                File.WriteAllText(store.MetaPath, good.Replace("\"fingerprintsRecorded\": 4", "\"fingerprintsRecorded\": 3"));
                var a = store.ValidateCache(files.Count);
                T.False(a.Usable, "files != fingerprints 必须拒绝");
                T.Contains(a.Reason, "files != fingerprints", "理由必须点明是哪一式");

                // (b) files != parsedOk + skipped + failed
                File.WriteAllText(store.MetaPath, good.Replace("\"filesParsedOk\": 3", "\"filesParsedOk\": 2"));
                var b = store.ValidateCache(files.Count);
                T.False(b.Usable, "三式不成立必须拒绝");
                T.Contains(b.Reason, "parsedOk + skippedUnchanged + parseFailed", "理由必须点明是哪一式");

                // (c) schemaVersion 升级后旧缓存必须被拒绝（不能静默当新格式读）
                // ★ Task 2b：这里改成从常量拼出「旧版本号」，不要再写死字面量 ——
                //   上一版写死 2，schema 升到 3 后 Replace 静默失配（测试会以此用例失败的形式暴露，
                //   但那已经浪费了一轮排查；用常量拼字符串就没有这个隐患）。
                var stale = good.Replace(
                    $"\"schemaVersion\": {DefIndexStore.CurrentSchemaVersion}",
                    $"\"schemaVersion\": {DefIndexStore.CurrentSchemaVersion - 1}");
                T.True(!string.Equals(stale, good, StringComparison.Ordinal),
                    "替换必须真的命中（否则这条用例是在测一个没坏的文件）");
                File.WriteAllText(store.MetaPath, stale);
                var c = store.ValidateCache(files.Count);
                T.False(c.Usable, "schemaVersion 不匹配必须拒绝");
                T.Contains(c.Reason, "schemaVersion", "理由必须点明 schema");

                // (d) 指纹表被截断（实际条数 != meta 记录条数）
                File.WriteAllText(store.MetaPath, good);
                var fps = store.LoadFingerprints();
                File.WriteAllText(store.FingerprintsPath, "{}");
                var d = store.ValidateCache(files.Count);
                T.False(d.Usable, "指纹表被清空必须拒绝");
                T.Contains(d.Reason, "指纹表实际条数", "理由必须点明指纹表");
                File.WriteAllText(store.FingerprintsPath,
                    System.Text.Json.JsonSerializer.Serialize(fps));

                // (e) 磁盘文件集与缓存不一致（多/少一个文件）
                var e = store.ValidateCache(files.Count + 1);
                T.False(e.Usable, "文件集不一致必须拒绝");
                T.Contains(e.Reason, "磁盘枚举不一致", "理由必须点明磁盘集合");

                // 复原后必须重新可用（防「一旦拒绝就永远拒绝」）
                T.True(store.ValidateCache(files.Count).Usable, "复原后必须重新可用");

                // 目录根本不存在索引时也不得抛异常
                var empty = new DefIndexStore(Path.Combine(dir, "none"));
                T.False(empty.ValidateCache(0).Usable, "无索引时必须返回不可用");
                T.Contains(empty.ValidateCache(0).Reason, "defs.jsonl", "无索引时要说明缺的是哪个文件");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        T.Case("★ 计数自洽：枚举后被删的文件会让三式的第一式不成立（活跃写入语料会偶发）", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "rimdev-t2-vanish-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var a = Path.Combine(dir, "a.xml");
                File.WriteAllText(a, "<Defs><ThingDef><defName>Vanish_A</defName></ThingDef></Defs>");
                var gone = Path.Combine(dir, "gone.xml");
                var files = new List<(string FileAbs, string ModPackageId, string ModRoot)>
                {
                    (a, "pkg", dir),
                    (gone, "pkg", dir),          // ★ 从未存在：模拟「枚举后被删」
                };

                var scan = DefScanner.Scan(files);
                var fps = DefIndexStore.FingerprintAll(files);
                Console.WriteLine($"       [vanish] files={files.Count} fingerprints={fps.Count} "
                                  + $"parsedOk={scan.Files.Count(f => f.ParseError is null)} failed={scan.FilesFailed}");

                T.Eq(scan.FilesScanned, scan.Files.Count(f => f.ParseError is null) + scan.FilesFailed,
                    "「读过的文件」必须被解析成功与解析失败精确划分");
                T.True(fps.Count < files.Count, "不存在的文件不该进指纹表");
                T.Eq(files.Count, fps.Count + scan.Files.Count(f => !fps.ContainsKey(f.FileAbs)),
                    "files == fingerprints + vanished（这就是三式第一式的真实形态）");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        // ───────────────────────── Step 6：RD2001 的 cause ─────────────────────────
        T.Case("★ RD2001 cause=Encoding：GBK 字节 + 无编码声明 → Encoding（而不是 Structure）", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "rimdev-t2-enc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var gbk = Path.Combine(dir, "gbk.xml");
                // 「中文」的 GBK 字节是 D6 D0 CE C4；0xD6 是 2 字节序列的首字节，0xD0 不是合法续字节
                // ⇒ XmlReader（默认 UTF-8）必抛 "Invalid character in the given encoding."
                var head = Encoding.ASCII.GetBytes("<Defs><ThingDef><label>");
                var tail = Encoding.ASCII.GetBytes("</label></ThingDef></Defs>");
                var bytes = head.Concat(new byte[] { 0xD6, 0xD0, 0xCE, 0xC4 }).Concat(tail).ToArray();
                File.WriteAllBytes(gbk, bytes);
                T.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                    "本用例的样本必须是**无 BOM** 的（否则测的就不是这个成因）");

                var r = DefScanner.ScanFile(gbk, "pkg", dir);
                T.NotNull(r.ParseError, "GBK 字节必须解析失败");
                T.Eq(r.ParseCause, ParseCause.Encoding, "必须归因为 Encoding：" + r.ParseError);
                Console.WriteLine($"       [cause/Encoding] {r.ParseError}");

                var broken = Path.Combine(dir, "broken.xml");
                File.WriteAllText(broken, "<Defs><ThingDef></Defs>");
                var r2 = DefScanner.ScanFile(broken, "pkg", dir);
                T.NotNull(r2.ParseError, "标签不配对必须解析失败");
                T.Eq(r2.ParseCause, ParseCause.Structure, "结构错必须归因为 Structure：" + r2.ParseError);
                Console.WriteLine($"       [cause/Structure] {r2.ParseError}");

                // 分类器不得把「结构错」误判成编码错（判错方向会让使用者去改编码而永远修不好）
                T.Eq(ParseCause.Classify("XmlException: The 'ThingDef' start tag on line 1 position 2 does not match "
                                         + "the end tag of 'Defs'. Line 1, position 20."), ParseCause.Structure,
                    "结构错消息不得被判成 Encoding");
                T.Eq(ParseCause.Classify("XmlException: Invalid character in the given encoding. Line 3, position 43."),
                    ParseCause.Encoding, "编码错消息必须判成 Encoding");

                // 良性文件不得报 ParseError，也就没有 cause
                var okFile = Path.Combine(dir, "ok.xml");
                File.WriteAllText(okFile, "<Defs><ThingDef><defName>Cause_Ok</defName></ThingDef></Defs>");
                T.Null(DefScanner.ScanFile(okFile, "pkg", dir).ParseCause, "良性文件不得有 cause");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        T.Case("★ active 集合 0 解析失败、官方内容 0 解析失败（新实现实测，非全库）", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
            var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;

            var activeFiles = DefScanner.CollectFiles(
                cat.Active.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)));
            var activeScan = DefScanner.Scan(activeFiles);
            var activeFailed = activeScan.Files.Where(f => f.ParseError is not null).ToList();
            Console.WriteLine($"       [active] files={activeFiles.Count} failed={activeFailed.Count}");
            foreach (var f in activeFailed.Take(5))
                Console.WriteLine($"         {f.FileAbs} cause={f.ParseCause} {f.ParseError}");
            T.Eq(activeScan.FilesFailed, 0, "启用集合不应有解析失败（历史锚点）");
            T.True(activeScan.Files.All(f => f.ParseCause is null), "无失败就不该有 cause");
        });

        // ───────────────────────── Step 7：excludesAbstract 口径 ─────────────────────────
        T.Case("★ excludesAbstract 的真实含义：只有抽象声明共用名时**不算**重复组", () =>
        {
            var defs = new List<RawDef>
            {
                // 两个抽象声明共用同一个 defName —— 引擎不会把它们建成 def，所以这不是重复
                new("ThingDef", "AbsOnly", "AbsA", null, null, true, "syn", @"C:\syn\a.xml", "Defs\\a.xml", 1),
                new("ThingDef", "AbsOnly", "AbsB", null, null, true, "syn", @"C:\syn\b.xml", "Defs\\b.xml", 2),
                // 一个抽象 + 两个真 def：真 def 之间才是重复（组数 1，声明数 2）
                new("ThingDef", "Both", "BothBase", null, null, true, "syn", @"C:\syn\c.xml", "Defs\\c.xml", 3),
                new("ThingDef", "Both", null, null, null, false, "syn", @"C:\syn\d.xml", "Defs\\d.xml", 4),
                new("ThingDef", "Both", null, null, null, false, "syn", @"C:\syn\e.xml", "Defs\\e.xml", 5),
            };
            var idx = DefIndex.Build(defs);

            var rimdevGroups = idx.ByTypeAndDefName.Count(kv => kv.Value.Count > 1);
            T.Eq(rimdevGroups, 1, "rimdev 口径（DefIndex.ByTypeAndDefName，已排除抽象）：只有 Both 是一组");
            T.Eq(idx.ByTypeAndDefName[("ThingDef", "Both")].Count, 2, "Both 组里只有 2 个**真**声明");

            // 「把 defs.jsonl 全部行都算进去」的探针口径（= 第 2 期 __hc-dups.ps1 的做法）
            var probeGroups = defs.Where(d => d.DefName is not null)
                .GroupBy(d => (d.DefType, d.DefName)).Count(g => g.Count() > 1);
            T.Eq(probeGroups, 2, "探针口径（含抽象）会多出一组 AbsOnly —— 这就是 13195 vs 9096 的那一类差");
            T.True(idx.TotalDefs == 5, "TotalDefs 仍按全部声明计");
            T.Eq(idx.AbstractDefs, 3, "抽象基类数（有 Name= 的声明）");
            Console.WriteLine($"       [excludesAbstract] rimdev 重复组={rimdevGroups} / 含抽象口径={probeGroups} "
                              + $"（AbsOnly 只有抽象声明，被正确排除）");
        });

        // ───────────────────────── Step 5：旧 vs 新的三条机制（合成语料，确定性） ─────────────────────────
        T.Case("★ 机制 M1：旧实现忽略 If* 条件 → 旧多算（合成 mod，逐字复现第 2 期语义）", () =>
        {
            using var m = new TempMod(
                "<loadFolders><v1.6><li IfModNotActive=\"active.mod\">Gated</li></v1.6></loadFolders>",
                Path.Combine("Gated", "Defs"));
            m.Write(Path.Combine("Gated", "Defs", "a.xml"), "<Defs/>");
            m.Write(Path.Combine("Gated", "Defs", "b.xml"), "<Defs/>");

            var doc = LoadFolderResolver.Load(m.Root);
            var resolved = LoadFolderResolver.Resolve(m.Root, "t.mod", doc, TestVersion,
                LoadFolderResolver.ActiveMatcher(new[] { "active.mod" }));
            T.Eq(resolved.DescendingOrder.Count, 0, "新实现：IfModNotActive 命中 ⇒ 该目录不加载");

            var legacy = LegacyProbe.LegacyFileCount(m.Root, "1.6", Array.Empty<string>());
            T.Eq(legacy, 2, "旧实现：忽略 If* ⇒ 照样枚举 Gated 下的 2 个文件（这就是旧多算的来源）");
            Console.WriteLine($"       [M1] new=0 legacy={legacy}");
        });

        T.Case("★ 机制 M2：有 loadFolders 但无目标版本键 → 旧返回**空数组**（不是 null）⇒ 0 个文件", () =>
        {
            using var m = new TempMod("<loadFolders><v1.5><li>1.5</li></v1.5></loadFolders>", "1.5");
            m.Write(Path.Combine("1.5", "Defs", "a.xml"), "<Defs/>");
            m.Write(Path.Combine("1.5", "Defs", "b.xml"), "<Defs/>");

            T.Eq(LegacyProbe.LegacyFileCount(m.Root, "1.6", Array.Empty<string>()), 0,
                "旧实现：无 v1.6/1.6 键 ⇒ Read 返回空数组 ⇒ `?? Fallback` 不触发 ⇒ 旧枚举 0 个（旧少算）");

            var doc = LoadFolderResolver.Load(m.Root);
            var resolved = LoadFolderResolver.Resolve(m.Root, "t.mod", doc, TestVersion,
                LoadFolderResolver.ActiveMatcher(Array.Empty<string>()));
            T.Eq(resolved.Origin, ResolutionOrigin.LowerVersion, "新实现：向下取版本");
            T.Eq(DefFileEnumerator.Enumerate(resolved.DescendingOrder).Count, 2,
                "新实现：真的枚举到 1.5 下的 2 个文件");
            Console.WriteLine($"       [M2] legacy=0 new={DefFileEnumerator.Enumerate(resolved.DescendingOrder).Count}");
        });

        T.Case("★ 机制 M3：无 loadFolders 时旧回退分支**漏 Common**", () =>
        {
            using var m = new TempMod(null, "1.6", "Common");
            m.Write(Path.Combine("1.6", "Defs", "a.xml"), "<Defs/>");
            m.Write(Path.Combine("Common", "Defs", "b.xml"), "<Defs/>");
            m.Write(Path.Combine("Common", "Defs", "c.xml"), "<Defs/>");

            T.Eq(LegacyProbe.LegacyFileCount(m.Root, "1.6", Array.Empty<string>()), 1,
                "旧回退 = [root, root\\1.6] ⇒ 只能看到 1.6 下的 1 个文件");

            var doc = LoadFolderResolver.Load(m.Root);
            var resolved = LoadFolderResolver.Resolve(m.Root, "t.mod", doc, TestVersion,
                LoadFolderResolver.ActiveMatcher(Array.Empty<string>()));
            T.Eq(resolved.DescendingOrder.Count, 3, "新回退 = [1.6, Common, root]");
            T.Eq(DefFileEnumerator.Enumerate(resolved.DescendingOrder).Count, 3, "新实现：3 个文件都算上");
            Console.WriteLine($"       [M3] legacy=1 new=3");
        });
    }

    private static readonly EngineVersionInfo TestVersion =
        new("1.6.4871", "1.6", new Version(1, 6, 4871, 591), "test", null);

    /// <summary>造一个「n 个良构 + m 个坏文件」的合成索引，供自洽断言用。</summary>
    private static (DefIndexStore Store, List<(string FileAbs, string ModPackageId, string ModRoot)> Files, ScanResult Scan)
        SaveSynthetic(string dir, int okFiles, int brokenFiles)
    {
        var files = new List<(string FileAbs, string ModPackageId, string ModRoot)>();
        for (var i = 0; i < okFiles; i++)
        {
            var p = Path.Combine(dir, $"ok{i}.xml");
            File.WriteAllText(p, $"<Defs><ThingDef><defName>Self_{i}</defName></ThingDef></Defs>");
            files.Add((p, "self.pkg", dir));
        }
        for (var i = 0; i < brokenFiles; i++)
        {
            var p = Path.Combine(dir, $"bad{i}.xml");
            File.WriteAllText(p, "<Defs><ThingDef></Defs>");
            files.Add((p, "self.pkg", dir));
        }

        var scan = DefScanner.Scan(files);
        var store = new DefIndexStore(dir);
        store.Save(scan, DefIndexStore.FingerprintAll(files), "self-test");
        return (store, files, scan);
    }

    /// <summary>
    /// 第 2 期（提交 061fadb）语义的**最小复现**，只为把三条机制的差值钉在自测里。
    /// 逐字对应：`LoadFoldersReader.Read(root, "1.6") ?? Fallback(root, "1.6")` +
    /// 「按绝对路径去重、不跳过点文件」的旧 `DeclScanner.CollectFiles` 口径。
    /// （全库逐 mod 明细由 `.probe/task2/legacy-diff` 复算，不放在自测里跑。）
    /// </summary>
    private static class LegacyProbe
    {
        public static int LegacyFileCount(string modRoot, string targetVersion, string[] activeIds)
        {
            var folders = Read(modRoot, targetVersion) ?? Fallback(modRoot, targetVersion);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in folders)
                foreach (var sub in new[] { "Defs", "Patches" })
                {
                    var p = Path.Combine(f, sub);
                    if (!Directory.Exists(p)) continue;
                    foreach (var x in Directory.EnumerateFiles(p, "*.xml", SearchOption.AllDirectories))
                        seen.Add(x);
                }
            return seen.Count;
        }

        /// <summary>逐字复现第 2 期的 Read：只找 v+版本 / 版本 两个键；无命中返回**空数组**（非 null）。</summary>
        private static string[]? Read(string modRoot, string targetVersion)
        {
            string? path = null;
            foreach (var n in new[] { "loadFolders.xml", "LoadFolders.xml" })
            {
                var p = Path.Combine(modRoot, n);
                if (File.Exists(p)) { path = p; break; }
            }
            if (path is null) return null;
            try
            {
                var doc = new XmlDocument { XmlResolver = null };
                doc.Load(path);
                XmlNode? section = null;
                foreach (var k in new[] { "v" + targetVersion, targetVersion })
                {
                    section = doc.DocumentElement?.SelectSingleNode(k);
                    if (section is not null) break;
                }
                if (section is null) return Array.Empty<string>();

                var result = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (XmlNode li in section.SelectNodes("li")!)
                {
                    var v = (li.InnerText ?? "").Trim();
                    if (v.Length == 0) continue;
                    if (v == "/" || v == "." || v == "\\")
                    {
                        if (seen.Add(modRoot)) result.Add(modRoot);
                        continue;
                    }
                    var abs = Path.Combine(modRoot, v.Replace('/', Path.DirectorySeparatorChar));
                    if (Directory.Exists(abs) && seen.Add(abs)) result.Add(abs);
                }
                return result.ToArray();
            }
            catch { return Array.Empty<string>(); }
        }

        /// <summary>逐字复现第 2 期的 Fallback：`[root, root\版本]`，**没有 Common**。</summary>
        private static string[] Fallback(string modRoot, string targetVersion)
        {
            var versioned = Path.Combine(modRoot, targetVersion);
            return Directory.Exists(versioned) ? new[] { modRoot, versioned } : new[] { modRoot };
        }
    }

    private sealed class TempMod : IDisposable
    {
        public string Root { get; }

        public TempMod(string? loadFoldersXml, params string[] dirs)
        {
            Root = Path.Combine(Path.GetTempPath(), "rimdev-t2-lf-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            foreach (var d in dirs) Directory.CreateDirectory(Path.Combine(Root, d));
            if (loadFoldersXml is not null)
                File.WriteAllText(Path.Combine(Root, "LoadFolders.xml"), loadFoldersXml);
        }

        /// <summary>写文件并**自动创建父目录**（早期版本直接用 File.WriteAllText 漏建 Defs 子目录，
        /// 结果两条机制用例失败在 IO 而不是断言上——夹具的毛病不要伪装成实现的问题）。</summary>
        public string Write(string rel, string content)
        {
            var full = Path.Combine(Root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            return full;
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { /* 临时目录，忽略 */ }
        }
    }
}
