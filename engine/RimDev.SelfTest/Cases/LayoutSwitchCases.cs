using RimDev.Core.Environment;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// 第 3 期 Task 9 · **版本切换**：布局识别 + 「布局 → 产物」映射。
///
/// 这一组**完全布局无关**（只用夹具目录与纯函数）：
/// 它证明「切到 1.6 / 切到 1.7」这条逻辑本身是对的，**不依赖本机是否真的装了 1.6**。
/// 真机切换（`_rimdev\rimdev.cmd --profile 1.6|1.7`）另由 build-all.ps1 的 shim 冒烟覆盖。
/// </summary>
public static class LayoutSwitchCases
{
    public static void Run()
    {
        T.Group("task9-layout");

        T.Case("布局→产物映射：Classic16→net8.0 / Waylith17→net10.0 / Unknown→无产物", () =>
        {
            T.Eq(RimDevProducts.ForLayout(RimWorldLayout.Classic16), RimDevProducts.Net8, "Classic16 应用 net8.0（1.6 侧产物）");
            T.Eq(RimDevProducts.ForLayout(RimWorldLayout.Waylith17), RimDevProducts.Net10, "Waylith17 应用 net10.0（1.7.x 线产物）");
            T.Null(RimDevProducts.ForLayout(RimWorldLayout.Unknown), "Unknown 必须没有可用产物（调用方据此退出 4，不许猜）");

            // 证伪型：产物路径必须跟着布局走，不能两条都指向同一个 exe
            var p8 = RimDevProducts.CliRelativePath(RimWorldLayout.Classic16);
            var p10 = RimDevProducts.CliRelativePath(RimWorldLayout.Waylith17);
            T.NotNull(p8, "Classic16 的 rimdev.exe 路径");
            T.NotNull(p10, "Waylith17 的 rimdev.exe 路径");
            T.True(p8 != p10, $"两种布局必须指向不同产物（实际都是 [{p8}]）");
            T.Contains(p8!, "net8.0", "Classic16 产物在 net8.0 目录里");
            T.Contains(p10!, "net10.0", "Waylith17 产物在 net10.0 目录里");
            T.Null(RimDevProducts.CliRelativePath(RimWorldLayout.Unknown), "Unknown 不给产物路径");
        });

        T.Case("布局识别只看 Assembly-CSharp 落在哪：Managed 下→Classic16 / 根下→Waylith17 / 都无→Unknown", () =>
        {
            WithTempRoots((mkRoot, root) =>
            {
                // ① 1.6 形状
                var classic = mkRoot();
                Directory.CreateDirectory(Path.Combine(classic, "RimWorldWin64_Data", "Managed"));
                File.WriteAllText(Path.Combine(classic, "RimWorldWin64_Data", "Managed", "Assembly-CSharp.dll"), "");
                T.Eq(GameEnvProbe.DetectLayout(classic), RimWorldLayout.Classic16, "Managed 下有 Assembly-CSharp ⇒ Classic16");
                T.Eq(RimDevProducts.ForLayout(GameEnvProbe.DetectLayout(classic)), RimDevProducts.Net8, "⇒ 用 net8.0 产物");

                // ② 1.7 形状
                var waylith = mkRoot();
                File.WriteAllText(Path.Combine(waylith, "Assembly-CSharp.dll"), "");
                T.Eq(GameEnvProbe.DetectLayout(waylith), RimWorldLayout.Waylith17, "根下有 Assembly-CSharp ⇒ Waylith17");
                T.Eq(RimDevProducts.ForLayout(GameEnvProbe.DetectLayout(waylith)), RimDevProducts.Net10, "⇒ 用 net10.0 产物");

                // ③ 两者都无 ⇒ Unknown（**不许**退化成「把根当 Managed 用」）
                var empty = mkRoot();
                T.Eq(GameEnvProbe.DetectLayout(empty), RimWorldLayout.Unknown, "两处都没有 ⇒ Unknown");
            });
        });

        T.Case("ManagedDir 跟随布局：Classic16→<root>\\RimWorldWin64_Data\\Managed；Waylith17→<root>", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "rimdev-managed-dir");
            T.Eq(Path.GetFullPath(GameEnvProbe.ManagedDirFor(root, RimWorldLayout.Waylith17)),
                 Path.GetFullPath(root), "1.7.x 线：ManagedDir == 游戏根（程序集就在根目录）");
            T.Contains(GameEnvProbe.ManagedDirFor(root, RimWorldLayout.Classic16),
                Path.Combine("RimWorldWin64_Data", "Managed"), "1.6：ManagedDir 是 RimWorldWin64_Data\\Managed");
        });

        T.Case("★ 证伪型：exe 文件名不参与布局判据（1.7 两份副本的 exe 集合就不一样）", () =>
        {
            WithTempRoots((mkRoot, root) =>
            {
                // 只有 RimWorldWin64.exe、没有 Assembly-CSharp ⇒ 仍必须是 Unknown，
                // 否则「有 RimWorldWin64.exe 就当成 1.6」会让 1.7 的桌面副本被误判成 Classic16。
                var exeOnly = mkRoot();
                File.WriteAllText(Path.Combine(exeOnly, "RimWorldWin64.exe"), "");
                T.Eq(GameEnvProbe.DetectLayout(exeOnly), RimWorldLayout.Unknown,
                    "只有 RimWorldWin64.exe 时不能判成 Classic16");

                // 反过来：有 RimWorld.exe + 根 Assembly-CSharp ⇒ Waylith17（不管有没有 RimWorldWin64.exe）
                var bothExes = mkRoot();
                File.WriteAllText(Path.Combine(bothExes, "RimWorldWin64.exe"), "");
                File.WriteAllText(Path.Combine(bothExes, "RimWorld.exe"), "");
                File.WriteAllText(Path.Combine(bothExes, "Assembly-CSharp.dll"), "");
                T.Eq(GameEnvProbe.DetectLayout(bothExes), RimWorldLayout.Waylith17,
                    "两个 exe 都在、程序集在根 ⇒ 仍是 Waylith17（exe 名不作判据）");
            });
        });

        T.Case("布局识别优先 1.6：两处都有 Assembly-CSharp 时必须先认 Classic16（不静默走 1.7）", () =>
        {
            WithTempRoots((mkRoot, root) =>
            {
                var both = mkRoot();
                Directory.CreateDirectory(Path.Combine(both, "RimWorldWin64_Data", "Managed"));
                File.WriteAllText(Path.Combine(both, "RimWorldWin64_Data", "Managed", "Assembly-CSharp.dll"), "");
                File.WriteAllText(Path.Combine(both, "Assembly-CSharp.dll"), "");
                T.Eq(GameEnvProbe.DetectLayout(both), RimWorldLayout.Classic16,
                    "两处都有 ⇒ Classic16（Managed 里的那份才是 1.6 引擎）");
            });
        });

        T.Case("FromRoot：Unknown 布局必须抛 RD1001 而不是给出一个能跑的 env", () =>
        {
            WithTempRoots((mkRoot, root) =>
            {
                var empty = mkRoot();
                var threw = false;
                try { GameEnvProbe.FromRoot(empty); }
                catch (GameEnvProbeException ex) { threw = true; T.Contains(ex.Message, "RD1001", "报错必须带 RD1001"); }
                T.True(threw, "两处都没有 Assembly-CSharp 时必须抛 GameEnvProbeException（不许退化）");
            });
        });
    }

    /// <summary>在临时工作区里造夹具根目录；回调结束时清理。</summary>
    private static void WithTempRoots(Action<Func<string>, string> body)
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "rimdev-task9-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(baseDir);
        var seq = 0;
        Func<string> mkRoot = () =>
        {
            var p = Path.Combine(baseDir, "root" + (++seq));
            Directory.CreateDirectory(p);
            return p;
        };
        try { body(mkRoot, baseDir); }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { /* 清理失败不影响断言 */ }
        }
    }
}
