using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;

namespace RimDev.Core.Checks;

public sealed class XmlWellFormedCheck : ICheck
{
    public string Id => Codes.XmlParseFailed;

    public IEnumerable<Diagnostic> Run(CheckContext ctx)
    {
        foreach (var f in ctx.Files)
        {
            // ★ 报告过滤器：指定了目标 mod 时只报它自己的文件（索引仍是全集）
            if (ctx.TargetModPackageId is not null && f.ModPackageId != ctx.TargetModPackageId) continue;
            if (f.ParseError is null) continue;
            if (f.CommentHasDoubleDash) continue;   // 交给 CommentDashCheck 报 RD2002
            yield return new Diagnostic(
                Codes.XmlParseFailed, Severity.Error, Scope.Offline,
                $"XML 解析失败：{f.ParseError}",
                "用严格解析器打开该文件核对；注意 XmlReader 的报错位置可能与真实问题行无关",
                FileAbs: f.FileAbs, Line: f.ErrorLine, Column: f.ErrorColumn,
                ModPackageId: f.ModPackageId);
        }
    }
}
