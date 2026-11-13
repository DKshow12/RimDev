using RimDev.Core.Diagnostics;

namespace RimDev.Core.Checks;

/// <summary>
/// ★ 更正（2026-10-14 实测）：`defName` 的唯一性是【按 DefType】的，**不是全局的**。
///   active 作用域实测：跨类型同名 **1202** 组（全部合法 —— 原版 Core 一个包就有 5 个不同
///   类型的 `Misc`：HediffDef / DesignationCategoryDef / StyleItemCategoryDef /
///   IncidentCategoryDef / PawnRenderTreeDef），而【同类型内】真重名只有 **1** 组。
///   归档的真实游戏日志里 `Duplicate defName` 命中为 **0** —— 游戏自己也不认为这些是重名。
///   按「全局唯一」实现会一次报出 1202 条假错误（`--scope all` 时 27722 条），
///   正是本项目最忌讳的「误报比不报更糟」。
///
/// ★ 抽象声明（`Abstract="True"`）已在 `DefIndex.Build` 处排除出 `ByTypeAndDefName` —— 它们不会被引擎
///   建成 def，其上的 `<defName>` 只是装饰（原版 PawnKinds_Mercenary.xml:198 的抽象模板也写了 defName）。
///   所以本检查**不要再加一层 `IsAbstract` 过滤**：唯一入口只有一个，加第二层只会让两处口径未来漂移。
///
/// 真重名（同类型同名）的后果：**同名会合并/遮蔽，被遮蔽的那份内容可能静默丢失** ——
/// 这是「改了却没用」最常见的原因之一。
/// </summary>
public sealed class DuplicateDefNameCheck : ICheck
{
    public string Id => Codes.DefNameDuplicate;

    public IEnumerable<Diagnostic> Run(CheckContext ctx)
    {
        // ★ 按 (DefType, defName) 判重 —— defName 唯一性是【按 DefType】的，用 ByDefName 会
        //   一次报出 1202 条假错误（详见本节开头的前提修正 ②）。
        foreach (var ((defType, name), defs) in ctx.Index.ByTypeAndDefName)
        {
            if (defs.Count < 2) continue;
            // ★ 报告过滤器：指定了目标 mod 时，只报“该 mod 参与其中”的重名
            if (ctx.TargetModPackageId is not null
                && !defs.Any(x => x.ModPackageId == ctx.TargetModPackageId)) continue;

            var first = defs[0];
            var rest = defs.Skip(1).ToList();
            yield return new Diagnostic(
                // 用 Warning 而非 Error：原版 Core 与 DLC 之间就有同类型同名覆盖的合法先例
                // （归档日志里 `Duplicate defName` 命中 0，游戏自己也不报错）。
                Codes.DefNameDuplicate, Severity.Warning, Scope.Offline,
                $"{defType} 的 defName `{name}` 被声明 {defs.Count} 次（同一 DefType 内同名会合并/遮蔽，被遮蔽的那份内容可能静默丢失）",
                "改名或用 ParentName 继承；若是有意覆盖，请确认被遮蔽的那份没有独占内容。注意原版 Core/Ideology 之间就有这种同类型覆盖（ThingSetMakerDef|MapGen_AncientComplexRoomLoot_Default），属正常模式",
                FileAbs: first.FileAbs, Line: first.Line,
                ModPackageId: first.ModPackageId, DefName: name, DefType: first.DefType,
                Suggestions: rest.Select(d => $"{d.ModPackageId}:{d.FileRelToMod}:{d.Line}").ToList(),
                Evidence: new Dictionary<string, string>
                {
                    ["declarations"] = string.Join(" | ", defs.Select(d => $"{d.ModPackageId}/{d.FileRelToMod}:{d.Line}")),
                    ["types"] = string.Join(",", defs.Select(d => d.DefType).Distinct())
                });
        }
    }
}
