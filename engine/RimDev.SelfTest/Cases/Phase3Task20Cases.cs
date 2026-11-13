using RimDev.Core.Diagnostics;
using RimDev.Core.Logs;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// ★★ 第 3 期 Task 20：**补丁失败的最后几种真机形态**（日志规则覆盖度收尾）。
///
/// 演进过程（每次都是跑真实日志才发现的）：
/// <code>
/// T15 首版    11 条规则    → 未归类 117
/// T15b 加 ①   12 条规则    → 未归类  98
/// T16  加 ②③ 15 条规则    → 未归类  11
/// T20  加 ④⑤⑥⑦ 19 条规则  → 未归类   4   ← 全是环境噪音/mod 自身警告
/// </code>
///
/// 本文件钉住 T20 新增的四种形态，每一种都对应引擎失败后果分级表里的一行：
/// <list type="number">
/// <item><c>PatchOperationSequence(count=N, lastFailedOperation=…)</c>
///   —— 引擎 <c>ToString()</c> 打出内部状态，**直接指出哪条子操作失败**及它的类型与 xpath。
///   比「第 N 个失败」信息量大得多。</item>
/// <item><c>Unexpected document element in patch XML</c>
///   —— 根不是 <c>&lt;Patch&gt;</c> ⇒ **整份文件被跳过**（里面所有操作都没跑）。</item>
/// <item><c>Unexpected element in patch XML</c>
///   —— 一级子元素不是 <c>&lt;Operation&gt;</c> ⇒ 该操作被跳过。</item>
/// <item><c>Attempted to use PatchOperation directly</c>
///   —— 缺 <c>Class=</c> ⇒ 回落基类，<c>ApplyWorker</c> 固定报错返回 false ⇒ **必然失败**。</item>
/// </list>
///
/// ★ T20b 的教训：① 的正则**不能只认 <c>PatchOperationSequence</c> 这个类名**。
///   实测又遇到自定义子类同样带这个状态（<c>ModSettingsFramework.PatchOperationModOption</c>）——
///   那些子类自己也是容器，<c>ToString()</c> 沿用了基类格式。所以只认「任意类名 + count=N + lastFailedOperation=」这个**形状**。
/// </summary>
public static class Phase3Task20Cases
{
    private static string WriteLog(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "rimdev-t20-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, "Player.log");
        File.WriteAllText(p, content);
        return p;
    }

    private static string Block(string body) =>
        "2026-11-13 20:46:05.251 ERROR [Verse] [FileSource] [Mod: Test (test.pkg)]\n" + body + "\n";

    public static void Run()
    {
        T.Case("task20 ★ Sequence 详情：lastFailedOperation 必须被认出（信息量最大的一条）", () =>
        {
            var p = WriteLog(Block(
                "[Test] Patch operation Verse.PatchOperationSequence(count=19, " +
                "lastFailedOperation=Verse.PatchOperationAdd(Defs/ThingDef[defName=\"X\"]/statBases)) failed"));
            var r = LogAnalyzer.Analyze(p);
            var f = r.Findings.FirstOrDefault(x => x.RuleId == "patch_sequence_detail");
            T.NotNull(f, "★ count=N + lastFailedOperation= 形态必须命中（它直接指出哪条子操作失败）");
            T.Eq(f!.Code, Codes.LogPatchXpathNoMatch, "归 RD7031（补丁失败族）");
        }, LayoutNeed.Any);

        T.Case("task20 ★★ 自定义子类也带这个状态（不能只认 PatchOperationSequence 类名）", () =>
        {
            // 实测原文：ModSettingsFramework.PatchOperationModOption(count=3, lastFailedOperation=...)
            var p = WriteLog(Block(
                "[Test] Patch operation ModSettingsFramework.PatchOperationModOption(count=3, " +
                "lastFailedOperation=Verse.PatchOperationReplace(x)) - MovePowerToResources failed"));
            var r = LogAnalyzer.Analyze(p);
            var f = r.Findings.FirstOrDefault(x => x.RuleId == "patch_sequence_detail");
            T.NotNull(f,
                "★ 自定义容器的 ToString() 沿用基类格式 ⇒ 只认类名会漏报（实测 1 条）");
        }, LayoutNeed.Any);

        T.Case("task20 ★ 整份文件被跳过：Unexpected document element", () =>
        {
            var p = WriteLog(Block(
                "C:\\x\\P.xml: Unexpected document element in patch XML; got Defs, expected 'Patch'"));
            var r = LogAnalyzer.Analyze(p);
            var f = r.Findings.FirstOrDefault(x => x.RuleId == "patch_document_rejected");
            T.NotNull(f, "★ 后果最重：整份文件所有操作都没跑，必须能被认出");
            T.Eq(f!.Code, Codes.LogPatchXpathNoMatch, "归 RD7031");
        }, LayoutNeed.Any);

        T.Case("task20 单个 Operation 被跳过：Unexpected element", () =>
        {
            var p = WriteLog(Block(
                "Unexpected element in patch XML; got Add, expected 'Operation'"));
            var r = LogAnalyzer.Analyze(p);
            T.NotNull(r.Findings.FirstOrDefault(x => x.RuleId == "patch_operation_rejected"),
                "一级子元素不是 Operation ⇒ 该操作被跳过");
        }, LayoutNeed.Any);

        T.Case("task20 ★ 缺 Class= 回落基类：Attempted to use PatchOperation directly", () =>
        {
            var p = WriteLog(Block("Attempted to use PatchOperation directly; patch will always fail"));
            var r = LogAnalyzer.Analyze(p);
            var f = r.Findings.FirstOrDefault(x => x.RuleId == "patch_base_class_used");
            T.NotNull(f, "★ 缺 Class= ⇒ 基类 ApplyWorker 固定失败（与离线 RD2014 missingClass 对应）");
            T.Eq(f!.Code, Codes.LogPatchXpathNoMatch, "归 RD7031");
        }, LayoutNeed.Any);

        T.Case("task20 ★ 环境噪音仍留在未归类（不许硬塞进补丁族）", () =>
        {
            // Vulkan JSON 与 Workshop 目录缺失都不是 mod 的补丁问题
            foreach (var noise in new[]
            {
                "loader_get_json: Failed to open JSON file C:\\x\\CrossVulkanLayer64.json",
                "Created WorkshopItem for 3018671708 but there is no folder for it.",
            })
            {
                var p = WriteLog(Block(noise));
                var r = LogAnalyzer.Analyze(p);
                var f = r.Findings.FirstOrDefault();
                T.NotNull(f, "应有诊断");
                T.Eq(f!.Code, Codes.LogUncategorized,
                    "环境噪音不该被归到补丁失败族（否则「mod 有问题」是冤案）");
            }
        }, LayoutNeed.Any);

        T.Case("task20 规则 id 全局唯一（19 条都不许撞名）", () =>
        {
            var ids = LogDiagnosisRules.All.Select(r => r.Id).ToList();
            T.Eq(ids.Count, ids.Distinct(StringComparer.Ordinal).Count(), "规则 id 必须唯一");
            T.True(ids.Count >= 19, $"规则数应 ≥19（实际 {ids.Count}）");
        }, LayoutNeed.Any);
    }
}
