using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;

namespace RimDev.Core.Checks;

/// <summary>
/// ParentName 链校验：父类不存在（RD2005）、继承环（RD2006）。
///
/// ★ 引擎口径（第 2 期 Task 6c：反编译 + 装载顺序实证，不是推测）。
///   本节**明确推翻**旧注释里的两处错误说法，不要再按旧说法改回去。
///
/// 【推翻旧说法①】旧注释：「先在 defName 空间找，再在抽象名空间找」——**错的**。
///   引擎 `Verse.XmlInheritance`：
///     private static Dictionary&lt;string, List&lt;XmlInheritanceNode&gt;&gt; nodesByName;   // 单个全局字典
///     public static void TryRegister(XmlNode node, ModContentPack mod) {
///         XmlAttribute a  = node.Attributes["Name"];            // ★ 建表【只用 Name 属性】
///         XmlAttribute a2 = node.Attributes["ParentName"];
///         ...
///         if (a != null) nodesByName.Add(a.Value, value);       // ← 全类里唯一一处写入
///     }
///     private static XmlInheritanceNode GetBestParentFor(XmlInheritanceNode node, string parentName) {
///         if (nodesByName.TryGetValue(parentName, out var v)) { /* 按 loadOrder 选，不做类型检查 */ }
///         Log.Error("XML error: Could not find parent node named \"" + parentName + "\" ...");   // ← RD2005 的引擎措辞
///     }
///   `<defName>` **不是继承名**，引擎的父节点表根本不看它。所以本检查的解析顺序是
///   **Name 空间优先 → defName 空间兜底**。
///
///   ★ 兜底是【有意为之的补丁近似】，不是「引擎也这么干」，**不能删**：
///     引擎在 `Verse.LoadedModManager.LoadAllActiveMods` 里的真实顺序是
///       XmlInheritance.Clear() → InitializeMods() → LoadModContent() → CreateModClasses()
///       → LoadModXML() → CombineIntoUnifiedXML() → TKeySystem.Parse() → ErrorCheckPatches()
///       → ApplyPatches(xmlDocument, assetlookup)          ← 补丁在这里改 XML
///       → ParseAndProcessXML() → ★ XmlInheritance.TryRegister(每个顶层节点) → ★ XmlInheritance.Resolve()
///     即 **注册发生在打补丁【之后】**：补丁补上的 `Name` 属性引擎看得见，我们的静态索引看不见。
///     实证：CombatExtended(3147447844) `Patches\ThingDef_Guns.xml:904-908` 用
///       &lt;Operation Class="PatchOperationAttributeAdd"&gt;
///         &lt;xpath&gt;Defs/ThingDef[defName="OSNGun_HMG"]&lt;/xpath&gt;
///         &lt;attribute&gt;Name&lt;/attribute&gt;&lt;value&gt;OSNGun_HMG&lt;/value&gt;
///       &lt;/Operation&gt;
///     给这个 def 补上 `Name="OSNGun_HMG"`，它的 `Defs\MechGuns.xml:54` 的
///     `&lt;ThingDef ParentName="OSNGun_HMG"&gt;` 才解析得开。全库同形 **61 条**（11 个 mod）。
///     若静态只查 Name 空间（无兜底），这 61 条会全部变成假阳 —— 比不报更糟。
///     第 3 期实现 `PatchOperation*` 解析后，应改为「应用补丁之后的精确口径」。
///
/// 【推翻旧说法②】旧注释：「继承沿 C# 类型层次走」——**错的**，那只是巧合。
///   结论（ParentName 按名字【全局】解析、不按 DefType 过滤）不变，但理由要换成：
///   `nodesByName` 是**单个全局字典**，不按元素名/DefType 分区，`GetBestParentFor` 也不做类型检查。
///   引擎只保证「找得到」；字段能不能塞进那个 C# 类型是后面反序列化的事（巧合，不是机制）。
///
/// 【引擎如何判环 —— 按声明，不按名字】`XmlInheritance.Resolve()` 在节点对象上判：解析不出父节点的
///   那条链报 `Cyclic inheritance hierarchy detected`。**两个不同声明共用一个名字，引擎不认为成环**。
///   所以本检查的 `seen` 键是**声明身份**（`FileAbs` + `Line`），不是 `DefName`/`NameAttr` 字符串；
///   诊断里展示的名字用 `NameAttr ?? DefName ?? 文件名:行号`（`ParentName` 指的就是 `Name`），
///   另用 `Evidence["cycle"]` 带出**声明链**（`FileRelToMod:Line`）以便区分同名的两处声明。
///   官方反例（旧实现在这里报 5 条假自环，全是假阳；归档的真实游戏日志里没有对应
///   `Cyclic inheritance hierarchy detected`）：
///     · `Data\Core\Defs\PawnKindDefs_Humanlikes\PawnKinds_Mercenary.xml:198`
///       `&lt;PawnKindDef ParentName="MercenaryMidTierBase" Name="MercenarySlasherBase" Abstract="True"&gt;`
///       第 199 行还写着 `&lt;defName&gt;Mercenary_Slasher&lt;/defName&gt;` —— **抽象节点的 defName 只是装饰**
///       （抽象声明不会被建成 def，见 <see cref="DefIndex.Build"/>），而第 232 行的真 def 也叫
///       `Mercenary_Slasher`。两个不同声明的「可引用名」同串 → 旧实现按名字判环即报假自环。
///       同类另有 `Tribal_ChiefMelee`、`TradersGuild_Slasher`。
///     · `Data\Core\Defs\SoundDefs\UI_Oneshots_Designation.xml`：第 3 行
///       `&lt;SoundDef Name="Designate_DragStandard_Changed" Abstract="True"&gt;`（只有 Name），
///       第 19 行的真 def 又叫 `Designate_DragStandard_Changed` —— 按 defName 优先解析父节点会指到它自己。
///
/// 【MayRequire 的保守方向】引擎在 `TryRegister` 里会跳过 `MayRequire` 未满足的节点，我们不跳 →
///   方向是**少报**（把不存在的父类当成存在），不会多报。
///
/// 两个易误报点（项目历史坑）：
/// 1. 原版抽象基类用 **`Name="X"` 属性**定义（如 &lt;ThingDef ParentName="BuildingBase" Name="Fleshmass"&gt;），
///    不是 &lt;defName&gt;。只按 defName 建索引会把 BaseBullet / BaseGun / BuildingBase 全判成"缺失"。
/// 2. `defName` 的唯一性是按 DefType 的（RD2004 判重用 ByTypeAndDefName），
///    但 `ParentName` 是**按名字全局解析**的 —— 两个不同机制，不要互相外推。
///    实测（官方 6 个包 / 13808 def / 4553 个带 ParentName）：
///      「按类型未解析 = 26」而「全局(name-only)未解析 = 0」。
///    那 26 条全部是「父类跨 XML 元素名存在于原版」：
///      · 18 条 `BackstoryDef -> Solid`：`Data\Core\Defs\BackstoryDefs\Solid\Solid_Rare.xml:4`
///        是 `&lt;BackStoryDef Abstract="True" Name="Solid"&gt;`（**大写 S**），而第 10/21/44/57/74 行
///        子节点写 `&lt;BackstoryDef ParentName="Solid"&gt;`（**小写 s**）—— 同一份文件里两种拼写混用，
///        它们映射到同一个 C# 类型 `Verse.BackstoryDef`（引擎对元素名→类型是**大小写不敏感**的；
///        `rimdev api type BackStoryDef` 查不到只是本工具的类型查找大小写敏感）；
///      · 1 条 `CreepJoinerFormKindDef -> BasePlayerPawnKind`（Anomaly）：父在 Core 里是
///        `&lt;PawnKindDef Abstract="True" …&gt;`；
///      · 其余 7 条同类（`StructureLayoutDef -> OrbitalPlatformBase`、`ThingDef -> FleckBase_Thrown`、
///        `FleckDef -> FleckGlowDistorted`）。
///    反向推论：**官方内容（Core + 5 DLC）必须 0 个未解析父类**，这既是健康判据，也是本口径的反证。
/// </summary>
public sealed class ParentChainCheck : ICheck
{
    public string Id => Codes.ParentNotFound;

    public IEnumerable<Diagnostic> Run(CheckContext ctx)
    {
        foreach (var d in ctx.Defs)
        {
            // ★ 报告过滤器（修正 ④）：只报目标 mod 自己的 def。
            //   索引/父类解析仍然在全集上做 —— 否则指向 Core 基类的 def 会被全部误报 RD2005。
            if (ctx.TargetModPackageId is not null && d.ModPackageId != ctx.TargetModPackageId) continue;

            if (string.IsNullOrEmpty(d.ParentName)) continue;

            // ★ 按名字【全局】解析：defName 空间或抽象名空间命中即算存在（口径见类注释）。
            var parentExists = ctx.Index.CanBeReferenced(d.ParentName!);
            if (!parentExists)
            {
                var suggestions = SuggestParent(ctx, d.ParentName);
                yield return new Diagnostic(
                    Codes.ParentNotFound, Severity.Error, Scope.Offline,
                    $"ParentName=\"{d.ParentName}\" 指向不存在的 def/抽象基类",
                    "检查拼写；原版抽象基类用 Name=\"X\" 定义，只能在抽象名空间里找到",
                    FileAbs: d.FileAbs, Line: d.Line,
                    ModPackageId: d.ModPackageId, DefName: d.DefName, DefType: d.DefType,
                    Suggestions: suggestions);
                continue;
            }

            if (HasCycle(ctx, d, out var chain, out var decls))
            {
                yield return new Diagnostic(
                    Codes.InheritanceCycle, Severity.Error, Scope.Offline,
                    $"ParentName 继承环：{string.Join(" → ", chain)}",
                    "打断环，让链最终落到一个不带 ParentName 的基类",
                    FileAbs: d.FileAbs, Line: d.Line,
                    ModPackageId: d.ModPackageId, DefName: d.DefName, DefType: d.DefType,
                    Evidence: new Dictionary<string, string>
                    {
                        // ★ 声明链（相对 mod 根，短且可读）：同名的两处不同声明靠它区分。
                        ["cycle"] = string.Join(" → ", decls.Select(x => $"{x.FileRelToMod}:{x.Line}"))
                    });
            }
        }
    }

    /// <summary>
    /// 从 <paramref name="start"/> 向上走 ParentName 链；成环则返回 true。
    /// <paramref name="chain"/> = 面向继承的展示名链；<paramref name="declarations"/> = 对应的声明链。
    /// </summary>
    private static bool HasCycle(CheckContext ctx, RawDef start, out List<string> chain, out List<RawDef> declarations)
    {
        chain = new List<string>();
        declarations = new List<RawDef>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cur = start;
        while (cur is not null && !string.IsNullOrEmpty(cur.ParentName))
        {
            // ★ 环的判定键是【声明身份】(FileAbs + Line)，不是名字 ——
            //   引擎 Resolve() 也按节点对象判环：两个不同声明共用一个名字，引擎不认为成环。
            //   官方反例见类注释（PawnKinds_Mercenary.xml:198 vs :232、UI_Oneshots_Designation.xml:3 vs :19）。
            var key = cur.FileAbs + ":" + cur.Line;
            if (!seen.Add(key))
            {
                chain.Add(DisplayName(cur));
                declarations.Add(cur);
                return true;
            }
            chain.Add(DisplayName(cur));
            declarations.Add(cur);

            // ★ 解析顺序对齐引擎（见类注释）：nodesByName 只认 Name 属性，故 Name 空间优先；
            //   defName 兜底是【有意为之的近似】—— 引擎在 ApplyPatches 之后才 TryRegister，
            //   所以补丁补上的 Name 属性（如 CE 给 OSNGun_HMG 补的）在静态索引里看不到。
            var pn = cur.ParentName!;
            var parent = ctx.Index.ByAbstractName.GetValueOrDefault(pn)?.FirstOrDefault()
                         ?? ctx.Index.ByDefName.GetValueOrDefault(pn)?.FirstOrDefault();
            if (parent is null) return false;
            cur = parent;
        }
        return false;
    }

    /// <summary>
    /// 面向继承的展示名：<c>ParentName</c> 指的就是 <c>Name</c> 属性，其次 <c>defName</c>，
    /// 两者都没有就用「文件名:行号」（声明定位，永不与另一处声明混淆）。
    /// </summary>
    private static string DisplayName(RawDef d) =>
        !string.IsNullOrEmpty(d.NameAttr) ? d.NameAttr
        : !string.IsNullOrEmpty(d.DefName) ? d.DefName
        : Path.GetFileName(d.FileAbs) + ":" + d.Line;

    private static List<string> SuggestParent(CheckContext ctx, string parentName)
    {
        var pool = ctx.Index.ByDefName.Keys.Concat(ctx.Index.ByAbstractName.Keys).Distinct();
        return pool
            .Select(n => (n, s: RimDev.Core.Metadata.MemberInfoIndex.Similarity(parentName, n)))
            .Where(x => x.s > 0)
            .OrderByDescending(x => x.s)
            .Take(5)
            .Select(x => x.n)
            .ToList();
    }
}
