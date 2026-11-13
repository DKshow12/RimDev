using System.Text;
using RimDev.Core.Catalog;
using RimDev.Core.Checks;
using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;
using RimDev.Core.Metadata;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// 第 3 期 Task 2c：<c>Verse.XmlToObjectUtils.DoFieldSearch</c> 里「直接按名查不到之后」的另外三条分支
///
/// <list type="number">
/// <item><b>② 别名</b>（<c>LoadAliasAttribute</c>，<c>EqualsIgnoreCase</c>）⇒ 命中即不报 RD4001；</item>
/// <item><b>②' <c>[Unsaved]</c></b>（<c>allowLoading == false</c>）⇒ RD4009(error)—— <b>只在别名路径上可达</b>；</item>
/// <item><b>④ 抑制</b>（元素属性 <c>IgnoreIfNoMatchingField="true"</c> / 类型属性 <c>IgnoreSavedElementAttribute</c>）
///   ⇒ RD4010(info)，不报 RD4001。</item>
/// </list>
///
/// ★ 用例设计纪律（与本项目既有做法一致）：每条断言都尽可能做成
///   「旧实现必失败 / 新实现必成功」的**可分辨样本**，并配**能证伪新实现**的反例
///   （例如「<c>&lt;shortHash&gt;</c> 直接命中 [Unsaved] 字段**不许**报 RD4009」——
///   若有人把 [Unsaved] 检查写成「任何命中都检查」，这条会立刻失败）。
/// </summary>
public static class Phase3Task2cCases
{
    public static void Run()
    {
        T.Group("phase3-task2c");

        // ============ ① 别名语料：照引擎（单次 GetFields）钉住 ============
        T.Case("★ 别名表①：PawnKindDef.defaultFactionDef 的别名 defaultFactionType（大小写不敏感）", () =>
        {
            var catalog = LoadCatalog();
            var kind = catalog.MembersOf(catalog.ResolveXmlTypeName("PawnKindDef")!);

            var f = kind.FindFieldByAlias("defaultFactionType");
            T.NotNull(f, "defaultFactionType 必须按别名命中（引擎的第二个分支）");
            T.Eq(f!.Name, "defaultFactionDef", "命中的是 defaultFactionDef");
            T.Eq(f.DeclaringType!.Name, "PawnKindDef", "声明类型");
            T.NotNull(kind.FindFieldByAlias("DEFAULTFACTIONTYPE"), "别名比较必须大小写不敏感（引擎 EqualsIgnoreCase）");
            T.NotNull(kind.FindFieldByAlias("DefaultFactionType"), "混合大小写也必须命中");
            T.Null(kind.FindField("defaultFactionType"), "★ 可分辨性：defaultFactionType 不是【字段名】（直接查名必失败，所以旧实现必报 RD4001）");
            T.Null(kind.FindFieldByAlias("defaultFactionDef"), "别名表里没有 defaultFactionDef 这个「别名」（它是字段名，不走别名分支）");
        });

        T.Case("★ 别名表②：StyleItemDef 系（HairDef/BeardDef/FurDef/TattooDef）的 hairGender/hairTags", () =>
        {
            var catalog = LoadCatalog();
            foreach (var t in new[] { "HairDef", "BeardDef", "FurDef", "TattooDef", "StyleItemDef" })
            {
                var m = catalog.MembersOf(catalog.ResolveXmlTypeName(t)!);
                var g = m.FindFieldByAlias("hairGender");
                var tags = m.FindFieldByAlias("hairTags");
                T.NotNull(g, $"{t} 上 hairGender 必须按别名命中");
                T.NotNull(tags, $"{t} 上 hairTags 必须按别名命中");
                T.Eq(g!.Name, "styleGender", $"{t} 的 hairGender 别名指向 styleGender");
                T.Eq(tags!.Name, "styleTags", $"{t} 的 hairTags 别名指向 styleTags");
            }
        });

        T.Case("★ 别名表③：别名表与引擎的「单次 GetFields」逐类型等价（刻意不合并基类）", () =>
        {
            var catalog = LoadCatalog();
            var flags = System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic;

            // 独立复算：完全按引擎的写法（单次 GetFields，无 BaseType 循环）数别名，
            // 与 MemberInfoIndex.AliasCount 对账。两者不等即为实现漂移。
            var mismatches = new List<string>();
            var checkedTypes = 0;
            foreach (var t in catalog.AllTypes)
            {
                // 只抽查「确实带别名的类型」+ 若干派生类型，避免对 1.6 万个类型各建一次成员表（那是这套 API 的正常开销之外的）
                if (!IsInteresting(t.Name) && t.Name != "PawnKindDef") continue;
                checkedTypes++;
                var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var f in t.GetFields(flags))
                        foreach (var a in RimWorldAttributes.LoadAliases(f))
                            expected.Add(a);
                }
                catch { continue; }

                var actual = catalog.MembersOf(t).AliasCount;
                if (actual != expected.Count)
                    mismatches.Add($"{t.Name}: 实现={actual} 引擎口径={expected.Count}");
            }
            Console.WriteLine($"       [别名等价] 抽查类型 {checkedTypes} 个 / 不一致 {mismatches.Count}");
            T.True(checkedTypes >= 6, $"应抽查到至少 6 个类型，实际 {checkedTypes}");
            T.Eq(mismatches.Count, 0, "别名表必须与引擎的单次 GetFields 结果逐类型一致：" + string.Join(" ; ", mismatches));

            static bool IsInteresting(string n) =>
                n is "HairDef" or "BeardDef" or "FurDef" or "TattooDef" or "StyleItemDef"
                  or "SoundDef" or "SubSoundDef" or "NameTriple" or "Trait" or "StorageSettings";
        });

        // ============ ② 别名抑制 RD4001（旧实现必报） ============
        T.Case("★ 别名抑制：defaultFactionType / hairGender / hairTags 一律【不报】RD4001（旧实现必报）", () =>
        {
            var d = RunFieldCheck("alias-ok.xml", """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <PawnKindDef>
                    <defName>SynAliasKind</defName>
                    <defaultFactionType>Spacer</defaultFactionType>
                  </PawnKindDef>
                  <HairDef>
                    <defName>SynAliasHair</defName>
                    <hairGender>Female</hairGender>
                    <hairTags><li>SynTag</li></hairTags>
                  </HairDef>
                  <CreepJoinerFormKindDef>
                    <defName>SynAliasCreep</defName>
                    <defaultFactionType>Spacer</defaultFactionType>
                  </CreepJoinerFormKindDef>
                  <PawnKindDef>
                    <defName>SynAliasKindCase</defName>
                    <DEFAULTFACTIONTYPE>Spacer</DEFAULTFACTIONTYPE>
                  </PawnKindDef>
                </Defs>
                """);
            Print(d, "别名抑制");
            var r4001 = d.Where(x => x.Code == Codes.FieldNotFound || x.Code == Codes.CaseMismatch).ToList();
            T.Eq(r4001.Count, 0, "别名命中必须一律不报 RD4001/RD4005：" +
                string.Join(" | ", r4001.Select(x => x.Code + " " + x.Message)));
            T.Eq(d.Count(x => x.Code == Codes.MissingFieldSuppressed), 0, "别名命中走的是②分支，不该顺带报 RD4010");
        });

        T.Case("★ 别名不跨类型（证伪用例）：ThingDef 上写 hairGender 仍必须 RD4001", () =>
        {
            var d = RunFieldCheck("alias-cross.xml", """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <ThingDef>
                    <defName>SynNoAliasCross</defName>
                    <hairGender>Female</hairGender>
                  </ThingDef>
                </Defs>
                """);
            Print(d, "别名不跨类型");
            var r4001 = d.Where(x => x.Code == Codes.FieldNotFound).ToList();
            T.Eq(r4001.Count, 1, "ThingDef 链上没有 hairGender 别名 ⇒ 必须仍报 RD4001（证明别名不是全局的）");
            T.Contains(r4001[0].Message, "hairGender", "点名字段名");
        });

        T.Case("★ 分支顺序：别名优先于抑制（同时带 IgnoreIfNoMatchingField 也不报 RD4010）", () =>
        {
            // 引擎顺序：①直接命中 → ②别名 → ④抑制；②命中就 return，④根本不会执行。
            var d = RunFieldCheck("alias-vs-ignore.xml", """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <PawnKindDef>
                    <defName>SynAliasVsIgnore</defName>
                    <defaultFactionType IgnoreIfNoMatchingField="True">Spacer</defaultFactionType>
                  </PawnKindDef>
                </Defs>
                """);
            Print(d, "顺序");
            T.Eq(d.Count, 0, "别名命中 ⇒ 既不报 RD4001 也不报 RD4010，实际：" +
                string.Join(" | ", d.Select(x => x.Code + " " + x.Message)));
        });

        // ============ ③ [Unsaved]：可达性与「不许过度报」 ============
        T.Case("★ [Unsaved] 属性读取：真实字段的 allowLoading 取值分布", () =>
        {
            var catalog = LoadCatalog();
            var def = catalog.ResolveXmlTypeName("ThingDef")!;
            var unsaved = FieldReflection.FindField(def, "shortHash", includeStaticFields: true);
            T.NotNull(unsaved, "Def.shortHash 必须存在（它是 [Unsaved] 字段）");
            T.True(RimWorldAttributes.TryGetUnsavedAllowLoading(unsaved!, out var allow), "shortHash 必须带 [Unsaved]");
            T.False(allow, "shortHash 的 allowLoading 必须是 false（引擎据此报错）");

            // 反例侧：原版确实存在 allowLoading = true 的 [Unsaved] 字段（全程序集实测 2 个）
            var prefs = catalog.ResolveXmlTypeName("PrefsData")!;
            var pause = FieldReflection.FindField(prefs, "pauseOnUrgentLetter", includeStaticFields: true);
            T.NotNull(pause, "PrefsData.pauseOnUrgentLetter 必须存在");
            T.True(RimWorldAttributes.TryGetUnsavedAllowLoading(pause!, out var allow2), "它必须带 [Unsaved]");
            T.True(allow2, "pauseOnUrgentLetter 的 allowLoading 必须是 true（引擎放行，我们也不许报）");
        });

        T.Case("★★ 可达性钉子：原版 41 个别名字段【0 个】带 [Unsaved] ⇒ RD4009 在原版类型上恒不触发", () =>
        {
            var catalog = LoadCatalog();
            var flags = System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic;

            var aliasFields = 0;
            var aliasWithUnsaved = 0;
            var aliasTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in catalog.AllTypes)
            {
                System.Reflection.FieldInfo[] fs;
                try { fs = t.GetFields(flags); } catch { continue; }
                foreach (var f in fs)
                {
                    if (RimWorldAttributes.LoadAliases(f).Count == 0) continue;
                    aliasFields++;
                    aliasTypes.Add(t.FullName ?? t.Name);
                    if (RimWorldAttributes.TryGetUnsavedAllowLoading(f, out _)) aliasWithUnsaved++;
                }
            }
            Console.WriteLine($"       [别名语料] 别名行数={aliasFields} 涉及类型={aliasTypes.Count} 其中带[Unsaved]={aliasWithUnsaved}");

            T.True(aliasFields >= 41, $"别名行数应 >= 41（实测口径：见报告），实际 {aliasFields}");
            // ★ 这一条就是「全库 RD4009 = 0」的**原因**：引擎的 [Unsaved] 检查只在别名分支之后可达，
            //   而别名字段里没有任何一个带 [Unsaved]（若将来原版加了，这条会失败并提醒更新报告）。
            T.Eq(aliasWithUnsaved, 0, "原版别名字段里不应有带 [Unsaved] 的（RD4009 在官方类型上恒不触发的依据）");
        });

        T.Case("★★ 不许过度报（证伪用例）：<shortHash> 直接命中 [Unsaved] 字段时【不许】报 RD4009", () =>
        {
            // 这是对「把 [Unsaved] 检查写在任何命中之后」那种（错误）实现的直接证伪：
            // 引擎的 DirectGetFieldByName 命中会提前 return，[Unsaved] 检查根本走不到。
            var d = RunFieldCheck("unsaved-direct.xml", """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <ThingDef>
                    <defName>SynUnsavedDirect</defName>
                    <shortHash>12345</shortHash>
                    <index>7</index>
                  </ThingDef>
                </Defs>
                """);
            Print(d, "Unsaved 直接命中");
            T.Eq(d.Count, 0, "直接命中 [Unsaved] 字段时引擎不报错（提前 return）⇒ 我们也不许报：" +
                string.Join(" | ", d.Select(x => x.Code + " " + x.Message)));
        });

        // ============ ④ 抑制（RD4010） ============
        T.Case("★ 抑制①：IgnoreIfNoMatchingField=\"True\"/\"TRUE\" ⇒ RD4010(info)，不报 RD4001", () =>
        {
            var d = RunFieldCheck("ignore-elem.xml", """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <ThingDef>
                    <defName>SynIgnoreElem</defName>
                    <someFutureField IgnoreIfNoMatchingField="True">1</someFutureField>
                    <anotherFutureField IgnoreIfNoMatchingField="TRUE">1</anotherFutureField>
                    <thirdFutureField IgnoreIfNoMatchingField="true" />
                    <hardField IgnoreIfNoMatchingField="False">1</hardField>
                    <oneField IgnoreIfNoMatchingField="1" />
                  </ThingDef>
                </Defs>
                """);
            Print(d, "抑制①");
            var r4010 = d.Where(x => x.Code == Codes.MissingFieldSuppressed).ToList();
            var r4001 = d.Where(x => x.Code == Codes.FieldNotFound).ToList();

            T.Eq(r4010.Count, 3, "True / TRUE / true 三种写法都必须抑制（InvariantCultureIgnoreCase），实际 " + r4010.Count);
            T.True(r4010.All(x => x.Severity == Severity.Info), "RD4010 必须是 info 级（引擎故意不报，不能当成 error）");
            T.True(r4010.All(x => x.Evidence!["suppressedBy"] == "elementAttribute"), "证据必须写明 suppressedBy=elementAttribute");
            T.True(r4010.All(x => x.Evidence!["suppressedDetail"] == "True"
                                  || x.Evidence!["suppressedDetail"] == "TRUE"
                                  || x.Evidence!["suppressedDetail"] == "true"),
                "证据必须保留元素属性的原始取值");

            T.Eq(r4001.Count, 2, "★ 证伪侧：\"False\" 与 \"1\" **不是** true ⇒ 必须仍报 RD4001，实际 " + r4001.Count);
            T.True(r4001.Any(x => x.Message.Contains("hardField", StringComparison.Ordinal)), "hardField 未被抑制");
            T.True(r4001.Any(x => x.Message.Contains("oneField", StringComparison.Ordinal)), "「1」不等于 true，不许被抑制");
        });

        T.Case("★ 抑制②：IgnoreIfNoMatchingField 写在【合法字段】上时无任何副作用（引擎第①分支先命中）", () =>
        {
            var d = RunFieldCheck("ignore-on-valid.xml", """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <ThingDef>
                    <defName>SynIgnoreOnValid</defName>
                    <label IgnoreIfNoMatchingField="True">x</label>
                  </ThingDef>
                </Defs>
                """);
            Print(d, "抑制②");
            T.Eq(d.Count, 0, "label 是真字段 ⇒ 提前命中，属性无副作用，实际：" +
                string.Join(" | ", d.Select(x => x.Code + " " + x.Message)));
        });

        T.Case("★ 抑制③：类型级 IgnoreSavedElementAttribute 在原版【0 个类型】声明（引擎分支保留但无见证）", () =>
        {
            var catalog = LoadCatalog();
            var holderTypes = new List<string>();
            foreach (var t in catalog.AllTypes)
            {
                if (RimWorldAttributes.IgnoreSavedElements(t).Count > 0) holderTypes.Add(t.FullName ?? t.Name);
            }
            Console.WriteLine($"       [类型级抑制] 声明 IgnoreSavedElementAttribute 的类型数 = {holderTypes.Count}");
            T.Eq(holderTypes.Count, 0,
                "原版应无任何类型声明 IgnoreSavedElementAttribute（实测 0）⇒ RD4010 的 typeAttribute 半边在原版内容上无见证；"
                + "命中的类型：" + string.Join(", ", holderTypes));

            var members = catalog.MembersOf(catalog.ResolveXmlTypeName("ThingDef")!);
            T.False(members.IgnoresSavedElement("renderNodeProperties"), "没有类型级忽略时该谓词必须恒 false");
        });

        // ============ 采集侧：一级子元素的属性现在被采集 ============
        T.Case("★ 采集侧：ScanTopLevelElements 采集一级子元素的 IgnoreIfNoMatchingField（旧实现只取名字与行号）", () =>
        {
            var dir = NewTempDir("rimdev-t2c-attr-");
            var path = Path.Combine(dir, "attr.xml");
            File.WriteAllText(path, """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <ThingDef>
                    <defName>SynAttrCaptured</defName>
                    <alpha IgnoreIfNoMatchingField="True" />
                    <beta />
                  </ThingDef>
                </Defs>
                """, new UTF8Encoding(false));

            var r = DefScanner.ScanFile(path, "syn.t2c", dir);
            T.Null(r.ParseError, "合成文件应解析成功");
            var def = r.Defs.Single();
            var elems = FieldNameCheck.ReadTopLevelElements(path, def.Line, FieldNameCheck.OrdinalWithinLine(r.Defs.ToList(), r.Defs.ToList().IndexOf(def)));
            var alpha = elems.Single(e => e.Name == "alpha");
            var beta = elems.Single(e => e.Name == "beta");
            T.Eq(alpha.IgnoreIfNoMatchingField, "True", "带属性的元素必须把属性值带出来");
            T.Null(beta.IgnoreIfNoMatchingField, "不带属性的元素必须是 null（不能凭空造值）");
            T.True(elems.All(e => e.Line > 0), "行号口径不变");

            // 按文件一次读出的分组路径也必须带属性（两条入口不许分叉）
            var grouped = FieldNameCheck.ReadElementsByDef(path);
            T.True(grouped.TryGetValue((def.Line, FieldNameCheck.OrdinalWithinLine(r.Defs.ToList(), r.Defs.ToList().IndexOf(def))), out var g), "按文件分组应能定位该 def");
            T.Eq(g!.Single(e => e.Name == "alpha").IgnoreIfNoMatchingField, "True", "分组路径同样要带属性");
        });

        // ============ 回归钉子（本 Task 指定的两个） ============
        T.Case("★ 回归钉子：ThingDef|renderNodeProperties 与 HediffDef|uiIconPath 仍是 RD4001(error)", () =>
        {
            var d = RunFieldCheck("nails.xml", """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <ThingDef>
                    <defName>SynNailThing</defName>
                    <renderNodeProperties>
                      <li><nodeClass>SomeNode</nodeClass></li>
                    </renderNodeProperties>
                  </ThingDef>
                  <HediffDef>
                    <defName>SynNailHediff</defName>
                    <uiIconPath>Things/Bad</uiIconPath>
                  </HediffDef>
                </Defs>
                """);
            Print(d, "回归钉子");
            var r4001 = d.Where(x => x.Code == Codes.FieldNotFound).ToList();
            T.Eq(r4001.Count, 2, "两个钉子都必须仍是 RD4001 error，实际：" + r4001.Count);
            T.True(r4001.All(x => x.Severity == Severity.Error), "必须是 error 级");
            T.True(r4001.Any(x => x.Message.Contains("renderNodeProperties", StringComparison.Ordinal)), "含 renderNodeProperties");
            T.True(r4001.Any(x => x.Message.Contains("uiIconPath", StringComparison.Ordinal)), "含 uiIconPath");
        });

        // ============ 真实语料：官方 6 包 ============
        T.Case("★ 官方 6 包：RD4001 / RD4009 必须为 0（三条机制不许在官方内容上造出任何 error）", () =>
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
            var diags = new FieldNameCheck().Run(ctx).ToList();

            var rd4001 = diags.Where(x => x.Code == Codes.FieldNotFound || x.Code == Codes.CaseMismatch).ToList();
            var rd4009 = diags.Where(x => x.Code == Codes.UnsavedFieldBlocked).ToList();
            var rd4010 = diags.Where(x => x.Code == Codes.MissingFieldSuppressed).ToList();
            Console.WriteLine($"       [官方] files={files.Count} defs={scan.Defs.Count} RD4001={rd4001.Count} " +
                              $"RD4009={rd4009.Count} RD4010={rd4010.Count}");

            T.True(scan.Defs.Count > 10_000, $"官方 def 数应 > 10000，实际 {scan.Defs.Count}");
            T.Eq(rd4001.Count, 0, "官方内容上 RD4001 必须 0；命中：" + string.Join(" | ",
                rd4001.Take(10).Select(x => $"{x.DefType}|{x.DefName} {x.Message}")));
            T.Eq(rd4009.Count, 0, "官方内容上 RD4009 必须 0；命中：" + string.Join(" | ",
                rd4009.Take(10).Select(x => x.Message)));
        });

        // ============ 真实语料：active 全集 ============
        T.Case("★ active 全集：RD4001=0 / RD4009=0，并报出 RD4010 的量级", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
            var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;
            var files = DefScanner.CollectFiles(
                cat.Active.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)));
            var scan = DefScanner.Scan(files);
            var idx = DefIndex.Build(scan.Defs);
            var ctx = new CheckContext(idx, scan.Files, cat, scan.Defs, null);
            var diags = new FieldNameCheck().Run(ctx).ToList();

            var rd4001 = diags.Where(x => x.Code == Codes.FieldNotFound || x.Code == Codes.CaseMismatch).ToList();
            var rd4009 = diags.Where(x => x.Code == Codes.UnsavedFieldBlocked).ToList();
            var rd4010 = diags.Where(x => x.Code == Codes.MissingFieldSuppressed).ToList();
            Console.WriteLine($"       [active] files={files.Count} defs={scan.Defs.Count} RD4001={rd4001.Count} " +
                              $"RD4009={rd4009.Count} RD4010={rd4010.Count}");

            T.Eq(rd4001.Count, 0, "active 全集上 RD4001 必须 0（第 2 期 Task 7b 的钉子继续成立）：" +
                string.Join(" | ", rd4001.Take(10).Select(x => $"{x.DefType}|{x.DefName} {x.Message}")));
            T.Eq(rd4009.Count, 0, "active 全集上 RD4009 必须 0：" +
                string.Join(" | ", rd4009.Take(10).Select(x => x.Message)));
        });
    }

    // ───────────────────────── 基础设施 ─────────────────────────

    private static ManagedAssemblyCatalog? _catalog;

    private static ManagedAssemblyCatalog LoadCatalog() =>
        _catalog ??= ManagedAssemblyCatalog.Load(GameEnvProbe.Discover(startDir: Facts.GameRoot).ManagedDir);

    /// <summary>把一段合成 XML 写成临时文件并只跑 FieldNameCheck（隔离其它规则）。</summary>
    private static List<Diagnostic> RunFieldCheck(string fileName, string xml)
    {
        var dir = NewTempDir("rimdev-t2c-fc-");
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, xml, new UTF8Encoding(false));
        var r = DefScanner.ScanFile(path, "syn.t2c", dir);
        if (r.ParseError is not null) throw new InvalidOperationException("合成文件解析失败：" + r.ParseError);

        var defs = r.Defs.ToList();
        var idx = DefIndex.Build(defs);
        var ctx = new CheckContext(idx, new[] { r }, null!, defs, null);
        return new FieldNameCheck().Run(ctx).ToList();
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
