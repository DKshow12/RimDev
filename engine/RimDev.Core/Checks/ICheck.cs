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
    string? TargetModPackageId);

public interface ICheck
{
    /// <summary>规则 id，对应错误码。</summary>
    string Id { get; }

    IEnumerable<Diagnostic> Run(CheckContext ctx);
}
