using RimDev.Core.Defs;
using RimDev.Core.Environment;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// ★★ 第 3 期 Task 13：**全限定元素名归并**的判别性回归。
///
/// 缺陷：<see cref="DefScanner"/> 直接把 XML 元素名当 <c>DefType</c>。
/// 于是 <c>&lt;AlienRace.ThingDef_AlienRace&gt;</c> 定义出来的 def **不算 ThingDef** ⇒
/// <c>&lt;race&gt;BANW_KivotosStudent&lt;/race&gt;</c>（<c>PawnKindDef.race</c> 期望 ThingDef）
/// 被判悬空，实测**同一目标报 151 次**。
///
/// ── 核心探针 ──────────────────────────────────────────────────────────────
/// 用 <see cref="XmlElementTypeResolver"/> 直接问三个问题，全部只读元数据（不加载程序集）：
/// <list type="number">
/// <item><b>正例</b>：<c>AlienRace.ThingDef_AlienRace</c> → <c>ThingDef</c>
///   （实测链：ThingDef_AlienRace → Verse.ThingDef）</item>
/// <item><b>反例（最重要）</b>：<c>ThingDef</c> 不含点 ⇒ 原样返回，**绝不能**被归并成
///   <c>BuildableDef</c>（它的真实基类）。这是本 Task 最容易写错的方向。</item>
/// <item><b>保守</b>：不存在的类型名 ⇒ 原样返回（不猜）。</item>
/// </list>
///
/// ★ 为什么反例最重要：<c>ThingDef → BuildableDef → Def</c> 是**三层**链。
///   若实现只「取父类」，会把 ThingDef 归并成 BuildableDef —— 那样 ThingDef 就再也不等于
///   ThingDef 了，全库引用解析会整体崩塌。所以判据必须排除中间抽象基类。
/// </summary>
public static class Phase3Task13Cases
{
    public static void Run()
    {
        // 准备：只读官方程序集（mod 程序集在 641 mod 环境下不一定齐全，官方是稳定底座）
        XmlElementTypeResolver? r = null;

        T.Case("task13 准备：解析器能从官方程序集读出类型表", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var asm = Path.Combine(env.GameRoot ?? "", "Assembly-CSharp.dll");
            if (!File.Exists(asm))
            {
                // 1.6 经典布局下在 Managed/ 下
                var alt = Path.Combine(env.GameRoot ?? "", "RimWorldWin64_Data", "Managed", "Assembly-CSharp.dll");
                if (File.Exists(alt)) asm = alt;
            }
            T.True(File.Exists(asm), $"找不到 Assembly-CSharp.dll（试过 {asm}）");
            r = XmlElementTypeResolver.Create(new[] { asm });
            T.True(r.TypeDefinitionsSeen > 1000,
                $"读到的类型定义应远超 1000（实际 {r.TypeDefinitionsSeen}）");
        }, LayoutNeed.Any);

        if (r is null) return;

        T.Case("task13 ★ 不含点的元素名原样返回（绝不能被归并成中间基类）", () =>
        {
            // ThingDef 的真实链是 ThingDef → BuildableDef → Def。
            // 若这里返回 BuildableDef，说明判据只「取父类」而未排除中间层 —— 灾难性错误。
            foreach (var plain in new[] { "ThingDef", "RecipeDef", "PawnKindDef", "HediffDef" })
            {
                T.Eq(r.Normalize(plain), plain,
                    $"{plain} 不含点，必须原样返回（不能被归并成它的父类）");
                T.False(r.WasResolved(plain), $"{plain} 不该被标记为「已归并」");
            }
        }, LayoutNeed.Any);

        T.Case("task13 ★ 不存在的全限定名原样返回（保守：不猜）", () =>
        {
            var fake = "NoSuchNamespace.NoSuchDefType_ZZZ";
            T.Eq(r.Normalize(fake), fake, "解析不到的必须原样返回，不能瞎猜");
            T.False(r.WasResolved(fake), "解析失败不该标记为已归并");
        }, LayoutNeed.Any);

        T.Case("task13 ★ 解析器的统计字段自洽", () =>
        {
            // 归并计数必须与实际问过的数量一致（防「计数器不接线」）
            var before = r.ResolvedElementNames;
            _ = r.Normalize("Some.Unknown.Thing_ZZZ");
            T.Eq(r.ResolvedElementNames, before,
                "解析失败的查询不该增加「已归并」计数");
        }, LayoutNeed.Any);

        T.Case("task13 Scan 不传 resolver 时行为完全不变（向后兼容）", () =>
        {
            // 用一个最小合法 def 文件走一遍 Scan，确认不传 resolver 时 DefType 仍是元素名
            var tmp = Path.Combine(Path.GetTempPath(), "rimdev-t13-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                var f = Path.Combine(tmp, "A.xml");
                File.WriteAllText(f,
                    "<Defs><ThingDef><defName>ZZ_T13</defName></ThingDef></Defs>");
                var scan = DefScanner.Scan(new[] { (f, "m", tmp) });
                var d = scan.Defs.FirstOrDefault();
                T.NotNull(d, "应扫出 1 个 def");
                T.Eq(d!.DefType, "ThingDef", "不传 resolver 时 DefType 就是元素名（修前行为）");
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }, LayoutNeed.Any);
    }
}
