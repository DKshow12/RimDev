using System.Reflection;
using System.Xml;
using RimDev.Core.Catalog;
using RimDev.Core.Checks;
using RimDev.Core.Defs;
using RimDev.Core.Metadata;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// 第 3 期 Task 5b：`InheritanceLayer.Merge`（引擎 XML 继承合并语义的复刻）+ 引擎对照台差分测试。
///
/// <para>
/// ★ 用例分工（照「**能证伪它**的用例」原则写）：
/// </para>
/// <list type="bullet">
/// <item><b>Step 1</b>：任务书给出的 8 条口径**各一条用例**，每条都先打印**引擎在本进程内跑出来的原始观测**
///   再断言 —— 主理人的 pwsh 运行只能当假设（本项目已有「人工转写反编译文本被实测推翻」的先例）。
///   同一条用例里同时断言**我方实现逐字符一致**（两侧共用同一个 <see cref="EngineInheritance.Normalize"/>）。</item>
/// <item><b>Step 3（主门禁）</b>：差分测试 —— 夹具 + 真语料（ThreatFleet 1.6\Defs 全部 + Data\Core\Defs 全部），
///   每个可比节点同时取引擎真值与我方实现，规范化后逐字符比对，**要求 0 不一致**。</item>
/// <item><b>Step 4</b>：对 `ParentChainCheck` 零回归（active 作用域 `unresolved == 0`、环 `0`）。</item>
/// <item><b>Step 5</b>：探针不可用时**必须报错**，不许静默 PASS（<see cref="RequireProbe"/>）。</item>
/// </list>
/// </summary>
public static class Phase3Task5bCases
{
    // ═══════════════════════════════════════════════════════════════════════════
    // 夹具
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 合成夹具：把 ①–⑥ 一次性摆出来（① 同名容器递归合并 / ② `&lt;li&gt;` 追加 /
    /// ③ 子块 `Inherit="False"` / ③b def 级 `Inherit="False"` / ④ 多级链 / ⑤ 文本覆盖 / ⑥ 新增子元素）。
    /// ★ `M` 必须带 `Name=` 才会进引擎的父节点表（`&lt;defName&gt;` 不参与继承解析）—— 这就是「多级链」的前提。
    /// </summary>
    private const string ExploreFixture = """
<Defs>
  <ThingDef Name="P" Abstract="True" Foo="1" Zed="0">
    <defName>PBase</defName>
    <label>parentlabel</label>
    <description>parentdesc</description>
    <statBases>
      <MaxHitPoints>100</MaxHitPoints>
      <Mass>5</Mass>
    </statBases>
    <race>
      <bodySize>1.0</bodySize>
    </race>
    <comps>
      <li Class="Same"><v>1</v></li>
    </comps>
  </ThingDef>
  <ThingDef Name="M" ParentName="P" Foo="2" Bar="3">
    <defName>Mid</defName>
    <statBases><Mass>9</Mass></statBases>
    <race><fleshType>Mechanoid</fleshType></race>
    <comps><li Class="Same"><v>9</v></li></comps>
  </ThingDef>
  <ThingDef ParentName="M"><defName>Leaf</defName></ThingDef>
  <ThingDef ParentName="P"><defName>TextOverride</defName><description>childdesc</description></ThingDef>
  <ThingDef ParentName="P"><defName>AddChild</defName><statBases><MarketValue>3</MarketValue></statBases></ThingDef>
  <ThingDef ParentName="P"><defName>BlockNoInherit</defName><statBases Inherit="False"><Mass>42</Mass></statBases></ThingDef>
  <ThingDef ParentName="P" Inherit="False"><defName>WholeNoInherit</defName></ThingDef>
</Defs>
""";

    /// <summary>消歧夹具：属性归谁 / 子块属性 / 文本混合 / 注释 / 空容器 / 白名单裸元素。</summary>
    private static readonly (string Name, string Xml)[] Disambig =
    {
        ("attrs-normal", "<Defs><Def Name=\"PA\" Abc=\"1\" Mno=\"2\"><defName>PA</defName><x>1</x></Def>"
            + "<Def ParentName=\"PA\"><defName>A1</defName></Def></Defs>"),
        ("attrs-inherit-false", "<Defs><Def Name=\"PB\" Abc=\"1\" Mno=\"2\"><defName>PB</defName><x>1</x></Def>"
            + "<Def ParentName=\"PB\" Inherit=\"False\"><defName>B1</defName></Def></Defs>"),
        ("attrs-inherit-true", "<Defs><Def Name=\"PC\" Abc=\"1\" Mno=\"2\"><defName>PC</defName><x>1</x></Def>"
            + "<Def ParentName=\"PC\" Inherit=\"True\"><defName>C1</defName></Def></Defs>"),
        ("attrs-abstract", "<Defs><Def Name=\"PD\" Abstract=\"True\"><defName>PD</defName><x>1</x></Def>"
            + "<Def ParentName=\"PD\"><defName>D1</defName></Def></Defs>"),
        ("subattrs", "<Defs><Def Name=\"PE\"><defName>PE</defName><blk Abc=\"1\" Def=\"2\"><inner>1</inner></blk></Def>"
            + "<Def ParentName=\"PE\"><defName>E1</defName><blk Def=\"9\" Ghi=\"3\"><inner2>2</inner2></blk></Def></Defs>"),
        ("subattrs-inherit-false", "<Defs><Def Name=\"PF\"><defName>PF</defName><blk Abc=\"1\" Def=\"2\"><inner>1</inner></blk></Def>"
            + "<Def ParentName=\"PF\"><defName>F1</defName><blk Inherit=\"False\" Def=\"9\"><inner2>2</inner2></blk></Def></Defs>"),
        ("text-replace", "<Defs><Def Name=\"PG\"><defName>PG</defName><t>abc</t><u>keep</u></Def>"
            + "<Def ParentName=\"PG\"><defName>G1</defName><t>XYZ</t></Def></Defs>"),
        ("text-plus-child", "<Defs><Def Name=\"PH\"><defName>PH</defName><t>abc</t></Def>"
            + "<Def ParentName=\"PH\"><defName>H1</defName><t>XYZ<w/></t></Def></Defs>"),
        ("text-mixed-inline", "<Defs><Def Name=\"PI\"><defName>PI</defName><t>abc<z/>def</t></Def>"
            + "<Def ParentName=\"PI\"><defName>I1</defName><t>XYZ</t></Def></Defs>"),
        ("comments", "<Defs><Def Name=\"PJ\"><defName>PJ</defName><!-- pc --><v>1</v></Def>"
            + "<Def ParentName=\"PJ\"><defName>J1</defName><!-- cc --></Def></Defs>"),
        ("empty-child-block", "<Defs><Def Name=\"PK\"><defName>PK</defName><blk><inner>1</inner></blk></Def>"
            + "<Def ParentName=\"PK\"><defName>K1</defName><blk/></Def></Defs>"),
        ("deep-container", "<Defs><Def Name=\"PL\"><defName>PL</defName><race><a><b>1</b></a></race></Def>"
            + "<Def ParentName=\"PL\"><defName>L1</defName><race><a><c>2</c></a></race></Def></Defs>"),
        ("li-with-class", "<Defs><Def Name=\"PM\"><defName>PM</defName><comps><li Class=\"X\"><a>1</a></li></comps></Def>"
            + "<Def ParentName=\"PM\"><defName>M1</defName><comps><li Class=\"X\"><a>2</a></li><li Class=\"Y\"><b>3</b></li></comps></Def></Defs>"),
        ("chain-3", "<Defs><Def Name=\"Q1\"><defName>Q1</defName><s><m>1</m></s></Def>"
            + "<Def Name=\"Q2\" ParentName=\"Q1\"><defName>Q2</defName><s><m>2</m></s></Def>"
            + "<Def ParentName=\"Q2\"><defName>Q3</defName><s><n>3</n></s></Def></Defs>"),
        ("text-many-children", "<Defs><Def Name=\"R1\"><defName>R1</defName><t><a/><b/><c/></t></Def>"
            + "<Def ParentName=\"R1\"><defName>R2</defName><t>XYZ</t></Def></Defs>"),
        ("noelement-child-keeps-parent", "<Defs><Def Name=\"R3\"><defName>R3</defName><t><a/></t></Def>"
            + "<Def ParentName=\"R3\"><defName>R4</defName><t></t></Def></Defs>"),
        ("noelement-both-empty", "<Defs><Def Name=\"R5\"><defName>R5</defName><t>abc</t></Def>"
            + "<Def ParentName=\"R5\"><defName>R6</defName><t></t></Def></Defs>"),
        ("allowdup-forcedTraits", "<Defs><Def Name=\"R7\"><defName>R7</defName><forcedTraits><X>1</X></forcedTraits></Def>"
            + "<Def ParentName=\"R7\"><defName>R8</defName><forcedTraits><X>2</X></forcedTraits></Def></Defs>"),
        ("allowdup-control", "<Defs><Def Name=\"R9\"><defName>R9</defName><other><X>1</X></other></Def>"
            + "<Def ParentName=\"R9\"><defName>RA</defName><other><X>2</X></other></Def></Defs>"),
        ("nested-inherit-false", "<Defs><Def Name=\"RB\"><defName>RB</defName><race><a><b>1</b><c>2</c></a></race></Def>"
            + "<Def ParentName=\"RB\"><defName>RC</defName><race><a Inherit=\"False\"><d>3</d></a></race></Def></Defs>"),
        ("li-no-class", "<Defs><Def Name=\"RD\"><defName>RD</defName><xs><li><a>1</a></li></xs></Def>"
            + "<Def ParentName=\"RD\"><defName>RE</defName><xs><li><a>2</a></li></xs></Def></Defs>"),
    };

    /// <summary>真语料抽样：ThreatFleet 与 Core 全部 XML（除夹具外的第二路材料）。</summary>
    private static readonly string ThreatFleetDefs =
        Path.Combine(Facts.GameRoot, "Mods", "ThreatFleet", "1.6", "Defs");

    private static readonly string CoreDefs = Path.Combine(Facts.GameRoot, "Data", "Core", "Defs");

    /// <summary>
    /// 真语料可比条目数的**下限**（防「差分测试退化成一个空循环」）。
    ///
    /// ★ 2026-10-15 实测：612 文件 / 6897 个顶层元素 / **2115** 个可比条目（引擎确实给出非恒等结果）。
    ///   下限取 **1500**（实测的约 70%）—— 只用于「防退化」，不参与验收判定：
    ///   验收是「不一致 == 0」，不是「样本数 ≥ N」。上限侧不设限，因为 ThreatFleet 正被另一个
    ///   写入者改造，语料会漂移；下界能抓住「整段解析没跑起来」这类静默失败，这就够了。
    /// </summary>
    private const int CorpusComparedFloor = 1500;

    // ═══════════════════════════════════════════════════════════════════════════
    // 探针（按需入口：rimdev-selftest --task5b-probe）
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>按需入口：只跑引擎对照台并把原始观测打印出来（秒级，不进常规跑）。</summary>
    public static int RunProbe()
    {
        Console.WriteLine($"[probe] Available={EngineInheritance.Available}");
        Console.WriteLine($"[probe] Status={EngineInheritance.Status}");
        if (!EngineInheritance.Available) return 1;

        Console.WriteLine("===== 夹具 1：8 条口径的合成夹具 =====");
        Dump(ExploreFixture);
        foreach (var (name, xml) in Disambig)
        {
            Console.WriteLine($"===== 消歧夹具：{name} =====");
            Dump(xml);
        }
        return 0;
    }

    private static void Dump(string fixtureXml, bool onlyWithParent = false)
    {
        var res = EngineInheritance.ResolveFixture(fixtureXml, out var err);
        if (res is null)
        {
            Console.WriteLine($"[probe] 夹具解析失败: {err}");
            return;
        }
        Console.WriteLine($"[probe] registered={res.Registered} skippedMayRequire={res.SkippedMayRequire} "
                          + $"skippedDuplicateName={res.SkippedDuplicateName} nameMap={res.NameMap.Count}");
        foreach (var n in res.Nodes)
        {
            if (onlyWithParent && string.IsNullOrEmpty(n.ParentName)) continue;
            Console.WriteLine($"[probe] --- {n.DefName} (Name={n.Node.Attributes?["Name"]?.Value}, "
                              + $"ParentName={n.ParentName}, ParentFound={n.ParentFound}, Registered={n.Registered}) ---");
            Console.WriteLine(n.Xml ?? "<null>");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 差分基础设施
    // ═══════════════════════════════════════════════════════════════════════════

    private sealed class DiffReport
    {
        /// <summary>引擎确实给出了「非原样」结果的条目数（= 真正参与逐字符比对的样本数）。</summary>
        public int Compared;

        /// <summary>引擎结果与「原节点的规范化 XML」相同 —— 引擎没解析（或合并恰好是恒等），无从比对。</summary>
        public int Ambiguous;

        /// <summary>
        /// 引擎回退到原节点、而我方却给出了不同结果 ⇒ 两侧对「能不能解析」的判断不一致（必须是 0）。
        /// </summary>
        public int EngineFallbackButMineDiffers;
        /// <summary>
        /// `CheckForDuplicateNodes` 触发的节点：引擎在真机里会打日志后继续，离线会整次中断
        /// ⇒ **取不到真值**，不能纳入比对（用例对它单独断言为 0）。
        /// </summary>
        public int NotComparable;

        /// <summary>
        /// 引擎**压根没处理**的节点（`MayRequire` / `Name` 撞名被 `TryRegister` 早退）——
        /// 引擎对它们的答案只是「原节点」，**不携带任何合并语义信息**，因此不能纳入差分。
        /// ★ 这与 `NotComparable` 不同：后者是「引擎会打日志、离线取不到真值」，前者是「引擎按设计跳过」。
        /// </summary>
        public int NotProcessedByEngine;

        public readonly List<string> Mismatches = new();


    }

    private static Func<string, XmlNode?> Lookup(EngineInheritance.Resolution res) =>
        name => res.NameMap.TryGetValue(name, out var n) ? n : null;

    /// <summary>
    /// 逐节点比对：引擎真值（<see cref="EngineInheritance.ResolvedNode.Xml"/>）vs
    /// 我方 <see cref="InheritanceLayer.ResolveNode"/>，两侧共用 <see cref="EngineInheritance.Normalize"/>。
    /// </summary>
    private static DiffReport DiffAgainstEngine(EngineInheritance.Resolution res)
    {
        var report = new DiffReport();
        var lookup = Lookup(res);

        foreach (var e in res.Nodes)
        {
            if (string.IsNullOrEmpty(e.ParentName)) continue;

            if (string.Equals(e.SkipReason, EngineInheritance.SkipReason.DuplicateNodeName, StringComparison.Ordinal))
            {
                // `CheckForDuplicateNodes` 触发的节点：引擎在真机里打日志后继续，离线会整次中断
                // ⇒ **取不到真值**，不能纳入比对（用例对它单独断言为 0）。
                report.NotComparable++;
                continue;
            }

            if (e.SkipReason is EngineInheritance.SkipReason.MayRequire or EngineInheritance.SkipReason.DuplicateName)
            {
                // 引擎**按设计**没处理这个节点（TryRegister 早退）⇒ 它的答案是原节点，
                // 不携带合并语义信息；我方 ResolveNode 会照常解析它 —— 两者不可比，不计入差分。
                report.NotProcessedByEngine++;
                continue;
            }

            var mine = EngineInheritance.Normalize(InheritanceLayer.ResolveNode(e.Node, lookup));
            var original = EngineInheritance.Normalize(e.Node);

            // ★ 未注册（MayRequire / 撞名 / 父名断链）时，引擎的答案是 `GetResolvedNodeFor` 末尾那句
            //   `return originalNode;` —— 即**原节点**。
            var engineXml = e.Xml ?? original;

            if (string.Equals(engineXml, original, StringComparison.Ordinal))
            {
                // 引擎拿不到解析结果（父名不存在 / 上游断链 / 成环 / 未被注册），返回的是原节点。
                if (string.Equals(mine, original, StringComparison.Ordinal)) report.Ambiguous++;
                else
                {
                    report.EngineFallbackButMineDiffers++;
                    if (report.Mismatches.Count < 20)
                        report.Mismatches.Add($"[引擎回退、我方解析] {Short(e.Label)}#{e.Index} {e.ElementName} "
                                              + $"defName={e.DefName} ParentName={e.ParentName} skip={e.SkipReason ?? "-"}");
                }
                continue;
            }

            report.Compared++;
            if (!string.Equals(mine, engineXml, StringComparison.Ordinal) && report.Mismatches.Count < 20)
            {
                report.Mismatches.Add($"[逐字符不一致] {Short(e.Label)}#{e.Index} {e.ElementName} "
                                      + $"defName={e.DefName} ParentName={e.ParentName}\n"
                                      + $"        引擎: {Clip(engineXml)}\n        我方: {Clip(mine)}");
            }
        }
        return report;
    }

    private static string Short(string path) => Path.GetFileName(path);
    private static string Clip(string s) => s.Length <= 400 ? s : s[..400] + "…";

    /// <summary>沿 `ParentName` 链的最长祖先深度（证明语料里真有**多级链**）。</summary>
    private static int MaxChainDepth(EngineInheritance.Resolution res)
    {
        var lookup = Lookup(res);
        var max = 0;
        foreach (var e in res.Nodes)
        {
            var seen = new HashSet<XmlNode>();
            var cur = e.Node;
            var depth = 0;
            while (true)
            {
                var parentName = cur.Attributes?["ParentName"]?.Value;
                if (string.IsNullOrEmpty(parentName) || !seen.Add(cur)) break;
                var parent = lookup(parentName!);
                if (parent is null) break;
                depth++;
                cur = parent;
            }
            if (depth > max) max = depth;
        }
        return max;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Step 1 / Step 3 的公共断言工具
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>探针不可用 ⇒ **必须**让用例失败（不许静默 PASS）。</summary>
    private static void RequireProbe(string where)
    {
        if (!EngineInheritance.Available)
            throw new InvalidOperationException(
                $"{where}: 引擎对照台不可用 —— 本 Task 的验收依据不存在，必须显式失败而不是跳过。Status={EngineInheritance.Status}");
    }

    /// <summary>
    /// 取引擎对某夹具节点的解析结果（打印原始观测），并断言我方实现**逐字符一致**。
    /// </summary>
    private static string ObserveAndMatch(string fixtureXml, string nodeName, string label)
    {
        RequireProbe(label);
        var res = EngineInheritance.ResolveFixture(fixtureXml, out var err);
        if (res is null) throw new InvalidOperationException($"{label}: 夹具解析失败 —— {err}");

        var entry = res.Nodes.FirstOrDefault(n =>
            string.Equals(n.DefName, nodeName, StringComparison.Ordinal)
            || string.Equals(n.Node.Attributes?["Name"]?.Value, nodeName, StringComparison.Ordinal));
        if (entry is null) throw new InvalidOperationException($"{label}: 夹具里找不到 {nodeName}");
        if (entry.Xml is null) throw new InvalidOperationException($"{label}: 引擎没给出 {nodeName} 的解析结果");

        var mine = EngineInheritance.Normalize(InheritanceLayer.ResolveNode(entry.Node, Lookup(res)));
        Console.WriteLine($"       [引擎观测 {label}] {entry.Xml}");
        Console.WriteLine($"       [我方实现 {label}] {mine}");
        T.Eq(mine, entry.Xml, $"{label}: Merge 结果必须与引擎逐字符一致");
        return entry.Xml;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Step 4：真语料上下文（active 作用域）
    // ═══════════════════════════════════════════════════════════════════════════


    private static CheckContext? _activeCtx;

    private static CheckContext ActiveContext()
    {
        if (_activeCtx is not null) return _activeCtx;

        var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
        var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
        var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;
        var order = LoadOrder.Build(env, cat, active, LoadOrder.ReadKnownExpansions(env.ModsConfigPath));
        var mods = cat.Active.ToList();

        var files = DefScanner.CollectFiles(mods.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)));
        var scan = DefScanner.Scan(files);
        _activeCtx = new CheckContext(DefIndex.Build(scan.Defs), scan.Files, cat, scan.Defs, null,
            LoadOrder: order, ModRank: order.RankFor(mods),
            ActivePackageIds: new HashSet<string>(active.Select(p => p.Trim().ToLowerInvariant()), StringComparer.Ordinal));
        return _activeCtx;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 用例
    // ═══════════════════════════════════════════════════════════════════════════

    public static void Run()
    {
        T.Group("phase3-task5b");

        // ★ 第 3 期 Task 9：本文件的**全部用例**都以「引擎对照台」为准绳，而对照台要载入
        //   1.6 的托管程序集链（ManagedDir 里的 UnityEngine.CoreModule.dll 等）。
        //   1.7.x 线没有这些文件（ManagedDir = 游戏根，Unity 已被 Waylith 取代）⇒
        //   整组按布局**显式 SKIP**（逐条记名），装上 1.6 后必须原样全部回绿。
        using var layoutScope = T.Scope(LayoutNeed.Classic16);
        // ── Step 5 的前置钉子：探针必须可用 ─────────────────────────────────────
        T.Case("★ (0) 引擎对照台必须可用（不可用即 FAIL，绝不静默跳过）", () =>
        {
            Console.WriteLine($"       [探针] Available={EngineInheritance.Available} / Status={EngineInheritance.Status}");
            RequireProbe("(0) 探针");
        });

        // ── Step 1：8 条口径（每条先打印引擎原始观测）──────────────────────────
        T.Case("★ ① 同名容器字段级递归合并（statBases/race 都保留父项、只覆盖同名子项）", () =>
        {
            var xml = ObserveAndMatch(ExploreFixture, "Mid", "① ");
            // ★ 任务书点名的接口 `ResolveToXml` 与差分用的 `ResolveFixture` 必须同源一致
            //   （两个入口各写一套实现就会分叉 —— 这正是本项目反复踩的坑）。
            var viaApi = EngineInheritance.ResolveToXml(ExploreFixture, "Mid", out var apiErr);
            T.Eq(viaApi, xml, $"① ResolveToXml 与 ResolveFixture 同源一致（err={apiErr ?? "-"}）");
            T.Contains(xml, "<statBases><MaxHitPoints>100</MaxHitPoints><Mass>9</Mass></statBases>",
                "① statBases：父的 MaxHitPoints 保留、子的 Mass 覆盖");
            T.Contains(xml, "<race><bodySize>1.0</bodySize><fleshType>Mechanoid</fleshType></race>",
                "① race：父的 bodySize 保留、子的 fleshType 追加");
        });

        T.Case("★ ② <li> 无条件追加：Class 完全相同的两条**都在**（这就是双 comp ⇒ 双 Gizmo 的机制）", () =>
        {
            var xml = ObserveAndMatch(ExploreFixture, "Mid", "② ");
            T.Contains(xml, "<comps><li Class=\"Same\"><v>1</v></li><li Class=\"Same\"><v>9</v></li></comps>",
                "② comps：父 1 条 + 子 1 条 = 2 条（同 Class 不合并）");
        });

        T.Case("★ ②b [XmlInheritanceAllowDuplicateNodes] 全程序集字段集：我方字面量表必须与反射结果**完全相同**", () =>
        {
            RequireProbe("②b ");
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var cat = ManagedAssemblyCatalog.Load(env.ManagedDir);
            var attrType = cat.Find("XmlInheritanceAllowDuplicateNodes");
            T.NotNull(attrType, "Verse.XmlInheritanceAllowDuplicateNodes 类型（★ 名字没有 Attribute 后缀）");

            // 引擎静态构造用的是 `allType.GetFields()`（公开、含继承）+ IsDefined(inherit:false)。
            var engineNames = new HashSet<string>(StringComparer.Ordinal);
            var declaredNames = new HashSet<string>(StringComparer.Ordinal);
            var nonList = new List<string>();
            var pairs = new List<string>();

            foreach (var t in cat.AllTypes)
            {
                FieldInfo[] fields;
                try { fields = t.GetFields(); } catch { continue; }
                foreach (var f in fields)
                {
                    if (!f.IsDefined(attrType!, inherit: false)) continue;
                    engineNames.Add(f.Name);
                    pairs.Add($"{t.Name}.{f.Name}");
                    var ft = f.FieldType;
                    if (!(ft.IsGenericType && ft.GetGenericTypeDefinition() == typeof(List<>)))
                        nonList.Add($"{t.Name}.{f.Name}:{ft.Name}");
                }

                FieldInfo[] declared;
                try
                {
                    declared = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic
                                           | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                }
                catch { continue; }
                foreach (var f in declared)
                    if (f.IsDefined(attrType!, inherit: false)) declaredNames.Add(f.Name);
            }

            Console.WriteLine($"       [引擎观测 ②b] GetFields()口径={engineNames.Count} 个：{string.Join(", ", engineNames.OrderBy(x => x, StringComparer.Ordinal))}");
            Console.WriteLine($"       [引擎观测 ②b] DeclaredOnly 口径={declaredNames.Count} 个；命中字段：{string.Join(", ", pairs.OrderBy(x => x, StringComparer.Ordinal))}");
            Console.WriteLine($"       [我方字面量 ②b] {string.Join(", ", InheritanceLayer.AllowDuplicateNodeFieldNames.OrderBy(x => x, StringComparer.Ordinal))}");

            // ★ 更硬的一路：读**引擎自己**的静态字段（`XmlInheritance.allowDuplicateNodesFieldNames`），
            //   它是 `IsListElement` 真正查的那张表；反射重扫只是复现它的构造过程。
            var live = EngineInheritance.EngineAllowDuplicateNodeFieldNames;
            Console.WriteLine($"       [引擎静态字段 ②b] {live.Count} 个：{string.Join(", ", live.OrderBy(x => x, StringComparer.Ordinal))}");
            T.True(live.SetEquals(InheritanceLayer.AllowDuplicateNodeFieldNames),
                "②b 我方字面量表必须与引擎**自己的** allowDuplicateNodesFieldNames 字段完全相同");

            T.True(engineNames.SetEquals(InheritanceLayer.AllowDuplicateNodeFieldNames),
                "②b 我方字面量表必须与引擎 GetFields() 口径的字段名集合完全相同");
            T.True(declaredNames.SetEquals(engineNames),
                "②b 两种扫描口径（GetFields / DeclaredOnly）字段名集合相同（交叉验证，排除继承重复计数）");
            T.Eq(nonList.Count, 0, "②b 这 6 个字段全是 List<T>");
            T.Contains(pairs.Any(p => p.EndsWith(".descriptionHyperlinks", StringComparison.Ordinal)) ? "yes" : "no", "yes",
                "②b Def.descriptionHyperlinks 在命中集里（证明它不是空扫）");
        });

        T.Case("★ ③ 子块 Inherit=\"False\" 只替换该块，其余块照常继承", () =>
        {
            var xml = ObserveAndMatch(ExploreFixture, "BlockNoInherit", "③ ");
            T.Contains(xml, "<statBases><Mass>42</Mass></statBases>", "③ statBases 只剩子自己的 Mass=42");
            T.Contains(xml, "<label>parentlabel</label>", "③ label 仍然继承自父");
            T.Contains(xml, "<comps><li Class=\"Same\"><v>1</v></li></comps>", "③ comps 仍然继承自父");
        });

        T.Case("★ ③b def 元素自身 Inherit=\"False\"：只保留自己的子元素，但**父的属性仍被复制**", () =>
        {
            var xml = ObserveAndMatch(ExploreFixture, "WholeNoInherit", "③b");
            T.Contains(xml, "Abstract=\"True\"", "③b 父的 Abstract 属性仍在（属性不清）");
            T.Contains(xml, "Foo=\"1\"", "③b 父的 Foo 属性仍在");
            T.Contains(xml, "<defName>WholeNoInherit</defName>", "③b 子元素只剩自己的 defName");
            T.Contains(xml, "ParentName=\"P\"", "③b 自己的 ParentName 属性被追加");
            T.False(xml.Contains("parentlabel", StringComparison.Ordinal), "③b 父的子元素（label）都被丢掉");
        });

        T.Case("★ ④ 多级链 A→B→C 逐级 clone-merge：Leaf 拿到 Mid **覆盖后**的值", () =>
        {
            var leaf = ObserveAndMatch(ExploreFixture, "Leaf", "④ ");
            T.Contains(leaf, "<Mass>9</Mass>", "④ Leaf 的 Mass = Mid 覆盖后的 9（不是 Base 的 5）");
            T.Contains(leaf, "<fleshType>Mechanoid</fleshType>", "④ Leaf 也拿到 Mid 的 fleshType");
            T.Contains(leaf, "<statBases><MaxHitPoints>100</MaxHitPoints><Mass>9</Mass></statBases>",
                "④ 整块 statBases 与 Mid 解析结果一致");

            var q3 = ObserveAndMatch(Disambig.Single(d => d.Name == "chain-3").Xml, "Q3", "④b");
            T.Contains(q3, "<s><m>2</m><n>3</n></s>", "④b 三级链：Q1 的 m 被 Q2 覆盖成 2，Q3 再加 n=3");
        });

        T.Case("★ ⑤ 子元素带文本 ⇒ 覆盖父文本（兄弟元素不受影响）", () =>
        {
            var xml = ObserveAndMatch(ExploreFixture, "TextOverride", "⑤ ");
            T.Contains(xml, "<description>childdesc</description>", "⑤ description 用子的文本");
            T.Contains(xml, "<label>parentlabel</label>", "⑤ 没有覆盖的 label 仍是父的");
        });

        T.Case("★ ⑥ 子节点新增父里没有的子元素 ⇒ 追加（并保留父的既有项与顺序）", () =>
        {
            var xml = ObserveAndMatch(ExploreFixture, "AddChild", "⑥ ");
            T.Contains(xml, "<statBases><MaxHitPoints>100</MaxHitPoints><Mass>5</Mass><MarketValue>3</MarketValue></statBases>",
                "⑥ 父的两项（原顺序）+ 子新增的 MarketValue");
        });

        // ── 引擎的「照抄不许改漂亮」四条边界（都有实测观测）────────────────────
        T.Case("★ ⑦ 属性语义两分支相反：普通分支只用**子**的属性；Inherit=False 保留父属性再追加", () =>
        {
            var a1 = ObserveAndMatch(Disambig.Single(d => d.Name == "attrs-normal").Xml, "A1", "⑦a");
            T.Eq(a1, "<Def ParentName=\"PA\"><defName>A1</defName><x>1</x></Def>",
                "⑦a 普通分支：父的 Name/Abc/Mno 全部不继承");
            T.False(a1.Contains("Abstract", StringComparison.Ordinal), "⑦a Abstract 不被继承");

            var b1 = ObserveAndMatch(Disambig.Single(d => d.Name == "attrs-inherit-false").Xml, "B1", "⑦b");
            T.Contains(b1, "Name=\"PB\" Abc=\"1\" Mno=\"2\"", "⑦b Inherit=False：父属性保留在**前**");
            T.False(b1.Contains("Inherit=", StringComparison.Ordinal), "⑦b Inherit 属性自身不写进结果");

            var c1 = ObserveAndMatch(Disambig.Single(d => d.Name == "attrs-inherit-true").Xml, "C1", "⑦c");
            T.Contains(c1, "Inherit=\"True\"", "⑦c Inherit=\"True\" 不是 False ⇒ 走普通分支，属性原样保留在结果里");

            var d1 = ObserveAndMatch(Disambig.Single(d => d.Name == "attrs-abstract").Xml, "D1", "⑦d");
            T.False(d1.Contains("Abstract", StringComparison.Ordinal), "⑦d 父的 Abstract 也不会被继承");
        });

        T.Case("★ ⑧ 子块属性同样「普通分支只用子的」/「Inherit=False 保留父的再追加」", () =>
        {
            var e1 = ObserveAndMatch(Disambig.Single(d => d.Name == "subattrs").Xml, "E1", "⑧a");
            T.Contains(e1, "<blk Def=\"9\" Ghi=\"3\"><inner>1</inner><inner2>2</inner2></blk>",
                "⑧a 子块属性 = 子的（父的 Abc 丢弃）；子元素 = 父的 inner + 子新增的 inner2");

            var f1 = ObserveAndMatch(Disambig.Single(d => d.Name == "subattrs-inherit-false").Xml, "F1", "⑧b");
            T.Contains(f1, "<blk Abc=\"1\" Def=\"9\"><inner2>2</inner2></blk>",
                "⑧b 子块 Inherit=False：父属性 Abc 保留 + 子属性 Def 追加；子元素只有子的 inner2");
        });

        T.Case("★★ ⑨ 引擎的「清空子节点」在文本分支里**只删掉第一个**（照抄的 bug，两侧必须同样）", () =>
        {
            var r2 = ObserveAndMatch(Disambig.Single(d => d.Name == "text-many-children").Xml, "R2", "⑨a");
            T.Contains(r2, "<t><b /><c />XYZ</t>",
                "⑨a 父 <t><a/><b/><c/></t> + 子 <t>XYZ</t> ⇒ 只剩 a 被删（引擎实测）");

            var i1 = ObserveAndMatch(Disambig.Single(d => d.Name == "text-mixed-inline").Xml, "I1", "⑨b");
            T.Contains(i1, "<t><z />defXYZ</t>", "⑨b 混合内容：只删首个文本 abc，其余保留后接 XYZ");

            var h1 = ObserveAndMatch(Disambig.Single(d => d.Name == "text-plus-child").Xml, "H1", "⑨c");
            T.Eq(h1, "<Def ParentName=\"PH\"><defName>H1</defName><t>XYZ</t></Def>",
                "⑨c 子元素里有文本又有嵌套元素 ⇒ 文本分支优先，嵌套元素 w 被丢弃");
        });

        T.Case("★ ⑩ 子没有元素子节点：父同名的块有元素 ⇒ 原样保留；都没有 ⇒ 清空", () =>
        {
            var r4 = ObserveAndMatch(Disambig.Single(d => d.Name == "noelement-child-keeps-parent").Xml, "R4", "⑩a");
            T.Contains(r4, "<t><a /></t>", "⑩a 子 <t></t> 不清掉父的 <a/>");
            var r6 = ObserveAndMatch(Disambig.Single(d => d.Name == "noelement-both-empty").Xml, "R6", "⑩b");
            T.Contains(r6, "<t />", "⑩b 两边都没有元素 ⇒ 父的裸文本被清掉");
        });

        T.Case("★ ⑪ 白名单字段（forcedTraits）下的**裸元素**也按列表追加；对照组必须合并", () =>
        {
            var r8 = ObserveAndMatch(Disambig.Single(d => d.Name == "allowdup-forcedTraits").Xml, "R8", "⑪a");
            T.Contains(r8, "<forcedTraits><X>1</X><X>2</X></forcedTraits>",
                "⑪a forcedTraits 在 allowDuplicateNodes 表里 ⇒ X 按列表追加（裸元素也追加）");
            var ra = ObserveAndMatch(Disambig.Single(d => d.Name == "allowdup-control").Xml, "RA", "⑪b");
            T.Contains(ra, "<other><X>2</X></other>", "⑪b 不在表里 ⇒ 同名裸元素正常合并（子的文本覆盖）");
        });

        T.Case("★ ⑫ 嵌套 Inherit=\"False\" / 空子块 / 注释 / li-no-Class 的引擎观测与我方一致", () =>
        {
            var rc = ObserveAndMatch(Disambig.Single(d => d.Name == "nested-inherit-false").Xml, "RC", "⑫a");
            T.Contains(rc, "<race><a><d>3</d></a></race>", "⑫a 嵌套块 Inherit=False：父的 b/c 丢掉，只剩子的 d");
            var k1 = ObserveAndMatch(Disambig.Single(d => d.Name == "empty-child-block").Xml, "K1", "⑫b");
            T.Contains(k1, "<blk><inner>1</inner></blk>", "⑫b 子 <blk/> 空 ⇒ 父的 inner 保留");
            var j1 = ObserveAndMatch(Disambig.Single(d => d.Name == "comments").Xml, "J1", "⑫c");
            T.Contains(j1, "<!-- pc -->", "⑫c 父的注释随克隆保留");
            T.False(j1.Contains("<!-- cc -->", StringComparison.Ordinal), "⑫c 子的注释不进结果（只处理 Element）");
            var re = ObserveAndMatch(Disambig.Single(d => d.Name == "li-no-class").Xml, "RE", "⑫d");
            T.Contains(re, "<xs><li><a>1</a></li><li><a>2</a></li></xs>", "⑫d 无 Class 的 li 同样无条件追加");
        });

        // ── Step 3：差分测试（主门禁）─────────────────────────────────────────
        T.Case("★★★ Step 3 差分 · 夹具全集：0 不一致", () =>
        {
            RequireProbe("Step 3 夹具");
            var fixtures = new List<(string Name, string Xml)> { ("explore", ExploreFixture) };
            fixtures.AddRange(Disambig);

            var compared = 0;
            var problems = new List<string>();
            foreach (var (name, xml) in fixtures)
            {
                var res = EngineInheritance.ResolveFixture(xml, out var err);
                T.NotNull(res, $"夹具 {name} 解析: {err}");
                var diff = DiffAgainstEngine(res!);
                compared += diff.Compared;
                Console.WriteLine($"       [夹具差分 {name}] 可比={diff.Compared} 引擎回退(模糊)={diff.Ambiguous} "
                                  + $"引擎回退但我方有解析={diff.EngineFallbackButMineDiffers} "
                                  + $"不可比(引擎会打日志)={diff.NotComparable} 不一致={diff.Mismatches.Count}");
                problems.AddRange(diff.Mismatches.Select(m => $"{name}: {m}"));
            }

            foreach (var p in problems.Take(20)) Console.WriteLine($"       [差异] {p}");
            T.Eq(problems.Count, 0, "Step 3 夹具差分：不一致条数必须为 0");
            T.True(compared >= 25, $"夹具可比条目数太少（{compared}）⇒ 差分测试退化，必须修夹具");
        });

        T.Case("★★★ Step 3 差分 · 真语料（ThreatFleet 1.6\\Defs 全部 + Data\\Core\\Defs 全部）：0 不一致", () =>
        {
            RequireProbe("Step 3 真语料");

            var files = new List<string>();
            foreach (var dir in new[] { ThreatFleetDefs, CoreDefs })
            {
                T.True(Directory.Exists(dir), $"语料目录必须存在: {dir}");
                files.AddRange(Directory.GetFiles(dir, "*.xml", SearchOption.AllDirectories)
                    .OrderBy(x => x, StringComparer.Ordinal));
            }

            var res = EngineInheritance.ResolveFiles(files, out var err);
            T.NotNull(res, $"真语料引擎解析: {err}");
            var r = res!;

            var diff = DiffAgainstEngine(r);
            var depth = MaxChainDepth(r);

            Console.WriteLine($"       [真语料差分] 文件={files.Count} 顶层元素={r.Nodes.Count}（带 ParentName={r.WithParentName}）registered={r.Registered} "
                              + $"skippedMayRequire={r.SkippedMayRequire} skippedDuplicateName={r.SkippedDuplicateName} "
                              + $"skippedUnresolvable={r.SkippedUnresolvable} skippedDuplicateNodeName={r.SkippedDuplicateNodeName} "
                              + $"读失败={r.Failures.Count}");
            Console.WriteLine($"       [真语料差分] 可比(引擎确实解析)={diff.Compared} 引擎回退(模糊)={diff.Ambiguous} "
                              + $"引擎回退但我方有解析={diff.EngineFallbackButMineDiffers} 不一致={diff.Mismatches.Count} "
                              + $"最长链深={depth}");
            Console.WriteLine($"       [真语料差分] 引擎未处理(MayRequire/撞名)={diff.NotProcessedByEngine} 不可比={diff.NotComparable}");

            foreach (var m in diff.Mismatches.Take(20)) Console.WriteLine($"       [差异] {m}");

            T.Eq(diff.Mismatches.Count, 0, "Step 3 真语料差分：不一致条数必须为 0");
            T.Eq(diff.EngineFallbackButMineDiffers, 0, "两侧对「能不能解析」的判断必须一致（否则差分样本被误选）");
            T.True(diff.Compared >= CorpusComparedFloor,
                $"可比条目数必须 ≥ {CorpusComparedFloor}（实际 {diff.Compared}）—— 防「差分测试退化成空循环」");
            T.True(r.SkippedMayRequire > 0, $"语料里必须含 MayRequire 节点（实际跳过 {r.SkippedMayRequire} 个）");
            T.True(depth >= 2, $"语料里必须含多级继承链（实测最长链深 {depth}）");
            // ★ `CheckForDuplicateNodes` 触发的节点离线取不到引擎真值（引擎会整次中断）——
            //   语料里必须一个都没有，否则差分范围被人为缩小，必须停下上报。
            T.Eq(r.SkippedDuplicateNodeName, 0,
                "真语料里不得有会触发引擎 CheckForDuplicateNodes 日志的节点（否则差分范围被缩小）");
            T.Eq(diff.NotComparable, 0, "差分里不得有「取不到真值」的条目");
        });

        // ── Step 4：零回归 ────────────────────────────────────────────────────
        T.Case("★★ Step 4 零回归：active 作用域 unresolved==0 / 环==0 / 分桶不变式成立", () =>
        {
            var ctx = ActiveContext();
            var a = InheritanceLayer.Analyze(ctx);

            var diags = new CheckRunner().Add(new ParentChainCheck()).Run(ctx).ToList();
            var rd2005 = diags.Count(d => d.Code == Codes.ParentNotFound);
            var rd2006 = diags.Count(d => d.Code == Codes.InheritanceCycle);

            Console.WriteLine($"       [Step 4] active: defs={ctx.Defs.Count} withParentName={a.WithParentName} "
                              + $"skippedByMayRequire={a.SkippedByMayRequire} nameSpace={a.NameSpace} "
                              + $"viaPatchLiteral={a.ViaPatchLiteral} defNameFallback={a.DefNameFallback} "
                              + $"unresolved={a.Unresolved} chainBroken={a.ChainBroken} "
                              + $"nameSpaceBlockedByMayRequire={a.NameSpaceBlockedByMayRequire} "
                              + $"RD2005={rd2005} RD2006={rd2006}");

            T.Eq(a.Unresolved, 0, "active 上 Unresolved 必须为 0（Task 5a 的锚点）");
            T.Eq(a.ChainBroken, 0, "active 上 ChainBroken 必须为 0");
            T.Eq(a.NameSpaceBlockedByMayRequire, 0, "active 上 nameSpaceBlockedByMayRequire 必须为 0");
            T.Eq(rd2005, 0, "active 上 RD2005 必须为 0");
            T.Eq(rd2006, 0, "active 上环必须为 0");
            T.Eq(a.WithParentName, a.SkippedByMayRequire + a.NameSpace + a.ViaPatchLiteral + a.DefNameFallback + a.Unresolved,
                "分桶不变式：withParentName == skippedByMayRequire + 四桶之和");
        });

        // ── Step 2 的反向钉子：Merge 不许改动解析口径 ──────────────────────────
        T.Case("★ (M) Merge 不改动任何解析口径：ResolveNode 只读入参、Merge 返回新对象", () =>
        {
            RequireProbe("(M)");
            var doc = new XmlDocument();
            doc.LoadXml("<Defs><A Name=\"MA\"><x>1</x></A><B ParentName=\"MA\"><y>2</y></B></Defs>");
            var parent = doc.DocumentElement!.ChildNodes[0]!;
            var child = doc.DocumentElement.ChildNodes[1]!;
            var before = EngineInheritance.Normalize(doc.DocumentElement);

            var merged = InheritanceLayer.Merge(parent, child);
            T.Eq(EngineInheritance.Normalize(doc.DocumentElement), before,
                "(M) Merge 不得改动入参所在的文档");
            T.False(ReferenceEquals(merged, parent), "(M) Merge 必须返回新对象");
            T.Contains(EngineInheritance.Normalize(merged), "<x>1</x>", "(M) 合并结果含父的 x");
            T.Contains(EngineInheritance.Normalize(merged), "<y>2</y>", "(M) 合并结果含子的 y");

            // 父名不存在的退化路径：与引擎一致地**返回原节点**
            var same = InheritanceLayer.ResolveNode(child, _ => null);
            T.True(ReferenceEquals(same, child), "(M) 父名找不到 ⇒ 与引擎一致地返回原节点对象");
        });
    }
}
