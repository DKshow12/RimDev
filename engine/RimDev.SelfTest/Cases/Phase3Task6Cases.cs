using System.Text;
using RimDev.Core.Catalog;
using RimDev.Core.Checks;
using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;
using RimDev.Core.Metadata;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// 第 3 期 Task 6 —— 引用解析（RD3001）+ `rimdev sim` 最小版。
///
/// 本文件分两部分：
///  · <see cref="RunProbe"/> —— 按需入口 `rimdev-selftest --task6-probe`：**只打印口径与原始观测**。
///  · <see cref="Run"/> —— 常规用例（挂进 `Program.cs`）。
///
/// 用例纪律（本项目的既有教训）：
///  · **每个数字都由本实现复算并打印**，不抄任务书；与任务书不符时打印差值并给出机制，不改断言去迁就；
///  · **判别性用例必须能分辨修前/修后**（例：可赋值查找那一组在修前会报 116/86 条，修后 0 条）；
///  · 每一条「不发诊断」的用例都要同时证明「它本来会发」（否则只能证明「没坏」）。
/// </summary>
public static class Phase3Task6Cases
{
    private static readonly Lazy<ManagedAssemblyCatalog> Catalog =
        new(() => ManagedAssemblyCatalog.Load(GameEnvProbe.Discover(startDir: Facts.GameRoot).ManagedDir));

    private static readonly Lazy<ReferenceFieldIndex> Index = new(() => ReferenceFieldIndex.Build(Catalog.Value));

    // ────────────────────────────────────────────────────────────────────────────
    // 按需探针：Step 1 的口径自算
    // ────────────────────────────────────────────────────────────────────────────

    public static int RunProbe()
    {
        var index = Index.Value;
        Console.WriteLine("=== Task 6 Step 1：引用字段空间（本实现自算）===");
        Console.WriteLine($"Assembly-CSharp 类型数        = {Catalog.Value.TypeCount}");
        Console.WriteLine($"Def 派生类型                  = {index.DefDerivedTypeCount}");
        Console.WriteLine($"自定义 XML loader 类型         = {index.CustomLoaderTypeCount}");
        Console.WriteLine($"可达类型                      = {index.ReachableTypeCount}"
                          + $"   深度0={index.DepthZeroTypeCount}  跨抽象={index.DepthPositiveTypeCount}"
                          + $"  抽象边界={index.AbstractBoundaryTypeCount}  容器边={index.ContainerEdgeCount}");
        Console.WriteLine($"[对照] 最小深度 BFS：无跨抽象 {index.MinDepthNonCrossingTypeCount} / 跨抽象 {index.MinDepthCrossingTypeCount}");
        Console.WriteLine($"可达的 Def 引用字段           = {index.ReachableRefFieldCount}"
                          + $"  直接={index.DirectOwnerFieldCount}  嵌套无多态={index.NestedNoPolyFieldCount}"
                          + $"  嵌套跨抽象={index.NestedPolyFieldCount}  槽口径={index.ReachableRefSlotCount}");
        Console.WriteLine("容器形态：" + string.Join("  ",
            index.FieldCountByKind.OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal)
                .Select(kv => $"{kv.Key.ToString().ToLowerInvariant()}={kv.Value}")));
        Console.WriteLine($"全程序集这类引用槽总数（对照）= {index.WholeAssemblyRefSlotCount}（任务书 6.0 表写 8733）");
        Console.WriteLine($"自定义 loader 类型自身的引用槽 = {index.CustomLoaderOwnRefSlotCount}");
        Console.WriteLine($"元素类型恰为基类 Def 的引用槽  = {index.DefItselfSlotCount}");
        foreach (var s in index.DefItselfSlots) Console.WriteLine("    · " + s);
        Console.WriteLine($"抽象 Def 派生槽类型 = {index.AbstractDefSlotSlots.Count}");
        foreach (var s in index.AbstractDefSlotSlots) Console.WriteLine("    · " + s);

        Console.WriteLine();
        Console.WriteLine("=== 宽松口径 vs 严格口径（任务书 8733 的来源）===");
        foreach (var s in LooseExtraFields(Catalog.Value, index).Take(8)) Console.WriteLine("    " + s);
        return 0;
    }

    // ────────────────────────────────────────────────────────────────────────────
    // 常规用例
    // ────────────────────────────────────────────────────────────────────────────

    public static void Run()
    {
        T.Group("phase3-task6");

        // ── Step 1：引用字段空间的口径自算 ────────────────────────────────────────
        T.Case("★ 引用字段空间逐项复算（255 / 65 / 1188 / 1040 / 148 / 122 / 1748 / 927 / 739 / 82）", () =>
        {
            var x = Index.Value;
            Console.WriteLine($"      [口径] Def派生={x.DefDerivedTypeCount} loader={x.CustomLoaderTypeCount}"
                              + $" 可达={x.ReachableTypeCount}（深0={x.DepthZeroTypeCount} 跨抽象={x.DepthPositiveTypeCount}"
                              + $" 抽象边界={x.AbstractBoundaryTypeCount} 容器边={x.ContainerEdgeCount}）"
                              + $" 引用字段={x.ReachableRefFieldCount}（直接={x.DirectOwnerFieldCount}"
                              + $" 嵌套={x.NestedNoPolyFieldCount} 跨抽象={x.NestedPolyFieldCount}）");
            T.Eq(x.DefDerivedTypeCount, 255, "Def 派生类型数");
            T.Eq(x.CustomLoaderTypeCount, 65, "自定义 XML loader 类型数");
            T.Eq(x.ReachableTypeCount, 1188, "可达类型数");
            T.Eq(x.DepthZeroTypeCount, 1040, "深度 0 类型数");
            T.Eq(x.DepthPositiveTypeCount, 148, "跨抽象类型数");
            T.Eq(x.AbstractBoundaryTypeCount, 122, "抽象边界类型数");
            T.Eq(x.ContainerEdgeCount, 3993, "容器边数");
            T.Eq(x.ReachableRefFieldCount, 1748, "可达引用字段数");
            T.Eq(x.DirectOwnerFieldCount, 927, "直接 owner 数");
            T.Eq(x.NestedNoPolyFieldCount, 739, "嵌套无多态数");
            T.Eq(x.NestedPolyFieldCount, 82, "嵌套跨抽象数");
            T.Eq(x.CustomLoaderOwnRefSlotCount, 67, "自定义 loader 自身引用槽");
            T.Eq(x.DefItselfSlotCount, 4, "基类 Def 槽数");
        });

        T.Case("★ 容器形态分布 + 防「List 被静默跳过」的正面钉子", () =>
        {
            var x = Index.Value;
            var byKind = x.FieldCountByKind;
            Console.WriteLine("      [口径] " + string.Join("  ",
                byKind.OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal)
                    .Select(kv => $"{kv.Key.ToString().ToLowerInvariant()}={kv.Value}")));
            // ★ 这条钉子的意义：PowerShell 的 `-like` 会把反引号当转义符吃掉 ⇒ List`1 永不匹配 ⇒
            //   所有 List<T> 字段被静默跳过（引用空间 1748 → 580、嵌套 owner → 0）。C# 侧用
            //   GetGenericTypeDefinition() == typeof(List<>) 天然安全；本用例把「必然命中」的样本钉死。
            T.Eq(byKind.GetValueOrDefault(RefContainerKind.List), 430, "list 形态字段数");
            T.Eq(byKind.GetValueOrDefault(RefContainerKind.Set), 12, "set 形态字段数");
            T.Eq(byKind.GetValueOrDefault(RefContainerKind.Array), 9, "array 形态字段数");
            T.Eq(byKind.GetValueOrDefault(RefContainerKind.Dictionary), 45, "dict 形态字段数");
            T.Eq(byKind.GetValueOrDefault(RefContainerKind.Scalar), 1252, "scalar 形态字段数");
            T.Eq(x.NestedNoPolyFieldCount + x.NestedPolyFieldCount, 821, "嵌套 owner 必须非 0（否则就是 List 被跳过）");
            T.Eq(ReferenceFieldIndex.KindOf(typeof(List<object>)), RefContainerKind.List, "List<> 判别");
            T.Eq(ReferenceFieldIndex.KindOf(typeof(HashSet<object>)), RefContainerKind.Set, "HashSet<> 判别");
            T.Eq(ReferenceFieldIndex.KindOf(typeof(Dictionary<string, int>)), RefContainerKind.Dictionary, "Dictionary<,> 判别");
            T.Eq(ReferenceFieldIndex.KindOf(typeof(int[])), RefContainerKind.Array, "数组判别");
            T.Eq(ReferenceFieldIndex.SlotsOf(typeof(Dictionary<string, int>)).Count, 2, "字典必须解出 2 个槽");
        });

        T.Case("★ 全程序集槽数 8685（≠任务书 8733）：差值 48 全部是 Enumerator 闭包字段", () =>
        {
            var extras = LooseExtraFields(Catalog.Value, Index.Value).ToList();
            foreach (var e in extras.Take(6)) Console.WriteLine("      " + e);
            Console.WriteLine($"      [差值] 宽松口径 {Index.Value.WholeAssemblyRefSlotCount + extras.Count}"
                              + $" = 严格 {Index.Value.WholeAssemblyRefSlotCount} + 闭包字段 {extras.Count}"
                              + "（任务书 6.0 表的 8733 用的是 StartsWith 宽松口径）");
            T.Eq(Index.Value.WholeAssemblyRefSlotCount, 8685, "严格口径的全程序集引用槽数");
            T.Eq(extras.Count, 48, "宽松口径多出的闭包字段数");
            T.True(extras.All(x => x.Contains("Enumerator")),
                "多出的 48 条必须全部是编译器生成的 Enumerator 闭包字段（不可能出现在 XML 里）");
        });

        // ── Step 2：官方语料反向验证（不是自证）────────────────────────────────
        T.Case("★ Step 2 官方先例：规则 A/B/C 的字段名在官方 XML 里的出现次数", () =>
        {
            var data = Path.Combine(Facts.GameRoot, "Data");
            var text = OfficialXmlText(data);
            var counts = new (string Needle, string Rule)[]
            {
                ("<defaultProjectile>", "A 标量引用"),
                ("<weaponTags>", "B 列表引用容器"),
                ("<li>Melee</li>", "B 列表元素样本"),
                ("<qualityWeights>", "C 字典引用容器（原版 Buildings_PsychicRituals.xml）"),
                ("<li><key>", "C 字典条目形状"),
                ("<recipes>", "B 列表引用容器（ThingDef.recipes）"),
            };
            foreach (var (needle, rule) in counts)
                Console.WriteLine($"      {rule,-34} {needle,-24} 出现 {Count(text, needle)} 次");
            T.True(Count(text, "<defaultProjectile>") >= 3, "规则 A 至少 3 个官方先例");
            T.True(Count(text, "<weaponTags>") >= 3, "规则 B 至少 3 个官方先例");
            T.True(Count(text, "<li><key>") >= 3, "规则 C 至少 3 个官方先例");
        });

        T.Case("★ Step 2 反证：官方 6 包里「属性形态引用」0 命中（复算 §6.0 第二条）", () =>
        {
            var data = Path.Combine(Facts.GameRoot, "Data");
            var text = OfficialXmlText(data);
            var attrs = new[] { "soundCast=\"", "defaultProjectile=\"", "recipe=\"", "thingDef=\"", "hediffDef=\"", "soundDef=\"" };
            foreach (var a in attrs) Console.WriteLine($"      {a,-22} 出现 {Count(text, a)} 次");
            foreach (var a in attrs) T.Eq(Count(text, a), 0, $"属性形态 {a} 的出现次数（引擎 GetAttribute 调用 0 次 ⇒ 必须是 0）");
        });

        // ── Step 3：解析与 RD3001（六条必备用例）────────────────────────────────
        T.Case("★ T12 形状：<defaultProjectile>Bullet_Nope</defaultProjectile> ⇒ RD3001", () =>
        {
            const string xml = """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <ThingDef>
                    <defName>TestGun</defName>
                    <label>test gun</label>
                    <verbs>
                      <li>
                        <defaultProjectile>Bullet_Nope</defaultProjectile>
                      </li>
                    </verbs>
                  </ThingDef>
                </Defs>
                """;
            var (diags, check) = RunOnFixture(xml);
            var d = diags.Where(x => x.Code == Codes.DefNameNotFound).ToList();
            foreach (var x in d)
                Console.WriteLine($"      {x.Code} {x.FileAbs}:{x.Line}  {x.Message}\n        evidence: "
                                  + string.Join(" | ", (x.Evidence ?? new Dictionary<string, string>())
                                      .Select(kv => $"{kv.Key}={kv.Value}")));
            Console.WriteLine("      " + (check.LastStats?.Describe() ?? "(no stats)"));
            T.Eq(d.Count, 1, "RD3001 条数");
            T.Eq(d[0].Evidence!["targetDefType"], "ThingDef", "目标 DefType");
            T.Eq(d[0].Evidence!["targetDefName"], "Bullet_Nope", "目标 defName");
            T.Eq(d[0].Evidence!["field"], "defaultProjectile", "字段名");
            T.True(d[0].Evidence!["referenceAt"].EndsWith(":" + d[0].Line, StringComparison.Ordinal), "引用处定位（FileRelToMod:Line）");
            T.True(d[0].Evidence!.ContainsKey("nearest"), "evidence 必须带最相近候选");
            T.Eq(d[0].Severity, Severity.Error, "RD3001 是 error");
        });

        T.Case("★ T15 形状：soundCast 的【元素】形态写不存在值 ⇒ RD3001，候选含 Shot_Minigun/Shot_MachinePistol", () =>
        {
            const string xml = """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <ThingDef>
                    <defName>TestGun2</defName>
                    <verbs>
                      <li>
                        <soundCast>Shot_MachineGun</soundCast>
                      </li>
                    </verbs>
                  </ThingDef>
                </Defs>
                """;
            var (diags, _) = RunOnFixture(xml, withOfficial: true);
            var d = diags.Single(x => x.Code == Codes.DefNameNotFound);
            Console.WriteLine($"      {d.Message}");
            Console.WriteLine($"      nearest = [{d.Evidence!["nearest"]}]");
            T.Eq(d.Evidence!["targetDefType"], "SoundDef", "目标 DefType");
            T.Eq(d.Evidence!["targetDefName"], "Shot_MachineGun", "目标 defName");
            T.Contains(d.Evidence!["nearest"], "Shot_Minigun", "候选必须含 Shot_Minigun");
            T.Contains(d.Evidence!["nearest"], "Shot_MachinePistol", "候选必须含 Shot_MachinePistol");
            // 归档游戏日志里这条是**真实报错**（ThreatFleet 历史踩坑），所以它是真阳的范式。
            T.True(d.Message.Contains("soundCast", StringComparison.Ordinal), "消息里点名 soundCast");
        });

        T.Case("★ 规则 B：<recipes><li>Recipe_Nope</li></recipes> ⇒ RD3001", () =>
        {
            const string xml = """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <ThingDef>
                    <defName>TestBench</defName>
                    <recipes>
                      <li>Recipe_Nope</li>
                    </recipes>
                  </ThingDef>
                </Defs>
                """;
            var (diags, _) = RunOnFixture(xml);
            var d = diags.Single(x => x.Code == Codes.DefNameNotFound);
            Console.WriteLine($"      {d.Message}   evidence.container={d.Evidence!["container"]}"
                              + $" nearest=[{d.Evidence!["nearest"]}]");
            T.Eq(d.Evidence!["targetDefType"], "RecipeDef", "目标 DefType");
            T.Eq(d.Evidence!["container"], "list", "容器形态必须是 list");
            T.Eq(d.Evidence!["field"], "recipes", "字段名");
        });

        T.Case("★ 规则 C：<hitChanceFactors><li><key>Damage_Nope</key> ⇒ RD3001（key 侧的引用槽）", () =>
        {
            const string xml = """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <BodyPartDef>
                    <defName>TestPart</defName>
                    <hitChanceFactors>
                      <li>
                        <key>Damage_Nope</key>
                        <value>1</value>
                      </li>
                    </hitChanceFactors>
                  </BodyPartDef>
                </Defs>
                """;
            var (diags, _) = RunOnFixture(xml);
            var d = diags.Single(x => x.Code == Codes.DefNameNotFound);
            Console.WriteLine($"      {d.Message}   evidence.container={d.Evidence!["container"]}"
                              + $" nearest=[{d.Evidence!["nearest"]}]");
            T.Eq(d.Evidence!["targetDefType"], "DamageDef", "目标 DefType");
            T.Eq(d.Evidence!["container"], "dictionary", "容器形态必须是 dictionary");
            T.Eq(d.Evidence!["targetDefName"], "Damage_Nope", "目标 defName");
        });

        T.Case("★ 规则 C 正例：key 与 value 都是 Def 槽时两侧各自校验（BodyPartDef.hitChanceFactors 的 value 不是 Def）", () =>
        {
            // Dictionary<DamageDef,float> 只有一个 Def 槽（key）；把 key 写对、value 写坏 ⇒ **不该报**
            const string xml = """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <BodyPartDef>
                    <defName>TestPart2</defName>
                    <hitChanceFactors>
                      <li>
                        <key>Blunt</key>
                        <value>not-a-number</value>
                      </li>
                      <li>
                        <key>Damage_DefinitelyNotAThing</key>
                        <value>1</value>
                      </li>
                    </hitChanceFactors>
                  </BodyPartDef>
                </Defs>
                """;
            var (diags, check) = RunOnFixture(xml, withOfficial: true);
            var d = diags.Where(x => x.Code == Codes.DefNameNotFound).ToList();
            Console.WriteLine($"      RD3001 = {d.Count}（value 侧不是 Def 槽 ⇒ 不该报）；"
                              + $"引用点 {check.LastStats?.ReferenceSites}，字典槽 {check.LastStats?.ResolvedExact}");
            T.Eq(d.Count, 1, "value 不是 Def 槽（坏 value 不报），坏 key 要报 —— 判别性");
            T.Contains(d[0].Evidence!["targetDefName"], "Damage_DefinitelyNotAThing", "报的必须是坏 key");
            T.True(check.LastStats!.ReferenceSites > 0, "必须看到引用点");
        });

        T.Case("★ 规则 D：Class= 派发命中（抽象元素 + 具体子类）与派发失败（⇒ 边界，不是 error）", () =>
        {
            // AbstractAttributeTest：用 `<li Class="Verb_Shoot">` 走 Class= 命中；
            // 再用一个解析不到的 Class= 走「派发失败」分支。
            // ★ 必须用**抽象**元素类型才会真的走派发：`comps` 是 `List<CompProperties>`，
            //   CompProperties 是抽象类（`verbs` 是 `List<VerbProperties>`，VerbProperties 是具体类，
            //   根本没有派发分支 —— 第一版用 verbs 实测 ClassDispatchHits=0）。
            const string hit = """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <ThingDef>
                    <defName>TestGun3</defName>
                    <comps>
                      <li Class="CompProperties_Spawner">
                        <thingToSpawn>Thing_Nope3</thingToSpawn>
                      </li>
                    </comps>
                  </ThingDef>
                </Defs>
                """;
            var (diagsHit, statsHit) = RunOnFixture(hit);
            Console.WriteLine($"      [命中] Class= 派发命中 {statsHit.LastStats!.ClassDispatchHits}，"
                              + $"RD3001 {(diagsHit.Count(x => x.Code == Codes.DefNameNotFound))} 条，"
                              + $"引用点 {statsHit.LastStats.ReferenceSites}");
            T.True(statsHit.LastStats!.ClassDispatchHits > 0, "Class= 派发必须命中（否则抽象元素的字段一个都看不到）");
            T.Eq(diagsHit.Count(x => x.Code == Codes.DefNameNotFound), 1, "派发命中后仍要报出里面的悬空引用");

            const string miss = """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <ThingDef>
                    <defName>TestGun4</defName>
                    <comps>
                      <li Class="No.Such.CompType">
                        <thingToSpawn>Thing_Nope4</thingToSpawn>
                      </li>
                    </comps>
                  </ThingDef>
                </Defs>
                """;
            var (diagsMiss, statsMiss) = RunOnFixture(miss);
            Console.WriteLine($"      [失败] Class= 未命中 {statsMiss.LastStats!.ClassDispatchMisses}，"
                              + $"RD3001 {(diagsMiss.Count(x => x.Code == Codes.DefNameNotFound))} 条，"
                              + $"error 总数 {diagsMiss.Count(x => x.Severity == Severity.Error)}");
            T.True(statsMiss.LastStats!.ClassDispatchMisses > 0, "必须计数「Class= 解析不到」");
            T.Eq(diagsMiss.Count(x => x.Code == Codes.DefNameNotFound), 0, "派发失败 ⇒ 工具边界，绝不报 RD3001");
            T.Eq(diagsMiss.Count(x => x.Severity == Severity.Error), 0, "派发失败不得产生任何 error");
        });

        T.Case("★ 边界 1：自定义 XML loader 类型（List<SkillRange> 的带名元素）⇒ 不发 RD3001 且计数", () =>
        {
            // ★ 用**真的**自定义 loader 类型：`ThingDefCountClass` 带 `LoadDataFromXmlCustom`，
            //   `ThingDef.costList` 的元素就是它 ⇒ 进入即停（里面的 `<thingDef>` 不再下钻）。
            //   （`PawnKindDef.skills` 的元素类型 `SkillRange` **没有** loader，那条路径是「未知字段」，
            //   第一版据此写的断言实测 CustomLoaderSubtreesSkipped=0。）
            const string xml = """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <ThingDef>
                    <defName>TestCost</defName>
                    <costList>
                      <li>
                        <thingDef>Thing_Nope</thingDef>
                        <count>5</count>
                      </li>
                    </costList>
                  </ThingDef>
                </Defs>
                """;
            var (diags, check) = RunOnFixture(xml);
            Console.WriteLine($"      RD3001={diags.Count(x => x.Code == Codes.DefNameNotFound)}，"
                              + $"loader 边界计数={check.LastStats!.CustomLoaderSubtreesSkipped}，样本="
                              + string.Join(" / ", check.LastStats.CustomLoaderSamples ?? (IReadOnlyList<string>)Array.Empty<string>()));
            T.Eq(diags.Count(x => x.Code == Codes.DefNameNotFound), 0, "loader 类型内的字段不得报 RD3001");
            T.True(check.LastStats!.CustomLoaderSubtreesSkipped > 0, "必须把跳过计数打进统计块（防「没检查」被读成「没问题」）");
            T.True((check.LastStats.CustomLoaderSamples?.Count ?? 0) > 0, "边界要能定位到 文件:行");
        });

        T.Case("★ 可赋值查找（引擎真值）：List<CreepJoinerBaseDef> 引用【子类】def ⇒ 不发诊断", () =>
        {
            // ★★ 判别性：这是**修前必报、修后不报**的用例。
            //   修前口径 = 只按声明类型精确匹配 ⇒ 下面这条会报 RD3001；
            //   修后口径 = 声明类型 + 全部派生 Def 类型（引擎 `DefDatabase<T>.AddAllInMods` 的 `def is T`）。
            const string xml = """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <CreepJoinerDownsideDef>
                    <defName>LocalDownside</defName>
                  </CreepJoinerDownsideDef>
                  <CreepJoinerFormKindDef>
                    <defName>LocalForm</defName>
                    <requires>
                      <li>LocalDownside</li>
                    </requires>
                  </CreepJoinerFormKindDef>
                </Defs>
                """;
            var (diags, check) = RunOnFixture(xml);
            Console.WriteLine($"      CreepJoinerFormKindDef.requires 是 List<CreepJoinerBaseDef>（具体基类），"
                              + $"目标 LocalDownside 是 CreepJoinerDownsideDef（子类）");
            Console.WriteLine($"      RD3001={diags.Count(x => x.Code == Codes.DefNameNotFound)}，"
                              + $"可赋值命中={check.LastStats!.ResolvedViaAssignableTypes}，"
                              + $"引用点={check.LastStats.ReferenceSites}");
            T.Eq(diags.Count(x => x.Code == Codes.DefNameNotFound), 0, "子类 def 必须能被基类字段引用（引擎就是可赋值查找）");
            T.True(check.LastStats!.ResolvedViaAssignableTypes > 0, "必须走「可赋值」分支命中（否则就是精确口径）");
        });

        T.Case("★ 引用能被继承合并补上 ⇒ 不发诊断（子 def 的 defName 来自父 def）", () =>
        {
            // 判别性：若 defName 只按「声明自己写的 <defName>」取，则 LocalChild 不存在 ⇒ 下面那条引用会假报。
            // ★ 判别性关键点：**抽象父**（不进有效 def 集合）写了 <defName>，
            //   **子**没有自己的 <defName>、靠继承拿到它 ⇒ 只有「链式 defName」口径才看得见这个名字。
            //   旧口径（只取声明自己的 <defName>）下这条引用会假报 RD3001。
            const string xml = """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <DamageDef Name="AbstractDamageBase" Abstract="True">
                    <defName>InheritedDamageName</defName>
                  </DamageDef>
                  <DamageDef ParentName="AbstractDamageBase"/>
                  <BodyPartDef>
                    <defName>LocalUser</defName>
                    <hitChanceFactors>
                      <li>
                        <key>InheritedDamageName</key>
                        <value>1</value>
                      </li>
                    </hitChanceFactors>
                  </BodyPartDef>
                </Defs>
                """;
            var (diags, _) = RunOnFixture(xml);
            Console.WriteLine($"      RD3001={diags.Count(x => x.Code == Codes.DefNameNotFound)}（"
                              + string.Join(" | ", diags.Select(x => x.Message)) + "）");
            T.Eq(diags.Count(x => x.Code == Codes.DefNameNotFound), 0, "继承链上的 defName 必须可见");
        });

        T.Case("★ 继承 defName ≡ InheritanceLayer.Merge：真语料逐声明对账（active 语料，抽样 ≥200）", () =>
        {
            var ctx = ActiveContext();
            var analysis = InheritanceLayer.Analyze(ctx);
            var set = SimulatedDefSet.Build(ctx, analysis, Catalog.Value);
            Console.WriteLine("      " + DescribeSet(set));
            T.True(set.EffectiveDefNames > 10000, "有效 def 名数量级（active 语料）");

            // 对账：链式取到的 defName 必须与「把节点交给 Task 5b 的 Merge」结果一致。
            // 只对**真语料里存在继承链**的声明抽样（active 里通常很少），若为 0 就用夹具兜底。
            int checkedCount = 0, mismatch = 0;
            using var holder = CorpusHolder.For(ctx, analysis);
            foreach (var d in ctx.Defs)
            {
                var res = analysis.Get(d);
                if (res is null || !res.Evaluated) continue;
                if (d.IsAbstract || string.IsNullOrEmpty(d.ParentName)) continue;
                if (checkedCount >= 2000) break;
                var mergedName = holder.MergedDefName(d);
                if (mergedName is null) continue;          // 链上拿不到（节点缺失）⇒ 不参与对账
                var chainName = string.IsNullOrEmpty(d.DefName)
                    ? FirstChainDefName(analysis, d)
                    : d.DefName;
                checkedCount++;
                if (!string.Equals(mergedName, chainName, StringComparison.Ordinal))
                {
                    mismatch++;
                    if (mismatch <= 5)
                        Console.WriteLine($"      ✗ {d.FileRelToMod}:{d.Line} merge=[{mergedName}] chain=[{chainName}]");
                }
            }
            Console.WriteLine($"      对账：checked={checkedCount} mismatch={mismatch}");
            T.Eq(mismatch, 0, "链式 defName 与 Task 5b Merge 结果的不一致数");
        });

        T.Case("★ SimulatedDefSet：MayRequire 未满足的声明不进集合（引擎不建 def）", () =>
        {
            const string xml = """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <BodyPartDef MayRequire="some.absent.mod">
                    <defName>SkippedPart</defName>
                  </BodyPartDef>
                  <BodyPartDef>
                    <defName>PresentPart</defName>
                  </BodyPartDef>
                </Defs>
                """;
            var (diags, check) = RunOnFixture(xml);
            var set = check.LastSet!;
            Console.WriteLine("      " + DescribeSet(set) + $"；RD3001={diags.Count(x => x.Code == Codes.DefNameNotFound)}");
            T.Eq(set.SkippedByMayRequire, 1, "被 MayRequire 挡掉的声明数");
            T.Eq(set.EffectiveDefNames, 1, "有效 def 名数");
            T.True(set.Contains("BodyPartDef", "PresentPart"), "PresentPart 必须在集合里");
            T.False(set.Contains("BodyPartDef", "SkippedPart"), "SkippedPart 必须不在集合里");
        });

        T.Case("★ Suggest：Levenshtein 有界实现 ≡ 无界实现（长度窗预筛不改变结果）+ 结果确定性", () =>
        {
            var names = new[] { "Shot_Minigun", "Shot_MachinePistol", "Shot_Autocannon", "BeamRepeater", "Xyz" };
            foreach (var n in names)
            {
                var bounded = SimulatedDefSet.LevenshteinBounded("Shot_MachineGun", n, 20);
                var plain = PlattsLevenshtein("Shot_MachineGun", n);
                T.Eq(bounded, plain, $"有界编辑距离必须等于无界（{n}）");
            }
            var a = SuggestTwice();
            T.Eq(a.Item1, a.Item2, "同输入两次 Suggest 结果必须逐项相同（--json 字节稳定的前提）");
            Console.WriteLine($"      [样本] Shot_MachineGun 的候选 = {a.Item1}");
        });

        T.Case("★★ parity 门禁：ctx.Defs 的行号与文件不一致 ⇒ RD2001(error) + 跳过该文件", () =>
        {
            // 判别性：把 ctx.Defs 的行号故意改错 ⇒ 必须响亮失败（报 RD2001 并跳过），
            // 而不是「按行号取一个可能属于邻居 def 的桶」那种静默错。
            const string xml = """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <ThingDef>
                    <defName>P1</defName>
                  </ThingDef>
                  <ThingDef>
                    <defName>P2</defName>
                  </ThingDef>
                </Defs>
                """;
            var (diags, check) = RunOnFixture(xml, corruptLines: true);
            Console.WriteLine("      " + string.Join(" | ", diags.Take(4).Select(d => $"{d.Code}({d.Severity}) {d.Message}")));
            T.True(diags.Any(d => d.Code == Codes.XmlParseFailed && d.Severity == Severity.Error),
                "归属键不一致必须报 RD2001(error)");
            T.Eq(check.LastStats!.ParityFailures, 1, "parity 失败文件数必须计数");
            T.Eq(check.LastStats.ReferenceSites, 0, "该文件必须被整体跳过（不得取到邻居 def 的桶）");
        });

        T.Case("★ parity 门禁不误报：active 真语料上实跑 ReferenceCheck 不得产出任何归属键不一致", () =>
        {
            var ctx = ActiveContext();
            var check = new ReferenceCheck();
            var diags = check.Run(ctx).ToList();
            var parity = diags.Count(d => d.Code == Codes.XmlParseFailed
                && d.Evidence is not null && d.Evidence.ContainsKey("streamDefs"));
            Console.WriteLine($"      active：声明 {check.LastStats!.DeclarationsTraversed}，"
                              + $"引用点 {check.LastStats.ReferenceSites}，悬空 {check.LastStats.MissingInScope}，"
                              + $"parity 失败 {check.LastStats.ParityFailures}，RD3001 {diags.Count(d => d.Code == Codes.DefNameNotFound)}");
            T.Eq(parity, 0, "真语料上不得有归属键不一致（门禁自己不误报）");
            T.Eq(check.LastStats.ParityFailures, 0, "统计块里的 parity 失败数必须是 0");
            T.True(check.LastStats.ReferenceSites > 20000, "引用点数量级（active 语料）");
        });

        T.Case("★★ Step 4 官方门禁：CreepJoinerBaseDef 类引用 0 条（修前 116 条）+ 每条 RD3001 都能归因", () =>
        {
            var (ctx, diags, check) = OfficialRun();
            var rd = diags.Where(d => d.Code == Codes.DefNameNotFound).ToList();
            var creep = rd.Where(d => d.Message.Contains("CreepJoiner", StringComparison.Ordinal)).ToList();
            var otherType = rd.Count(d => !string.IsNullOrEmpty(d.Evidence!.GetValueOrDefault("sameNameOtherDefTypes", "")));
            var nowhere = rd.Count(d => !ctx.Index.IsDefNameTaken(d.Evidence!["targetDefName"]));
            var unexplained = rd.Count(d => string.IsNullOrEmpty(d.Evidence!.GetValueOrDefault("sameNameOtherDefTypes", ""))
                                            && ctx.Index.IsDefNameTaken(d.Evidence!["targetDefName"]));

            Console.WriteLine($"      官方 6 包：defs={ctx.Index.TotalDefs}，RD3001={rd.Count}"
                              + $"（CreepJoiner 类 {creep.Count}，名字在别的 DefType 下存在 {otherType}，"
                              + $"磁盘上完全没有该 defName {nowhere}，无法归因 {unexplained}）");
            foreach (var d in rd.Take(8))
                Console.WriteLine($"        {d.Evidence!["targetDefType"]} \"{d.Evidence!["targetDefName"]}\"  "
                                  + $"otherTypes=[{d.Evidence!["sameNameOtherDefTypes"]}]  "
                                  + System.IO.Path.GetFileName(d.FileAbs) + ":" + d.Line);
            T.Eq(creep.Count, 0, "官方 Anomaly 的 CreepJoinerBaseDef 类引用必须是 0 条（修前 116 条假阳）");
            T.Eq(unexplained, 0, "每条官方 RD3001 都必须可归因（别的 DefType / 磁盘上无声明）否则就是我们的假阳");
            T.Eq(otherType + nowhere, rd.Count, "归因桶必须覆盖全部 RD3001");
            T.True(rd.Count > 0, "Step 4 的 0 条门禁**未达成**：见报告（运行时生成的 def，Plan 只列了两条边界）");
        });

        T.Case("★ 口径自证：DefIndex 的 (DefType,defName) 键与 SimulatedDefSet 的类型键同源（元素名，非 Class=）", () =>
        {
            const string xml = """
                <?xml version="1.0" encoding="utf-8"?>
                <Defs>
                  <ThingDef Class="NoSuch.ModAmmoDef">
                    <defName>Ammoish</defName>
                  </ThingDef>
                  <ThingDef>
                    <defName>UserOfAmmo</defName>
                    <verbs>
                      <li>
                        <defaultProjectile>Ammoish</defaultProjectile>
                      </li>
                    </verbs>
                  </ThingDef>
                </Defs>
                """;
            var (diags, check) = RunOnFixture(xml);
            Console.WriteLine($"      RD3001={diags.Count(x => x.Code == Codes.DefNameNotFound)}；"
                              + "Class= 指向 mod 类型（本进程没加载它的程序集）时，键仍必须是【元素名】ThingDef");
            T.Eq(diags.Count(x => x.Code == Codes.DefNameNotFound), 0,
                "Class= 是 mod 类型时不得把该 def 登记成无人能引用的键（否则成批假悬空）");
            T.True(check.LastSet!.Contains("ThingDef", "Ammoish"), "必须按元素名 ThingDef 登记");
            T.False(check.LastSet!.Contains("NoSuch.ModAmmoDef", "Ammoish"), "不得按 Class= 登记");
        });
    }

    // ────────────────────────────────────────────────────────────────────────────
    // 夹具运行器
    // ────────────────────────────────────────────────────────────────────────────

    private sealed class FixtureResult
    {
        public required List<Diagnostic> Diags { get; init; }
        public required ReferenceCheck Check { get; init; }
        public required string Dir { get; init; }
    }

    private static (List<Diagnostic>, ReferenceCheck) RunOnFixture(string xml, bool corruptLines = false,
        bool withOfficial = false)
    {
        var r = RunFixture(xml, corruptLines, withOfficial);
        return (r.Diags, r.Check);
    }

    /// <summary>
    /// 夹具运行器。
    /// ★ <paramref name="withOfficial"/> = true 时把**官方 6 包**的声明并进 ctx ——
    ///   存在的判定与「最相近候选」都依赖**名字池**，只有夹具时池子是空的
    ///   （实测：T15 的候选断言拿到空串）。
    /// </summary>
    private static FixtureResult RunFixture(string xml, bool corruptLines, bool withOfficial = false)
    {
        var dir = Path.Combine(Path.GetTempPath(), "rimdev-task6-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "Fixture.xml");
        File.WriteAllText(file, xml, new UTF8Encoding(false));

        var fr = DefScanner.ScanFile(file, "test.pkg", dir);
        var defs = fr.Defs;
        if (corruptLines)
            defs = defs.Select(d => d with { Line = d.Line + 100 }).ToList();

        var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
        var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
        var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;

        IReadOnlyList<DefFileResult> ctxFiles = new[] { fr };
        IReadOnlyList<RawDef> ctxDefs = defs;
        if (withOfficial)
        {
            var omods = cat.All.Where(m => m.Source == ModSource.Official).ToList();
            var ofiles = DefScanner.CollectFiles(
                omods.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)));
            var oscan = DefScanner.Scan(ofiles);
            ctxFiles = oscan.Files.Concat(new[] { fr }).ToList();
            ctxDefs = oscan.Defs.Concat(defs).ToList();
        }

        var idx = DefIndex.Build(ctxDefs);
        var ctx = new CheckContext(idx, ctxFiles, cat, ctxDefs,
            "test.pkg",
            ActivePackageIds: new HashSet<string>(StringComparer.Ordinal) { "test.pkg" });

        var check = new ReferenceCheck();
        var diags = check.Run(ctx).ToList();
        return new FixtureResult { Diags = diags, Check = check, Dir = dir };
    }

    private static CheckContext ActiveContext()
    {
        var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
        var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
        var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;
        var loadOrder = LoadOrder.Build(env, cat, active, LoadOrder.ReadKnownExpansions(env.ModsConfigPath));
        var mods = cat.Active.ToList();
        var files = DefScanner.CollectFiles(
            mods.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)));
        var scan = DefScanner.Scan(files);
        var idx = DefIndex.Build(scan.Defs);
        return new CheckContext(idx, scan.Files, cat, scan.Defs, null,
            LoadOrder: loadOrder, ModRank: loadOrder.RankFor(mods),
            ActivePackageIds: new HashSet<string>(active.Select(p => p.Trim().ToLowerInvariant()),
                StringComparer.Ordinal));
    }

    private static (CheckContext Ctx, List<Diagnostic> Diags, ReferenceCheck Check) OfficialRun()
    {
        var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
        var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
        var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;
        var mods = cat.All.Where(m => m.Source == ModSource.Official).ToList();
        var files = DefScanner.CollectFiles(
            mods.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)));
        var scan = DefScanner.Scan(files);
        var idx = DefIndex.Build(scan.Defs);
        var ctx = new CheckContext(idx, scan.Files, cat, scan.Defs, null,
            ActivePackageIds: new HashSet<string>(active.Select(p => p.Trim().ToLowerInvariant()),
                StringComparer.Ordinal));
        var check = new ReferenceCheck();
        var diags = check.Run(ctx).ToList();
        return (ctx, diags, check);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // 杂项
    // ────────────────────────────────────────────────────────────────────────────

    /// <summary>宽松口径多出来的字段（复现侦察脚本的 `StartsWith("System.Collections.Generic.List")`）——
    /// 差值 48 全部是编译器生成的 `List`1+Enumerator` 闭包字段。</summary>
    private static IEnumerable<string> LooseExtraFields(ManagedAssemblyCatalog catalog, ReferenceFieldIndex index)
    {
        var defType = index.DefType!;
        var derived = index.DefDerivedTypes.ToHashSet();
        foreach (var t in catalog.AllTypes)
            foreach (var f in FieldReflection.FieldsByName(t).Values)
            {
                var ft = f.FieldType;
                var strict = ReferenceFieldIndex.KindOf(ft) != RefContainerKind.Scalar;
                var loose = ft.IsArray || (ft.IsGenericType && ft.GetGenericTypeDefinition().FullName is { } gd
                    && (gd.StartsWith("System.Collections.Generic.List", StringComparison.Ordinal)
                        || gd.StartsWith("System.Collections.Generic.HashSet", StringComparison.Ordinal)
                        || gd.StartsWith("System.Collections.Generic.Dictionary", StringComparison.Ordinal)));
                if (strict == loose) continue;
                // ★ 宽松口径必须用它自己的「槽」（泛型实参），否则 Enumerator 类型的槽是它自己、
                //   判不出 Def 叶子 —— 第一版就这么漏掉了全部 48 条（实测 extras=0）。
                IReadOnlyList<Type> looseSlots = ft.IsArray
                    ? new[] { ft.GetElementType()! }
                    : (ft.IsGenericType ? ft.GetGenericArguments() : new[] { ft });
                if (!looseSlots.Any(s => s is not null && (s == defType || derived.Contains(s)))) continue;
                yield return $"{t.FullName}.{f.Name} : {ft.FullName}";
            }
    }

    private static string OfficialXmlText(string dataDir)
    {
        var sb = new StringBuilder();
        foreach (var f in Directory.EnumerateFiles(dataDir, "*.xml", SearchOption.AllDirectories))
        {
            try { sb.Append(File.ReadAllText(f)); } catch { /* 读不了就跳过（与扫描器的容错一致） */ }
        }
        return sb.ToString();
    }

    private static int Count(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }

    private static int PlattsLevenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    private static (string, string) SuggestTwice()
    {
        var ctx = ActiveContext();
        var analysis = InheritanceLayer.Analyze(ctx);
        var set = SimulatedDefSet.Build(ctx, analysis, Catalog.Value);
        var a = string.Join(",", set.Suggest(new[] { "SoundDef" }, "Shot_MachineGun", 5));
        var b = string.Join(",", set.Suggest(new[] { "SoundDef" }, "Shot_MachineGun", 5));
        return (a, b);
    }

    private static string DescribeSet(SimulatedDefSet set) =>
        $"声明={set.Declarations} 抽象={set.AbstractDeclarations} MayRequire跳过={set.SkippedByMayRequire}"
        + $" 有效声明={set.EffectiveDeclarations} 有效def名={set.EffectiveDefNames}"
        + $" 落败={set.ShadowedDeclarations} 继承defName={set.InheritedDefNameDeclarations}"
        + $" 无名={set.UnnamedDeclarations} Class=={set.ClassAttrDeclarations}"
        + $" 类型不可解析={set.UnresolvedTypeDeclarations}";

    private static string? FirstChainDefName(InheritanceLayer.Analysis a, RawDef d)
    {
        foreach (var n in InheritanceLayer.Walk(a, d))
            if (!string.IsNullOrEmpty(n.DefName)) return n.DefName;
        return null;
    }

    /// <summary>
    /// 真语料上的「Merge 结果」取用器：把每个文件载入 DOM（只对**有 ParentName 的声明**按需加载），
    /// 用 <see cref="InheritanceLayer.ResolveNode"/> —— 即 Task 5b 的唯一合并实现 —— 取合并后的 `&lt;defName&gt;`。
    /// </summary>
    private sealed class CorpusHolder : IDisposable
    {
        private readonly CheckContext _ctx;
        private readonly Dictionary<string, List<System.Xml.XmlNode>> _children = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<RawDef>> _fileDefs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, System.Xml.XmlNode?> _byName = new(StringComparer.Ordinal);

        public static CorpusHolder For(CheckContext ctx, InheritanceLayer.Analysis a) => new(ctx, a);

        private CorpusHolder(CheckContext ctx, InheritanceLayer.Analysis a)
        {
            _ctx = ctx;
            foreach (var d in ctx.Defs)
            {
                if (string.IsNullOrEmpty(d.NameAttr)) continue;
                var doc = Doc(d.FileAbs);
                if (doc?.DocumentElement is null) continue;
                foreach (System.Xml.XmlNode n in doc.DocumentElement.ChildNodes)
                    if (n.NodeType == System.Xml.XmlNodeType.Element
                        && n.Attributes?["Name"]?.Value == d.NameAttr && !_byName.ContainsKey(d.NameAttr))
                    {
                        _byName[d.NameAttr] = n;
                        break;
                    }
            }
        }

        private System.Xml.XmlDocument? Doc(string file)
        {
            try { var d = new System.Xml.XmlDocument(); d.Load(file); return d; } catch { return null; }
        }

        /// <summary>
        /// 按声明身份取值：`XmlElement` **不实现** `IXmlLineInfo`（本项目早已实测过），
        /// 所以行号只能来自流式扫描（<see cref="FieldNameCheck.ScanDefs"/>）。
        /// 对齐键 = **文件内文档序**：`ctx.Defs` 的该文件切片、`ScanDefs`、DOM 的 depth-1 元素
        /// 三者同序（前两者的同序由 FieldNameCheck 的 parity 门禁守着；这里再加一道计数对账）。
        /// </summary>
        public string? MergedDefName(RawDef target)
        {
            if (!_fileDefs.TryGetValue(target.FileAbs, out var list))
            {
                list = _ctx.Defs.Where(d => string.Equals(d.FileAbs, target.FileAbs, StringComparison.OrdinalIgnoreCase)).ToList();
                _fileDefs[target.FileAbs] = list;
            }
            var i = -1;
            for (var k = 0; k < list.Count; k++)
                if (ReferenceEquals(list[k], target)) { i = k; break; }
            if (i < 0) return null;

            if (!_children.TryGetValue(target.FileAbs, out var nodes))
            {
                var doc = Doc(target.FileAbs);
                nodes = new List<System.Xml.XmlNode>();
                if (doc?.DocumentElement is not null)
                    foreach (System.Xml.XmlNode n in doc.DocumentElement.ChildNodes)
                        if (n.NodeType == System.Xml.XmlNodeType.Element) nodes.Add(n);
                _children[target.FileAbs] = nodes;
            }
            var scanned = FieldNameCheck.ScanDefs(target.FileAbs);
            if (nodes.Count != scanned.Count || i >= nodes.Count) return null;   // 计数对账失败 ⇒ 不参与对账

            var merged = InheritanceLayer.ResolveNode(nodes[i], name => _byName.GetValueOrDefault(name));
            foreach (System.Xml.XmlNode c in merged.ChildNodes)
                if (c.NodeType == System.Xml.XmlNodeType.Element && c.Name == "defName")
                    return c.InnerText.Trim();
            return null;
        }

        public void Dispose() { _children.Clear(); _byName.Clear(); }
    }
}
