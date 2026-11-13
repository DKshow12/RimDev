using RimDev.Core.Catalog;
using RimDev.Core.Checks;
using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// 第 3 期 Task 4b：同一 packageId 多份安装不再放大重复诊断（用户决策 (b)）。
///
/// ★ 用例分工（照「能证伪它的用例」原则写，不是照「能验证它的用例」）：
/// <list type="bullet">
/// <item><b>(a)</b> 跨安装副本 ⇒ 一条 RD2026 都不报（正向）；</item>
/// <item><b>(b)</b> 同一份安装内不同相对路径 ⇒ 仍然报（**主门禁**：不许把真缺陷一起吞掉）；</item>
/// <item><b>(c)</b> 同一份安装内同一文件同一相对路径的不同行 ⇒ 仍然报（主理人探针发现的全库 8 条形态）；</item>
/// <item><b>(d)/(e)</b> 胜者根优先 / 保序 / 胜者根不在输入时的退化；</item>
/// <item><b>(f)</b> 桶内只有一个根或 ModRoot 全为 null ⇒ 整桶不动；</item>
/// <item><b>(g)(h)(i)</b> 真语料：全库 CE → 0、其余 6 个 mod 共 14 条、折叠计数 3878 / packages 只有 CE；</item>
/// <item><b>(j)</b> 既有不变量按新口径仍然闭合；</item>
/// <item><b>(k)</b> active 集合不受影响（CE 不在启用集）；</item>
/// <item><b>(l)</b> 索引缓存往返带上 ModRoot（schema 4）。</item>
/// </list>
/// </summary>
public static class Phase3Task4bCases
{
    private static readonly EngineVersionInfo TestVersion =
        new("1.6.4871", "1.6", new Version(1, 6, 4871, 591), "test", null);

    /// <summary>Combat Extended 的 packageId（本机装了两份，About.xml 里都是它）。</summary>
    private const string CePackageId = "CETeam.CombatExtended";

    // ───────────────────────── 夹具 ─────────────────────────

    /// <summary>临时 mod 目录：可以写任意相对路径的 XML，并按引擎语义算出 Resolution。</summary>
    private sealed class TempMod : IDisposable
    {
        public string Root { get; }
        public string PackageId { get; }

        public TempMod(string packageId)
        {
            PackageId = packageId;
            Root = Path.Combine(Path.GetTempPath(), "rimdev-t4b-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public void Write(string relPath, string content)
        {
            var full = Path.Combine(Root, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        /// <summary>一个文件里放 <paramref name="count"/> 个**同名** ThingDef（用不同缩进/空行区分行号）。</summary>
        public static string ThingDefs(string defName, int count = 1)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Defs>\n");
            for (var i = 0; i < count; i++)
                sb.Append("  <ThingDef>\n    <defName>").Append(defName).Append("</defName>\n  </ThingDef>\n");
            sb.Append("</Defs>\n");
            return sb.ToString();
        }

        public ModInfo ToModInfo()
        {
            var doc = LoadFolderResolver.Load(Root);
            var resolved = LoadFolderResolver.Resolve(Root, PackageId, doc, TestVersion,
                LoadFolderResolver.ActiveMatcher(Array.Empty<string>()), new[] { "1.6" });

            return new ModInfo(
                PackageId: PackageId,
                DisplayName: PackageId,
                RootPath: Root,
                Source: ModSource.Local,
                PublishedFileId: null,
                SupportedVersions: new[] { "1.6" },
                LoadFolders: resolved.DescendingOrder.ToArray(),
                Dependencies: Array.Empty<string>(),
                LoadAfter: Array.Empty<string>(),
                LoadBefore: Array.Empty<string>(),
                IsActive: true,
                HasAssemblies: false,
                AboutPath: null,
                AboutError: null,
                Resolution: resolved);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { /* 临时目录，忽略 */ }
        }
    }

    /// <summary>真扫文件、真建索引（只是不扫盘上的工坊），与生产路径同一条 CollectFiles 链路。</summary>
    /// <param name="catalog">
    /// 目录表覆盖：真实环境里「目录表胜者」与「扫描顺序」是两件事（胜者由来源优先级 + 枚举顺序决定，
    /// 而收集到的文件顺序可以不同）。用例要钉住「胜者根优先」就必须能把两者分开，否则构造不出反例。
    /// </param>
    private static CheckContext BuildContext(
        IReadOnlyList<ModInfo> mods, LoadOrder order, string? target = null, ModCatalog? catalog = null)
    {
        var files = DefScanner.CollectFiles(
            mods.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)));
        var scan = DefScanner.Scan(files);
        var idx = DefIndex.Build(scan.Defs);
        return new CheckContext(idx, scan.Files, catalog ?? ModCatalog.FromMods(mods), scan.Defs, target,
            LoadOrder: order, ModRank: order.RankFor(mods));
    }

    private static LoadOrder OrderOf(params ModInfo[] mods) =>
        LoadOrder.FromEntries(mods.Select(m => (m, LoadOrigin.ActiveMods)));

    /// <summary>直接构造 RawDef（用于「ModRoot 全为 null」这类不可能从磁盘出现的形态）。</summary>
    private static RawDef Raw(string pkg, string rel, int line, string? root, string defName) =>
        new(DefType: "ThingDef", DefName: defName, NameAttr: null, ParentName: null, MayRequire: null,
            IsAbstract: false, ModPackageId: pkg, FileAbs: Path.Combine(root ?? @"X:\syn", rel),
            FileRelToMod: rel, Line: line, ClassAttr: null, ModRoot: root);

    // ───────────────────────── 全库上下文（只扫一次，多条用例共享） ─────────────────────────

    private static CheckContext? _allLib;

    private static CheckContext AllLibrary()
    {
        if (_allLib is not null) return _allLib;

        var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
        var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
        var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;
        var order = LoadOrder.Build(env, cat, active, LoadOrder.ReadKnownExpansions(env.ModsConfigPath));
        _allLib = BuildContext(cat.All, order);

        Console.WriteLine($"    [全库] mods={cat.All.Count} xml={_allLib.Files.Count} defs={_allLib.Defs.Count}");
        return _allLib;
    }

    // ───────────────────────── 用例 ─────────────────────────

    public static void Run()
    {
        T.Group("phase3-task4b");

        // ═══════════════ (a) 正向：跨安装副本不报 ═══════════════

        T.Case("★ (a) 跨安装副本（同 packageId、同相对路径、不同安装根）→ 折叠成 1 条且 RD2026 = 0", () =>
        {
            using var a = new TempMod("syn.replica");
            using var b = new TempMod("syn.replica");
            a.Write(Path.Combine("Defs", "A.xml"), TempMod.ThingDefs("SynReplica"));
            b.Write(Path.Combine("Defs", "A.xml"), TempMod.ThingDefs("SynReplica"));

            var ma = a.ToModInfo();
            var mb = b.ToModInfo();
            var mods = new[] { ma, mb };
            var ctx = BuildContext(mods, OrderOf(ma, mb));

            var group = ctx.Index.ByTypeAndDefName[("ThingDef", "SynReplica")];
            T.Eq(group.Count, 2, "索引里应有 2 条声明（两份安装各一条）");
            T.Eq(group[0].ModRoot, ma.RootPath, "第一条来自 A 安装根");
            T.Eq(group[1].ModRoot, mb.RootPath, "第二条来自 B 安装根");

            var collapse = InstallReplicaCollapse.Collapse(group, ctx.Catalog);
            T.Eq(collapse.Kept.Count, 1, "折叠后只留 1 条");
            T.Eq(collapse.Dropped.Count, 1, "折叠掉 1 条");
            T.Eq(collapse.Packages.Count, 1, "涉及 1 个 packageId");

            var an = ShadowingCheck.Analyze(ctx);
            var rd2026 = an.Diagnostics.Count(d => d.Code == Codes.DefDuplicateWithinMod);
            Console.WriteLine($"    [a] Kept={collapse.Kept.Count} Dropped={collapse.Dropped.Count} RD2026={rd2026}");
            T.Eq(rd2026, 0, "无害双装不得报 RD2026");
            T.Eq(an.Stats.InstallReplicaDeclarationsDropped, 1, "折叠计数必须报出来（CLI 的 installReplicas 靠它）");
            T.Eq(string.Join(",", an.Stats.InstallReplicaPackages ?? Array.Empty<string>()), "syn.replica",
                "涉及的 packageId");
            // 折叠后这一组**根本不是重复组**：连 RD2004 也不该报。
            T.Eq(new DuplicateDefNameCheck().Run(ctx).Count(), 0, "RD2004 同样不该报");
        });

        // ═══════════════ (b) 主门禁：同一份安装内不同相对路径仍要报 ═══════════════

        T.Case("★★ (b) 同一份安装内**不同相对路径** → 仍然报 RD2026（主门禁）", () =>
        {
            using var a = new TempMod("syn.within");
            a.Write(Path.Combine("Defs", "A.xml"), TempMod.ThingDefs("SynWithin"));
            a.Write(Path.Combine("Defs", "B.xml"), TempMod.ThingDefs("SynWithin"));

            var ma = a.ToModInfo();
            var ctx = BuildContext(new[] { ma }, OrderOf(ma));

            var group = ctx.Index.ByTypeAndDefName[("ThingDef", "SynWithin")];
            T.Eq(group.Count, 2, "索引里 2 条声明");
            var collapse = InstallReplicaCollapse.Collapse(group, ctx.Catalog);
            T.Eq(collapse.Dropped.Count, 0, "不同相对路径**不是**副本，一条都不许折叠");
            T.Eq(group[0].ModRoot, group[1].ModRoot, "两条的安装根其实相同");

            var an = ShadowingCheck.Analyze(ctx);
            var rd2026 = an.Diagnostics.Count(d => d.Code == Codes.DefDuplicateWithinMod);
            Console.WriteLine($"    [b] Dropped={collapse.Dropped.Count} RD2026={rd2026}");
            T.Eq(rd2026, 1, "同一份安装内的真重复必须照报");
            T.Eq(an.Stats.WithinModGroups, 1, "统计口径同步");
        });

        // ═══════════════ (c) 同一文件同一相对路径的不同行仍要报 ═══════════════

        T.Case("★★ (c) 同一份安装内**同一文件、同一相对路径、不同行** → 仍然报 RD2026（全库 8 条形态）", () =>
        {
            using var a = new TempMod("syn.samefile");
            a.Write(Path.Combine("Defs", "A.xml"), TempMod.ThingDefs("SynSameFile", count: 2));

            var ma = a.ToModInfo();
            var ctx = BuildContext(new[] { ma }, OrderOf(ma));

            var group = ctx.Index.ByTypeAndDefName[("ThingDef", "SynSameFile")];
            T.Eq(group.Count, 2, "索引里 2 条声明");
            T.Eq(group[0].FileRelToMod, group[1].FileRelToMod, "相对路径相同（这正是与跨安装副本不可区分的地方）");
            T.True(group[0].Line != group[1].Line, "两条必须在不同行（否则构造不出这个形态）");

            var collapse = InstallReplicaCollapse.Collapse(group, ctx.Catalog);
            Console.WriteLine($"    [c] roots=[{group[0].ModRoot}] Dropped={collapse.Dropped.Count}");
            T.Eq(collapse.Dropped.Count, 0, "同一安装根 ⇒ 整桶不动（判别键必须是安装根不同）");

            var an = ShadowingCheck.Analyze(ctx);
            T.Eq(an.Diagnostics.Count(d => d.Code == Codes.DefDuplicateWithinMod), 1, "必须照报 RD2026");
        });

        // ═══════════════ (d) 胜者根优先 + 保序 ═══════════════

        T.Case("★ (d) 胜者根优先 + 保序：输入里败者在先，也必须留下目录表胜者那一份", () =>
        {
            using var winner = new TempMod("syn.pick");
            using var loser = new TempMod("syn.pick");
            winner.Write(Path.Combine("Defs", "A.xml"), TempMod.ThingDefs("SynPick"));
            loser.Write(Path.Combine("Defs", "A.xml"), TempMod.ThingDefs("SynPick"));

            var mw = winner.ToModInfo();
            var ml = loser.ToModInfo();

            // ★ 目录表里 mw 是胜者（FromMods 的 byPkg 先到先得）；
            //   而扫描输入**故意把败者排在前面** —— 若实现写成「保留输入最早的一份」，这里就会留下败者。
            //   注意：目录表必须显式传（不能由 mods 顺序推），否则两者恰好一致、反例就不成立。
            var ctx = BuildContext(new[] { ml, mw }, OrderOf(mw, ml), catalog: ModCatalog.FromMods(new[] { mw, ml }));
            var group = ctx.Index.ByTypeAndDefName[("ThingDef", "SynPick")];
            T.Eq(group[0].ModRoot, ml.RootPath, "输入顺序：败者在前（构造用的前提）");

            var collapse = InstallReplicaCollapse.Collapse(group, ctx.Catalog);
            T.Eq(collapse.Kept.Count, 1, "折叠后 1 条");
            T.Eq(collapse.Kept[0].ModRoot, mw.RootPath, "留下的必须是目录表胜者（不是输入最早的那条）");
            T.Eq(collapse.Dropped[0].ModRoot, ml.RootPath, "被折叠的是败者那份");

            // 保序：单条声明必须原样返回（同一个实例、同一个顺序）。
            var single = new List<RawDef> { Raw("syn.one", Path.Combine("Defs", "A.xml"), 3, @"X:\one", "SynOne") };
            var one = InstallReplicaCollapse.Collapse(single, ctx.Catalog);
            T.Eq(one.Kept.Count, 1, "单条声明：Kept 1 条");
            T.True(ReferenceEquals(one.Kept[0], single[0]), "单条声明：必须是同一个实例（保序、不复制）");
            T.Eq(one.Dropped.Count, 0, "单条声明：不折叠");
        });

        T.Case("★ (d2) 目录表胜者根不在输入里（合成目录表）→ 保留输入顺序最早的一份，不猜不丢", () =>
        {
            using var x = new TempMod("syn.absent");
            using var y = new TempMod("syn.absent");
            x.Write(Path.Combine("Defs", "A.xml"), TempMod.ThingDefs("SynAbsent"));
            y.Write(Path.Combine("Defs", "A.xml"), TempMod.ThingDefs("SynAbsent"));
            using var other = new TempMod("syn.absent");   // 目录表里的「胜者」指向一个从未扫描的目录

            var mx = x.ToModInfo();
            var my = y.ToModInfo();
            var mo = other.ToModInfo();
            var cat = ModCatalog.FromMods(new[] { mo });    // byPkg 里只有 mo（胜者根 = other.Root）

            var group = new List<RawDef>
            {
                Raw("syn.absent", Path.Combine("Defs", "A.xml"), 3, mx.RootPath, "SynAbsent"),
                Raw("syn.absent", Path.Combine("Defs", "A.xml"), 4, my.RootPath, "SynAbsent"),
            };
            var collapse = InstallReplicaCollapse.Collapse(group, cat);
            T.Eq(collapse.Kept.Count, 1, "仍然折叠成 1 条");
            T.Eq(collapse.Kept[0].ModRoot, mx.RootPath, "没有匹配时保留输入顺序最早的一份");
        });

        // ═══════════════ (f) 桶内只有 1 个根 / 全 null ═══════════════

        T.Case("★ (f) 桶内只有一个安装根 / ModRoot 全为 null → 整桶不动（合成/外部构造形态）", () =>
        {
            using var a = new TempMod("syn.sameroot");
            var ma = a.ToModInfo();
            var cat = ModCatalog.FromMods(new[] { ma });

            // ① 两个声明、同一个非空安装根
            var sameRoot = new List<RawDef>
            {
                Raw("syn.sameroot", Path.Combine("Defs", "A.xml"), 3, ma.RootPath, "SynSameRoot"),
                Raw("syn.sameroot", Path.Combine("Defs", "A.xml"), 4, ma.RootPath, "SynSameRoot"),
            };
            var r1 = InstallReplicaCollapse.Collapse(sameRoot, cat);
            T.Eq(r1.Dropped.Count, 0, "同一个安装根 ⇒ 不折叠");
            T.Eq(r1.Kept.Count, 2, "两条都留下");

            // ② ModRoot 全为 null（测试/外部构造；磁盘路径永远会填）
            var nullRoot = new List<RawDef>
            {
                Raw("syn.nullroot", Path.Combine("Defs", "A.xml"), 3, null, "SynNullRoot"),
                Raw("syn.nullroot", Path.Combine("Defs", "A.xml"), 4, null, "SynNullRoot"),
            };
            var r2 = InstallReplicaCollapse.Collapse(nullRoot, cat);
            T.Eq(r2.Dropped.Count, 0, "全 null ⇒ 不折叠（不许把「未知」当「不同」）");
            T.Eq(r2.Kept.Count, 2, "两条都留下");

            // ③ 一空一根：不同非空根只有 1 个 ⇒ 同样不折叠
            var mixed = new List<RawDef>
            {
                Raw("syn.mixed", Path.Combine("Defs", "A.xml"), 3, @"X:\a", "SynMixed"),
                Raw("syn.mixed", Path.Combine("Defs", "A.xml"), 4, null, "SynMixed"),
            };
            T.Eq(InstallReplicaCollapse.Collapse(mixed, cat).Dropped.Count, 0, "非空根只有 1 个 ⇒ 不折叠");
        });

        // ═══════════════ 真语料 ═══════════════

        T.Case("★★ (g) 真语料：全库 CE 的 RD2026 == 0，其余 6 个 mod 合计 14 条（逐 mod 表）", () =>
        {
            var ctx = AllLibrary();
            var an = ShadowingCheck.Analyze(ctx);
            var rd2026 = an.Diagnostics.Where(d => d.Code == Codes.DefDuplicateWithinMod).ToList();

            var byMod = rd2026
                .GroupBy(d => d.ModPackageId ?? "(null)", StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Console.WriteLine($"    [g] 全库 RD2026={rd2026.Count} 折叠={an.Stats.InstallReplicaDeclarationsDropped}"
                + $" packages=[{string.Join(", ", an.Stats.InstallReplicaPackages ?? new List<string>())}]");
            foreach (var g in byMod) Console.WriteLine($"        {g.Count(),5}  {g.Key}");

            T.Eq(rd2026.Count(d => string.Equals(d.ModPackageId, CePackageId, StringComparison.OrdinalIgnoreCase)), 0,
                "CE（同 packageId 装两份）必须一条 RD2026 都不报 —— 这正是本 Task 的目的");
            T.Eq(rd2026.Count, 14, "其余 6 个 mod 的真重复条目数（修复前 3,892 = 14 + CE 3,878）");
            T.Eq(byMod.Count, 6, "涉及的 mod 个数");
            T.Eq(string.Join(",", byMod.Select(g => g.Count()).OrderBy(x => x)), "1,1,2,2,3,5",
                "逐 mod 计数多重集（任务书：HuaxiaScenario 5 + WeaponFittingMilira 3 + ReplaceStuff 2 + yaopt 2 + kacpdw 1 + XiaoYcha 1）");
        });

        T.Case("★★ (h) 真语料：折叠计数 == 3878、packages == [CETeam.CombatExtended]（= 门禁 5 的 Core 侧）", () =>
        {
            var ctx = AllLibrary();
            var s = ShadowingCheck.Analyze(ctx).Stats;

            Console.WriteLine($"    [h] declarationsDropped={s.InstallReplicaDeclarationsDropped}"
                + $" packages=[{string.Join(", ", s.InstallReplicaPackages ?? new List<string>())}]");

            T.Eq(s.InstallReplicaDeclarationsDropped, 3878, "被折叠的声明数（任务书期望表：3,878）");
            var pkgs = s.InstallReplicaPackages ?? new List<string>();
            T.Eq(pkgs.Count, 1, "受影响的 packageId 只有 1 个");
            T.Eq(pkgs[0], CePackageId, "那个 packageId 就是 CE");
        });

        T.Case("★ (j) 既有不变量按新口径仍然闭合（全库）", () =>
        {
            var ctx = AllLibrary();
            var an = ShadowingCheck.Analyze(ctx);
            var s = an.Stats;

            Console.WriteLine($"    [j] 重复组={s.DuplicateGroups}（单={s.GroupsInSingleMod} 跨={s.GroupsAcrossMods}）"
                + $" RD2026={an.Diagnostics.Count(d => d.Code == Codes.DefDuplicateWithinMod)}"
                + $" WithinModGroups={s.WithinModGroups}"
                + $" RD2025={an.Diagnostics.Count(d => d.Code == Codes.DefShadowed)}"
                + $" CrossLoss={s.CrossModLosingDeclarations}");

            T.Eq(s.GroupsInSingleMod + s.GroupsAcrossMods, s.DuplicateGroups,
                "「单 mod 组 + 跨 mod 组」必须精确等于重复组数（同一个划分）");
            T.Eq(an.Diagnostics.Count(d => d.Code == Codes.DefDuplicateWithinMod), s.WithinModGroups,
                "RD2026 条数必须等于 (mod,DefType,defName) 三元组重复数");
            T.Eq(an.Diagnostics.Count(d => d.Code == Codes.DefShadowed), s.CrossModLosingDeclarations,
                "RD2025 条数必须等于跨 mod 落败声明数");
            T.True(s.DuplicateGroups > 1000, $"全库重复组应 > 1000，实际 {s.DuplicateGroups}");
        });

        T.Case("★ (i) 真语料：启用集不受影响（CE 不在启用集 ⇒ 折叠 0 条、RD2026 = 0）", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
            var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;
            var order = LoadOrder.Build(env, cat, active, LoadOrder.ReadKnownExpansions(env.ModsConfigPath));

            var ctx = BuildContext(cat.Active, order);
            var an = ShadowingCheck.Analyze(ctx);
            var rd2026 = an.Diagnostics.Count(d => d.Code == Codes.DefDuplicateWithinMod);

            Console.WriteLine($"    [i] active mods={cat.Active.Count} defs={ctx.Defs.Count}"
                + $" 折叠={an.Stats.InstallReplicaDeclarationsDropped} RD2026={rd2026}");

            T.Eq(an.Stats.InstallReplicaDeclarationsDropped, 0, "启用集里没有重复安装 ⇒ 折叠必须是 0（防过度折叠）");
            T.Eq(rd2026, 0, "active 全集上不得有 mod 内重复声明（门禁 3）");
        });

        // ═══════════════ 两个消费者共用一套口径 + 缓存往返 ═══════════════

        T.Case("★ (k) 两个消费者共用同一折叠口径（DuplicateDefNameCheck 与 ShadowingCheck 不许各写一套）", () =>
        {
            // 构造一个能**分辨**「用 Kept 还是用原始声明集」的局面：
            //   A 安装：Defs\A.xml 与 Defs\C.xml 都声明 SynBoth（**同一安装内不同相对路径** = 真重复）；
            //   B 安装（同 packageId）：Defs\A.xml 也声明 SynBoth（这是副本，必须被折叠）。
            //   ⇒ 原始声明数 = 3，Kept = 2。若 evidence/消息用原始集合，会写成「被声明 3 次」且 3 个片段。
            using var a = new TempMod("syn.both");
            using var b = new TempMod("syn.both");
            a.Write(Path.Combine("Defs", "A.xml"), TempMod.ThingDefs("SynBoth"));
            a.Write(Path.Combine("Defs", "C.xml"), TempMod.ThingDefs("SynBoth"));
            b.Write(Path.Combine("Defs", "A.xml"), TempMod.ThingDefs("SynBoth"));

            var ma = a.ToModInfo();
            var mb = b.ToModInfo();
            var ctx = BuildContext(new[] { ma, mb }, OrderOf(ma, mb));

            var an = ShadowingCheck.Analyze(ctx);
            var rd2004 = new DuplicateDefNameCheck().Run(ctx).ToList();
            var rawDecls = ctx.Index.ByTypeAndDefName[("ThingDef", "SynBoth")].Count;

            Console.WriteLine($"    [k] raw={rawDecls} 折叠={an.Stats.InstallReplicaDeclarationsDropped}"
                + $" RD2026={an.Diagnostics.Count(d => d.Code == Codes.DefDuplicateWithinMod)} RD2004={rd2004.Count}");

            T.Eq(rawDecls, 3, "构造前提：原始声明 3 条（含 1 条副本）");
            T.Eq(an.Stats.InstallReplicaDeclarationsDropped, 1, "副本折叠计数");
            T.Eq(an.Diagnostics.Count(d => d.Code == Codes.DefDuplicateWithinMod), 1, "只剩一条真重复");
            T.Eq(rd2004.Count, 1, "RD2004 也只剩一条真重复（副本被同一个 helper 折叠掉）");
            T.Contains(rd2004[0].Message, "被声明 2 次", "消息里的次数必须是 Kept 的数（原始声明数是 3）");
            var decl = rd2004[0].Evidence!["declarations"];
            T.Eq(decl.Split(" | ").Length, 2, "evidence.declarations 必须列 Kept 的 2 条，不是原始的 3 条");
        });

        T.Case("★ (l) 索引缓存往返带上 ModRoot（schema 4：不落盘的话缓存路径上修复会静默失效）", () =>
        {
            using var a = new TempMod("syn.cache");
            a.Write(Path.Combine("Defs", "A.xml"), TempMod.ThingDefs("SynCache"));

            var ma = a.ToModInfo();
            var files = DefScanner.CollectFiles(new[] { (ma.PackageId, ma.RootPath, (IReadOnlyList<string>)ma.LoadFolders) });
            var scan = DefScanner.Scan(files);
            T.True(scan.Defs.Count > 0, "应有 def");
            T.NotNull(scan.Defs[0].ModRoot, "扫描产物必须带安装根");

            var tmp = Path.Combine(Path.GetTempPath(), "rimdev-t4b-idx-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new DefIndexStore(tmp);
                store.Save(scan, DefIndexStore.FingerprintAll(files), "test");
                T.Eq(store.LoadMeta()!.SchemaVersion, DefIndexStore.CurrentSchemaVersion, "meta 里的 schema 版本");
                var back = store.LoadDefs();
                T.Eq(back.Count, scan.Defs.Count, "读回的 def 数");
                T.Eq(back[0].ModRoot, scan.Defs[0].ModRoot, "ModRoot 必须往返一致（否则折叠会静默退化成不折叠）");
                T.Eq(back[0].FileRelToMod, scan.Defs[0].FileRelToMod, "相对路径往返一致");
            }
            finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
        });
    }
    // ───────────────────────── 按需入口：全库 A/B 对照（不进常规跑，约 60 s） ─────────────────────────

    /// <summary>
    /// Step 7 门禁 4 需要的「修复前 / 修复后」逐码对照。
    ///
    /// ★ 为什么要在这里做 A/B，而不是拿两个时间的 CLI 输出对比：**语料是活的**
    ///   （另一个写入者在改 Mods\ThreatFleet），跨时间对比会把「代码改动」与「语料漂移」混在一起。
    ///   这里在同一份 ctx 上把两份口径各跑一遍，差值就**只**可能来自折叠。
    ///
    /// ★ 怎么在**不改生产代码**的前提下得到「修复前」：<see cref="RawDef.ModRoot"/> 全抹成 null ——
    ///   折叠器的判别键正是「不同的非空安装根 ≥ 2」，全 null 时它的每个桶都"根数 &lt; 2" ⇒ 整桶不动，
    ///   于是 Kept == 输入全集，等价于修复前的行为（循环里其余逻辑一字未改）。
    /// </summary>
    public static void RunMeasurement()
    {
        var ctx = AllLibrary();

        var preDefs = ctx.Defs.Select(d => d with { ModRoot = null }).ToList();
        var preCtx = new CheckContext(DefIndex.Build(preDefs), ctx.Files, ctx.Catalog, preDefs, null,
            LoadOrder: ctx.LoadOrder, ModRank: ctx.ModRank);

        var pre = ShadowingCheck.Analyze(preCtx);
        var post = ShadowingCheck.Analyze(ctx);
        var pre2004 = new DuplicateDefNameCheck().Run(preCtx).Count();
        var post2004 = new DuplicateDefNameCheck().Run(ctx).Count();

        Console.WriteLine("[task4b-measure] scope=all（同一份 ctx 上的 A/B，排除语料漂移）");
        Console.WriteLine($"  scanned           mods={ctx.Catalog.All.Count} xml={ctx.Files.Count} defs={ctx.Defs.Count}");
        Console.WriteLine($"  RD2026（error）   {pre.Diagnostics.Count(d => d.Code == Codes.DefDuplicateWithinMod)}"
                          + $" → {post.Diagnostics.Count(d => d.Code == Codes.DefDuplicateWithinMod)}");
        Console.WriteLine($"  RD2004（warning） {pre2004} → {post2004}");
        Console.WriteLine($"  RD2025（info）    {pre.Diagnostics.Count(d => d.Code == Codes.DefShadowed)}"
                          + $" → {post.Diagnostics.Count(d => d.Code == Codes.DefShadowed)}");
        Console.WriteLine($"  重复组            {pre.Stats.DuplicateGroups} → {post.Stats.DuplicateGroups}"
                          + $"（单 mod {pre.Stats.GroupsInSingleMod}→{post.Stats.GroupsInSingleMod}"
                          + $" / 跨 mod {pre.Stats.GroupsAcrossMods}→{post.Stats.GroupsAcrossMods}）");
        Console.WriteLine($"  同 mod 落败       {pre.Stats.SameModLosingDeclarations} → {post.Stats.SameModLosingDeclarations}");
        Console.WriteLine($"  跨 mod 落败       {pre.Stats.CrossModLosingDeclarations} → {post.Stats.CrossModLosingDeclarations}");
        Console.WriteLine($"  声明总数          {pre.Stats.TotalDeclarations} → {post.Stats.TotalDeclarations}");
        Console.WriteLine($"  折叠              declarationsDropped={post.Stats.InstallReplicaDeclarationsDropped}"
                          + $" packages=[{string.Join(", ", post.Stats.InstallReplicaPackages ?? new List<string>())}]");

        // 逐 mod 的 RD2026（修复前 / 修复后），差值必须能逐 mod 归因。
        static Dictionary<string, int> ByMod(IEnumerable<Diagnostic> ds)
        {
            var m = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in ds.Where(x => x.Code == Codes.DefDuplicateWithinMod))
            {
                var k = d.ModPackageId ?? "(null)";
                m[k] = m.GetValueOrDefault(k) + 1;
            }
            return m;
        }

        var preMod = ByMod(pre.Diagnostics);
        var postMod = ByMod(post.Diagnostics);
        Console.WriteLine("  ── RD2026 逐 mod（修复前 → 修复后）──");
        foreach (var k in preMod.Keys.Concat(postMod.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(k => preMod.GetValueOrDefault(k)))
            Console.WriteLine($"     {preMod.GetValueOrDefault(k),5} → {postMod.GetValueOrDefault(k),4}  {k}");

        // RD2025 差值的逐 mod 归因（同样按新口径统计，便于看「差值到底落在谁身上」）。
        static Dictionary<string, int> ShadowByMod(IEnumerable<Diagnostic> ds)
        {
            var m = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in ds.Where(x => x.Code == Codes.DefShadowed))
            {
                var k = d.ModPackageId ?? "(null)";
                m[k] = m.GetValueOrDefault(k) + 1;
            }
            return m;
        }

        var preSh = ShadowByMod(pre.Diagnostics);
        var postSh = ShadowByMod(post.Diagnostics);
        Console.WriteLine("  ── RD2025 逐 mod（修复前 → 修复后；只看有差值的）──");
        foreach (var k in preSh.Keys.Concat(postSh.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(k => preSh.GetValueOrDefault(k) != postSh.GetValueOrDefault(k))
                     .OrderByDescending(k => preSh.GetValueOrDefault(k) - postSh.GetValueOrDefault(k)))
            Console.WriteLine($"     {preSh.GetValueOrDefault(k),5} → {postSh.GetValueOrDefault(k),4}  {k}");
    }
}
