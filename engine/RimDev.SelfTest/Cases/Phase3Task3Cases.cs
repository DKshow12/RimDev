using RimDev.Core.Catalog;
using RimDev.Core.Checks;
using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// 第 3 期 Task 3：mod 加载顺序 + 文件夹优先级 + 遮蔽（RD2025/RD2026）+ 顺序一致性（RD2011）。
///
/// ★ 用例分工（照「能证伪它的用例」原则写，不是照「能验证它的用例」）：
/// <list type="bullet">
/// <item>真语料：加载序 17 条、官方零 error、active 全集计数、全库三数（Step 4 对账）。</item>
/// <item>合成反例（Step 7 强制要求）：<c>loadAfter</c> 与顺序矛盾 → 必须报 RD2011 且**绝不是 error**；
///   以及它的证伪型孪生用例：顺序一致时**绝不报**。</item>
/// <item>合成证伪型：任务书 §2.2 说「同一 mod 内 <c>1.6\Defs\A.xml</c> 与 <c>Defs\A.xml</c> 是两个键、
///   会同时进入候选」——本组用一条用例把这句话钉死为**错的**（见用例注释里的反编译证据）。</item>
/// </list>
/// </summary>
public static class Phase3Task3Cases
{
    private static readonly EngineVersionInfo TestVersion =
        new("1.6.4871", "1.6", new Version(1, 6, 4871, 591), "test", null);

    // ───────────────────────── 夹具 ─────────────────────────

    /// <summary>临时 mod 目录：可以写任意相对路径的 XML，并按引擎语义算出 Resolution。</summary>
    private sealed class TempMod : IDisposable
    {
        public string Root { get; }
        public string PackageId { get; }

        public TempMod(string packageId, string? loadFoldersXml = null, params string[] dirs)
        {
            PackageId = packageId;
            Root = Path.Combine(Path.GetTempPath(), "rimdev-t3-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            foreach (var d in dirs) Directory.CreateDirectory(Path.Combine(Root, d));
            if (loadFoldersXml is not null)
                File.WriteAllText(Path.Combine(Root, "LoadFolders.xml"), loadFoldersXml);
        }

        /// <summary>写一个文件（自动建父目录）—— 早期版本忘了建目录，结果失败在 IO 而不是断言上。</summary>
        public void Write(string relPath, string content)
        {
            var full = Path.Combine(Root, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public static string ThingDefs(string defName) =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Defs>\n  <ThingDef>\n    <defName>"
            + defName + "</defName>\n  </ThingDef>\n</Defs>\n";

        public ModInfo ToModInfo(string[]? loadAfter = null, string[]? loadBefore = null)
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
                LoadAfter: loadAfter ?? Array.Empty<string>(),
                LoadBefore: loadBefore ?? Array.Empty<string>(),
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

    /// <summary>把一组合成 mod 变成可跑规则的上下文（真扫文件、真建索引，只是不扫盘上的工坊）。</summary>
    private static CheckContext BuildContext(
        IReadOnlyList<ModInfo> mods, LoadOrder order, string? target = null)
    {
        var files = DefScanner.CollectFiles(
            mods.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)));
        var scan = DefScanner.Scan(files);
        var idx = DefIndex.Build(scan.Defs);
        return new CheckContext(idx, scan.Files, ModCatalog.FromMods(mods), scan.Defs, target,
            LoadOrder: order, ModRank: order.RankFor(mods));
    }

    private static List<Diagnostic> RunShadowing(CheckContext ctx) => new ShadowingCheck().Run(ctx).ToList();

    // ───────────────────────── 用例 ─────────────────────────

    public static void Run()
    {
        T.Group("phase3-task3");

        // ═══════════════ Step 1：加载顺序模型 ═══════════════

        T.Case("★ Step 1 加载序：Core=0 → knownExpansions(5 DLC) → activeMods（真环境）", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
            var expansions = LoadOrder.ReadKnownExpansions(env.ModsConfigPath);
            var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;
            var order = LoadOrder.Build(env, cat, active, expansions);

            foreach (var e in order.Entries)
                Console.WriteLine($"    [order {e.Order,2}] {e.PackageId,-36} origin={e.Origin}  folders={e.Folders.Count}");

            // ★ 门禁 6 的 17 行：这不是巧合 —— activeMods 里**也**列了 Core 与 5 个 DLC，
            //   它们先被 Core/knownExpansions 放进序里，第三次遇到时只跳过（不重复计数）。
            T.Eq(order.Entries.Count, Facts.ActiveModCount,
                "加载序条目数必须等于 activeMods 条数（Core/DLC 在 activeMods 里也出现，靠去重抵消）");
            T.Eq(order.ActivePackageIds.Count, Facts.ActiveModCount, "activeMods 原始条数");

            var first = order.Entries[0];
            T.Eq(first.Origin, LoadOrigin.OfficialData, "第 0 位必须是官方内容");
            T.True(first.Mod.RootPath.EndsWith(Path.Combine("Data", "Core"), StringComparison.OrdinalIgnoreCase),
                $"第 0 位必须是 Data\\Core，实际 {first.Mod.RootPath}");
            T.Eq(first.PackageId, order.CorePackageId, "CorePackageId 与序位 0 必须一致");
            T.Eq(first.PackageId, "Ludeon.RimWorld", "Core 的 packageId");

            T.Eq(order.Entries.Count(e => e.Origin == LoadOrigin.OfficialData), 6, "官方条目数（Core + 5 DLC）");
            T.Eq(order.Entries.Count(e => e.Origin == LoadOrigin.ActiveMods), order.Entries.Count - 6,
                "其余都是 ActiveMods");
            // ★ 数组用 .Length（.Count 是 Enumerable.Count 扩展方法 —— 写成属性会得到 CS0019「方法组」）
            for (var i = 1; i <= expansions.Length && i < order.Entries.Count; i++)
                // ★ 大小写不能按 Ordinal 比：ModsConfig.xml 写的是 `ludeon.rimworld.royalty`，
                //   而 About.xml 里的 packageId 是 `Ludeon.RimWorld.Royalty`。引擎两边都用 packageIdLowerCase
                //   比较，本工具同样全程 OrdinalIgnoreCase（LoadOrder 的字典都是这个比较器）。
                T.True(string.Equals(order.Entries[i].PackageId, expansions[i - 1], StringComparison.OrdinalIgnoreCase),
                    $"序位 {i} 必须跟 knownExpansions 同序：期望 {expansions[i - 1]}，实际 {order.Entries[i].PackageId}");

            // ★ 去重不是「丢了一条」：Core + 5 DLC 在 activeMods 里又出现了一遍，被明确记下来。
            T.Eq(order.SkippedAlreadyOrdered.Count, 6,
                "activeMods 里重复出现的 Core + 5 DLC 必须被记录（不是静默丢弃）");
            T.Eq(order.UnresolvedPackageIds.Count, 0, "配置里不该有解析不到的 packageId");
        });

        T.Case("★ Step 1 knownExpansions 只取该段（不能扫全文的 <li>，那会混进 activeMods 的 17 条）", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var exp = LoadOrder.ReadKnownExpansions(env.ModsConfigPath);
            var act = ModCatalog.ReadActiveMods(env.ModsConfigPath);
            T.Eq(exp.Length, 5, "knownExpansions 是 5 个 DLC");
            T.Eq(act.Length, 17, "activeMods 是 17 条");
            // 两段真的不同 ⇒ 用同一个正则扫全文会得到 22 条（历史坑）。
            T.False(exp.SequenceEqual(act), "两段内容不同，混用会得到 22 条而不是 17/5");
            T.True(act.Any(x => string.Equals(x, "Ludeon.RimWorld", StringComparison.OrdinalIgnoreCase)),
                "activeMods 里**确实**也列了 Core（它就是门禁 6 的 17 的来源）；注意 ModsConfig 里写的是全小写 ludeon.rimworld");
        });

        // ═══════════════ Step 2：文件夹优先级 / 去重键（证伪型） ═══════════════

        T.Case("★★ 证伪：同一 mod 内 `1.6\\Defs\\A.xml` 与 `Defs\\A.xml` 是**同一个相对键**，只留一份", () =>
        {
            // 反编译证据（mvid 61e4173561894da49d210260257b5097，Verse.DirectXmlLoader.XmlAssetsInModFolder）：
            //     string text = list[i];                                  // list = foldersToLoadDescendingOrder
            //     string key  = fileInfo.FullName.Substring(text.Length + 1);   // ★ 相对【文件夹根】，不是 mod 根
            //     dictionary.TryAdd(key, fileInfo);                       // 先到先得 ⇒ 文件夹下标小者赢
            // 因此 <mod>\1.6\Defs\A.xml 与 <mod>\Defs\A.xml 的 key **都是** "Defs\A.xml" ⇒ 同一个键。
            // 任务书 §2.2 的结论句「是两个不同的键、会同时进入候选」与它自己上一句给出的去重键互相矛盾；
            // 本条用例把「实际行为」钉死，避免以后有人按那句话把这里改坏。
            using var m = new TempMod("syn.dual", "<loadFolders><v1.6><li>/</li><li>1.6</li></v1.6></loadFolders>",
                "Defs", Path.Combine("1.6", "Defs"));
            m.Write(Path.Combine("Defs", "A.xml"), TempMod.ThingDefs("SynDual"));
            m.Write(Path.Combine("1.6", "Defs", "A.xml"), TempMod.ThingDefs("SynDual"));

            var mod = m.ToModInfo();
            T.Eq(mod.LoadFolders.Length, 2, "回退/loadFolders 分支下根与 1.6 并存 ⇒ 两个加载目录");
            T.True(mod.LoadFolders[0].EndsWith("1.6", StringComparison.Ordinal), "1.6 优先级更高（倒序追加）");

            var files = DefFileEnumerator.Enumerate(mod.LoadFolders);
            T.Eq(files.Count, 1, "两个磁盘文件 → 同一个相对键 → 只留 1 个候选（不是 2 个）");
            T.Eq(files[0].FolderIndex, 0, "留下的是优先级更高的那个（1.6）");
            T.True(files[0].FileAbs.Contains(Path.Combine("1.6", "Defs")), "胜者是 1.6\\Defs\\A.xml");

            // 推论：这条构造**不会**产生「同 mod 内重名」（写两遍同名 def 不会各进一次索引）。
            var order = LoadOrder.FromEntries(new[] { (mod, LoadOrigin.ActiveMods) });
            var ctx = BuildContext(new[] { mod }, order);
            T.Eq(ctx.Defs.Count, 1, "只应索引到 1 个 def 声明");
            T.Eq(RunShadowing(ctx).Count, 0, "同一个物理 def 只出现一次 ⇒ 不报 RD2025/RD2026");
        });

        T.Case("★ Step 2 文件夹优先级：(modLoadOrder, folderIndex) 字典序 —— mod 序位小的先占用相对键", () =>
        {
            using var a = new TempMod("syn.first", "<loadFolders><v1.6><li>/</li></v1.6></loadFolders>", "Defs");
            using var b = new TempMod("syn.second", "<loadFolders><v1.6><li>/</li></v1.6></loadFolders>", "Defs");
            a.Write(Path.Combine("Defs", "Same.xml"), TempMod.ThingDefs("SynSame"));
            b.Write(Path.Combine("Defs", "Same.xml"), TempMod.ThingDefs("SynSame"));

            var ma = a.ToModInfo();
            var mb = b.ToModInfo();

            // 每个 mod 各有一份自己的字典 ⇒ 文件层不会跨 mod 去重；遮蔽发生在 def 层，由 mod 序位决定胜者。
            var orderAB = LoadOrder.FromEntries(new[] { (ma, LoadOrigin.ActiveMods), (mb, LoadOrigin.ActiveMods) });
            var ctxAB = BuildContext(new[] { ma, mb }, orderAB);
            var dAB = RunShadowing(ctxAB);
            T.Eq(dAB.Count(x => x.Code == Codes.DefShadowed), 1, "跨 mod 同名 ⇒ 1 条 RD2025");
            T.Eq(dAB.Single(x => x.Code == Codes.DefShadowed).Evidence!["winnerMod"], "syn.first",
                "mod 序位小者赢");
        });

        // ═══════════════ Step 7：RD2011 反例 + 证伪型 ═══════════════

        T.Case("★★ Step 7 反例：A 声明 loadAfter B，而 activeMods 把 A 放在 B 之前 → 必须报 RD2011", () =>
        {
            using var a = new TempMod("syn.a", "<loadFolders><v1.6><li>/</li></v1.6></loadFolders>", "Defs");
            using var b = new TempMod("syn.b", "<loadFolders><v1.6><li>/</li></loadFolders>", "Defs");
            a.Write(Path.Combine("Defs", "A.xml"), TempMod.ThingDefs("SynA"));
            b.Write(Path.Combine("Defs", "B.xml"), TempMod.ThingDefs("SynB"));

            // ★ 这就是任务书要求的局面：声明 loadAfter B，但顺序把 A 排在 B **之前**（序位 0 vs 1）。
            var ma = a.ToModInfo(loadAfter: new[] { "syn.b" });
            var mb = b.ToModInfo();
            var order = LoadOrder.FromEntries(new[] { (ma, LoadOrigin.ActiveMods), (mb, LoadOrigin.ActiveMods) });
            var diags = RunShadowing(BuildContext(new[] { ma, mb }, order));

            var rd = diags.Where(d => d.Code == Codes.LoadOrderViolation).ToList();
            T.Eq(rd.Count, 1, $"必须报 1 条 RD2011，实际 {rd.Count}（全部诊断：{string.Join(",", diags.Select(d => d.Code))}）");
            T.Eq(rd[0].Severity, Severity.Info, "RD2011 必须是 info");
            T.True(rd[0].Severity != Severity.Error, "★ 绝不能是 error（错报成 error 会让使用者直接不看报告）");
            T.Eq(rd[0].Evidence!["kind"], "loadAfter", "evidence.kind 必须区分 loadAfter / loadBefore");
            T.Eq(rd[0].Evidence!["declaredTarget"], "syn.b", "必须打印声明指向谁");
            Console.WriteLine($"    [RD2011] {rd[0].Message}");
        });

        T.Case("★★ Step 7 证伪型：顺序**一致**（B 在 A 之前）时，绝不报 RD2011", () =>
        {
            using var a = new TempMod("syn.a", "<loadFolders><v1.6><li>/</li></loadFolders>", "Defs");
            using var b = new TempMod("syn.b", "<loadFolders><v1.6><li>/</li></loadFolders>", "Defs");
            a.Write(Path.Combine("Defs", "A.xml"), TempMod.ThingDefs("SynA"));
            b.Write(Path.Combine("Defs", "B.xml"), TempMod.ThingDefs("SynB"));

            var ma = a.ToModInfo(loadAfter: new[] { "syn.b" });
            var mb = b.ToModInfo();
            // B 在前 ⇒ loadAfter 满足。
            var order = LoadOrder.FromEntries(new[] { (mb, LoadOrigin.ActiveMods), (ma, LoadOrigin.ActiveMods) });
            var diags = RunShadowing(BuildContext(new[] { ma, mb }, order));

            T.Eq(diags.Count(d => d.Code == Codes.LoadOrderViolation), 0,
                "顺序一致时 RD2011 必须为 0（否则规则会把正确的配置报成问题）");
        });

        T.Case("★ RD2011 另外两条防假阳边界：对方不在加载序里 / 自引用", () =>
        {
            using var a = new TempMod("syn.a", "<loadFolders><v1.6><li>/</li></loadFolders>", "Defs");
            a.Write(Path.Combine("Defs", "A.xml"), TempMod.ThingDefs("SynA"));

            // ① 对方未启用（不在加载序里）：引擎根本不会比较两者 ⇒ 命题不成立，不报。
            var ma1 = a.ToModInfo(loadAfter: new[] { "ghost.mod" });
            var o1 = LoadOrder.FromEntries(new[] { (ma1, LoadOrigin.ActiveMods) });
            T.Eq(RunShadowing(BuildContext(new[] { ma1 }, o1))
                    .Count(d => d.Code == Codes.LoadOrderViolation), 0,
                "目标未启用 ⇒ 不报 RD2011（这条直接决定官方内容上会不会刷假阳）");

            // ② 自引用：<li>自己</li>，原版内容里就有这种冗余写法。
            var ma2 = a.ToModInfo(loadAfter: new[] { "syn.a" }, loadBefore: new[] { "syn.a" });
            var o2 = LoadOrder.FromEntries(new[] { (ma2, LoadOrigin.ActiveMods) });
            T.Eq(RunShadowing(BuildContext(new[] { ma2 }, o2))
                    .Count(d => d.Code == Codes.LoadOrderViolation), 0, "自引用不报 RD2011");
        });

        T.Case("★ RD2011 loadBefore 分支同样成立（声明 loadBefore 但排在对方之后 → info）", () =>
        {
            using var a = new TempMod("syn.a", "<loadFolders><v1.6><li>/</li></loadFolders>", "Defs");
            using var b = new TempMod("syn.b", "<loadFolders><v1.6><li>/</li></loadFolders>", "Defs");
            a.Write(Path.Combine("Defs", "A.xml"), TempMod.ThingDefs("SynA"));
            b.Write(Path.Combine("Defs", "B.xml"), TempMod.ThingDefs("SynB"));

            var mb = b.ToModInfo();
            var ma = a.ToModInfo(loadBefore: new[] { "syn.b" });   // A 声明「我在 B 之前」，实际 A 在 B 之后
            var order = LoadOrder.FromEntries(new[] { (mb, LoadOrigin.ActiveMods), (ma, LoadOrigin.ActiveMods) });
            var rd = RunShadowing(BuildContext(new[] { ma, mb }, order))
                .Where(d => d.Code == Codes.LoadOrderViolation).ToList();

            T.Eq(rd.Count, 1, "loadBefore 方向同样要报");
            T.Eq(rd[0].Severity, Severity.Info, "RD2011 恒为 info");
            T.Eq(rd[0].Evidence!["kind"], "loadBefore", "evidence.kind=loadBefore");
        });

        T.Case("★ RD2011 报告范围：扫描集合之外的 mod 不进报告（`--only-official` 泄漏的回归钉子）", () =>
        {
            // 实测发现的真缺陷：`check --scope active --only-official` 只扫了官方 6 包，
            // 却把 3 条关于 brrainz.harmony 等第三方 mod 的 RD2011 一并报了出来。
            // 根因是 RD2011 拿【规范加载序】比较，而报告没有用【本次扫描集合】定界。
            using var a = new TempMod("syn.a", "<loadFolders><v1.6><li>/</li></loadFolders>", "Defs");
            using var b = new TempMod("syn.b", "<loadFolders><v1.6><li>/</li></loadFolders>", "Defs");
            using var c = new TempMod("syn.c", "<loadFolders><v1.6><li>/</li></loadFolders>", "Defs");
            a.Write(Path.Combine("Defs", "A.xml"), TempMod.ThingDefs("SynA"));
            b.Write(Path.Combine("Defs", "B.xml"), TempMod.ThingDefs("SynB"));
            c.Write(Path.Combine("Defs", "C.xml"), TempMod.ThingDefs("SynC"));

            var ma = a.ToModInfo(loadAfter: new[] { "syn.b" });   // 违反顺序（A 在 B 之前）
            var mb = b.ToModInfo();
            var mc = c.ToModInfo();
            var order = LoadOrder.FromEntries(new[]
                { (ma, LoadOrigin.ActiveMods), (mb, LoadOrigin.ActiveMods), (mc, LoadOrigin.ActiveMods) });

            // ① 声明的 mod 在扫描集合里 ⇒ 报（它确实参与了这条违规）
            T.Eq(RunShadowing(BuildContext(new[] { ma, mc }, order))
                    .Count(d => d.Code == Codes.LoadOrderViolation), 1,
                "声明者在自己被扫到的集合里 ⇒ 必须报");

            // ② 扫描集合里没有它 ⇒ 绝不能报（否则 `--only-official` 会泄漏第三方 mod 的诊断）
            T.Eq(RunShadowing(BuildContext(new[] { mc }, order))
                    .Count(d => d.Code == Codes.LoadOrderViolation), 0,
                "扫描集合里没有声明者 ⇒ 不得报告（范围泄漏）");
        });

        // ═══════════════ Step 3：遮蔽胜者（RD2025 / RD2026） ═══════════════

        T.Case("★ RD2026：同一个 mod 内同一 (DefType, defName) 声明两次 → Error", () =>
        {
            using var m = new TempMod("syn.dup", "<loadFolders><v1.6><li>/</li></loadFolders>", "Defs");
            m.Write(Path.Combine("Defs", "a.xml"), TempMod.ThingDefs("SynDup"));
            m.Write(Path.Combine("Defs", "b.xml"), TempMod.ThingDefs("SynDup"));   // 相对键不同 ⇒ 两份都进候选

            var mod = m.ToModInfo();
            var order = LoadOrder.FromEntries(new[] { (mod, LoadOrigin.ActiveMods) });
            var diags = RunShadowing(BuildContext(new[] { mod }, order));

            var rd = diags.Where(d => d.Code == Codes.DefDuplicateWithinMod).ToList();
            T.Eq(rd.Count, 1, $"必须报 1 条 RD2026（实际 {string.Join(",", diags.Select(d => d.Code))}）");
            T.Eq(rd[0].Severity, Severity.Error, "RD2026 是 error（同一个 mod 自己重复声明 = 作者失误）");
            T.Contains(rd[0].Evidence!["declarations"], "a.xml", "evidence 必须列出全部声明点");
            T.Contains(rd[0].Evidence!["declarations"], "b.xml", "evidence 必须列出全部声明点");
            T.False(diags.Any(d => d.Code == Codes.DefShadowed), "同一个 mod 内不算「跨 mod 遮蔽」");
            Console.WriteLine($"    [RD2026] {rd[0].Message}");
        });

        T.Case("★★ RD2025：跨 mod 落败 → Info，且必须带胜者文件/败者文件/def 名/胜者来源 mod", () =>
        {
            using var p = new TempMod("syn.win", "<loadFolders><v1.6><li>/</li></loadFolders>", "Defs");
            using var q = new TempMod("syn.lose", "<loadFolders><v1.6><li>/</li></loadFolders>", "Defs");
            p.Write(Path.Combine("Defs", "a.xml"), TempMod.ThingDefs("SynClash"));
            q.Write(Path.Combine("Defs", "z.xml"), TempMod.ThingDefs("SynClash"));

            var mp = p.ToModInfo();
            var mq = q.ToModInfo();
            var order = LoadOrder.FromEntries(new[] { (mp, LoadOrigin.ActiveMods), (mq, LoadOrigin.ActiveMods) });
            var diags = RunShadowing(BuildContext(new[] { mp, mq }, order));

            var rd = diags.Where(d => d.Code == Codes.DefShadowed).ToList();
            T.Eq(rd.Count, 1, "1 条跨 mod 落败");
            T.Eq(rd[0].Severity, Severity.Info, "RD2025 是 info（跨 mod 覆盖是生态常态，报 error 等于制造噪音）");
            T.Eq(rd[0].FileAbs, Path.Combine(q.Root, "Defs", "z.xml"), "诊断必须定位到**败者**文件");
            T.Eq(rd[0].Evidence!["winnerFile"], Path.Combine(p.Root, "Defs", "a.xml"), "胜者文件");
            T.Eq(rd[0].Evidence!["loserFile"], Path.Combine(q.Root, "Defs", "z.xml"), "败者文件");
            T.Eq(rd[0].Evidence!["defName"], "SynClash", "def 名");
            T.Eq(rd[0].Evidence!["winnerMod"], "syn.win", "胜者来源 mod");
            T.Eq(rd[0].Evidence!["winnerModName"], "syn.win", "胜者来源 mod 的展示名");
            T.Eq(rd[0].Evidence!["winnerSource"], "Local", "胜者来源类型");
            T.Eq(rd[0].Evidence!["defType"], "ThingDef", "DefType 必须一并打印（唯一性是按 DefType 的）");
            T.Contains(rd[0].Message, "SynClash", "消息里要能直接看到 def 名");
            Console.WriteLine($"    [RD2025] {rd[0].Message}");
        });

        T.Case("★★ 胜者判定键（证伪型）：把加载序反过来，胜者必须跟着换", () =>
        {
            using var p = new TempMod("syn.win", "<loadFolders><v1.6><li>/</li></loadFolders>", "Defs");
            using var q = new TempMod("syn.lose", "<loadFolders><v1.6><li>/</li></loadFolders>", "Defs");
            p.Write(Path.Combine("Defs", "a.xml"), TempMod.ThingDefs("SynClash"));
            q.Write(Path.Combine("Defs", "z.xml"), TempMod.ThingDefs("SynClash"));
            var mp = p.ToModInfo();
            var mq = q.ToModInfo();

            var d1 = RunShadowing(BuildContext(new[] { mp, mq },
                LoadOrder.FromEntries(new[] { (mp, LoadOrigin.ActiveMods), (mq, LoadOrigin.ActiveMods) })));
            var d2 = RunShadowing(BuildContext(new[] { mp, mq },
                LoadOrder.FromEntries(new[] { (mq, LoadOrigin.ActiveMods), (mp, LoadOrigin.ActiveMods) })));

            T.Eq(d1.Single(x => x.Code == Codes.DefShadowed).Evidence!["winnerMod"], "syn.win", "序 A 在前 ⇒ A 赢");
            T.Eq(d2.Single(x => x.Code == Codes.DefShadowed).Evidence!["winnerMod"], "syn.lose",
                "序反过来 ⇒ 胜者必须跟着换（若键用错，这里会不动，用例就会失败）");
        });

        T.Case("★ 报告过滤器：目标 mod 不参与的遮蔽组不进报告（但统计仍按整个 scope 算）", () =>
        {
            using var p = new TempMod("syn.win", "<loadFolders><v1.6><li>/</li></loadFolders>", "Defs");
            using var q = new TempMod("syn.lose", "<loadFolders><v1.6><li>/</li></loadFolders>", "Defs");
            using var r = new TempMod("syn.other", "<loadFolders><v1.6><li>/</li></loadFolders>", "Defs");
            p.Write(Path.Combine("Defs", "a.xml"), TempMod.ThingDefs("SynClash"));
            q.Write(Path.Combine("Defs", "z.xml"), TempMod.ThingDefs("SynClash"));
            r.Write(Path.Combine("Defs", "r.xml"), TempMod.ThingDefs("SynOther"));

            var mp = p.ToModInfo();
            var mq = q.ToModInfo();
            var mr = r.ToModInfo();
            var mods = new[] { mp, mq, mr };
            var order = LoadOrder.FromEntries(new[] { (mp, LoadOrigin.ActiveMods), (mq, LoadOrigin.ActiveMods), (mr, LoadOrigin.ActiveMods) });

            var filtered = RunShadowing(BuildContext(mods, order, target: "syn.other"));
            T.Eq(filtered.Count(d => d.Code == Codes.DefShadowed), 0, "目标 mod 不参与 ⇒ 不报");

            // ★ 但 Analyze 的统计必须仍是整个 scope 的（「量的」与「报的」是两件事）。
            var full = ShadowingCheck.Analyze(BuildContext(mods, order, target: "syn.other"));
            T.Eq(full.Stats.DuplicateGroups, 1, "统计不随报告过滤而变");
            T.Eq(full.Stats.CrossModLosingDeclarations, 1, "落败声明计数不随报告过滤而变");
        });

        // ═══════════════ 真语料：零假阳门 ═══════════════

        T.Case("★ 官方 6 个包（Core + 5 DLC）：RD2026 必须为 0（新规则不许在原版内容上造出 error）", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
            var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;
            var order = LoadOrder.Build(env, cat, active, LoadOrder.ReadKnownExpansions(env.ModsConfigPath));

            var official = cat.All.Where(m => m.Source == ModSource.Official).ToList();
            T.Eq(official.Count, 6, "官方包数");

            var ctx = BuildContext(official, order);
            var diags = RunShadowing(ctx);
            var an = ShadowingCheck.Analyze(ctx);

            Console.WriteLine($"    [官方] def={ctx.Defs.Count} 重复组={an.Stats.DuplicateGroups}"
                + $" 单 mod 组={an.Stats.GroupsInSingleMod} 跨 mod 组={an.Stats.GroupsAcrossMods}"
                + $" RD2026={diags.Count(d => d.Code == Codes.DefDuplicateWithinMod)}"
                + $" RD2025={diags.Count(d => d.Code == Codes.DefShadowed)}"
                + $" error={diags.Count(d => d.Severity == Severity.Error)}");

            T.Eq(diags.Count(d => d.Severity == Severity.Error), 0,
                "官方内容上 RD2026 必须 0 —— 否则就是我们的遮蔽判据错了（原版 Core↔DLC 之间的覆盖是合法的）");
        });

        T.Case("★ active 全集（17 个 mod）：RD2026=0 / RD2025=1 / 没有任何 error", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
            var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;
            var order = LoadOrder.Build(env, cat, active, LoadOrder.ReadKnownExpansions(env.ModsConfigPath));

            var ctx = BuildContext(cat.Active, order);
            var diags = RunShadowing(ctx);
            var an = ShadowingCheck.Analyze(ctx);

            Console.WriteLine($"    [active] def={ctx.Defs.Count} 重复组={an.Stats.DuplicateGroups}"
                + $" 单 mod 组={an.Stats.GroupsInSingleMod} 跨 mod 组={an.Stats.GroupsAcrossMods}"
                + $" RD2026={diags.Count(d => d.Code == Codes.DefDuplicateWithinMod)}"
                + $" RD2025={diags.Count(d => d.Code == Codes.DefShadowed)}"
                + $" RD2011={diags.Count(d => d.Code == Codes.LoadOrderViolation)}");

            T.Eq(diags.Count(d => d.Severity == Severity.Error), 0, "active 全集上不得有 error");
            T.Eq(diags.Count(d => d.Code == Codes.DefDuplicateWithinMod), 0, "active 全集上没有 mod 内重复声明");

            // ★ 与既有的 DuplicateDefNameCheck（RD2004）口径互相对齐：active 上真重名组恰好 1 组
            //   （ThingSetMakerDef|MapGen_AncientComplexRoomLoot_Default，Core 与 Ideology 各一份）。
            T.Eq(an.Stats.DuplicateGroups, 1, "active 全集的重复组必须与已知的 1 组一致");
            T.Eq(an.Stats.GroupsAcrossMods, 1, "那一组跨 Core/Ideology ⇒ 跨 mod 组 = 1");
            T.Eq(diags.Count(d => d.Code == Codes.DefShadowed), 1, "RD2025 恰好 1 条（一个落败声明）");
        });

        // ═══════════════ Step 4：全库三数与历史探针对账 ═══════════════

        T.Case("★★ Step 4 全库重复组：def 级三数 + 文件级三数（与历史探针 13195/3488/9707 逐项对账）", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
            var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;
            var order = LoadOrder.Build(env, cat, active, LoadOrder.ReadKnownExpansions(env.ModsConfigPath));

            var mods = cat.All;
            var ctx = BuildContext(mods, order);

            // ── 口径 A：def 级（= 本 Task 实际实现的判据）──
            var an = ShadowingCheck.Analyze(ctx);
            var s = an.Stats;
            Console.WriteLine($"    [全库/def 级] mods={mods.Count} xml={ctx.Files.Count} defs={ctx.Defs.Count}");
            Console.WriteLine($"    [全库/def 级] 重复组={s.DuplicateGroups}"
                + $" （单 mod 组={s.GroupsInSingleMod} + 跨 mod 组={s.GroupsAcrossMods}）");
            Console.WriteLine($"    [全库/def 级] 有 mod 内重复的组={s.GroupsWithAnyWithinModDup}"
                + $"  (mod,DefType,defName) 三元组重复数={s.WithinModGroups}");
            Console.WriteLine($"    [全库/def 级] 落败声明：同 mod={s.SameModLosingDeclarations} 跨 mod={s.CrossModLosingDeclarations}"
                + $"  → RD2026={an.Diagnostics.Count(d => d.Code == Codes.DefDuplicateWithinMod)}"
                + $" RD2025={an.Diagnostics.Count(d => d.Code == Codes.DefShadowed)}");

            // 自洽：两个划分都必须闭合（口径写不清楚正是本项目历史上出错的原因）。
            T.Eq(s.GroupsInSingleMod + s.GroupsAcrossMods, s.DuplicateGroups,
                "「单 mod 组 + 跨 mod 组」必须精确等于重复组数（同一个划分）");
            T.Eq(an.Diagnostics.Count(d => d.Code == Codes.DefDuplicateWithinMod), s.WithinModGroups,
                "RD2026 条数必须等于 (mod,DefType,defName) 三元组重复数");
            T.Eq(an.Diagnostics.Count(d => d.Code == Codes.DefShadowed), s.CrossModLosingDeclarations,
                "RD2025 条数必须等于跨 mod 落败声明数");

            // ── 口径 C：按 **defName 全局**分组（不分 DefType）—— 已被项目证伪的口径 ──
            //    算它只为判定「探针的三数是否来自这个口径」（见用例注释）。
            var gAll = GlobalNameGroups(ctx, includeAbstract: true);
            var gNoAbs = GlobalNameGroups(ctx, includeAbstract: false);
            Console.WriteLine($"    [全库/全局 defName] 重复组={gAll.Total}"
                + $" （同 mod 内重复={gAll.WithinMod} 跨 mod={gAll.CrossMod}；含抽象声明）");
            Console.WriteLine($"    [全库/全局 defName] 重复组={gNoAbs.Total}"
                + $" （同 mod 内重复={gNoAbs.WithinMod} 跨 mod={gNoAbs.CrossMod}；排除抽象声明）");

            // ── 口径 B：文件级（同一相对键被 ≥2 个 (mod, folderIndex) 提供）──
            var fl = FileLevelDuplicates(mods);
            Console.WriteLine($"    [全库/文件级] 文件（未去重计数）={fl.Files} 唯一相对键={fl.UniqueKeys}"
                + $" 被丢弃的重复候选={fl.Files - fl.UniqueKeys}");
            Console.WriteLine($"    [全库/文件级] 重复键组={fl.Total} （同 mod 内重复={fl.WithinMod} 跨 mod 同名={fl.CrossMod}）"
                + $" 其中「同 mod 内且跨 mod」={fl.Both}");

            T.True(s.DuplicateGroups > 1000, $"全库重复组应 > 1000，实际 {s.DuplicateGroups}");
            T.True(fl.Total > 1000, $"文件级重复键应 > 1000，实际 {fl.Total}");
        });

        T.Case("★★ 官方 6 包上跑【全部】默认规则 → error 仍必须 0（回归门：新规则不许破坏它）", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
            var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;
            var order = LoadOrder.Build(env, cat, active, LoadOrder.ReadKnownExpansions(env.ModsConfigPath));

            var official = cat.All.Where(m => m.Source == ModSource.Official).ToList();
            var diags = CheckRunner.Default().Run(BuildContext(official, order));
            var errors = diags.Where(d => d.Severity == Severity.Error).ToList();

            Console.WriteLine($"    [官方/全规则] 诊断={diags.Count} error={errors.Count}"
                + $"  RD2025={diags.Count(d => d.Code == Codes.DefShadowed)}"
                + $" RD2011={diags.Count(d => d.Code == Codes.LoadOrderViolation)}");
            T.Eq(errors.Count, 0, "官方内容上 error 必须 0（假阳比漏报更贵）");
        });
    }

    // ───────────────────────── 文件级重复键测量（Step 4 对账用） ─────────────────────────

    private sealed record GroupStats(int Total, int WithinMod, int CrossMod);

    /// <summary>
    /// 按 **defName 全局**（**不分 DefType**）统计重复组。
    ///
    /// ★ 这【不是】正确口径：defName 的唯一性是按 <c>(DefType, defName)</c> 的（项目已用三路证据钉死，
    ///   见 DefIndex / DuplicateDefNameCheck 的类注释：active 集合上跨类型同名 1220 组全是合法的）。
    ///   这里把它算出来只有一个目的：检验历史探针的 13195/3488/9707 是不是来自这个被证伪的口径。
    /// </summary>
    private static GroupStats GlobalNameGroups(CheckContext ctx, bool includeAbstract)
    {
        int total = 0, within = 0, cross = 0;
        foreach (var kv in ctx.Index.ByDefName)
        {
            var list = includeAbstract ? kv.Value : kv.Value.Where(d => !d.IsAbstract).ToList();
            if (list.Count < 2) continue;
            total++;
            if (list.GroupBy(d => d.ModPackageId, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() >= 2)) within++;
            if (list.Select(d => d.ModPackageId).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2) cross++;
        }
        return new GroupStats(total, within, cross);
    }

    private sealed record FileLevelStats(
        int Files, int UniqueKeys, int Total, int WithinMod, int CrossMod, int Both);

    /// <summary>
    /// **文件级**重复键统计：键 = **相对各自文件夹根**的路径（引擎字典键），贡献者 = (packageId, folderIndex)。
    /// 与 <see cref="DefFileEnumerator"/> 不同，这里**故意不去重**，才能数出「被丢弃的候选」。
    ///
    /// ★ 这不是生产逻辑，只是一条测量探针：用来判定历史探针的三数到底是 def 级还是文件级口径。
    /// </summary>
    private static FileLevelStats FileLevelDuplicates(IReadOnlyList<ModInfo> mods)
    {
        var contributors = new Dictionary<string, List<(string Pkg, int Index)>>(StringComparer.Ordinal);
        var files = 0;

        foreach (var m in mods)
        {
            var folders = m.Resolution?.DescendingOrder ?? m.LoadFolders;
            for (var i = 0; i < folders.Count; i++)
            {
                foreach (var sub in DefFileEnumerator.KnownSubfolders)
                {
                    var di = Path.Combine(folders[i], sub);
                    if (!Directory.Exists(di)) continue;

                    string[] found;
                    try { found = Directory.GetFiles(di, "*.xml", SearchOption.AllDirectories); }
                    catch { continue; }

                    foreach (var full in found)
                    {
                        var name = Path.GetFileName(full);
                        if (name.StartsWith("._", StringComparison.Ordinal)) continue;
                        if (name.StartsWith(".", StringComparison.Ordinal)) continue;
                        if (full.Length <= folders[i].Length + 1) continue;

                        var key = full.Substring(folders[i].Length + 1);
                        files++;
                        if (!contributors.TryGetValue(key, out var l))
                            contributors[key] = l = new List<(string, int)>();
                        l.Add((m.PackageId, i));
                    }
                }
            }
        }

        int total = 0, within = 0, cross = 0, both = 0;
        foreach (var kv in contributors)
        {
            if (kv.Value.Count < 2) continue;
            total++;
            var anyWithin = kv.Value.GroupBy(x => x.Pkg, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() >= 2);
            var modCount = kv.Value.Select(x => x.Pkg).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (anyWithin) within++;
            if (modCount >= 2) cross++;
            if (anyWithin && modCount >= 2) both++;
        }

        return new FileLevelStats(files, contributors.Count, total, within, cross, both);
    }
}
