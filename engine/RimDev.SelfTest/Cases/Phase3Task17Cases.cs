using RimDev.Core.Checks;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// ★★ 第 3 期 Task 17：**慢规则的用户告知**（不是性能优化，是"别让用户以为挂了"）。
///
/// 背景（2026-11-13 实测，本机 629 mod / 1.7.5005）：
/// <code>
/// check --scope active 全程 857,925 ms（14.3 分钟），其中：
///   RD2009 补丁层     821,939 ms  ← 96%
///   RD3001 引用解析    29,185 ms
///   RD4001 字段名       2,781 ms
///   RD2005 父名           713 ms
///   其余 5 条规则合计      81 ms
/// </code>
/// 用户看到的是**命令十几分钟没有任何输出** ⇒ 以为挂了 ⇒ Ctrl+C。
///
/// ★ 这不是「性能优化」任务 —— 13.7 分钟是补丁层在 24,838 条操作 × 100,236 节点 DOM 上的
///   固有成本（`Docs/10` §2.1 记过同一笔账：全库 18.8 万操作约 38 分钟）。
///   本 Task 只做一件事：**提前告知**，让等待可解释。
///
/// ★ 关键设计：**只在规模足够大时告知**。
///   官方 6 包跑 RD2009 只要 ~0.7 s，对它说「请等待…」是误导
///   —— 用户会以为这个常用命令要等很久。判据用 def 数（50,000 为界）。
/// </summary>
public static class Phase3Task17Cases
{
    public static void Run()
    {
        T.Case("task17 ★ 慢规则集合只含已知会慢的（不许把全部规则都标慢）", () =>
        {
            T.True(CheckRunner.SlowRules.Count <= 3,
                $"慢规则集合应很少（实际 {CheckRunner.SlowRules.Count} 个）—— 标多了等于没标");
            T.True(CheckRunner.SlowRules.Contains("RD2009"), "RD2009（补丁层）必须在内");
            // 显式的反例：这些规则实测都在毫秒级，标成慢会让告知失去意义
            foreach (var fast in new[] { "RD2001", "RD2002", "loadFolders", "RD2025" })
                T.False(CheckRunner.SlowRules.Contains(fast),
                    $"{fast} 实测毫秒级，不该被标成慢规则");
        }, LayoutNeed.Any);

        T.Case("task17 ★ def 数门槛必须落在两个真实世界之间", () =>
        {
            // 实测两个锚点：官方 14,084 def（0.7 s） / 629 mod 100,236 def（13.7 min）
            // 门槛必须 > 官方、< 全量，否则会犯两种错之一：
            //   太低 ⇒ 官方也被预告（误导）
            //   太高 ⇒ 640 mod 也不预告（等于没做）
            T.True(CheckRunner.SlowRuleDefThreshold > 14_084,
                $"门槛 {CheckRunner.SlowRuleDefThreshold} 必须大于官方 def 数 14,084（否则官方被误报为慢）");
            T.True(CheckRunner.SlowRuleDefThreshold < 100_236,
                $"门槛 {CheckRunner.SlowRuleDefThreshold} 必须小于全量 100,236（否则 640 mod 也不预告）");
        }, LayoutNeed.Any);

        T.Case("task17 耗时阈值是 3 秒（常规操作都在毫秒级）", () =>
        {
            T.Eq(CheckRunner.SlowRuleMs, 3000,
                "3 秒：本工具常规操作都在毫秒到几百毫秒级，超 3 秒才值得提示");
        }, LayoutNeed.Any);

        T.Case("task17 ★ 官方规模上不该产生任何慢规则预告（实测）", () =>
        {
            // 端到端：用官方 6 包跑一次，确认 Defs.Count < 门槛
            var env = RimDev.Core.Environment.GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var active = RimDev.Core.Catalog.ModCatalog.ReadActiveMods(env.ModsConfigPath);
            var cat = RimDev.Core.Catalog.ModCatalog.Build(env, env.TargetVersion, active).Catalog;
            var official = cat.Active
                .Where(m => m.Source == RimDev.Core.Catalog.ModSource.Official)
                .ToList();
            var files = RimDev.Core.Defs.DefScanner.CollectFiles(
                official.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)).ToList());
            var scan = RimDev.Core.Defs.DefScanner.Scan(files);

            T.True(scan.Defs.Count < CheckRunner.SlowRuleDefThreshold,
                $"官方 def 数 {scan.Defs.Count} 应低于门槛 {CheckRunner.SlowRuleDefThreshold}"
                + "（否则常见的小规模用法会被误导性提示）");
        }, LayoutNeed.Any);
    }
}
