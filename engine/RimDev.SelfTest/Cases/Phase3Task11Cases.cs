using RimDev.Core.Catalog;
using RimDev.Core.Checks;
using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// ★★ 第 3 期 Task 11：**「引用方 mod 未启用」这一类假阳的判别性回归**。
///
/// 缺陷（主理人体检实测，`--scope all` 全库 3971 条 RD3001）：
/// <code>
/// 官方内容                1 条   ← 真阳性（Ambient_MetalHell）
/// 引用方未启用           3256 条  ← 假阳：这些 mod 根本没跑，悬空不影响当前游戏
/// 引用方已启用            714 条  ← 真缺陷（mod 装了但前置没装）
/// </code>
/// 修前这 3971 条**全部**是 <c>Severity.Error</c>，于是真缺陷（715 条）被 3256 条噪音淹没 ——
/// 用户看到的 exit=2 与「满屏红字」不能区分「我的 mod 装坏了」和「我订阅了但没启用的 mod 装坏了」。
///
/// ── 本文件的核心探针（为什么它能分辨修前/修后）────────────────────────────────
/// **同一份语料、同一个扫描结果，只切换 <c>ActivePackageIds</c>**：
/// <list type="number">
/// <item>启用集 = 官方全集 ⇒ 官方那条 RD3001 必须仍是 <c>Error</c>（**不许误降**）；</item>
/// <item>启用集 = 去掉那条悬空所属的包 ⇒ 同一条 RD3001 必须变成 <c>Info</c>，
///   且 evidence 里 <c>reporterEnabled=false</c>；</item>
/// <item>启用集 = null ⇒ 退化为「全部视为已启用」，与修前行为一致（向后兼容）。</item>
/// </list>
/// 修前：三种情形**全部**给 <c>Error</c> ⇒ 用例 2 必红。修后：三种情形各得其所。
/// 这是一个「只改一个输入、其余全不变」的翻转探针，不依赖任何合成语料，也不会被语料漂移拖红。
///
/// ★ 大小写口径（本 Task 最容易被写错的地方，单独钉一条）：
///   <c>ActivePackageIds</c> 由 <c>NormalizeActiveModIds</c> 产出，**全小写**；
///   而 <c>RawDef.ModPackageId</c> 保留 About.xml 里的**原始大小写**
///   （官方就是 <c>Ludeon.RimWorld.Anomaly</c> 这种）。
///   若实现里漏了 <c>ToLowerInvariant()</c>，官方会被判成「未启用」而把唯一真阳性降级
///   —— 实测那样会错降 671 条。所以下面专门有一条**大小写混写**的用例。
/// </summary>
public static class Phase3Task11Cases
{
    /// <summary>官方那条真悬空引用的目标名，与 `08` 里的钉子同源。</summary>
    private const string OfficialDanglingTarget = "Ambient_MetalHell";

    public static void Run()
    {
        // ── 准备：官方语料的索引与 def（**只跑一次**，下面几条用例复用）──
        ModCatalog? cat = null;
        DefIndex? idx = null;
        IReadOnlyList<RawDef>? defs = null;
        IReadOnlyList<DefFileResult>? files = null;

        T.Case("task11 准备：官方语料扫描可用", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
            cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;

            var official = cat.Active.Where(m => m.Source == ModSource.Official).ToList();
            T.True(official.Count > 0, $"官方包数 {official.Count} 应 > 0");

            var collected = DefScanner.CollectFiles(
                official.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)).ToList());
            var scan = DefScanner.Scan(collected);
            defs = scan.Defs;
            files = scan.Files;
            idx = DefIndex.Build(defs);
            T.True(defs.Count > 0, $"官方 def 数 {defs.Count} 应 > 0");
            T.True(files.Count > 0, $"官方文件数 {files.Count} 应 > 0");
        }, LayoutNeed.Any);

        if (cat is null || idx is null || defs is null || files is null) return;

        // 官方那条真悬空**所属的包**（Anomaly），下面用它做「剔除」探针。
        // 判据用 evidence 里的目标名，不猜包名 —— 包名可能随版本变。
        string? danglingPkg = defs
            .Where(d => d.FileRelToMod.Contains("Weathers.xml", StringComparison.OrdinalIgnoreCase))
            .Select(d => d.ModPackageId)
            .FirstOrDefault();

        // 官方全集（小写，与 NormalizeActiveModIds 同口径）
        var officialAll = cat.Active
            .Where(m => m.Source == ModSource.Official)
            .Select(m => m.PackageId.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        // ── 用例 1：启用集 = 官方全集 ⇒ 官方那条仍是 Error（防误降）──
        T.Case("task11 ★ 启用集含官方 ⇒ 官方真悬空仍是 Error（不许误降）", () =>
        {
            var ctx = new CheckContext(idx, files, cat, defs, null, null, null, officialAll);
            var rc = new ReferenceCheck();
            var diags = rc.Run(ctx).Where(d => d.Code == Codes.DefNameNotFound).ToList();

            var officialOne = diags
                .Where(d => (d.Evidence?.GetValueOrDefault("targetDefName") ?? "") == OfficialDanglingTarget)
                .ToList();
            T.True(officialOne.Count > 0, "官方 Ambient_MetalHell 那条 RD3001 应仍在报告里");
            foreach (var d in officialOne)
            {
                T.Eq(d.Severity, Severity.Error,
                    $"{OfficialDanglingTarget} 在官方启用时必须仍是 Error（漏了 ToLower 会把它降成 Info）");
                T.Eq(d.Evidence?.GetValueOrDefault("reporterEnabled"), "true", "reporterEnabled 应为 true");
            }
            T.Eq(rc.LastStats!.NotEnabledReporter, 0, "官方全集下不应有任何降级");
        }, LayoutNeed.Any);

        // ── 用例 2：★ 核心翻转 —— 把那条悬空的所属包从启用集里剔掉 ⇒ 必须降为 Info ──
        T.Case("task11 ★★ 引用方未启用 ⇒ 同一条 RD3001 翻转为 Info（核心判别）", () =>
        {
            if (string.IsNullOrEmpty(danglingPkg))
            {
                T.True(false, "没能定位到 Ambient_MetalHell 所属包 —— 语料变了，请复核本用例的前提");
                return;
            }

            var narrowed = new HashSet<string>(officialAll, StringComparer.Ordinal);
            narrowed.Remove(danglingPkg.ToLowerInvariant());

            var ctx2 = new CheckContext(idx, files, cat, defs, null, null, null, narrowed);
            var rc2 = new ReferenceCheck();
            var diags2 = rc2.Run(ctx2).Where(d => d.Code == Codes.DefNameNotFound).ToList();

            var one2 = diags2
                .Where(d => (d.Evidence?.GetValueOrDefault("targetDefName") ?? "") == OfficialDanglingTarget)
                .ToList();
            T.True(one2.Count > 0, "该条 RD3001 仍然必须**存在**（降级不是不报）");
            foreach (var d in one2)
            {
                T.Eq(d.Severity, Severity.Info,
                    $"引用方 {danglingPkg} 不在启用集 ⇒ 必须降为 Info");
                T.Eq(d.Evidence?.GetValueOrDefault("reporterEnabled"), "false",
                    "evidence 里必须写明判据 reporterEnabled=false");
                T.Contains(d.Message, "[未启用 mod]", "Message 要带前缀，让读者一眼看出这是未启用 mod");
            }
            T.True(rc2.LastStats!.NotEnabledReporter > 0,
                $"降级计数应 > 0（实际 {rc2.LastStats!.NotEnabledReporter}）");
        }, LayoutNeed.Any);

        // ── 用例 3：activeIds = null ⇒ 向后兼容（全部视为已启用）──
        T.Case("task11 未提供 ActivePackageIds ⇒ 退化为全部已启用（向后兼容）", () =>
        {
            var ctx3 = new CheckContext(idx, files, cat, defs, null);   // 只给 5 个位置参数
            var rc3 = new ReferenceCheck();
            var diags3 = rc3.Run(ctx3).Where(d => d.Code == Codes.DefNameNotFound).ToList();

            T.True(diags3.Count > 0, "旧调用方式仍应产出 RD3001");
            T.True(diags3.All(d => d.Severity == Severity.Error),
                "未给启用集时不应降级任何一条（保持修前行为）");
            T.Eq(rc3.LastStats!.NotEnabledReporter, 0, "未给启用集时降级计数必须为 0");
        }, LayoutNeed.Any);

        // ── 用例 4：大小写混写 —— 等价于用例 1，防「漏了 ToLower」──
        T.Case("task11 ★ 启用集用【原始大小写】给 ⇒ 结果必须与全小写一致（大小写不敏感）", () =>
        {
            // 故意不做 ToLowerInvariant：模拟「调用方给的是 About.xml 里的原样 id」。
            // 归一发生在 InheritanceLayer.NormalizeActiveModIds，所以结果应与用例 1 相同。
            var mixedCase = cat!.Active
                .Where(m => m.Source == ModSource.Official)
                .Select(m => m.PackageId)          // 原样大小写
                .ToHashSet(StringComparer.Ordinal);

            var ctx4 = new CheckContext(idx, files, cat, defs, null, null, null, mixedCase);
            var rc4 = new ReferenceCheck();
            var diags4 = rc4.Run(ctx4).Where(d => d.Code == Codes.DefNameNotFound).ToList();

            var one4 = diags4
                .Where(d => (d.Evidence?.GetValueOrDefault("targetDefName") ?? "") == OfficialDanglingTarget)
                .ToList();
            T.True(one4.Count > 0, "该条 RD3001 应仍在");
            foreach (var d in one4)
                T.Eq(d.Severity, Severity.Error,
                    "原始大小写的启用集，归一后必须命中 ⇒ 仍为 Error（不是 Info）");
            T.Eq(rc4.LastStats!.NotEnabledReporter, 0, "原始大小写输入下也不应产生降级");
        }, LayoutNeed.Any);

        // ── 用例 5：计数自洽 —— 降级数 + 剩余 error 数 == 总 RD3001 数 ──
        T.Case("task11 计数自洽：降级数 + 仍报 error 数 == RD3001 总数", () =>
        {
            if (string.IsNullOrEmpty(danglingPkg)) return;
            var narrowed5 = new HashSet<string>(officialAll, StringComparer.Ordinal);
            narrowed5.Remove(danglingPkg.ToLowerInvariant());

            var ctx5 = new CheckContext(idx, files, cat, defs, null, null, null, narrowed5);
            var rc5 = new ReferenceCheck();
            var diags5 = rc5.Run(ctx5).Where(d => d.Code == Codes.DefNameNotFound).ToList();

            var asInfo = diags5.Count(d => d.Severity == Severity.Info);
            var asError = diags5.Count(d => d.Severity == Severity.Error);

            T.Eq(asInfo, rc5.LastStats!.NotEnabledReporter,
                "info 条数必须等于降级计数（否则计数器与实际行为脱节）");
            T.Eq(asInfo + asError, diags5.Count, "info + error 必须等于总数");
            T.True(diags5.All(d => d.Severity is Severity.Info or Severity.Error),
                "RD3001 只该有 Info/Error 两档，不该出现 Warning");
        }, LayoutNeed.Any);

        // ── 用例 6：Describe() 要在有降级时打印出来（否则变化不可解释）──
        T.Case("task11 Describe() 在无降级时不加噪音、有降级时必打印计数", () =>
        {
            var plain = new ReferenceStats(0, 0, 0, 0, 0, 0, 0, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            T.False(plain.Describe().Contains("未启用引用方降级"),
                "降级数为 0 时不该多打一段（默认输出保持干净）");

            var withDeg = new ReferenceStats(0, 0, 0, 0, 0, 0, 0, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3, 0);
            T.Contains(withDeg.Describe(), "未启用引用方降级 3",
                "有降级时必须打印，否则 3971→43 这种变化无法解释");
        }, LayoutNeed.Any);
    }
}
