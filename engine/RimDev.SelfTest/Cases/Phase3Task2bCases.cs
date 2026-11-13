using System.Text;
using RimDev.Core.Catalog;
using RimDev.Core.Checks;
using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;
using RimDev.Core.Metadata;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// 第 3 期 Task 2b：RD4001 的两条假阳机制（机制 A：Class= 优先；机制 B：含 '.' 不退化简单名）
/// 与 <see cref="ParentChainCheck"/> 的提速等价性。
///
/// ★ 用例设计纪律（本项目已因此栽过）：每条断言都要是「旧实现必失败 / 新实现必成功」的**可分辨样本**。
///   下面前 7 条都用合成 XML 明确写出「旧实现在这里会报什么」；第 6 条那四个元素名的简单名
///   （AbilityDef / StructureLayoutDef / PrefabDef / HeadTypeDef）**确实存在于原版程序集**
///   （实测 `RimWorld.AbilityDef` / `RimWorld.StructureLayoutDef` / `RimWorld.PrefabDef` / `Verse.HeadTypeDef`），
///   所以旧实现（`Find()` 的简单名兜底）在它们上面必定报 RD4001 —— 这就是「可分辨」的证据。
/// </summary>
public static class Phase3Task2bCases
{
    public static void Run()
    {
        T.Group("phase3-task2b");

        // ============ 机制 A：DefScanner 捕获 Class= ============
        T.Case("★ 机制A：DefScanner 捕获 def 元素上的 Class=，ResolvedTypeName 以 Class= 优先、其次元素名", () =>
        {
            var dir = NewTempDir("rimdev-t2b-scan-");
            var path = Path.Combine(dir, "defs.xml");
            File.WriteAllText(path, """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <Def Class="Rimatomics.RimatomicResearchDef">
                    <defName>SynClass</defName>
                    <PointCost>1</PointCost>
                  </Def>
                  <ThingDef>
                    <defName>SynPlain</defName>
                    <label>plain</label>
                  </ThingDef>
                </Defs>
                """, new UTF8Encoding(false));

            var r = DefScanner.ScanFile(path, "syn.t2b", dir);
            T.Null(r.ParseError, "合成文件应解析成功");
            T.Eq(r.Defs.Count, 2, "两个 def");

            var withClass = r.Defs.First(d => d.DefName == "SynClass");
            T.Eq(withClass.ClassAttr, "Rimatomics.RimatomicResearchDef", "ClassAttr 必须被捕获");
            T.Eq(withClass.ResolvedTypeName, "Rimatomics.RimatomicResearchDef", "有 Class= 时 ResolvedTypeName 用 Class=");
            T.Eq(withClass.DefType, "Def", "元素名仍然保留（DefType 语义不变）");

            var plain = r.Defs.First(d => d.DefName == "SynPlain");
            T.Null(plain.ClassAttr, "无 Class= 时 ClassAttr 为 null");
            T.Eq(plain.ResolvedTypeName, "ThingDef", "无 Class= 时 ResolvedTypeName = 元素名");

            // ★ 子节点的 <li Class="..."> 是【另一件事】，本 Task 不动：深度>1 的 Class 不许被当成 def 的 ClassAttr
            var path2 = Path.Combine(dir, "li.xml");
            File.WriteAllText(path2, """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <ThingDef>
                    <defName>SynLi</defName>
                    <comps>
                      <li Class="Syn.SomeCompProperties" />
                    </comps>
                  </ThingDef>
                </Defs>
                """, new UTF8Encoding(false));
            var r2 = DefScanner.ScanFile(path2, "syn.t2b", dir);
            var li = r2.Defs.Single();
            T.Null(li.ClassAttr, "★ <li Class=> 是子节点多态，绝不能被读成 def 的 Class=");
            T.Eq(li.ResolvedTypeName, "ThingDef", "子节点 Class 不影响 def 的类型解析");
        });

        // ============ 机制 A：Class= 指向 mod 类型 → RD2017 info ============
        T.Case("★ 机制A①：Class= 指向 mod 自定义类型 → RD2017(info)，且【绝不】报 RD4001", () =>
        {
            // 旧实现下的可分辨性：元素名 Def 能解析成 Verse.Def，于是 PointCost 会被判成
            // 「Def 上没有字段 PointCost」→ RD4001（全库实测 1467 条里的一类）。
            var d = RunFieldCheck("a.xml", """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <Def Class="Rimatomics.RimatomicResearchDef">
                    <defName>SynRimatomics</defName>
                    <PointCost>1</PointCost>
                    <WorkType>Mining</WorkType>
                  </Def>
                </Defs>
                """);
            Print(d, "机制A①");
            var r4001 = d.Where(x => x.Code == Codes.FieldNotFound).ToList();
            var r2017 = d.Where(x => x.Code == Codes.UnknownDefType).ToList();
            T.Eq(r4001.Count, 0, "Class= 指向 mod 类型时【不许】报 RD4001（旧实现必然报）："
                + string.Join(" | ", r4001.Select(x => x.Message)));
            T.Eq(r2017.Count, 1, "应报一条 RD2017（info，工具边界）");
            T.Eq(r2017[0].Severity, Severity.Info, "RD2017 必须是 info 级");
            T.Contains(r2017[0].Message, "Rimatomics.RimatomicResearchDef", "RD2017 必须点名 Class= 的值");
            T.Eq(r2017[0].Evidence!["resolutionSource"], "ClassAttribute", "证据里必须写明类型名来自 Class=");
        });

        // ============ 机制 B：含 '.' 的元素名 → RD2017，不退化 ============
        T.Case("★ 机制B②：元素名含 '.'（全名形态）→ RD2017(info)，禁止退化到简单名", () =>
        {
            // 可分辨性：简单名 AbilityDef **存在**（RimWorld.AbilityDef），旧实现会兜底命中它，
            // 于是 castTime 被判成「AbilityDef 上没有字段」→ RD4001（全库 7223 条 VEF.Abilities.AbilityDef 的一类）。
            var d = RunFieldCheck("b.xml", """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <VEF.Abilities.AbilityDef>
                    <defName>SynVefAbility</defName>
                    <castTime>60</castTime>
                    <castSound>Cast</castSound>
                  </VEF.Abilities.AbilityDef>
                </Defs>
                """);
            Print(d, "机制B②");
            T.Eq(d.Count(x => x.Code == Codes.FieldNotFound), 0,
                "含 '.' 的元素名解析不到时必须报 RD2017，而不是拿简单名的字段表报 RD4001");
            var r2017 = d.Where(x => x.Code == Codes.UnknownDefType).ToList();
            T.Eq(r2017.Count, 1, "应报一条 RD2017");
            T.Eq(r2017[0].Evidence!["simpleNameFallback"], "forbidden(dotted)",
                "证据里必须写明「含点 → 禁止简单名兜底」");
        });

        // ============ 回归钉子③：原版元素名 + 真不存在字段 → 仍 RD4001 ============
        T.Case("★ 回归钉子③：原版元素名 + 真不存在字段 → 仍然 RD4001(error)（不许为降数字放宽）", () =>
        {
            // ★ 第 3 期 Task 2c 校准：原钉子之一被证伪，已换成本 Task 指定的两个钉子。
            //  · ThingDef.renderNodeProperties —— 反射确认 ThingDef 链（ThingDef < BuildableDef < Def < Editable）
            //    既无该字段、也无该别名 ⇒ 引擎必报 error。**保留**（本 Task 指定的钉子之一）。
            //  · HediffDef.uiIconPath —— uiIconPath 只声明在 BuildableDef 上（ThingDef 继承它、HediffDef 不继承），
            //    HediffDef 链（HediffDef < Def < Editable）无它 ⇒ 必报 error（全库 24 条真错）。**新增钉子**。
            //  · 原钉子 PawnKindDef.defaultFactionType **已被反射证据证伪**：PawnKindDef.defaultFactionDef 带
            //    [LoadAlias("defaultFactionType")]，引擎认这个旧名 ⇒ 它根本不是 error（全库 2565 条属假阳）。
            //    该断言已删除，改由 Phase3Task2cCases 的「别名抑制」用例从正面钉住。
            var d = RunFieldCheck("c.xml", """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <ThingDef>
                    <defName>SynBadThing</defName>
                    <renderNodeProperties>
                      <li><nodeClass>SomeNode</nodeClass></li>
                    </renderNodeProperties>
                  </ThingDef>
                  <HediffDef>
                    <defName>SynBadHediff</defName>
                    <uiIconPath>Things/Bad</uiIconPath>
                  </HediffDef>
                </Defs>
                """);
            Print(d, "回归钉子③");
            var r4001 = d.Where(x => x.Code == Codes.FieldNotFound).ToList();
            T.Eq(r4001.Count, 2, "两条都必须仍是 RD4001 error，实际：" + r4001.Count);
            T.True(r4001.All(x => x.Severity == Severity.Error), "RD4001 必须是 error 级");
            T.True(r4001.Any(x => x.Message.Contains("renderNodeProperties", StringComparison.Ordinal)), "应含 renderNodeProperties");
            T.True(r4001.Any(x => x.Message.Contains("uiIconPath", StringComparison.Ordinal)), "应含 uiIconPath");
            T.True(r4001.All(x => x.Evidence!["resolutionSource"] == "elementName"), "这两条来自元素名路径");
        });

        // ============ 回归钉子④：Class= 指向原版类型 → 仍 RD4001 ============
        T.Case("★ 回归钉子④：Class= 指向【原版】类型且字段不存在 → 仍 RD4001(error)（Class= 不是免死金牌）", () =>
        {
            var d = RunFieldCheck("e.xml", """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <ThingDef Class="Verse.ThingDef">
                    <defName>SynClassVanilla</defName>
                    <renderNodeProperties />
                  </ThingDef>
                </Defs>
                """);
            Print(d, "回归钉子④");
            var r4001 = d.Where(x => x.Code == Codes.FieldNotFound).ToList();
            T.Eq(r4001.Count, 1, "Class= 命中原版类型时必须照常校验字段");
            T.Eq(r4001[0].Evidence!["validatedTypeName"], "Verse.ThingDef", "校验的是 Class= 指定的类型");
            T.Eq(r4001[0].Evidence!["resolutionSource"], "ClassAttribute", "证据写明来自 Class=");
            T.Eq(r4001[0].Evidence!["defTypeFullName"], "Verse.ThingDef", "解析出的类型必须是 Verse.ThingDef");
        });

        // ============ 回归钉子⑤：四个高频假阳形态全部消失 ============
        T.Case("★ 回归钉子⑤：四个高频假阳（VEF/KCSG/AlphaPrefabs/FacialAnimation）在新实现下全部消失", () =>
        {
            // ★ 这四个元素名的【简单名】确实存在于原版程序集（RimWorld.AbilityDef /
            //   RimWorld.StructureLayoutDef / RimWorld.PrefabDef / Verse.HeadTypeDef），
            //   所以旧实现必然兜底命中并报 RD4001 —— 这是「可分辨样本」的硬证据。
            var catalog = ManagedAssemblyCatalog.Load(GameEnvProbe.Discover(startDir: Facts.GameRoot).ManagedDir);
            foreach (var simple in new[] { "AbilityDef", "StructureLayoutDef", "PrefabDef", "HeadTypeDef" })
                T.NotNull(catalog.Find(simple), $"简单名 {simple} 必须存在于原版程序集（否则本用例不是可分辨样本）");

            var d = RunFieldCheck("f.xml", """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <VEF.Abilities.AbilityDef>
                    <defName>FP_Vef</defName>
                    <targetingParameters />
                    <castTime>60</castTime>
                  </VEF.Abilities.AbilityDef>
                  <KCSG.StructureLayoutDef>
                    <defName>FP_Kcsg</defName>
                    <spawnConduits>false</spawnConduits>
                    <terrainGrid />
                  </KCSG.StructureLayoutDef>
                  <AlphaPrefabs.PrefabDef>
                    <defName>FP_Ap</defName>
                    <shortLabel>short</shortLabel>
                    <layout />
                  </AlphaPrefabs.PrefabDef>
                  <FacialAnimation.HeadTypeDef>
                    <defName>FP_Fa</defName>
                    <texPath>Things/Pawn/Head</texPath>
                  </FacialAnimation.HeadTypeDef>
                </Defs>
                """);
            Print(d, "回归钉子⑤");
            T.Eq(d.Count(x => x.Code == Codes.FieldNotFound), 0,
                "四个高频假阳必须全部消失，实际仍有：" + string.Join(" | ",
                    d.Where(x => x.Code == Codes.FieldNotFound).Select(x => x.Message)));
            T.Eq(d.Count(x => x.Code == Codes.UnknownDefType), 4, "四个 def 各报一条 RD2017（info）");
            T.Eq(d.Count(x => x.Severity == Severity.Error), 0, "这个合成文件上不应有任何 error");
        });

        // ============ 官方 6 包零假阳（真实语料，走完整五条规则） ============
        T.Case("★ 官方 6 包（Core + 5 DLC）：error 0（真实语料，走 CheckRunner.Default 全部规则）", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
            var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;
            var official = cat.All.Where(m => m.Source == ModSource.Official).ToList();

            var files = DefScanner.CollectFiles(
                official.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)));
            var scan = DefScanner.Scan(files);
            var idx = DefIndex.Build(scan.Defs);
            var ctx = new CheckContext(idx, scan.Files, cat, scan.Defs, null);
            var diags = CheckRunner.Default().Run(ctx);
            var errors = diags.Where(x => x.Severity == Severity.Error).ToList();

            Console.WriteLine($"       [官方] mod={official.Count} files={files.Count} defs={scan.Defs.Count} "
                              + $"RD4001={diags.Count(x => x.Code == Codes.FieldNotFound)} "
                              + $"RD2017={diags.Count(x => x.Code == Codes.UnknownDefType)} "
                              + $"error={errors.Count}");
            T.Eq(official.Count, 6, "官方包数（Core + 5 DLC）");
            T.True(scan.Defs.Count > 10_000, $"官方 def 数应 > 10000，实际 {scan.Defs.Count}");
            // ★ Task 6b 改口径：见 AssertOfficialErrorBaseline（「假阳 = 0」，那条真缺陷逐条钉死）。
            Phase3Task6Cases.AssertOfficialErrorBaseline(errors, "phase3-task2b 官方 6 包");
        });

        // ============ 提速的等价性（合成池 + 边界） ============
        T.Case("★ ParentChainCheck 提速：建议与旧实现逐条相同（合成池 + 全部分支/边界）", () =>
        {
            var pool = new List<string>
            {
                // 全等 / 仅大小写 / 前缀（双向）/ 编辑距离 / 长度窗外 / 空串
                "Solid", "solid", "SOLID", "SolidIceBase", "Sol", "Soli",
                "Solidx", "Solidy", "Solidz", "Solids", "Solidw", "Solidv",
                "BenchBase", "Bench", "Benchx", "BlenchBase", "BenehBase",
                "ResourceBase", "ResourcBase", "ResourceBas",
                "abcdefghij", "abcdefghix", "xbcdefghij", "ab", "a",
                "AlphaPrefabs.PrefabDef", "KCSG.StructureLayoutDef",
                "ThingDef", "thingdef", "ThingDefs", "HediffDef",
                "", "Z", "ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ"
            };
            var newPool = new NameSuggestionPool(pool);
            var queries = new[]
            {
                "Solid", "solid", "SOLID", "Solit", "SolidIce", "SolidIceBaseXXX",
                "BenchBase", "BlenchBase", "ResourceBase", "abcdegfhij",
                "ThingDef", "thingde", "HediffDef", "AlphaPrefabs.PrefabDe",
                "Z", "", "ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ", "abcdefghij",
                "SolidIceBase", "ab", "a"
            };

            var mismatches = new List<string>();
            foreach (var q in queries)
            {
                var expected = ReferenceSuggest(pool, q);
                var actual = newPool.Suggest(q);
                if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
                    mismatches.Add($"[{q}] 旧=[{string.Join(",", expected)}] 新=[{string.Join(",", actual)}]");
            }
            Console.WriteLine($"       [提速等价·合成] 查询 {queries.Length} 个 / 不一致 {mismatches.Count}");
            T.Eq(mismatches.Count, 0, "建议必须逐条相同：" + string.Join(" ; ", mismatches));
        });

        // ============ 提速的等价性（真实 active 语料，1.4 万个真实名字的真实同分结构） ============
        T.Case("★ ParentChainCheck 提速：真实语料（active 全集名字池）上逐条等价", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
            var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;
            var files = DefScanner.CollectFiles(
                cat.Active.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)));
            var scan = DefScanner.Scan(files);
            var idx = DefIndex.Build(scan.Defs);

            // ★ 顺序必须与旧实现逐字相同（ByDefName.Keys 再 ByAbstractName.Keys，保序去重）
            var ordered = idx.ByDefName.Keys.Concat(idx.ByAbstractName.Keys).Distinct().ToList();
            var pool = NameSuggestionPool.FromIndex(idx);
            T.Eq(pool.PoolSize, ordered.Count, "池大小必须等于旧实现的池大小");

            // 抽样查询：真实名字 + 故意写错的近邻（后者才是走 Levenshtein 的那条路）
            var queries = new List<string>();
            for (var i = 0; i < ordered.Count; i += 137) queries.Add(ordered[i]);
            foreach (var q in queries.Take(40).ToList())
            {
                queries.Add(q + "X");
                queries.Add(q.Length > 2 ? q[..^1] : q + "Y");
            }

            var mismatches = new List<string>();
            foreach (var q in queries)
            {
                var expected = ReferenceSuggest(ordered, q);
                var actual = pool.Suggest(q);
                if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
                    mismatches.Add($"[{q}] 旧=[{string.Join(",", expected)}] 新=[{string.Join(",", actual)}]");
            }
            Console.WriteLine($"       [提速等价·真实] 池={pool.PoolSize} 查询={queries.Count} "
                              + $"相似度调用={pool.SimilarityCalls} 预筛剪掉={pool.CandidatesSkipped} "
                              + $"memo命中={pool.MemoHits} 不一致={mismatches.Count}");
            T.True(pool.PoolSize > 10_000, $"active 名字池应 > 10000，实际 {pool.PoolSize}");
            T.Eq(mismatches.Count, 0, "真实语料上建议必须逐条相同：" + string.Join(" ; ", mismatches.Take(5)));
        });

        // ============ 提速的等价性（带状 Levenshtein 的截断必须安全） ============
        T.Case("★ 带状 Levenshtein 的截断安全：0.6 相似度阈值附近的候选不被误剪/误判", () =>
        {
            // 构造「编辑距离刚好压在 0.4*maxLen 边界上」的名字：maxLen=10 时 d=4 → sim=0.6（算命中）；
            // d=5 → 0.5（不算）。两种都必须与旧实现同判。
            var pool = new List<string>
            {
                "abcdefghij",           // 基准
                "abcdXfghij",           // d=1
                "abcdefghiX",           // d=1
                "abcdeXghiX",           // d=2
                "abcdXXghiX",           // d=3
                "abcdXXXhiX",           // d=4  → sim=0.6（命中）
                "abcdXXXXiX",           // d=5  → sim=0.5（不命中）
                "abcXXXXXiX",           // d=6
                "abcde",                // 长度差 5 = 0.5*maxLen → 剔除
                "abcd",                 // 长度差 6 → 剔除
                "abcdef",               // 长度差 4 = 0.4*maxLen → 剔除（d>=4 且 maxLen=10 → sim<=0.6）
                "abcdefg",              // 长度差 3
                "abcdefgh",             // 长度差 2  → 前缀关系？不，是「b 是 a 的前缀」→ 前缀分支
                "abcdefghijX"           // 前缀分支（a 是 b 的前缀）
            };
            var newPool = new NameSuggestionPool(pool);
            foreach (var q in new[] { "abcdefghij", "abcdefghijX", "abcdXXXXXj", "ABCDEFGHIJ", "abcdefghijk" })
            {
                var expected = ReferenceSuggest(pool, q);
                var actual = newPool.Suggest(q);
                Console.WriteLine($"       [边界] q={q} 旧=[{string.Join(",", expected)}] 新=[{string.Join(",", actual)}]");
                T.True(expected.SequenceEqual(actual, StringComparer.Ordinal),
                    $"边界样本 q={q} 必须逐条相同：旧=[{string.Join(",", expected)}] 新=[{string.Join(",", actual)}]");
            }
        });
    }

    // ───────────────────────── 基础设施 ─────────────────────────

    /// <summary>
    /// 把一段合成 XML 写成临时文件并让 <see cref="FieldNameCheck"/> 跑一遍（只跑该规则，隔离其他规则）。
    /// ★ Catalog 传 null：FieldNameCheck 只用 ctx.Defs / ctx.TargetModPackageId（若以后用到 Catalog，
    ///   这条测试会以 NullReference 的形式立刻暴露，属于可接受的失败方式）。
    /// </summary>
    private static List<Diagnostic> RunFieldCheck(string fileName, string xml)
    {
        var dir = NewTempDir("rimdev-t2b-fc-");
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, xml, new UTF8Encoding(false));
        var r = DefScanner.ScanFile(path, "syn.t2b", dir);
        if (r.ParseError is not null) throw new InvalidOperationException("合成文件解析失败：" + r.ParseError);

        var defs = r.Defs.ToList();
        var idx = DefIndex.Build(defs);
        var ctx = new CheckContext(idx, new[] { r }, null!, defs, null);
        return new FieldNameCheck().Run(ctx).ToList();
    }

    /// <summary>旧实现逐字复制版（只用于自测对照，**不要**在生产路径上调用）。</summary>
    private static List<string> ReferenceSuggest(IEnumerable<string> pool, string parentName)
    {
        return pool
            .Select(n => (n, s: MemberInfoIndex.Similarity(parentName, n)))
            .Where(x => x.s > 0)
            .OrderByDescending(x => x.s)
            .Take(5)
            .Select(x => x.n)
            .ToList();
    }

    private static string NewTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Print(IEnumerable<Diagnostic> diags, string tag)
    {
        foreach (var d in diags)
            Console.WriteLine($"       [{tag}] {d.Severity.ToString().ToLowerInvariant()} [{d.Code}] line={d.Line} {d.Message}");
    }
}
