using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// ★★ 第 3 期 Task 12：**「跳过被当成失败」这一类假阳的判别性回归**（补丁层三态）。
///
/// 缺陷（源码审查发现，`PatchLayer.cs` 修前）：
/// <code>
/// // 自定义 Class= 被跳过时
/// return false;                                         // ← 修前
/// // 而 Sequence 遇 false 就 break
/// if (!ApplyElement(...)) { worker = false; break; }    // ← 于是后续兄弟全被中止
/// </code>
/// 引擎里那个自定义补丁类是**真执行**的，返回值取决于 mod 代码 ——
/// 我们"不执行它"不等于"它失败了"。把"跳过"当"失败"会让父 Sequence 提前中止，
/// 后续兄弟操作**一条都不求值** ⇒ Sequence 后半段成片漏报（既不报 RD2009、也不报实际效果）。
///
/// ── 核心探针（为什么能分辨修前/修后）────────────────────────────────────
/// 一个 Sequence 里放两条：自定义类 → 一条正常 Add。
/// <list type="bullet">
/// <item><b>修前</b>：第 1 条返回 false ⇒ 中止 ⇒ 第 2 条不执行 ⇒ 目标节点下**没有** ZZ_Marker。</item>
/// <item><b>修后</b>：第 1 条返回 Unknown ⇒ 继续 ⇒ 第 2 条执行 ⇒ ZZ_Marker **出现**。</item>
/// </list>
/// 同一份输入、只看一个可观测结果（统一 XML 里有没有那个元素）—— 纯粹的翻转探针。
///
/// ★ 另两条钉住的口径：
/// <list type="number">
/// <item><b>Unknown 要向上传播</b>：Sequence 含 Unknown ⇒ 它自己也是 Unknown，不是 Succeeded。</item>
/// <item><b>真失败仍要中止</b>：0 命中的 mutating 操作返回 Failed，Sequence 必须照旧 break。
///   这条松了就会从「假阳」变「假阴」—— 更贵的方向。</item>
/// </list>
/// </summary>
public static class Phase3Task12Cases
{
    private const string DefsXml =
        "<Defs><ThingDef><defName>ZZ_Exists</defName><statBases/></ThingDef></Defs>";

    public static void Run()
    {
        T.Case("task12 ★★ 自定义 Class 不再中止 Sequence（核心判别）", () =>
        {
            var r = RunPatch("""
                <Operation Class="PatchOperationSequence">
                  <operations>
                    <li Class="MyMod.CustomPatch.CompPatch"><xpath>/Defs</xpath></li>
                    <li Class="PatchOperationAdd">
                      <xpath>/Defs/ThingDef[defName="ZZ_Exists"]/statBases</xpath>
                      <value><ZZ_Marker>1</ZZ_Marker></value>
                    </li>
                  </operations>
                </Operation>
                """);

            var hit = r.Document.SelectSingleNode(
                "//ThingDef[defName=\"ZZ_Exists\"]/statBases/ZZ_Marker");
            T.NotNull(hit,
                "自定义 Class 之后的操作必须仍被执行（修前因 Sequence 中止而丢失）");

            // 第二条操作必须有 outcome 记录（修前它根本不会进循环）
            T.True(r.Outcomes.Count(o => o.Kind == PatchOpKind.Add) >= 1,
                "被跳过操作之后的那条 Add 必须有 outcome（否则「没报」会被读成「没问题」）");
        }, LayoutNeed.Any);

        T.Case("task12 ★ Unknown 向上传播：Sequence 含自定义类 ⇒ 自身也是 Unknown", () =>
        {
            var r = RunPatch("""
                <Operation Class="PatchOperationSequence">
                  <operations>
                    <li Class="MyMod.CustomPatch.CompPatch"><xpath>/Defs</xpath></li>
                  </operations>
                </Operation>
                """);

            T.True(r.Outcomes.Count > 0, "至少应有一条顶层操作的求值结果");
            // 顶层 Sequence 的 outcome 是 Outcomes[0]（顶层遍历按文档序）
            var seq = r.Outcomes.First(o => o.Kind == PatchOpKind.Sequence);
            T.Eq(seq.Kind, PatchOpKind.Sequence, "前置：应能定位到顶层 Sequence");
            // 真值不可判定 ⇒ Result 必须是 Unknown（不是 Failed，也不是 Succeeded）
            T.Eq(seq.Result, ApplyResult.Unknown,
                "顶层 Sequence 含自定义类 ⇒ 真值不可判定，Result 必须是 Unknown");
            // ★ 口径澄清：Applied 与 Result 是**两个不同的问题**。
            //   Applied = 「文档有没有被改动」（旧语义，Sequence 不中止 ⇒ true）
            //   Result  = 「真值可不可判定」（新三态，含自定义类 ⇒ Unknown）
            //   这两个值在 Unknown 场景下**必然不一致** —— 这正是为什么需要新字段。
            T.True(seq.Applied,
                "Sequence 未中止、子操作执行了 ⇒ Applied=true（文档已改）");
            T.True(r.Diagnostics.Any(d => d.Code == Codes.CustomPatchClass),
                "自定义类本身仍要报 RD2014（降级信息，不是沉默）");
        }, LayoutNeed.Any);

        T.Case("task12 ★ 真失败仍要中止 Sequence（不许从假阳变假阴）", () =>
        {
            var r = RunPatch("""
                <Operation Class="PatchOperationSequence">
                  <operations>
                    <li Class="PatchOperationAdd">
                      <xpath>/Defs/ThingDef[defName="ZZ_NotThere"]/statBases</xpath>
                      <value><ZZ_No>1</ZZ_No></value>
                    </li>
                    <li Class="PatchOperationAdd">
                      <xpath>/Defs/ThingDef[defName="ZZ_Exists"]/statBases</xpath>
                      <value><ZZ_Marker>1</ZZ_Marker></value>
                    </li>
                  </operations>
                </Operation>
                """);

            var hit = r.Document.SelectSingleNode(
                "//ThingDef[defName=\"ZZ_Exists\"]/statBases/ZZ_Marker");
            T.Null(hit, "真失败后 Sequence 必须中止 —— 第二条不该执行（否则是假阴）");
        }, LayoutNeed.Any);

        T.Case("task12 首败后「被跳过的兄弟数」可见（防「后半段没问题」的误读）", () =>
        {
            var r = RunPatch("""
                <Operation Class="PatchOperationSequence">
                  <operations>
                    <li Class="PatchOperationAdd">
                      <xpath>/Defs/ThingDef[defName="ZZ_NotThere"]/statBases</xpath>
                      <value><ZZ_No>1</ZZ_No></value>
                    </li>
                    <li Class="PatchOperationAdd"><xpath>/Defs/ThingDef[defName="ZZ_Exists"]/statBases</xpath><value><ZZ_M1>1</ZZ_M1></value></li>
                    <li Class="PatchOperationAdd"><xpath>/Defs/ThingDef[defName="ZZ_Exists"]/statBases</xpath><value><ZZ_M2>1</ZZ_M2></value></li>
                    <li Class="PatchOperationAdd"><xpath>/Defs/ThingDef[defName="ZZ_Exists"]/statBases</xpath><value><ZZ_M3>1</ZZ_M3></value></li>
                  </operations>
                </Operation>
                """);

            var noMatch = r.Diagnostics.FirstOrDefault(d => d.Code == Codes.PatchXpathNoMatch);
            T.NotNull(noMatch, "首败那条应报 RD2009");

            // ★ 口径修正：跳过数写在**产生它的那一层**的 outcome 上（顶层 Sequence），
            //   不在首败那条 Add 的 evidence 上 —— 那条 Add 自己并不知道有兄弟被跳过。
            var seq = r.Outcomes.FirstOrDefault(o => o.Kind == PatchOpKind.Sequence);
            T.NotNull(seq, "应能定位到顶层 Sequence 的 outcome");
            T.Eq(seq!.SkippedFollowups, 3,
                "首败之后还有 3 条兄弟，Sequence 的 outcome 必须报 3");
            T.Eq(seq.Result, ApplyResult.Failed, "首败中止 ⇒ Sequence 自身是 Failed（真失败，不是 Unknown）");
        }, LayoutNeed.Any);

        T.Case("task12 无自定义类时行为不变（回归：三态改造不许动正常路径）", () =>
        {
            var r = RunPatch("""
                <Operation Class="PatchOperationSequence">
                  <operations>
                    <li Class="PatchOperationAdd">
                      <xpath>/Defs/ThingDef[defName="ZZ_Exists"]/statBases</xpath>
                      <value><ZZ_Marker>1</ZZ_Marker></value>
                    </li>
                  </operations>
                </Operation>
                """);

            T.NotNull(r.Document.SelectSingleNode(
                "//ThingDef[defName=\"ZZ_Exists\"]/statBases/ZZ_Marker"), "正常 Add 必须生效");
            T.False(r.Diagnostics.Any(d => d.Code == Codes.CustomPatchClass),
                "没有自定义类时不该报 RD2014");
        }, LayoutNeed.Any);
    }

    /// <summary>把补丁 XML 落成临时文件跑完整链（与 Task 4 用例同一夹具模式）。</summary>
    private static PatchRunResult RunPatch(string patchXml)
    {
        using var d = new TempDir();
        var defFile = d.Write("Defs/A.xml", DefsXml);
        var patchFile = d.Write("Patches/P.xml", "<Patch>" + patchXml + "</Patch>");
        return PatchLayer.Apply(
            new[] { (defFile, "m") },
            new[] { (patchFile, "m") },
            new HashSet<string>(StringComparer.Ordinal));
    }

    // ── 夹具（与 Phase3Task4Cases 同源）────────────────────────────────
    private sealed class TempDir : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rimdev-t12-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(_root);

        public string Write(string rel, string content)
        {
            var p = Path.Combine(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, content);
            return p;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}
