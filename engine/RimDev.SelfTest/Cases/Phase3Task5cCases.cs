using System.Text;
using RimDev.Core.Catalog;
using RimDev.Core.Checks;
using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// 第 3 期 Task 5c — <c>FieldNameCheck</c> 的**归属键**：同一物理行有多个 def 时，
/// 每个 def 只能校验**它自己**的一级子元素。
///
/// 背景（`08-第3期验收记录.md` §5.6，判据已经做实，本 Task 只修）：原实现按 <c>defLine</c> 单键建桶，
/// 于是同一物理行上的 N 个 def 各自被喂了 <b>N 个 def 的全部一级子元素的并集</b>，
/// 「另一个 def 的字段」被拿本 def 的类型去校验 ⇒ RD4001 假阳（全库 66 条 / active 31 条）。
///
/// ★ 本文件的用例设计原则（任务书 Step 2）：**必须能分辨修前/修后**。
///   所以判别性用例里除了断言「修后正确」，还把**旧实现的行为在本用例内就地仿真一遍**
///   （<see cref="SimulatePreFixBuckets"/>，只按行号建桶）并要求那两条断言在旧口径下必然失败
///   —— 若哪天有人把键改回单行号，这条用例会当场变红，而不是「依然绿」。
/// </summary>
public static class Phase3Task5cCases
{
    // ───────────────────────── 夹具 ─────────────────────────

    /// <summary>
    /// ★ 判别性夹具：`&lt;Defs&gt;` 下**两个不同类型的 def 落在同一个物理行**（第 4 行）。
    /// `ThingDef` 带 `graphicData`（ThingDef 真字段）、`RecipeDef` 带 `jobString`（RecipeDef 真字段）。
    /// 行号刻意写死（5 行），避免依赖具体换行。
    /// </summary>
    private const string CompactSingleLineFixture = """
        <?xml version="1.0" encoding="utf-8"?>
        <Defs>
          <ThingDef><defName>SynCompactForge</defName><graphicData><texPath>Things/Building/Forge</texPath></graphicData></ThingDef><RecipeDef><defName>SynCompactRecipe</defName><jobString>Making something</jobString></RecipeDef>
          <ThingDef><defName>SynSpreadOut</defName><graphicData><texPath>Things/Building/Other</texPath></graphicData></ThingDef>
        </Defs>
        """;

    private static string NewTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 旧实现口径的就地仿真：**只按 def 起始行**建桶（同一行的全部 def 共享一个桶）。
    /// 用来证明本文件的判别性断言在修前必然失败。
    /// </summary>
    private static Dictionary<int, List<(string Name, int Line, string? IgnoreIfNoMatchingField)>>
        SimulatePreFixBuckets(string fileAbs)
    {
        var byDefLine = new Dictionary<int, List<(string, int, string?)>>();
        foreach (var lineGroup in FieldNameCheck.ScanDefs(fileAbs).GroupBy(x => x.DefLine))
        {
            var bucket = new List<(string, int, string?)>();
            foreach (var def in lineGroup)
                bucket.AddRange(def.Children.Select(c => (c.Name, c.Line, c.IgnoreIfNoMatchingField)));
            byDefLine[lineGroup.Key] = bucket;
        }
        return byDefLine;
    }

    public static void Run()
    {
        T.Group("phase3-task5c");

        // ═══════════════════════════════════════════════════════════════════
        // Step 2 · ★ 判别性用例（修前必失败）
        // ═══════════════════════════════════════════════════════════════════
        T.Case("★ 判别性：同一物理行 ThingDef + RecipeDef 必须各校验【自己的】一级子元素（旧口径必失败）", () =>
        {
            var dir = NewTempDir("rimdev-t5c-compact-");
            var path = Path.Combine(dir, "compact.xml");
            File.WriteAllText(path, CompactSingleLineFixture, new UTF8Encoding(false));

            var r = DefScanner.ScanFile(path, "syn.t5c", dir);
            T.Null(r.ParseError, "合成夹具必须解析成功");
            T.Eq(r.Defs.Count, 3, "夹具里应有 3 个 def");
            var thing = r.Defs.Single(d => d.DefName == "SynCompactForge");
            var recipe = r.Defs.Single(d => d.DefName == "SynCompactRecipe");
            T.Eq(thing.Line, recipe.Line, "★ 前置条件：ThingDef 与 RecipeDef 必须落在**同一物理行**（否则这条用例没有判别力）");

            // ── 修后：按 (line, ordinal) 取桶 ──
            var buckets = FieldNameCheck.ReadElementsByDef(path);
            var all = r.Defs.ToList();
            var thingElems = buckets[(thing.Line, FieldNameCheck.OrdinalWithinLine(all, all.IndexOf(thing)))];
            var recipeElems = buckets[(recipe.Line, FieldNameCheck.OrdinalWithinLine(all, all.IndexOf(recipe)))];

            var thingNames = thingElems.Select(e => e.Name).ToList();
            var recipeNames = recipeElems.Select(e => e.Name).ToList();
            Console.WriteLine($"       [5c] 同行 def 数=2 @ line {thing.Line}；ThingDef 一级子元素 = {string.Join("/", thingNames)}");
            Console.WriteLine($"       [5c]   RecipeDef 一级子元素 = {string.Join("/", recipeNames)}");

            T.True(thingNames.Contains("graphicData"), "ThingDef 必须拿到自己的 graphicData");
            T.False(thingNames.Contains("jobString"), "★ ThingDef **不得**拿到 RecipeDef 的 jobString");
            T.True(recipeNames.Contains("jobString"), "RecipeDef 必须拿到自己的 jobString");
            T.False(recipeNames.Contains("graphicData"), "★ RecipeDef **不得**拿到 ThingDef 的 graphicData");

            // ── 修前口径仿真：同一条断言必须**失败**（证明这条用例有判别力）──
            var oldBuckets = SimulatePreFixBuckets(path);
            var oldThing = oldBuckets[thing.Line].Select(e => e.Name).ToList();
            var oldRecipe = oldBuckets[recipe.Line].Select(e => e.Name).ToList();
            T.True(oldThing.Contains("jobString"),
                "★ 判别力自检：旧口径下 ThingDef 的桶里**必然**含 jobString（这条断言失败说明仿真没写对）");
            T.True(oldRecipe.Contains("graphicData"),
                "★ 判别力自检：旧口径下 RecipeDef 的桶里**必然**含 graphicData（这条断言失败说明仿真没写对）");
            Console.WriteLine($"       [5c] 旧口径仿真：ThingDef 桶 = {string.Join("/", oldThing)} → 含 jobString（= 全库那 66 条假阳的机制）");
        });

        // ═══════════════════════════════════════════════════════════════════
        // 端到端：走真正的 FieldNameCheck.Run，断言 RD4001 不出现
        // ═══════════════════════════════════════════════════════════════════
        T.Case("★ 端到端：紧凑单行夹具上 RD4001 必须为 0（ThingDef 不报 jobString、RecipeDef 不报 graphicData）", () =>
        {
            var dir = NewTempDir("rimdev-t5c-e2e-");
            var path = Path.Combine(dir, "compact.xml");
            File.WriteAllText(path, CompactSingleLineFixture, new UTF8Encoding(false));

            var r = DefScanner.ScanFile(path, "syn.t5c", dir);
            var idx = DefIndex.Build(r.Defs);
            // catalog 传 null!：CheckContext 的 ModCatalog 只有 loadFolders/遮蔽规则用，本规则不读它；
            // 真正的成员表由 FieldNameCheck.Run 自己从 Managed 目录加载（与 Phase3Task2cCases 同一先例）。
            var ctx = new CheckContext(idx, new[] { r }, null!, r.Defs.ToList(), null);
            var diags = new FieldNameCheck().Run(ctx).ToList();

            var rd4001 = diags.Where(d => d.Code == Codes.FieldNotFound || d.Code == Codes.CaseMismatch).ToList();
            Console.WriteLine($"       [5c][e2e] defs={r.Defs.Count} 诊断={diags.Count} RD4001={rd4001.Count}");
            foreach (var d in rd4001)
                Console.WriteLine($"         RD4001: {d.DefType}|{d.DefName} {d.Message} @{Path.GetFileName(d.FileAbs)}:{d.Line}");

            T.Eq(rd4001.Count, 0, "★ 修好后本夹具必须 0 条 RD4001（旧实现会给 ThingDef 报 jobString、给 RecipeDef 报 graphicData）："
                + string.Join(" | ", rd4001.Select(d => $"{d.DefType}|{d.DefName} {d.Message}")));
        });

        // ═══════════════════════════════════════════════════════════════════
        // Step 1 · 归属键的形状：序数语义 + 越界必须取不到（不许静默取邻居）
        // ═══════════════════════════════════════════════════════════════════
        T.Case("★ 归属键：(line, ordinal) 的序数语义正确，且越界序数必须【取不到】而不是取到邻居", () =>
        {
            var dir = NewTempDir("rimdev-t5c-key-");
            var path = Path.Combine(dir, "key.xml");
            File.WriteAllText(path, CompactSingleLineFixture, new UTF8Encoding(false));

            var r = DefScanner.ScanFile(path, "syn.t5c", dir);
            var defs = r.Defs.ToList();
            var line = defs[0].Line;

            T.Eq(defs[0].Line, defs[1].Line, "前两个 def 必须同行");
            T.Eq(FieldNameCheck.OrdinalWithinLine(defs, 0), 0, "同行第 1 个 def 的序数 = 0");
            T.Eq(FieldNameCheck.OrdinalWithinLine(defs, 1), 1, "同行第 2 个 def 的序数 = 1");
            T.Eq(FieldNameCheck.OrdinalWithinLine(defs, 2), 0, "另一行的 def 序数重新从 0 起（序数是【行内】的）");

            var buckets = FieldNameCheck.ReadElementsByDef(path);
            T.True(buckets.ContainsKey((line, 0)), "(line,0) 必须存在");
            T.True(buckets.ContainsKey((line, 1)), "(line,1) 必须存在");
            T.False(buckets.ContainsKey((line, 2)), "★ 越界序数必须**不存在**（消费侧 TryGetValue 取不到就跳过，绝不落到邻居的桶）");

            // ReadTopLevelElements 的 ordinal 参数必须真的生效（否则同行的 def 会拿到同一个桶）
            var e0 = FieldNameCheck.ReadTopLevelElements(path, line, 0).Select(x => x.Name).ToList();
            var e1 = FieldNameCheck.ReadTopLevelElements(path, line, 1).Select(x => x.Name).ToList();
            T.True(e0.Contains("graphicData") && !e0.Contains("jobString"), "ordinal=0 ⇒ ThingDef 的子元素");
            T.True(e1.Contains("jobString") && !e1.Contains("graphicData"), "ordinal=1 ⇒ RecipeDef 的子元素");
        });

        // ═══════════════════════════════════════════════════════════════════
        // Step 1 的「两侧判据是否同一套」：★ 结构对账（ProbeStructure vs DefScanner）
        // ═══════════════════════════════════════════════════════════════════
        T.Case("★ 结构对账（夹具）：元素扫描数到的 def 行/序号必须与 DefScanner 报出的 RawDef 行号逐个一致", () =>
        {
            var dir = NewTempDir("rimdev-t5c-parity-");
            var path = Path.Combine(dir, "parity.xml");
            File.WriteAllText(path, CompactSingleLineFixture, new UTF8Encoding(false));

            var r = DefScanner.ScanFile(path, "syn.t5c", dir);
            var probe = FieldNameCheck.ProbeStructure(path);

            T.Eq(probe.Count, r.Defs.Count, "元素扫描数到的 def 个数必须等于 DefScanner 的 def 个数");
            T.True(probe.Select(x => x.ElementLine).SequenceEqual(r.Defs.Select(d => d.Line)),
                "★ 逐个行号必须一致（这就是「两侧判据同一套」的判据）");
            T.True(probe.All(x => x.ChildCount > 0), "每行上的 def 都应有子元素");
            // 同行序号必须严格是 0,1,…（不是全 0 —— 全 0 就是「整段偏移」的隐患）
            var sameLine = probe.Where(x => x.ElementLine == probe[0].ElementLine).ToList();
            T.Eq(sameLine.Count, 2, "第 1 行上有 2 个 def");
            T.True(sameLine.Select(x => x.OrdinalWithinLine).SequenceEqual(new[] { 0, 1 }),
                "同行序号必须是 0,1（旧实现只能给出一个桶 ⇒ 这里会退化成只有一个序号）");
        });

        // ═══════════════════════════════════════════════════════════════════
        // ★★ 结构对账（真语料）：全库任意文件都成立 —— 这是「不会整段偏移」的正面证据
        // ═══════════════════════════════════════════════════════════════════
        T.Case("★★ 结构对账（active 真语料）：逐文件 probe 行号序列 == DefScanner 行号序列，不一致必须为 0", () =>
        {
            var env = RimDev.Core.Environment.GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
            var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;
            var files = DefScanner.CollectFiles(
                cat.Active.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)));
            var scan = DefScanner.Scan(files);

            int multiDefLines = 0, mismatched = 0, comparedDefs = 0, filesWithDefs = 0;
            var samples = new List<string>();
            foreach (var g in scan.Defs.GroupBy(d => d.FileAbs, StringComparer.OrdinalIgnoreCase))
            {
                var defs = g.ToList();
                var probe = FieldNameCheck.ProbeStructure(g.Key);
                comparedDefs += defs.Count;
                filesWithDefs++;
                if (probe.Count != defs.Count
                    || !probe.Select(x => x.ElementLine).SequenceEqual(defs.Select(d => d.Line)))
                {
                    mismatched++;
                    if (samples.Count < 5)
                        samples.Add($"{Path.GetFileName(g.Key)}: probe={probe.Count} raw={defs.Count}");
                }
                foreach (var lineGroup in defs.GroupBy(d => d.Line))
                    if (lineGroup.Count() > 1)
                    {
                        multiDefLines++;
                        break;
                    }
            }

            Console.WriteLine($"       [5c][对账] files={filesWithDefs} defs={comparedDefs} "
                + $"含多-def-同行的文件={multiDefLines} 不一致文件={mismatched}");
            T.True(filesWithDefs > 0, "必须有可比对的文件（否则这条用例是空谈）");
            T.True(multiDefLines > 0, "★ 真实语料里必须**确实存在**「同一行多个 def」的文件（否则这条用例证明不了修好了）");
            T.Eq(mismatched, 0, "★ 两侧对「什么算一个 def」的判据必须一致（不一致就会从「张冠李戴」变成「整段偏移」）："
                + string.Join(" | ", samples));
        });

        // ═══════════════════════════════════════════════════════════════════
        // 响亮失败：parity 门禁不许在真语料上误报（否则会静默丢整文件的诊断）
        // ═══════════════════════════════════════════════════════════════════
        T.Case("★ parity 门禁不许误报：active 真语料上 FieldNameCheck 不得产出任何「归属键不一致」RD2001", () =>
        {
            var env = RimDev.Core.Environment.GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
            var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;
            var files = DefScanner.CollectFiles(
                cat.Active.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)));
            var scan = DefScanner.Scan(files);
            var idx = DefIndex.Build(scan.Defs);
            var ctx = new CheckContext(idx, scan.Files, cat, scan.Defs, null);
            var diags = new FieldNameCheck().Run(ctx).ToList();

            var mismatch = diags.Where(d => d.Code == Codes.XmlParseFailed
                && d.Message.Contains("def 归属键与 DefScanner", StringComparison.Ordinal)).ToList();
            Console.WriteLine($"       [5c][门禁] active 诊断={diags.Count}／归属键不一致={mismatch.Count}");
            T.Eq(mismatch.Count, 0, "★ 两侧判据一致时这条门禁**一次都不该触发**（触发=报错并跳过整个文件，会静默丢诊断）："
                + string.Join(" | ", mismatch.Take(5).Select(d => d.Message)));
        });
    }


}
