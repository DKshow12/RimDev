using System.Reflection;
using RimDev.Core.Catalog;
using RimDev.Core.Checks;
using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;
using RimDev.Core.Metadata;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// 第 3 期 Task 4：<c>PatchLayer</c>（PatchOperation 家族静态求值）与 <c>PatchCheck</c>（RD2009/RD2014）。
///
/// ★ 用例分工（照「能证伪它的用例」原则写，不是照「能验证它的用例」）：
/// <list type="bullet">
/// <item><b>每个操作家族 ≥ 1 条</b>：Add / Insert / Replace / Remove / SetName / AttributeAdd / AttributeSet /
///   AttributeRemove / Sequence / Conditional / Test / FindMod / AddModExtension。</item>
/// <item><b>能证伪的孪生用例</b>：Sequence 遇到失败的 Test 必须**停下**（后继操作不许执行）；
///   Conditional 的 xpath 未命中必须**不报 RD2009**（控制流不是「打空」）；FindMod 写 packageId 必须**不命中**
///   （引擎比的是 <c>ModMetaData.Name</c>）；扫描集合之外的 mod 的坏补丁**不许**进报告。</item>
/// <item><b>真语料</b>：官方 6 包的 14 个补丁文件上 RD2009 必须为 0（实施计划 §5 的风险表把这条定为回归门）；
///   Step 6 的 CE 反例（<c>OSNGun_HMG</c>）用真文件求值。</item>
/// <item><b>Step 1 时序</b>：从**真实程序集**的 IL 里读调用顺序 —— 不是照抄反编译散文。</item>
/// </list>
/// </summary>
public static class Phase3Task4Cases
{
    private static readonly EngineVersionInfo TestVersion =
        new("1.6.4871", "1.6", new Version(1, 6, 4871, 591), "test", null);

    // ───────────────────────── 夹具 ─────────────────────────

    private sealed class TempDir : IDisposable
    {
        public string Root { get; }

        public TempDir()
        {
            Root = Path.Combine(Path.GetTempPath(), "rimdev-t4-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

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

    private const string DefsXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <Defs>
          <ThingDef>
            <defName>Base</defName>
            <comps>
              <li Class="CompProperties_A"><tag>A</tag></li>
            </comps>
          </ThingDef>
          <ThingDef ParentName="Base">
            <defName>Child</defName>
          </ThingDef>
        </Defs>
        """;

    private static string Patch(string operations) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Patch>\n" + operations + "\n</Patch>\n";

    /// <summary>跑一次合成语料的补丁求值（defs 一份、patches 一份）。</summary>
    private static PatchRunResult Run(string ops, string? defsXml = null,
        IReadOnlySet<string>? activeNames = null, TempDir? owner = null)
    {
        var d = owner ?? new TempDir();
        try
        {
            var defFile = d.Write("Defs/A.xml", defsXml ?? DefsXml);
            var patchFile = d.Write("Patches/P.xml", Patch(ops));
            return PatchLayer.Apply(new[] { (defFile, "test.mod") }, new[] { (patchFile, "test.mod") },
                activeNames ?? new HashSet<string>(StringComparer.Ordinal));
        }
        finally
        {
            if (owner is null) d.Dispose();
        }
    }

    // ───────────────────────── 用例 ─────────────────────────
    public static void Run()
    {
        T.Group("phase3-task4");

        // ═══════════ Step 2：操作家族逐个 ═══════════

        T.Case("★ Add：默认 order=Append（引擎 `private Order order;` 无初始化器 ⇒ 0=Append）", () =>
        {
            var r = Run("""
                <Operation Class="PatchOperationAdd">
                  <xpath>Defs/ThingDef[defName="Base"]/comps</xpath>
                  <value><li Class="CompProperties_B"><tag>B</tag></li></value>
                </Operation>
                """);
            T.Eq(r.Outcomes.Count, 1, "操作数");
            T.Eq(r.Outcomes[0].Matched, 1, "XPath 命中数");
            T.True(r.Outcomes[0].Applied, "应视为成功");
            T.Eq(r.Document.SelectNodes("Defs/ThingDef[defName=\"Base\"]/comps/li")!.Count, 2, "comps 下应有 2 个 li");
            // Append ⇒ 新节点在最后（tag=B）
            T.Eq(r.Document.SelectSingleNode("Defs/ThingDef[defName=\"Base\"]/comps/li[last()]/tag")!.InnerText, "B",
                "Append 时新 li 必须在末尾");
        });

        T.Case("★ Add + order=Prepend：新节点插到最前", () =>
        {
            var r = Run("""
                <Operation Class="PatchOperationAdd">
                  <xpath>Defs/ThingDef[defName="Base"]/comps</xpath>
                  <order>Prepend</order>
                  <value><li Class="CompProperties_B"><tag>B</tag></li></value>
                </Operation>
                """);
            T.Eq(r.Document.SelectSingleNode("Defs/ThingDef[defName=\"Base\"]/comps/li[1]/tag")!.InnerText, "B",
                "Prepend 时新 li 必须在最前");
        });

        T.Case("★ Insert：默认 order=Prepend（插在命中节点的**前面**，是兄弟节点）", () =>
        {
            var r = Run("""
                <Operation Class="PatchOperationInsert">
                  <xpath>Defs/ThingDef[defName="Base"]/comps/li</xpath>
                  <value><li Class="CompProperties_C"><tag>C</tag></li></value>
                </Operation>
                """);
            T.Eq(r.Outcomes[0].Matched, 1, "命中");
            var tags = r.Document.SelectNodes("Defs/ThingDef[defName=\"Base\"]/comps/li")!
                .Cast<System.Xml.XmlNode>().Select(n => n.SelectSingleNode("tag")!.InnerText).ToList();
            T.Eq(string.Join(",", tags), "C,A", "Insert 默认 Prepend：新节点在前");
        });

        T.Case("★★ Insert 的**反序**是引擎行为，不是 bug（照抄 `foreach … InsertAfter`）", () =>
        {
            // 引擎原文：order==Append 时 `foreach (child in value.ChildNodes) parent.InsertAfter(Import(child), node);`
            // —— 每次都在**同一个锚点**后面插入，两个 child 的结果是 B2,B1 的反序。
            var r = Run("""
                <Operation Class="PatchOperationInsert">
                  <xpath>Defs/ThingDef[defName="Base"]/comps/li</xpath>
                  <order>Append</order>
                  <value>
                    <li Class="CompProperties_C"><tag>C1</tag></li>
                    <li Class="CompProperties_C"><tag>C2</tag></li>
                  </value>
                </Operation>
                """);
            var tags = r.Document.SelectNodes("Defs/ThingDef[defName=\"Base\"]/comps/li")!
                .Cast<System.Xml.XmlNode>().Select(n => n.SelectSingleNode("tag")!.InnerText).ToList();
            T.Eq(string.Join(",", tags), "A,C2,C1", "多 child + Append ⇒ 反序（引擎原样，不许「修正」）");
        });

        T.Case("★ Replace：整块替换命中节点", () =>
        {
            var r = Run("""
                <Operation Class="PatchOperationReplace">
                  <xpath>Defs/ThingDef[defName="Base"]/comps</xpath>
                  <value><li Class="CompProperties_D"><tag>D</tag></li></value>
                </Operation>
                """);
            // 目标是 <comps> 本身 ⇒ 新 <li> 落在 ThingDef 下（不是 comps/li）
            T.Eq(r.Document.SelectNodes("Defs/ThingDef[defName=\"Base\"]/li")!.Count, 1, "替换后 ThingDef 下应有 1 个 li");
            T.Null(r.Document.SelectSingleNode("Defs/ThingDef[defName=\"Base\"]/comps"), "旧的 comps 元素必须消失");
            T.Eq(r.Document.SelectSingleNode("Defs/ThingDef[defName=\"Base\"]/li/tag")!.InnerText, "D", "内容被换掉");
        });

        T.Case("★ Remove：删掉命中节点", () =>
        {
            var r = Run("""
                <Operation Class="PatchOperationRemove">
                  <xpath>Defs/ThingDef[defName="Child"]</xpath>
                </Operation>
                """);
            T.Null(r.Document.SelectSingleNode("Defs/ThingDef[defName=\"Child\"]"), "Child 应被删掉");
            T.Eq(r.Document.SelectNodes("Defs/*")!.Count, 1, "只剩 Base");
        });

        T.Case("★ SetName：换元素名（引擎用 InnerXml 复制 ⇒ **属性会丢**，照抄）", () =>
        {
            var r = Run("""
                <Operation Class="PatchOperationSetName">
                  <xpath>Defs/ThingDef[defName="Child"]</xpath>
                  <name>ThingDefRenamed</name>
                </Operation>
                """);
            T.Null(r.Document.SelectSingleNode("Defs/ThingDef[defName=\"Child\"]"), "旧元素名应消失");
            T.NotNull(r.Document.SelectSingleNode("Defs/ThingDefRenamed/defName"), "新元素名应存在");
            T.Null(r.Document.SelectSingleNode("Defs/ThingDefRenamed/@ParentName"),
                "★ 属性必须丢掉（引擎 CreateElement+InnerXml 的行为，不是我们的实现选择）");
        });

        T.Case("★ AttributeAdd：只在属性**不存在**时加（已存在则不改且不算成功）", () =>
        {
            var r = Run("""
                <Operation Class="PatchOperationAttributeAdd">
                  <xpath>Defs/ThingDef[defName="Base"]</xpath>
                  <attribute>Name</attribute>
                  <value>Base</value>
                </Operation>
                <Operation Class="PatchOperationAttributeAdd">
                  <xpath>Defs/ThingDef[defName="Base"]</xpath>
                  <attribute>Name</attribute>
                  <value>SHOULD_NOT_APPEAR</value>
                </Operation>
                """);
            T.Eq(r.Document.SelectSingleNode("Defs/ThingDef[defName=\"Base\"]")!.Attributes!["Name"]!.Value, "Base",
                "第一次加上的值必须保留");
            T.Eq(r.Outcomes[0].Applied, true, "第一次应是成功");
            T.Eq(r.Outcomes[1].Applied, false, "属性已存在 ⇒ 引擎 result 保持 false（第二次是「没打中」）");
            T.Eq(r.NameAdditions.Count, 1, "只有真正加上的那次记进 NameAdditions");
        });

        T.Case("★ AttributeSet：存在则改、不存在则建（与 AttributeAdd 语义不同）", () =>
        {
            var r = Run("""
                <Operation Class="PatchOperationAttributeSet">
                  <xpath>Defs/ThingDef[defName="Child"]</xpath>
                  <attribute>ParentName</attribute>
                  <value>NewParent</value>
                </Operation>
                <Operation Class="PatchOperationAttributeSet">
                  <xpath>Defs/ThingDef[defName="Child"]</xpath>
                  <attribute>Category</attribute>
                  <value>X</value>
                </Operation>
                """);
            var child = r.Document.SelectSingleNode("Defs/ThingDef[defName=\"Child\"]")!;
            T.Eq(child.Attributes!["ParentName"]!.Value, "NewParent", "已存在 ⇒ 改值");
            T.Eq(child.Attributes!["Category"]!.Value, "X", "不存在 ⇒ 建（AttributeSet 的第二个分支）");
            T.Eq(r.NameAdditions.Count, 0, "AttributeSet 不是 AttributeAdd，不进 NameAdditions");
        });

        T.Case("★ AttributeRemove：只删存在的（不存在时不算成功）", () =>
        {
            var r = Run("""
                <Operation Class="PatchOperationAttributeRemove">
                  <xpath>Defs/ThingDef[defName="Child"]</xpath>
                  <attribute>ParentName</attribute>
                </Operation>
                <Operation Class="PatchOperationAttributeRemove">
                  <xpath>Defs/ThingDef[defName="Child"]</xpath>
                  <attribute>NoSuchAttribute</attribute>
                </Operation>
                """);
            T.Null(r.Document.SelectSingleNode("Defs/ThingDef[defName=\"Child\"]")!.Attributes!["ParentName"], "应被删");
            T.Eq(r.Outcomes[0].Applied, true, "删到了 ⇒ 成功");
            T.Eq(r.Outcomes[1].Applied, false, "没这个属性 ⇒ 失败");
        });

        T.Case("★★ Sequence：按顺序执行；遇到失败的 Test 必须**停下**（证伪型）", () =>
        {
            var r = Run("""
                <Operation Class="PatchOperationSequence">
                  <operations>
                    <li Class="PatchOperationAdd">
                      <xpath>Defs/ThingDef[defName="Base"]</xpath>
                      <value><marker>1</marker></value>
                    </li>
                    <li Class="PatchOperationTest">
                      <xpath>Defs/ThingDef[defName="NoSuchDef"]</xpath>
                    </li>
                    <li Class="PatchOperationAdd">
                      <xpath>Defs/ThingDef[defName="Base"]</xpath>
                      <value><marker>2</marker></value>
                    </li>
                  </operations>
                </Operation>
                """);
            var markers = r.Document.SelectNodes("Defs/ThingDef[defName=\"Base\"]/marker")!
                .Cast<System.Xml.XmlNode>().Select(n => n.InnerText).ToList();
            T.Eq(string.Join(",", markers), "1", "Test 失败后第 3 条操作**不许**执行（引擎 `return false`）");
            // 3 个 outcome：Sequence 自己 + 真正执行到的 2 条（第 3 条因 Test 失败**根本没被求值**）
            T.Eq(r.Outcomes.Count, 3, "Sequence + 执行到的 2 条嵌套操作");
            // ★ 顺序是**子操作在前、容器在后**（Add,Test,Sequence），这是「自己的结果只有跑完子操作才知道」
            //   的必然结果：ApplyElement 先 Dispatch（递归执行并记录子操作），再记录自己的 outcome。
            //   前序（Sequence,Add,Test）在实现上拿不到 —— 记录时 Applied/Matched 还没算出来。**别把它「修」回前序。**
            //   本条真正在钉的是「第 3 条操作根本没被求值」，由上面 markers == "1" 那条证伪型断言承担。
            T.Eq(string.Join(",", r.Outcomes.Select(o => o.Kind)), "Add,Test,Sequence",
                "outcome 记录顺序：子操作在前、容器在后（容器结果未知前不能记录）");
            T.Eq(r.Diagnostics.Count(d => d.Code == Codes.PatchXpathNoMatch), 0,
                "★ Test 失败不是「补丁打空」：控制流操作一律不报 RD2009");
        });

        T.Case("★ Conditional 真分支 / 假分支各走一次", () =>
        {
            var r = Run("""
                <Operation Class="PatchOperationConditional">
                  <xpath>Defs/ThingDef[defName="Base"]</xpath>
                  <match Class="PatchOperationAdd">
                    <xpath>Defs/ThingDef[defName="Base"]</xpath>
                    <value><hit>match</hit></value>
                  </match>
                  <nomatch Class="PatchOperationAdd">
                    <xpath>Defs/ThingDef[defName="Base"]</xpath>
                    <value><hit>nomatch</hit></value>
                  </nomatch>
                </Operation>
                <Operation Class="PatchOperationConditional">
                  <xpath>Defs/ThingDef[defName="Missing"]</xpath>
                  <match Class="PatchOperationAdd">
                    <xpath>Defs/ThingDef[defName="Base"]</xpath>
                    <value><miss>match</miss></value>
                  </match>
                  <nomatch Class="PatchOperationAdd">
                    <xpath>Defs/ThingDef[defName="Base"]</xpath>
                    <value><miss>nomatch</miss></value>
                  </nomatch>
                </Operation>
                """);
            T.NotNull(r.Document.SelectSingleNode("Defs/ThingDef[defName=\"Base\"]/hit[.=\"match\"]"), "真分支");
            T.NotNull(r.Document.SelectSingleNode("Defs/ThingDef[defName=\"Base\"]/miss[.=\"nomatch\"]"), "假分支");
            T.Eq(r.Diagnostics.Count(d => d.Code == Codes.PatchXpathNoMatch), 0,
                "★ Conditional 的 xpath 未命中是**设计意图**（要走 nomatch），绝不是「补丁打空」");
        });

        T.Case("★ FindMod：判据是 **ModMetaData.Name（显示名）**，写 packageId 必须不命中（证伪型）", () =>
        {
            var active = new HashSet<string>(new[] { "Combat Extended" }, StringComparer.Ordinal);
            var r = Run("""
                <Operation Class="PatchOperationFindMod">
                  <mods>
                    <li>CETeam.CombatExtended</li>
                  </mods>
                  <match Class="PatchOperationAdd">
                    <xpath>Defs/ThingDef[defName="Base"]</xpath>
                    <value><byPackageId>1</byPackageId></value>
                  </match>
                  <nomatch Class="PatchOperationAdd">
                    <xpath>Defs/ThingDef[defName="Base"]</xpath>
                    <value><byPackageId>0</byPackageId></value>
                  </nomatch>
                </Operation>
                """, activeNames: active);
            // ★ 即便「Combat Extended」在启用列表里，写 packageId 也命中不了 —— 引擎比的是 Name。
            T.NotNull(r.Document.SelectSingleNode("Defs/ThingDef[defName=\"Base\"]/byPackageId[.=\"0\"]"),
                "packageId 写法必须走 nomatch（引擎 ModLister.HasActiveModWithName 是 Ordinal 比 Name）");
        });

        T.Case("★ FindMod：写显示名必须命中 match 分支", () =>
        {
            var active = new HashSet<string>(new[] { "Combat Extended" }, StringComparer.Ordinal);
            var r = Run("""
                <Operation Class="PatchOperationFindMod">
                  <mods><li>Combat Extended</li></mods>
                  <match Class="PatchOperationAdd">
                    <xpath>Defs/ThingDef[defName="Base"]</xpath>
                    <value><hit>yes</hit></value>
                  </match>
                </Operation>
                """, activeNames: active);
            T.NotNull(r.Document.SelectSingleNode("Defs/ThingDef[defName=\"Base\"]/hit[.=\"yes\"]"), "显示名命中");
        });

        T.Case("★ AddModExtension：没有 modExtensions 时自动创建，有时追加", () =>
        {
            var r = Run("""
                <Operation Class="PatchOperationAddModExtension">
                  <xpath>Defs/ThingDef[defName="Base"]</xpath>
                  <value><li Class="SomeExt"><a>1</a></li></value>
                </Operation>
                <Operation Class="PatchOperationAddModExtension">
                  <xpath>Defs/ThingDef[defName="Base"]</xpath>
                  <value><li Class="SomeExt2"><b>2</b></li></value>
                </Operation>
                """);
            T.Eq(r.Document.SelectNodes("Defs/ThingDef[defName=\"Base\"]/modExtensions")!.Count, 1,
                "第二次不许再造一个 modExtensions");
            T.Eq(r.Document.SelectNodes("Defs/ThingDef[defName=\"Base\"]/modExtensions/li")!.Count, 2, "两个扩展都进去了");
        });

        // ═══════════ Step 3 / Step 4：RD2009 与 RD2014 ═══════════

        T.Case("★★ Step 3：XPath 0 匹配 → RD2009 warning（且只对会改文档的操作）", () =>
        {
            using var dir = new TempDir();
            var r = Run("""
                <Operation Class="PatchOperationAdd">
                  <xpath>Defs/ThingDef[defName="Typo_Def"]</xpath>
                  <value><x>1</x></value>
                </Operation>
                """, owner: dir);
            var d = r.Diagnostics.Single(x => x.Code == Codes.PatchXpathNoMatch);
            T.Eq(d.Severity, Severity.Warning, "RD2009 必须是 warning（`01` §7 码表）");
            // ★ 行号断言必须**自校验**：不许写死数字（补丁文件的头部行数会随 Patch() 夹具变化），
            //   而是回到那份文件里确认「第 Line 行真的含这个 XPath」。
            T.True(d.Line > 0, "必须给出行号");
            var srcLines = File.ReadAllLines(d.FileAbs!);
            T.Contains(srcLines[d.Line!.Value - 1], "Typo_Def", "行号必须指向 <xpath> 那一行");
            T.Contains(d.Message, "Typo_Def", "消息里要有那个 XPath");
            T.Eq(d.Evidence!["outcome"], "noMatch", "evidence 区分 0 匹配与求值异常");
            T.NotNull(d.FileAbs, "必须带文件位置");
            T.Eq(r.Outcomes[0].Matched, 0, "命中数 0");
        });

        T.Case("★★ Step 3 证伪型：Test / Conditional 的 0 匹配**不许**报 RD2009", () =>
        {
            var r = Run("""
                <Operation Class="PatchOperationTest">
                  <xpath>Defs/ThingDef[defName="Nope"]</xpath>
                </Operation>
                <Operation Class="PatchOperationConditional">
                  <xpath>Defs/ThingDef[defName="Nope"]</xpath>
                  <nomatch Class="PatchOperationRemove">
                    <xpath>Defs/ThingDef[defName="Child"]</xpath>
                  </nomatch>
                </Operation>
                """);
            T.Eq(r.Diagnostics.Count(d => d.Code == Codes.PatchXpathNoMatch), 0,
                "控制流操作 0 匹配是设计意图，报 RD2009 会在官方内容上立刻造出假阳");
            T.Null(r.Document.SelectSingleNode("Defs/ThingDef[defName=\"Child\"]"), "nomatch 分支确实执行了");
        });

        T.Case("★★ Step 3：XPath 语法错 → RD2009（outcome=xpathError，与 0 匹配分开）", () =>
        {
            var r = Run("""
                <Operation Class="PatchOperationAdd">
                  <xpath>Defs/ThingDef[defName=</xpath>
                  <value><x>1</x></value>
                </Operation>
                """);
            var d = r.Diagnostics.Single(x => x.Code == Codes.PatchXpathNoMatch);
            T.Eq(d.Evidence!["outcome"], "xpathError", "必须是求值失败，不是 0 匹配");
            T.Contains(d.Message, "求值失败", "消息要说清是哪一种");
        });

        T.Case("★★ Step 4：自定义 Class= → RD2014 info 且**跳过**（DOM 不许被改动）", () =>
        {
            var r = Run("""
                <Operation Class="CombatExtended.PatchOperationMakeGunCECompatible">
                  <xpath>Defs/ThingDef[defName="Base"]</xpath>
                  <defName>Base</defName>
                  <statBases><Mass>1</Mass></statBases>
                </Operation>
                """);
            var d = r.Diagnostics.Single(x => x.Code == Codes.CustomPatchClass);
            T.Eq(d.Severity, Severity.Info, "RD2014 是 info（`01` §7：「需真机验证」）");
            T.Eq(d.Evidence!["reason"], "customClass", "归因");
            T.Eq(r.Outcomes[0].Skipped, true, "被跳过");
            T.Eq(r.Stats.SkippedCustomClass, 1, "计数");
            T.Eq(r.Outcomes[0].Matched, -1, "被跳过的操作不做 XPath 求值");
            T.Null(r.Document.SelectSingleNode("Defs/ThingDef[defName=\"Base\"]/statBases"),
                "★ 不许执行 mod 代码，也不许近似执行");
        });

        T.Case("★★ Step 4：缺 Class= → 也是 RD2014，但 reason=missingClass（引擎会回落基类并必然失败）", () =>
        {
            var r = Run("""
                <Operation>
                  <xpath>Defs/ThingDef[defName="Base"]</xpath>
                </Operation>
                """);
            var d = r.Diagnostics.Single(x => x.Code == Codes.CustomPatchClass);
            T.Eq(d.Evidence!["reason"], "missingClass", "归因");
            T.Eq(r.Outcomes[0].Kind, PatchOpKind.Unknown, "类型未识别");
        });

        // ═══════════ 结构边界（引擎硬要求） ═══════════

        T.Case("★★ 根不是 <Patch> 的文件**不是**补丁文件（引擎 Log.Error 后整份跳过）", () =>
        {
            using var d = new TempDir();
            var defFile = d.Write("Defs/A.xml", DefsXml);
            // 把补丁写成 <Defs> 根 —— 引擎 LoadPatches 会 `documentElement.Name != "Patch"` → Error + continue
            var bad = d.Write("Patches/P.xml", DefsXml);
            var r = PatchLayer.Apply(new[] { (defFile, "m") }, new[] { (bad, "m") },
                new HashSet<string>(StringComparer.Ordinal));
            T.Eq(r.Stats.PatchFiles, 0, "一个补丁文件都不该被接受");
            T.Eq(r.Stats.TopLevelOperations, 0, "没有任何操作被执行");
            T.Eq(r.Outcomes.Count, 0, "没有 outcome");
        });

        T.Case("★ 一级子元素不叫 Operation 时跳过（引擎 `childNode.Name != 'Operation'` → Error + continue）", () =>
        {
            var r = Run("""
                <Add Class="PatchOperationAdd">
                  <xpath>Defs/ThingDef[defName="Base"]</xpath>
                  <value><x>1</x></value>
                </Add>
                <Operation Class="PatchOperationAdd">
                  <xpath>Defs/ThingDef[defName="Base"]</xpath>
                  <value><y>2</y></value>
                </Operation>
                """);
            T.Eq(r.Stats.TopLevelOperations, 1, "只有名字叫 Operation 的才算一条操作");
            T.Null(r.Document.SelectSingleNode("Defs/ThingDef[defName=\"Base\"]/x"), "非 Operation 元素不许执行");
            T.NotNull(r.Document.SelectSingleNode("Defs/ThingDef[defName=\"Base\"]/y"), "Operation 元素照常执行");
        });

        // ═══════════ Step 1：时序（真实程序集的 IL 证据） ═══════════

        T.Case("★★ Step 1 时序（真实程序集 IL）：ApplyPatches 在 ParseAndProcessXML 之前", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var asm = ManagedAssemblyCatalog.Load(env.ManagedDir);

            var lmm = asm.AllTypes.First(t => t.FullName == "Verse.LoadedModManager");
            var loadAll = lmm.GetMethod("LoadAllActiveMods", BindingFlags.Public | BindingFlags.Static)!;
            var apply = lmm.GetMethod("ApplyPatches", BindingFlags.Public | BindingFlags.Static)!;
            var parse = lmm.GetMethod("ParseAndProcessXML", BindingFlags.Public | BindingFlags.Static)!;

            var il = loadAll.GetMethodBody()!.GetILAsByteArray()!;
            var iApply = TokenOffset(il, apply.MetadataToken);
            var iParse = TokenOffset(il, parse.MetadataToken);
            T.True(iApply >= 0, "LoadAllActiveMods 里应能读到 ApplyPatches 的 call");
            T.True(iParse >= 0, "LoadAllActiveMods 里应能读到 ParseAndProcessXML 的 call");
            T.True(iApply < iParse,
                $"IL 字节偏移必须 ApplyPatches({iApply}) < ParseAndProcessXML({iParse})");
            Console.WriteLine($"    [Step1/IL] LoadAllActiveMods: ApplyPatches@{iApply} < ParseAndProcessXML@{iParse}");
        });

        T.Case("★★ Step 1 时序（真实程序集 IL）：XmlInheritance.TryRegister / Resolve 的**唯一调用者**是 ParseAndProcessXML", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var asm = ManagedAssemblyCatalog.Load(env.ManagedDir);

            var lmm = asm.AllTypes.First(t => t.FullName == "Verse.LoadedModManager");
            var inherit = asm.AllTypes.First(t => t.FullName == "Verse.XmlInheritance");
            var tryReg = inherit.GetMethod("TryRegister", BindingFlags.Public | BindingFlags.Static)!;
            var resolve = inherit.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static)!;

            var callersTry = CallersOf(asm, lmm, tryReg.MetadataToken);
            var callersResolve = CallersOf(asm, lmm, resolve.MetadataToken);

            T.Eq(string.Join(",", callersTry), "ParseAndProcessXML", "TryRegister 的调用者");
            T.Eq(string.Join(",", callersResolve), "ParseAndProcessXML", "Resolve 的调用者");
            Console.WriteLine($"    [Step1/IL] TryRegister 调用者={string.Join(",", callersTry)}；"
                + $"Resolve 调用者={string.Join(",", callersResolve)}");
        });

        // ═══════════ Step 5/6：补丁补 Name → ParentName 可解析（真实反例） ═══════════

        T.Case("★★ Step 5：补丁给父 def 补 Name 后，子 def 的 ParentName 才能进 Name 空间（合成）", () =>
        {
            var r = Run("""
                <Operation Class="PatchOperationAttributeAdd">
                  <xpath>Defs/ThingDef[defName="Base"]</xpath>
                  <attribute>Name</attribute>
                  <value>BaseName</value>
                </Operation>
                """);
            T.Eq(r.NameAdditions.Count, 1, "补上了一条 Name");
            T.Eq(r.NameAdditions[0].AddedName, "BaseName", "补的值");
            T.Eq(r.NameAdditions[0].TargetDefName, "Base", "打在哪个 def 上");
            T.NotNull(r.Document.SelectSingleNode("Defs/ThingDef[@Name=\"BaseName\"]"), "Name 空间里现在有它了");
            // 证伪：**不打补丁**时 Name 空间里没有它（同一份 def 语料经 DefScanner 读出来就是没有 Name=）
            using var d = new TempDir();
            var rawFile = d.Write("Defs/A.xml", DefsXml);
            var rawScan = DefScanner.ScanFile(rawFile, "test.mod", d.Root);
            T.False(rawScan.Defs.Any(x => string.Equals(x.NameAttr, "BaseName", StringComparison.Ordinal)),
                "未打补丁时 Name 空间里不该有 BaseName（这正是「靠 defName 兜底」的那批）");
        });

        T.Case("★★ Step 6（真实语料）：Orostonian 的 CE 集成补丁给 OSNGun_HMG 补 Name，MechGuns 的子 def 才解析得开", () =>
        {
            // ⚠️ 口径：这两份文件都在 <mod>\CombatExtended\ 下，而该目录的 loadFolders 条件是
            //    `IfModActive="CETeam.CombatExtended,..."`；**本机并没有启用 CE**，
            //    所以按真实启用集它根本不会被加载（--scope all 也不会）。这里显式把 CE 当作启用，
            //    才能复现任务书 §6 引用的那条反例。
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var cat = ModCatalog.Build(env, env.TargetVersion, ModCatalog.ReadActiveMods(env.ModsConfigPath)).Catalog;
            var mod = cat.All.FirstOrDefault(m =>
                File.Exists(Path.Combine(m.RootPath, "CombatExtended", "Patches", "ThingDef_Guns.xml")));
            T.NotNull(mod, "本机应存在带 CombatExtended\\Patches\\ThingDef_Guns.xml 的 mod（Orostonian）");

            var doc = LoadFolderResolver.Load(mod!.RootPath);
            var resolved = LoadFolderResolver.Resolve(mod.RootPath, mod.PackageId, doc, TestVersion,
                LoadFolderResolver.ActiveMatcher(new[] { "CETeam.CombatExtended", "CETeam.CombatExtended_steam" }),
                mod.SupportedVersions);
            var files = DefFileEnumerator.Enumerate(resolved.DescendingOrder);
            var scan = DefScanner.Scan(files.Select(f => (f.FileAbs, mod.PackageId, mod.RootPath)));

            var defs = scan.Files.Where(f => f.ParseError is null && f.RootKind is null)
                .Select(f => (f.FileAbs, f.ModPackageId)).ToList();
            var patches = scan.Files.Where(f => f.RootKind == "Patch")
                .Select(f => (f.FileAbs, f.ModPackageId)).ToList();
            T.True(patches.Count > 0, "CE 集成目录里应有补丁文件");

            // ① 打补丁**之前**：Name 空间里没有 OSNGun_HMG（子 def 只能靠 defName 兜底）
            var nameBefore = scan.Defs.Any(d => string.Equals(d.NameAttr, "OSNGun_HMG", StringComparison.Ordinal));
            T.False(nameBefore, "补丁之前 Name 空间里不该有 OSNGun_HMG");

            var run = PatchLayer.Apply(defs, patches, new HashSet<string>(StringComparer.Ordinal));

            // ② 打补丁**之后**：Name 空间里有了
            T.True(run.NameAdditions.Any(a => a.AddedName == "OSNGun_HMG"),
                "必须看到 AttributeAdd(Name=OSNGun_HMG) 真的被应用");
            T.NotNull(run.Document.SelectSingleNode("Defs/ThingDef[@Name=\"OSNGun_HMG\"]"),
                "打完补丁后 Name 空间必须命中");
            // ③ 那个靠 ParentName 指过来的子 def 确实在树里
            T.NotNull(run.Document.SelectSingleNode("Defs/ThingDef[defName=\"OSNGun_HMG_Mech\"]"),
                "MechGuns.xml 的子 def 必须在统一树里");
            Console.WriteLine($"    [Step6/真实反例] mod={mod.PackageId} defs={defs.Count} patches={patches.Count} "
                + $"nameAdded={run.NameAdditions.Count}（OSNGun_HMG → 命中）");
        });

        // ═══════════ PatchCheck：范围、报告过滤、零假阳门 ═══════════

        T.Case("★★ PatchCheck 报告范围：**未启用** mod 的坏补丁不许进报告（证伪型）", () =>
        {
            using var d = new TempDir();
            // 两个合成 mod：一个启用、一个未启用；未启用的那个补丁必然打空
            var activeRoot = Path.Combine(d.Root, "Active");
            var idleRoot = Path.Combine(d.Root, "Idle");
            Directory.CreateDirectory(activeRoot);
            Directory.CreateDirectory(idleRoot);
            File.WriteAllText(Path.Combine(activeRoot, "Defs.xml"), DefsXml);
            File.WriteAllText(Path.Combine(idleRoot, "Defs.xml"), DefsXml);
            Directory.CreateDirectory(Path.Combine(idleRoot, "Patches"));
            File.WriteAllText(Path.Combine(idleRoot, "Patches", "Broken.xml"), Patch("""
                <Operation Class="PatchOperationAdd">
                  <xpath>Defs/ThingDef[defName="DoesNotExist"]</xpath>
                  <value><x>1</x></value>
                </Operation>
                """));

            var mods = new List<ModInfo>
            {
                MakeMod("test.active", "Active Mod", activeRoot, isActive: true),
                MakeMod("test.idle", "Idle Mod", idleRoot, isActive: false),
            };
            var files = DefScanner.CollectFiles(mods.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)));
            // 手工把两个目录都塞进 loadFolders（合成 mod 没有 loadFolders.xml，走回退分支：root + 1.6）
            var scan = DefScanner.Scan(files);
            var ctx = new CheckContext(DefIndex.Build(scan.Defs), scan.Files, ModCatalog.FromMods(mods), scan.Defs, null);
            var diags = new PatchCheck().Run(ctx).ToList();

            T.Eq(diags.Count(x => x.Code == Codes.PatchXpathNoMatch), 0,
                "未启用 mod 的补丁不该被求值（引擎 ApplyPatches 只跑 runningMods）");
        });

        T.Case("★ PatchCheck：启用 mod 的坏补丁必须报 RD2009（证明上面那条不是「规则没生效」）", () =>
        {
            using var d = new TempDir();
            var root = Path.Combine(d.Root, "Active");
            Directory.CreateDirectory(Path.Combine(root, "Patches"));
            File.WriteAllText(Path.Combine(root, "Defs.xml"), DefsXml);
            File.WriteAllText(Path.Combine(root, "Patches", "Broken.xml"), Patch("""
                <Operation Class="PatchOperationAdd">
                  <xpath>Defs/ThingDef[defName="DoesNotExist"]</xpath>
                  <value><x>1</x></value>
                </Operation>
                """));

            var mods = new List<ModInfo> { MakeMod("test.active", "Active Mod", root, isActive: true) };
            var files = DefScanner.CollectFiles(mods.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)));
            var scan = DefScanner.Scan(files);
            var ctx = new CheckContext(DefIndex.Build(scan.Defs), scan.Files, ModCatalog.FromMods(mods), scan.Defs, null);
            var check = new PatchCheck();
            var diags = check.Run(ctx).ToList();

            T.Eq(diags.Count(x => x.Code == Codes.PatchXpathNoMatch), 1, "应报 1 条 RD2009");
            T.Eq(diags[0].Evidence!["evaluatedConfiguration"], "active∩scope", "必须写明求值范围（防假 0）");
            T.NotNull(check.LastRun, "LastRun 必须有值（CLI 的 patchEvaluation 靠它）");
            T.Eq(check.LastRun!.TopLevelOperations, 1, "顶层操作数");
        });

        T.Case("★★ 零假阳门：官方 6 包的 14 个补丁文件上 RD2009 / RD2014 必须为 0", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
            var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;
            var order = LoadOrder.Build(env, cat, active, LoadOrder.ReadKnownExpansions(env.ModsConfigPath));
            var official = cat.All.Where(m => m.Source == ModSource.Official).ToList();

            var files = DefScanner.CollectFiles(official.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)));
            var scan = DefScanner.Scan(files);
            var ctx = new CheckContext(DefIndex.Build(scan.Defs), scan.Files, ModCatalog.FromMods(official), scan.Defs,
                null, LoadOrder: order, ModRank: order.RankFor(official));
            var check = new PatchCheck();
            var diags = check.Run(ctx).ToList();

            T.True(check.LastRun is not null, "官方内容上补丁层必须真的跑过（否则 0 是假 0）");
            T.True(check.LastRun!.PatchFiles > 0, $"官方补丁文件数应 > 0，实际 {check.LastRun.PatchFiles}");
            T.Eq(diags.Count(x => x.Code == Codes.PatchXpathNoMatch), 0, "官方补丁不允许出现「打空」");
            T.Eq(diags.Count(x => x.Code == Codes.CustomPatchClass), 0, "官方补丁不允许出现自定义类");
            Console.WriteLine($"    [官方/补丁层] patchFiles={check.LastInputs?.PatchFiles} "
                + $"topOps={check.LastRun.TopLevelOperations} defNodes {check.LastRun.DefNodesBeforePatches}"
                + $"→{check.LastRun.DefNodesAfterPatches} RD2009=0 RD2014=0");
        });

        // ═══════════ Step 1：真实语料上的阶段计数（实测，不是抄注释） ═══════════

        T.Case("★★ Step 1 阶段计数（启用集实测）：assets/unified/patchOps/registered 全部由代码算出", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
            var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;
            var order = LoadOrder.Build(env, cat, active, LoadOrder.ReadKnownExpansions(env.ModsConfigPath));

            var files = DefScanner.CollectFiles(cat.Active.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)));
            var scan = DefScanner.Scan(files);
            var defs = scan.Files.Where(f => f.ParseError is null && f.RootKind is null)
                .Select(f => (f.FileAbs, f.ModPackageId)).ToList();
            var patches = scan.Files.Where(f => f.RootKind == "Patch")
                .OrderBy(f => order.RankFor(cat.Active).TryGetValue(f.ModPackageId, out var r) ? r : int.MaxValue)
                .ThenBy(f => f.FileAbs, StringComparer.OrdinalIgnoreCase)
                .Select(f => (f.FileAbs, f.ModPackageId)).ToList();

            var names = new HashSet<string>(cat.Active.Select(m => m.DisplayName), StringComparer.Ordinal);
            var run = PatchLayer.Apply(defs, patches, names);
            Console.WriteLine("    [Step1/实测] " + run.Stats.Describe());

            T.True(run.Stats.UnifiedDefNodes > 1000, "统一树的 def 节点数应 > 1000");
            T.Eq(run.Stats.RegisteredNodes, run.Stats.DefNodesAfterPatches,
                "阶段 6 注册的是「补丁之后」的节点（时序的直接推论）");
            T.True(run.Stats.PatchFiles > 0, "启用集里应有补丁文件");
        });
    }

    // ───────────────────────── 度量（--task4-measure） ─────────────────────────

    /// <summary>
    /// Step 6 的**全库口径测量**：全库 def 扫描 + 全库补丁操作抽取 + 逐个候选真求值。
    ///
    /// 为什么不做成常驻用例：全库扫描实测 ~60 s（与 Task 3 的 Step 4 用例同量级），
    /// 而它量的是**会随语料漂移**的数字（另一个写入者同时在改 Mods\ThreatFleet）。
    /// 所以入口放在 <c>rimdev-selftest --task4-measure</c>，按需跑、把数字打印出来。
    ///
    /// 口径（写清楚，因为「61 条 / 11 个 mod」是历史记录、不是本口径）：
    ///   ① fallbackHit = 某个 def 的 <c>ParentName=P</c>，其中 P **不在** Name 空间、
    ///      但**在** defName 空间（= 第 2 期 ParentChainCheck 靠 defName 兜底命中的那一批）；
    ///   ② nameAddOp = 全库补丁里所有 <c>PatchOperationAttributeAdd</c> + <c>attribute=Name</c>；
    ///   ③ converted = fallbackHit 且其 P 被某个 nameAddOp 的 <c>&lt;value&gt;</c> 命中，
    ///      并且该操作的 XPath 在「含 defName=P 的 def」的小 DOM 上**真的选中了节点**（真求值确认，不是文本推测）。
    /// </summary>
    public static void RunMeasurement()
    {
        var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
        var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
        var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;

        Console.WriteLine("=== Step 6 测量（全库口径） ===");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var files = DefScanner.CollectFiles(cat.All.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)));
        var scan = DefScanner.Scan(files);
        Console.WriteLine($"全库 def 扫描：files={files.Count} defs={scan.Defs.Count} failed={scan.Files.Count(f => f.ParseError is not null)} "
            + $"elapsed={sw.ElapsedMilliseconds} ms");
        sw.Restart();

        var defFiles = scan.Files.Where(f => f.ParseError is null && f.RootKind is null)
            .Select(f => (f.FileAbs, f.ModPackageId)).ToList();
        var patchFiles = scan.Files.Where(f => f.RootKind == "Patch")
            .Select(f => (f.FileAbs, f.ModPackageId)).ToList();
        Console.WriteLine($"全库 Defs 文件={defFiles.Count} / Patches 文件={patchFiles.Count}");

        var nameAdds = PatchLayer.CollectNameAttributeAdds(patchFiles);
        Console.WriteLine($"补丁里的 AttributeAdd(attribute=Name) 操作：{nameAdds.Count} 条，"
            + $"涉及 {nameAdds.Select(a => a.ModPackageId).Distinct(StringComparer.OrdinalIgnoreCase).Count()} 个 mod；"
            + $"抽取耗时={sw.ElapsedMilliseconds} ms");
        sw.Restart();

        // 口径 ①：ParentName 落在 defName 空间、不在 Name 空间
        var nameSpace = new HashSet<string>(scan.Defs.Where(d => !string.IsNullOrEmpty(d.NameAttr)).Select(d => d.NameAttr!),
            StringComparer.Ordinal);
        var byDefName = scan.Defs.Where(d => !string.IsNullOrEmpty(d.DefName))
            .GroupBy(d => d.DefName!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var fallback = new List<(RawDef Def, string Parent)>();
        foreach (var d in scan.Defs)
        {
            if (string.IsNullOrEmpty(d.ParentName)) continue;
            var p = d.ParentName!;
            if (nameSpace.Contains(p)) continue;
            if (!byDefName.ContainsKey(p)) continue;
            fallback.Add((d, p));
        }

        var fallbackMods = fallback.Select(x => x.Def.ModPackageId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Console.WriteLine($"口径① fallbackHit（ParentName ∈ defName 空间 ∉ Name 空间）= {fallback.Count} 条，"
            + $"涉及 {fallbackMods} 个 mod");

        // 口径 ③：真求值确认
        var addsByValue = nameAdds.GroupBy(a => a.AddedName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var converted = new List<string>();
        var convertedMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (def, parent) in fallback)
        {
            if (!addsByValue.TryGetValue(parent, out var ops)) continue;
            // 小 DOM：只有「defName == parent」的那些 def（补丁的 XPath 一般就是按 defName 定位它）
            var mini = new System.Xml.XmlDocument();
            mini.AppendChild(mini.CreateElement("Defs"));
            foreach (var target in byDefName[parent])
            {
                if (target.IsAbstract) continue;
                try
                {
                    var src = new System.Xml.XmlDocument();
                    src.Load(target.FileAbs);
                    var node = src.SelectSingleNode($"Defs/*[defName=\"{parent}\"]");
                    if (node is not null)
                        mini.DocumentElement!.AppendChild(mini.ImportNode(node, true));
                }
                catch { /* 坏文件已被 RD2001 报 */ }
            }

            var hit = ops.Any(o =>
            {
                try { return mini.SelectSingleNode(o.XPath) is not null; }
                catch { return false; }
            });
            if (hit)
            {
                converted.Add($"{def.ModPackageId}: {def.DefName} --ParentName--> {parent}");
                convertedMods.Add(def.ModPackageId);
            }
        }

        Console.WriteLine($"口径③ converted（补丁补的 Name 让 ParentName 进 Name 空间）= {converted.Count} 条，"
            + $"涉及 {convertedMods.Count} 个 mod；确认耗时={sw.ElapsedMilliseconds} ms");
        foreach (var line in converted.Take(20)) Console.WriteLine("    " + line);
        if (converted.Count > 20) Console.WriteLine($"    ...（还有 {converted.Count - 20} 条）");
    }

    // ───────────────────────── 小工具 ─────────────────────────

    private static ModInfo MakeMod(string pkg, string name, string root, bool isActive)
    {
        var doc = LoadFolderResolver.Load(root);
        var resolved = LoadFolderResolver.Resolve(root, pkg, doc, TestVersion,
            LoadFolderResolver.ActiveMatcher(Array.Empty<string>()), new[] { "1.6" });
        return new ModInfo(pkg, name, root, ModSource.Local, null, new[] { "1.6" },
            resolved.DescendingOrder.ToArray(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            isActive, false, null, null, resolved);
    }

    /// <summary>在 IL 里找 <c>call/callvirt/newobj</c> 后跟指定 metadata token 的字节偏移；-1 = 没有。</summary>
    private static int TokenOffset(byte[] il, int token)
    {
        for (var i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] is not (0x28 or 0x6F or 0x73)) continue;
            if (BitConverter.ToInt32(il, i + 1) == token) return i;
        }
        return -1;
    }

    /// <summary>列出 <paramref name="host"/> 上**哪些方法**的 IL 里出现了对 <paramref name="token"/> 的调用。</summary>
    private static List<string> CallersOf(ManagedAssemblyCatalog asm, Type host, int token)
    {
        var result = new List<string>();
        foreach (var m in host.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                          | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var body = m.GetMethodBody();
            var il = body?.GetILAsByteArray();
            if (il is null) continue;
            if (TokenOffset(il, token) >= 0) result.Add(m.Name);
        }
        return result;
    }
    private static string Dump(System.Xml.XmlNode? n) => n?.OuterXml ?? "(null)";
}
