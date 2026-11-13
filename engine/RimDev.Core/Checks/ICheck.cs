using RimDev.Core.Catalog;
using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;

namespace RimDev.Core.Checks;

/// <summary>
/// 规则上下文：一次 check 运行里所有规则共享的输入。
///
/// ★ 契约（第 2 期 Task 6 修正 ④）：
///   <see cref="Index"/> / <see cref="Defs"/> / <see cref="Files"/> 必须建自【全集扫描】，
///   <see cref="TargetModPackageId"/> 只作【报告过滤器】。
///
///   为什么：若索引只含目标 mod，则 ParentName 指向 Core 基类的 def 会被**全部误报** RD2005
///   （ThreatFleet 自身只有 ~185 个 def，父类大多在 Core/DLC 里）。
///   反过来，每条规则都必须尊重 <see cref="TargetModPackageId"/> —— 只报「该 mod 参与」的问题，
///   但索引 / 父类解析 / 重名判定都在全集上做。两者的分工就是本类型的全部语义。
/// </summary>
public sealed record CheckContext(
    DefIndex Index,
    IReadOnlyList<DefFileResult> Files,
    ModCatalog Catalog,
    IReadOnlyList<RawDef> Defs,
    string? TargetModPackageId,
    // ★ 第 3 期 Task 3 追加（加在末尾且可空 = 向后兼容：现有 5 个位置参数的构造调用全部不受影响）：
    //   LoadOrder = 引擎语义的 mod 加载顺序（Core → knownExpansions → activeMods），RD2011 的判据；
    //   ModRank   = **本次扫描子集**里每个 packageId 的序位（--scope all / --only-official 会缩小集合），
    //               遮蔽胜者判定键 (modLoadOrder, folderIndex) 用的就是它。
    //   两者为 null 时 ShadowingCheck 会退化为「按目录表顺序」，而不是静默失效。
    LoadOrder? LoadOrder = null,
    IReadOnlyDictionary<string, int>? ModRank = null,

    // ★ 第 3 期 Task 5a 追加（同样加在末尾且可空 = 向后兼容）：
    //   ActivePackageIds = 【已启用】的 packageId 全集（小写）。`MayRequire*` 的判据是
    //   `ModLister.GetActiveModWithIdentifier`，硬条件是 `item.Active` ⇒ 是 **Active 而不是 All**。
    //   ★ `Docs\01` §5.1.5 写的「判定集合 = 已安装的 packageId 全集（不是 activeMods）」
    //     已被反编译实证**否定**（见 `InheritanceLayer` 类注释里的 `AllModsActiveNoSuffix` 原文）。
    //   ★ 必须由调用方显式给：`check --only-official` 会让 `Catalog` 变窄，而「本机启用了什么」
    //     与本次扫描范围无关。为 null 时 InheritanceLayer 回落到 `Catalog.Active`。
    IReadOnlySet<string>? ActivePackageIds = null);

public interface ICheck
{
    /// <summary>规则 id，对应错误码。</summary>
    string Id { get; }

    IEnumerable<Diagnostic> Run(CheckContext ctx);
}
