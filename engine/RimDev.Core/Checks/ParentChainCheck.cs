using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;

namespace RimDev.Core.Checks;

/// <summary>
/// ParentName 链校验：父类不存在（RD2005）、继承环（RD2006）。
///
/// 两个易误报点（项目历史坑）：
/// 1. 原版抽象基类用 **`Name="X"` 属性**定义（如 &lt;ThingDef ParentName="BuildingBase" Name="Fleshmass"&gt;），
///    不是 &lt;defName&gt;。只按 defName 建索引会把 BaseBullet / BaseGun / BuildingBase 全判成"缺失"。
///    所以查找顺序是：先在 defName 空间找，再在抽象名空间找 —— **按名字全局解析，不限定 DefType**。
///
/// ★ 口径纠正（2026-10-14 实测，推翻上一版「两者都必须在同一 DefType 内」的判断）：
///   `defName` 的唯一性是按 DefType 的（RD2004 判重用 ByTypeAndDefName），
///   但 `ParentName` 是**按名字全局解析**的（RD2005/RD2006 用）—— 这是两个不同机制，不要互相外推。
///   实测（官方 6 个包 / 13808 def / 4553 个带 ParentName）：
///     「按类型未解析 = 26」而「全局(name-only)未解析 = 0」。
///   那 26 条全部是「父类跨 XML 元素名存在于原版」：
///     · 18 条 `BackstoryDef -> Solid`：`Data\Core\Defs\BackstoryDefs\Solid\Solid_Rare.xml:4`
///       是 `<BackStoryDef Abstract="True" Name="Solid">`（**大写 S**），而第 10/21/44/57/74 行
///       子节点写 `<BackstoryDef ParentName="Solid">`（**小写 s**）—— 同一份文件里两种拼写混用，
///       它们映射到同一个 C# 类型 `Verse.BackstoryDef`（引擎对元素名→类型是**大小写不敏感**的；
///       `rimdev api type BackStoryDef` 查不到只是本工具的类型查找大小写敏感）；
///     · 1 条 `CreepJoinerFormKindDef -> BasePlayerPawnKind`（Anomaly）：父在 Core 里是
///       `<PawnKindDef Abstract="True" …>`，而该类型的 C# 基类才是 PawnKindDef —— 继承沿
///       **C# 类型层次**走，不按 XML 元素名；
///     · 其余 7 条同类（`StructureLayoutDef -> OrbitalPlatformBase`、`ThingDef -> FleckBase_Thrown`、
///       `FleckDef -> FleckGlowDistorted`）。
///   反向推论：**官方内容（Core + 5 DLC）必须 0 个未解析父类**，这既是健康判据，也是本口径的反证。
/// 2. 父类可能来自**未启用**的 mod。本检查只判"整个目录表里有没有"，
///    严格模式（只认启用集合）留给第 3 期。
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

            // ★ 按名字【全局】解析：defName 空间或抽象名空间命中即算存在（口径纠正，见类注释）。
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

            if (HasCycle(ctx, d, out var chain))
            {
                yield return new Diagnostic(
                    Codes.InheritanceCycle, Severity.Error, Scope.Offline,
                    $"ParentName 继承环：{string.Join(" → ", chain)}",
                    "打断环，让链最终落到一个不带 ParentName 的基类",
                    FileAbs: d.FileAbs, Line: d.Line,
                    ModPackageId: d.ModPackageId, DefName: d.DefName, DefType: d.DefType);
            }
        }
    }

    private static bool HasCycle(CheckContext ctx, RawDef start, out List<string> chain)
    {
        chain = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cur = start;
        while (cur is not null && !string.IsNullOrEmpty(cur.ParentName))
        {
            var key = cur.ReferableName ?? cur.FileAbs + ":" + cur.Line;
            if (!seen.Add(key)) { chain.Add(key); return true; }

            // ★ ParentName 按【名字全局】解析 —— 与上面的存在性判定必须同一口径
            //   （理由见类注释：按类型会把官方 26 条合法继承误判成缺失）。若两处口径不一致，
            //   会出现「存在性判定说父类存在、走链时却解析到另一个类型」的自相矛盾。
            //   ★ 实测更正：官方内容上两种口径报出的环【数量与链都相同】（都是 5）——
            //   所以这里选全局，理由是与存在性判定同口径，而**不是**「按类型会多报环」。
            var pn = cur.ParentName!;
            var parent = ctx.Index.ByDefName.GetValueOrDefault(pn)?.FirstOrDefault()
                         ?? ctx.Index.ByAbstractName.GetValueOrDefault(pn)?.FirstOrDefault();
            if (parent is null) return false;
            chain.Add(parent.ReferableName ?? parent.FileAbs + ":" + parent.Line);
            cur = parent;
        }
        return false;
    }

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
