using RimDev.Core.Defs;
using RimDev.Core.Environment;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// ★★ 第 3 期 Task 14：**真实 mod 程序集上的归并端到端验证**。
///
/// Task 13 的用例证明了解析器机制正确（不含点原样、不存在原样、不传就退化）。
/// 本文件补上**最关键的一条**：它能不能在**真实的第三方 mod DLL** 上解析出正确的 Def 基类。
///
/// ── 为什么必须单独一条 ────────────────────────────────────────────────────
/// 官方程序集里没有 <c>AlienRace.ThingDef_AlienRace</c>（那是 mod 提供的类型）。
/// 只测官方 ⇒ 正好绕过了本 Task 要解决的场景。所以这里必须点名一个**真实 mod DLL**。
///
/// ── 已验证的事实（用 tools/mdtype 独立核对过，2026-11-12）────────────────
/// <code>
/// 程序集 AlienRace.dll (380,416 B)
///   AlienRace.ThingDef_AlienRace      → Verse.ThingDef      (外部)
///   AlienRace.AlienBackstoryDef       → RimWorld.BackstoryDef (外部)
/// </code>
/// ⇒ 归并目标分别是 <c>ThingDef</c> 与 <c>BackstoryDef</c>，正好对应线上两批最大的悬空
/// （<c>PawnKindDef.race</c> 151 条、<c>BackstoryDef</c> 424 条）。
///
/// ── 保守行为 ──────────────────────────────────────────────────────────────
/// 若本机没装该 mod（换机器 / 清理过工坊），用例**跳过而非失败**——
/// 它不是「代码正确性」的判据，是「本机语料够不够」的判据。
/// 用 <c>T.Mark</c> 记录跳过原因，避免「没跑」被读成「通过」。
/// </summary>
public static class Phase3Task14Cases
{
    /// <summary>提供 AlienRace 命名空间的工坊 mod（含 1.7 分支的 DLL）。</summary>
    private const string AlienRaceWorkshopId = "2640710953";

    public static void Run()
    {
        string? alienRaceDll = FindAlienRaceAssembly();

        if (alienRaceDll is null)
        {
            T.Mark($"task14 跳过：本机没有工坊 {AlienRaceWorkshopId} 的 1.7 AlienRace.dll（不是缺陷，是语料条件）");
            return;
        }

        XmlElementTypeResolver? r = null;

        T.Case("task14 准备：解析器能吃下真实 mod DLL", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var official = Path.Combine(env.GameRoot ?? "", "Assembly-CSharp.dll");
            if (!File.Exists(official))
                official = Path.Combine(env.GameRoot ?? "", "RimWorldWin64_Data", "Managed", "Assembly-CSharp.dll");

            r = XmlElementTypeResolver.Create(new[] { official, alienRaceDll });
            T.True(r.TypeDefinitionsSeen > 1000,
                $"官方 + AlienRace 的类型总数应远超 1000（实际 {r.TypeDefinitionsSeen}）");
        }, LayoutNeed.Any);

        if (r is null) return;

        T.Case("task14 ★★ AlienRace.ThingDef_AlienRace → ThingDef（线上 151 条悬空的病根）", () =>
        {
            const string full = "AlienRace.ThingDef_AlienRace";
            var norm = r.Normalize(full);

            T.Eq(norm, "ThingDef",
                $"{full} 的直接基类是 Verse.ThingDef（mdtype 独立核对过）⇒ 必须归并成 ThingDef");
            T.True(r.WasResolved(full), "应标记为已归并");

            // 关键：归并结果必须**等于**引用方字段声明的类型，否则归并没有意义。
            // PawnKindDef.race 的类型就是 ThingDef —— 相等才说明这 151 条会被正确解析。
            T.Eq(norm, "ThingDef", "与 PawnKindDef.race 的字段类型一致");
        }, LayoutNeed.Any);

        T.Case("task14 ★★ AlienRace.AlienBackstoryDef → BackstoryDef（线上 424 条悬空）", () =>
        {
            const string full = "AlienRace.AlienBackstoryDef";
            T.Eq(r.Normalize(full), "BackstoryDef",
                $"{full} 的直接基类是 RimWorld.BackstoryDef ⇒ 必须归并成 BackstoryDef");
        }, LayoutNeed.Any);

        T.Case("task14 ★ 归并目标绝不是中间抽象基类", () =>
        {
            // ThingDef_AlienRace 若被归并成 BuildableDef（ThingDef 的父类），
            // 它仍然不等于 ThingDef ⇒ 151 条悬空一条都不会减少。这条专门盯这个方向。
            var norm = r.Normalize("AlienRace.ThingDef_AlienRace");
            T.True(norm != "BuildableDef", "绝不能归并成 BuildableDef（那是 ThingDef 的父类，不是 DefType）");
            T.True(norm != "Def", "绝不能归并成根类 Def");
        }, LayoutNeed.Any);

        T.Case("task14 ★ 归并后的 DefType 能在 DefScanner 里端到端生效", () =>
        {
            // 端到端：造一份用全限定元素名的 XML，扫描后 DefType 应已是 ThingDef
            var tmp = Path.Combine(Path.GetTempPath(), "rimdev-t14-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                var f = Path.Combine(tmp, "A.xml");
                File.WriteAllText(f,
                    "<Defs><AlienRace.ThingDef_AlienRace Name=\"ZZ_Alien\">" +
                    "<defName>ZZ_Alien</defName></AlienRace.ThingDef_AlienRace></Defs>");

                var withResolver = DefScanner.Scan(new[] { (f, "m", tmp) }, resolver: r);
                var d1 = withResolver.Defs.FirstOrDefault();
                T.NotNull(d1, "传 resolver 时应扫出 def");
                T.Eq(d1!.DefType, "ThingDef",
                    "全限定元素名必须被归一化成 ThingDef（这正是 151 条悬空的修复点）");
                T.True(DefScanner.LastResolvedElementNames >= 1,
                    $"归一化计数应 ≥ 1（实际 {DefScanner.LastResolvedElementNames}）");

                // 同一份文件不传 resolver ⇒ 保持修前行为（元素名原样）
                var without = DefScanner.Scan(new[] { (f, "m", tmp) });
                var d2 = without.Defs.FirstOrDefault();
                T.NotNull(d2, "不传 resolver 也应扫出 def");
                T.Eq(d2!.DefType, "AlienRace.ThingDef_AlienRace",
                    "不传 resolver 时保持修前行为（元素名原样）—— 这是 A/B 对照的基线");
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }, LayoutNeed.Any);
    }

    /// <summary>按工坊 ID 找 1.7（或 1.6）分支的 AlienRace.dll；找不到返回 null。</summary>
    private static string? FindAlienRaceAssembly()
    {
        try
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var ws = env.WorkshopDir;
            if (string.IsNullOrEmpty(ws)) return null;

            var modRoot = Path.Combine(ws, AlienRaceWorkshopId);
            if (!Directory.Exists(modRoot)) return null;

            // 优先 1.7，其次 1.6，最后根 Assemblies
            foreach (var sub in new[] { "1.7", "1.6", "" })
            {
                var p = Path.Combine(modRoot, sub, "Assemblies", "AlienRace.dll");
                if (File.Exists(p)) return p;
            }
            return null;
        }
        catch { return null; }
    }
}
