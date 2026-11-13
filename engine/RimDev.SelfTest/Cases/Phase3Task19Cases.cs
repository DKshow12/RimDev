using RimDev.Core.Checks;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// ★★ 第 3 期 Task 19：**按规则子集运行**（`--rules` / `--skip-rules`）。
///
/// 动机（实测，本机 629 mod / 1.7.5005）：
/// <code>
/// check --scope active 全量 = 857,925 ms（14.3 分钟）
///   RD2009 补丁层       821,939 ms  ← 96%
///   RD3001 引用解析      29,185 ms
///   其余 7 条             6,801 ms
/// </code>
/// 想「只看引用问题」却必须陪跑补丁层 13.7 分钟 —— 这是可用性问题，不是性能问题。
///
/// ★ 关键设计：<b>必须在执行前剔除</b>，不能用 <c>Run(ctx, filter)</c>。
///   那个 filter 是**结果过滤** —— 诊断全部算完才剔除，耗时一分不少。
///   实测对比（同机同语料）：
/// <code>
/// 全量 9 条规则   857,925 ms / 1,678 MB
/// 只跑 RD3001      28,389 ms /   555 MB   ← 快 30 倍，内存 1/3
/// </code>
///
/// ⚠️ 与之配套的诚实要求：<b>规则子集必须可见</b>。
///   否则「只跑了 2 条规则」的输出会被读成「其他 7 条没问题」—— 那是**假 0**，
///   正是本项目最忌的一类错误。
/// </summary>
public static class Phase3Task19Cases
{
    public static void Run()
    {
        T.Case("task19 ★ 两个集合都空 ⇒ 全跑（保持原行为，不许悄悄少跑）", () =>
        {
            var r = CheckRunner.Default();
            var before = r.Checks.Count;
            var kept = r.SelectRules(null, null);
            T.Eq(kept.Count, before, "不传过滤时必须一条不少");
            T.Eq(r.Checks.Count, before, "内部清单也不许被裁");
        }, LayoutNeed.Any);

        T.Case("task19 ★ --rules 只保留指定规则（真的不跑其他）", () =>
        {
            var r = CheckRunner.Default();
            var kept = r.SelectRules(new[] { "RD3001" }, null);
            T.Eq(kept.Count, 1, "只应保留 1 条");
            T.Eq(kept[0], "RD3001", "留下的必须是指定那条");
            T.Eq(r.Checks.Count, 1,
                "★ 内部清单也必须被裁 —— 只是结果过滤的话耗时降不下来");
        }, LayoutNeed.Any);

        T.Case("task19 --skip-rules 剔除指定规则（慢规则可跳过）", () =>
        {
            var all = CheckRunner.Default().Checks.Select(c => c.Id).ToList();
            var r = CheckRunner.Default();
            var kept = r.SelectRules(null, new[] { "RD2009" });
            T.Eq(kept.Count, all.Count - 1, "只少 1 条");
            T.False(kept.Contains("RD2009"), "RD2009 必须被剔除");
            T.True(kept.Contains("RD3001"), "其他规则必须保留");
        }, LayoutNeed.Any);

        T.Case("task19 ★ 两者同时给 ⇒ 先取交集再剔除", () =>
        {
            var r = CheckRunner.Default();
            // 只要 RD2009 与 RD3001，但跳过 RD2009 ⇒ 只剩 RD3001
            var kept = r.SelectRules(new[] { "RD2009", "RD3001" }, new[] { "RD2009" });
            T.Eq(kept.Count, 1, "交集 2 条，剔除 1 条 ⇒ 剩 1 条");
            T.Eq(kept[0], "RD3001", "应剩下 RD3001");
        }, LayoutNeed.Any);

        T.Case("task19 大小写不敏感（用户记不住 RD2009 / rd2009）", () =>
        {
            var r = CheckRunner.Default();
            var kept = r.SelectRules(new[] { "rd3001" }, null);
            T.Eq(kept.Count, 1, "小写也必须匹配");
            T.Eq(kept[0], "RD3001", "返回的是规范 id，不是用户输入原文");
        }, LayoutNeed.Any);

        T.Case("task19 不存在的 id 被忽略（不抛异常、不报假错）", () =>
        {
            var r = CheckRunner.Default();
            var kept = r.SelectRules(new[] { "RD3001", "NO_SUCH_RULE" }, null);
            T.Eq(kept.Count, 1, "只保留真实存在的那条");
            T.Eq(kept[0], "RD3001", "无关 id 静默忽略");
        }, LayoutNeed.Any);

        T.Case("task19 ★ 慢规则确实可以被跳过（否则这个功能没意义）", () =>
        {
            // 这条守的是「SlowRules 与 SelectRules 能配合」——
            // 若 RD2009 因为某种原因无法被剔除，那 --skip-rules 就是空头支票。
            var r = CheckRunner.Default();
            var kept = r.SelectRules(null, CheckRunner.SlowRules);
            T.False(kept.Contains("RD2009"), "RD2009（最慢的一条）必须能被跳过");
            T.True(kept.Count > 0, "跳过慢规则后仍应有其他规则可跑");
        }, LayoutNeed.Any);

        T.Case("task19 规则顺序保持注册序（输出稳定，便于 diff）", () =>
        {
            var all = CheckRunner.Default().Checks.Select(c => c.Id).ToList();
            var r = CheckRunner.Default();
            var kept = r.SelectRules(null, new[] { "RD3001" });
            // 留下的相对顺序应与全量里的一致
            var expected = all.Where(x => x != "RD3001").ToList();
            T.Eq(string.Join(",", kept), string.Join(",", expected),
                "剔除后顺序必须与注册序一致");
        }, LayoutNeed.Any);
    }
}
