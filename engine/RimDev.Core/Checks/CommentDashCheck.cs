using RimDev.Core.Diagnostics;

namespace RimDev.Core.Checks;

/// <summary>
/// XML 注释体内含 `--` → 整份文件解析失败，且**报错位置与真实问题行无关**。
/// 项目历史：4 次踩坑，其中 3 次的触发源是"看起来很无辜"的表格分隔线 `|------|-----|`。
/// </summary>
public sealed class CommentDashCheck : ICheck
{
    public string Id => Codes.CommentContainsDoubleDash;

    public IEnumerable<Diagnostic> Run(CheckContext ctx)
    {
        foreach (var f in ctx.Files)
        {
            // ★ 报告过滤器：指定了目标 mod 时只报它自己的文件（索引仍是全集）
            if (ctx.TargetModPackageId is not null && f.ModPackageId != ctx.TargetModPackageId) continue;
            if (!f.CommentHasDoubleDash) continue;
            yield return new Diagnostic(
                Codes.CommentContainsDoubleDash, Severity.Error, Scope.Offline,
                "XML 注释体内含 `--`，会导致整份文件解析失败（报错位置与真实问题行无关）",
                "注释里的装饰线一律用 `=`（如 `<!-- ===== 段落 ===== -->`），表格分隔线写成 `|====|`",
                FileAbs: f.FileAbs, Line: f.ErrorLine, Column: f.ErrorColumn,
                ModPackageId: f.ModPackageId);
        }
    }
}
