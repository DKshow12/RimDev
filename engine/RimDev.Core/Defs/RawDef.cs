namespace RimDev.Core.Defs;

/// <summary>
/// 一个 def 的「头」信息——只保留结构定位所需的最小集，不保留 XML 原文
/// （原文留给第 3 期的 sim/引用解析按需重读）。
/// </summary>
public sealed record RawDef(
    string DefType,          // 元素名，如 "ThingDef"；**也可能是 "VEF.Abilities.AbilityDef" 这类带命名空间的类型名**（见 ClassAttr）
    string? DefName,         // <defName>
    string? NameAttr,        // Name="..."（抽象基类用）
    string? ParentName,      // ParentName="..."
    string? MayRequire,      // MayRequire="..."
    bool IsAbstract,         // Abstract="True" 或 有 NameAttr 且无 DefName
    string ModPackageId,
    string FileAbs,
    string FileRelToMod,
    int Line,                // 1-based

    // ★ 第 3 期 Task 2b：def 元素上的 Class="..." 属性。
    //   为什么必须捕获：RimWorld 对 def 元素是【按 Class= 指定的类型】实例化并反射填字段的，
    //   元素名只给「默认类型」用。实测（全库 RD4001 归因）：<Def Class="Rimatomics.RimatomicResearchDef">、
    //   <ThingDef Class="CombatExtended.AmmoDef"> 这类写法用元素名（Def/ThingDef）去查字段表，
    //   必然报出成千上万条假阳（Def|PointCost 1467 条、ThingDef|ammoClass 2439 条）。
    //
    //   ★ 注意与「<li Class="...">」区分：后者是【子节点】的多态（comps/modExtensions 等），
    //     属于「按 Class 分派内层字段」的问题，本 Task 不动（FieldNameCheck 的既有边界）。
    //   加在末尾且可选 = 向后兼容：既有位置参数构造不受影响。
    string? ClassAttr = null)
{
    /// <summary>
    /// ★ 字段规则应当作用到的类型名：<c>Class=</c> 优先，其次元素名。
    /// 这是「解析顺序 = Class 优先」的单一真源（不要在消费者里各写一遍 if）。
    /// </summary>
    public string ResolvedTypeName => string.IsNullOrWhiteSpace(ClassAttr) ? DefType : ClassAttr!;

    /// <summary>可用于被引用的名字（defName 优先，其次 Name=）。</summary>
    public string? ReferableName => !string.IsNullOrEmpty(DefName) ? DefName : NameAttr;
}
