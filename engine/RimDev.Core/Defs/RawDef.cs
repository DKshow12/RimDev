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
    string? ClassAttr = null,

    // ★ 第 3 期 Task 4b：这条声明来自哪个【安装根】（= ModInfo.RootPath / DefScanner.ScanFile 的 modRoot）。
    //
    //   为什么必须有它（**没有它就无法区分两类完全相反的东西**）：
    //   同一 packageId 在本机可以装两份（实测 Combat Extended：工坊 2890901044 与 3495749827），
    //   两份的 (ModPackageId, FileRelToMod) **可以完全相同** —— 因为 FileRelToMod 是相对各自安装根算的。
    //   于是「跨安装副本」（无害双装，不该报 RD2026）与「同一份安装内同一文件里声明两次」（真缺陷）
    //   在 (pkg, rel) 这个键上**不可区分**（全库实测有 8 条后者这种形态）。
    //   只有加上 ModRoot 才能写出正确的折叠条件：同 packageId + 同 FileRelToMod + **ModRoot 不同**。
    //
    //   加在末尾且可选 = 向后兼容：既有位置参数构造不受影响；
    //   索引缓存（DefIndexStore）已同步落盘该字段并把 schemaVersion 升到 4（缺失会让走缓存的消费方
    //   拿到 null，从而静默丢掉本 Task 的修复 —— 正是本项目反复踩到的「不报错、只是答案错」）。
    string? ModRoot = null,


    // ★ 第 3 期 Task 5a：`MayRequireAnyOf="A,B"`（**逗号分隔**，值是 packageId 列表）。
    //
    //   为什么必须捕获：引擎 `XmlInheritance.TryRegister` 在注册父节点前先过 `MayRequire*`，
    //   **不满足就跳过注册** ⇒ 那个 `Name=` 在引擎的父节点表里**根本不存在**；静态分析不建模它，
    //   就会把「引用一个被 MayRequire 挡掉的父类」判成可解析（漏报）。
    //   ★ 两个属性的语义**不同**（反编译逐行实证，不要按直觉写成同一个）：
    //     · `MayRequire`      = `ModLister.AllModsActiveNoSuffix(value.Split(','))` → **每一项都要**命中（**与**）；
    //     · `MayRequireAnyOf` = `ModLister.AnyFromListActive(value.Split(','))`    → **任一项**命中即可（**或**）。
    //   判定集合 = **已启用（Active）** 的 packageId（`GetActiveModWithIdentifier` 逐条要求 `item.Active`），
    //   **不是「已安装」**；后缀归一（`_steam`）见 `InheritanceLayer.NormalizeActiveModIds` 的注。
    //
    //   加在末尾且可选 = 向后兼容：既有位置参数构造不受影响；索引缓存（DefIndexStore）已同步落盘该字段
    //   并把 schemaVersion 升到 5（缺失会让走缓存的消费方拿到 null，静默丢掉本 Task 的行为）。
    string? MayRequireAnyOf = null)
{
    /// <summary>
    /// ★ 字段规则应当作用到的类型名：<c>Class=</c> 优先，其次元素名。
    /// 这是「解析顺序 = Class 优先」的单一真源（不要在消费者里各写一遍 if）。
    /// </summary>
    public string ResolvedTypeName => string.IsNullOrWhiteSpace(ClassAttr) ? DefType : ClassAttr!;

    /// <summary>可用于被引用的名字（defName 优先，其次 Name=）。</summary>
    public string? ReferableName => !string.IsNullOrEmpty(DefName) ? DefName : NameAttr;
}
