using RimDev.Core.Diagnostics;
using RimDev.Core.Logs;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// ★★ 第 3 期 Task 16：**补丁失败的五种真机形态**（日志规则覆盖度）。
///
/// 背景：Task 15 上线后跑真实日志，发现 **98 条落在「未归类」**。逐条追查发现它们不是噪音，
/// 而是**同一件事的五种不同说法** —— 引擎在不同阶段、对不同操作类型，报错文案不一样：
///
/// <code>
/// ① Failed to find a node with the given xpath
///    阶段 ApplyPatches，xpath 打空。                   实测 19 条
/// ② Patch operation Verse.PatchOperationAdd(&lt;xpath&gt;) failed
///    阶段 ClearCachedPatches，同上事件。                实测 85 条（占大头！）
/// ③ Patch operation CombatExtended.PatchOperationMakeGunCECompatible failed
///    ② 的变体 —— **不带括号**（操作类没有 xpath 属性）。 实测 1 条
/// ④ PatchOperationSequence: Error in the operation at position=N
///    嵌套 Sequence 里第 N 个子操作失败。                 实测 1 条
/// ⑤ Tried to set a value (1) to a field which does not exist: &lt;字段&gt; on &lt;类型&gt;
///    字段名错（对应离线 RD4001）。                       实测 1 条
/// </code>
///
/// ★ 为什么必须分开测：② 的正则若写成 `Patch operation \S+\(.*\) failed` 就会漏掉 ③（无括号）；
///   若假设 xpath 单行，还会漏掉 xpath 含换行的那种。**每一个子形态都是一次真实漏报。**
///
/// ★ 本文件的用例全部用**合成日志文本**（形态取自本机实测），不依赖真实 Player.log 内容 ——
///   它随 mod 变动而变，拿它做断言会变成"语料金丝雀"，不是"代码正确性"判据。
/// </summary>
public static class Phase3Task16Cases
{
    private static string WriteLog(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "rimdev-t16-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, "Player.log");
        File.WriteAllText(p, content);
        return p;
    }

    /// <summary>包一条日志头 + 正文，模拟真实块。</summary>
    private static string Block(string body) =>
        "2026-11-11 23:05:27.515 ERROR [Verse] [FileSource] [Mod: Test Mod (test.pkg)]\n" +
        body + "\n";

    public static void Run()
    {
        T.Case("task16 ① Failed to find a node with the given xpath", () =>
        {
            var p = WriteLog(Block(
                "Verse.PatchOperationReplace(xpath=\"/Defs/ThingDef[defName=\\\"X\\\"]\"): " +
                "Failed to find a node with the given xpath"));
            var r = LogAnalyzer.Analyze(p);
            T.NotNull(r.Findings.FirstOrDefault(f => f.Code == Codes.LogPatchXpathNoMatch),
                "必须命中 RD7031");
        }, LayoutNeed.Any);

        T.Case("task16 ② ★ Patch operation …(<xpath>) failed（实测 85 条，占大头）", () =>
        {
            var p = WriteLog(Block(
                "[Test Mod] Patch operation Verse.PatchOperationAdd(" +
                "Defs/ThingDef[defName=\"X\"]/statBases) failed"));
            var r = LogAnalyzer.Analyze(p);
            var f = r.Findings.FirstOrDefault();
            T.NotNull(f, "必须有诊断");
            T.Eq(f!.Code, Codes.LogPatchXpathNoMatch, "② 也必须归到 RD7031（同一事件）");
            T.Eq(f.RuleId, "patch_operation_failed", "规则 id 应可区分阶段");
        }, LayoutNeed.Any);

        T.Case("task16 ③ ★★ 不带括号的变体（正则写成 \\(.*\\) 就会漏掉它）", () =>
        {
            // 真实原文：Patch operation CombatExtended.PatchOperationMakeGunCECompatible failed
            // 这个操作类没有 xpath 属性 ⇒ 引擎不打括号。写成必含括号的正则 ⇒ 漏报。
            var p = WriteLog(Block(
                "[Test Mod] Patch operation CombatExtended.PatchOperationMakeGunCECompatible failed"));
            var r = LogAnalyzer.Analyze(p);
            var f = r.Findings.FirstOrDefault();
            T.NotNull(f, "不带括号的补丁失败也必须被认出");
            T.Eq(f!.Code, Codes.LogPatchXpathNoMatch, "同上，归 RD7031");
        }, LayoutNeed.Any);

        T.Case("task16 ③b xpath 含换行（不能假设单行）", () =>
        {
            // 真实原文：xpath 里 `or` 连接多个 defName 时引擎会换行
            var p = WriteLog(Block(
                "[Test Mod] Patch operation Verse.PatchOperationAdd(Defs/ThingDef[defName=\"A\" or \n" +
                "\t\tdefName=\"B\"]/statBases) failed"));
            var r = LogAnalyzer.Analyze(p);
            T.NotNull(r.Findings.FirstOrDefault(f => f.Code == Codes.LogPatchXpathNoMatch),
                "xpath 跨行也必须命中");
        }, LayoutNeed.Any);

        T.Case("task16 ④ PatchOperationSequence: Error in the operation at position=N", () =>
        {
            var p = WriteLog(Block(
                "Verse.PatchOperationSequence: Error in the operation at position=2"));
            var r = LogAnalyzer.Analyze(p);
            var f = r.Findings.FirstOrDefault();
            T.NotNull(f, "Sequence 子操作失败必须被认出");
            T.Eq(f!.RuleId, "patch_sequence_error",
                "★ 这条对应离线 Task 12 的「Sequence 首败中止」—— 真机侧实证");
        }, LayoutNeed.Any);

        T.Case("task16 ⑤ Tried to set a value … to a field which does not exist", () =>
        {
            var p = WriteLog(Block(
                "Tried to set a value (1) to a field which does not exist: " +
                "MechFussionCore on ThingDefCountClass"));
            var r = LogAnalyzer.Analyze(p);
            var f = r.Findings.FirstOrDefault();
            T.NotNull(f, "字段名错必须被认出");
            T.Eq(f!.Code, Codes.LogXmlUnknownField,
                "★ 归 RD7021（与离线 RD4001 字段不存在对应）");
            T.Eq(f.RuleId, "field_not_exist", "规则 id 应可区分具体形态");
        }, LayoutNeed.Any);

        T.Case("task16 ★ 普通 mod 自身警告仍留在未归类（不许硬塞进补丁类）", () =>
        {
            // 这条是 mod 自己打的警告（Nivarian / Wolfein 那类），不是补丁失败。
            // 硬塞进 RD7031 会把「mod 作者自己的提醒」误报成「补丁打空」。
            var p = WriteLog(Block(
                "[Verse] [Likely: Some Race] Harmony compatibility warning!"));
            var r = LogAnalyzer.Analyze(p);
            var f = r.Findings.FirstOrDefault();
            T.NotNull(f, "应有诊断");
            T.Eq(f!.Code, Codes.LogUncategorized,
                "普通警告不该被归到补丁失败类");
        }, LayoutNeed.Any);

        T.Case("task16 规则唯一性：没有两条规则用同一个 id", () =>
        {
            var ids = LogDiagnosisRules.All.Select(r => r.Id).ToList();
            T.Eq(ids.Count, ids.Distinct(StringComparer.Ordinal).Count(),
                "规则 id 必须唯一（否则 byRule 计数会串）");
            var codes = LogDiagnosisRules.All.Select(r => r.Code).Distinct().ToList();
            T.True(codes.Count >= 3, "至少覆盖 3 种诊断码");
        }, LayoutNeed.Any);
    }
}
