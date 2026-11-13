using RimDev.Core.Diagnostics;
using RimDev.Core.Logs;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// ★★ 第 3 期 Task 15：**日志诊断**的判别性回归。
///
/// 这是本工具第一次具备「真机视角」：前面所有命令都是**离线预测**（读 XML 猜引擎会怎样），
/// 而日志是**引擎自己说的话**。两者互补，且能互相验证：
/// <code>
/// 离线 RD2009（预测「补丁打空」） ↔ 日志 RD7031（引擎真的报 Failed to find a node）
/// 离线 RD3001（预测「引用悬空」） ↔ 日志 RD7020（引擎真的报 is not defined）
/// </code>
///
/// ── 本文件的探针分三类 ───────────────────────────────────────────────────
/// <list type="number">
/// <item><b>解析正确性</b>：多行块切分、续行归属、时间戳边界</item>
/// <item><b>两条移植纪律</b>：噪音黑名单（<c>[brrainz.harmony]</c> 不算 mod 的锅）、
///   版本号假阳性（<c>[1.5.2]</c> 不是 packageId）</item>
/// <item><b>真实日志形态</b>：<c>[Mod: 名称 (pkg)]</c> 与 <c>Source file: …</c>
///   —— 这两种是本机日志的主流形态，只认 <c>[author.mod]</c> 会产生巨量「未归因」</item>
/// </list>
///
/// ★ 关键设计：<b>不依赖真实 Player.log 的内容</b>（它会随 mod 变动而变）。
///   全部用**合成日志文本**——形态取自实测（本机 2.4 MB 日志里 62 条 <c>[FileSource]</c> 块），
///   但内容可控，不会因为用户换了 mod 就变红。
/// </summary>
public static class Phase3Task15Cases
{
    /// <summary>写一份合成日志到临时文件，返回路径。</summary>
    private static string WriteLog(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "rimdev-t15-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, "Player.log");
        File.WriteAllText(p, content);
        return p;
    }

    public static void Run()
    {
        T.Case("task15 多行块切分：续行必须归属上一条（不能丢内容）", () =>
        {
            // ★ 这是本机真实形态：时间戳行只有一句空壳，真正内容在下一行（且【无缩进】）。
            //   若续行判据只认「4 空格 + at」，这条会退化成空壳 ⇒ 规则永不命中。
            var path = WriteLog(
                "2026-11-11 23:03:52.628 ERROR [Verse] [FileSource] [Mod: Test Mod (test.mod)]\n" +
                "[Likely: XML Extensions] [Test Mod - Start of stack trace]\n" +
                "Verse.PatchOperationReplace(xpath=\"/Defs/ThingDef[defName = \"X\"]\"): Failed to find a node with the given xpath\n" +
                "[End of stack trace]\n" +
                "Source file: C:\\Games\\RimWorld\\Mods\\Test\\Patches\\P.xml\n" +
                "\n" +
                "2026-11-11 23:03:53.000 INFO [Verse] 普通信息行\n");

            var blocks = PlayerLogReader.ReadBlocks(path).ToList();
            T.True(blocks.Count >= 2, $"应切出 ≥2 块（实际 {blocks.Count}）");

            var b0 = blocks[0];
            T.Eq(b0.Level, "ERROR", "第一块应是 ERROR");
            T.Contains(b0.Text, "Test Mod", "第一块正文应含 mod 名");
            // 关键：真正内容在 StackLines 里，且必须被保留
            T.True(b0.StackLines.Any(l => l.Contains("Failed to find a node with the given xpath")),
                "★ 续行必须被保留 —— 否则规则永远匹配不到真正的错误内容");
            T.True(b0.StackLines.Any(l => l.Contains("Source file:")),
                "Source file 行也属于该块");
        }, LayoutNeed.Any);

        T.Case("task15 ★ 规则命中：补丁打空（RD7031，与离线 RD2009 对应）", () =>
        {
            var path = WriteLog(
                "2026-11-11 23:03:52.628 ERROR [Verse] [FileSource] [Mod: T (test.mod)]\n" +
                "Verse.PatchOperationReplace(xpath=\"/Defs/ThingDef[defName = \"X\"]/verbs\"): " +
                "Failed to find a node with the given xpath\n");
            var r = LogAnalyzer.Analyze(path);
            var f = r.Findings.FirstOrDefault(x => x.Code == Codes.LogPatchXpathNoMatch);
            T.NotNull(f, "必须命中 RD7031（补丁打空）");
            T.Eq(f!.Severity, Severity.Error, "补丁打空是 error 级");
        }, LayoutNeed.Any);

        T.Case("task15 ★ 噪音黑名单：框架名不该被归因成「有问题的 mod」", () =>
        {
            // [brrainz.harmony] 在日志里出现 ≠ Harmony 有问题
            var path = WriteLog(
                "2026-11-11 23:00:00.000 ERROR [Verse] [brrainz.harmony] 某个框架内部行\n" +
                "2026-11-11 23:00:01.000 ERROR [Verse] [unlimitedhugs.hugslib] 另一个框架行\n");
            var r = LogAnalyzer.Analyze(path);
            T.True(r.Findings.Count > 0, "应有诊断");
            foreach (var f in r.Findings)
            {
                T.False(f.RelatedModIds.Contains("brrainz.harmony"), "harmony 不该进相关 mod 列表");
                T.False(f.RelatedModIds.Contains("unlimitedhugs.hugslib"), "hugslib 同上");
            }
        }, LayoutNeed.Any);

        T.Case("task15 ★ 版本号假阳性：[1.5.2] 不是 packageId", () =>
        {
            var path = WriteLog(
                "2026-11-11 23:00:00.000 ERROR [Verse] [1.5.2] 版本号伪装成包名\n" +
                "2026-11-11 23:00:01.000 ERROR [Verse] [1.4] 同上\n");
            var r = LogAnalyzer.Analyze(path);
            foreach (var f in r.Findings)
                foreach (var id in f.RelatedModIds)
                    T.False(LogDiagnosisRules.VersionLookalike.IsMatch(id),
                        $"{id} 是版本号形态，不该被当成 packageId");
        }, LayoutNeed.Any);

        T.Case("task15 ★ 真实日志形态：[Mod: 名称 (pkg)] 必须能被归因", () =>
        {
            // 本机 2.4 MB 日志里主流形态就是它；只认 [author.mod] 会归因不到
            var path = WriteLog(
                "2026-11-11 23:03:52.628 ERROR [Verse] [FileSource] [Mod: 测试模组 (test.mypkg)]\n" +
                "Verse.PatchOperationAdd(xpath=\"/Defs\"): Failed to find a node with the given xpath\n");
            var r = LogAnalyzer.Analyze(path);
            var f = r.Findings.FirstOrDefault();
            T.NotNull(f, "应有诊断");
            T.True(f!.RelatedModIds.Contains("test.mypkg"),
                "★ [Mod: 名称 (pkg)] 形态必须被认出 —— 否则真实日志全是「未归因」");
        }, LayoutNeed.Any);

        T.Case("task15 ★ 真实日志形态：Mod=… (pkg) File=… 也能归因", () =>
        {
            var path = WriteLog(
                "2026-11-11 23:00:00.000 ERROR [Verse] [XmlSource] Mod=测试二 (second.pkg) " +
                "File=C:\\x\\y.xml System.Xml.XmlException: 某字段错\n");
            var r = LogAnalyzer.Analyze(path);
            var f = r.Findings.FirstOrDefault();
            T.NotNull(f, "应有诊断");
            T.True(f!.RelatedModIds.Contains("second.pkg"), "Mod=… (pkg) 形态必须能提取");
        }, LayoutNeed.Any);

        T.Case("task15 ★ 未归类固定为 info，且不参与「新增」判定", () =>
        {
            var path = WriteLog(
                "2026-11-11 23:00:00.000 ERROR [Verse] 一条完全不匹配任何规则的随机错误\n");
            var r = LogAnalyzer.Analyze(path);
            var f = r.Findings.FirstOrDefault();
            T.NotNull(f, "应有诊断");
            T.Eq(f!.Code, Codes.LogUncategorized, "未命中规则 ⇒ RD7030");
            T.Eq(f.Severity, Severity.Info,
                "★ RD7030 必须是 info —— 它不参与「比基线新增」判定；若算 error，游戏版本一变门禁就炸");
        }, LayoutNeed.Any);

        T.Case("task15 只看诊断级别：INFO 行不产生诊断", () =>
        {
            var path = WriteLog(
                "2026-11-11 23:00:00.000 INFO [Verse] 普通信息\n" +
                "2026-11-11 23:00:01.000 DEBUG [Verse] 调试信息\n");
            var r = LogAnalyzer.Analyze(path);
            T.Eq(r.Findings.Count, 0, "INFO/DEBUG 不该产生任何诊断（省 90% 时间）");
        }, LayoutNeed.Any);

        T.Case("task15 真实日志冒烟（存在则跑，不存在则显式跳过）", () =>
        {
            // 用环境探测到的真实日志做一次冒烟：不校验内容（会随 mod 变），
            // 只校验「能跑完 + 耗时合理 + 块数 > 0」。
            var env = RimDev.Core.Environment.GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var p = env.PlayerLogPath;
            if (p is null || !File.Exists(p))
            {
                T.Mark($"task15 真实日志冒烟跳过：{p} 不存在（不是缺陷，是环境条件）");
                return;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = LogAnalyzer.Analyze(p);
            sw.Stop();
            T.True(r.BlocksScanned > 0, $"应扫出日志块（实际 {r.BlocksScanned}）");
            T.True(sw.ElapsedMilliseconds < 30_000,
                $"2-3 MB 日志应在 30 秒内完成（实际 {sw.ElapsedMilliseconds} ms）");
            T.Mark($"task15 真实日志：{r.BlocksScanned} 块 / error {r.ErrorCount} warning {r.WarningCount} info {r.InfoCount} / {sw.ElapsedMilliseconds} ms");
        }, LayoutNeed.Any);
    }
}
