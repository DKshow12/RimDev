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
            // ★ Task 2 Step 6 / F4：RD2001 必须能区分「你的文件坏了」与「工具读不了」。
            //   实测全库 13 个失败**全部**是 Encoding（GBK/无 BOM 的字节 + 无编码声明）。
            var cause = f.ParseCause ?? RimDev.Core.Diagnostics.ParseCause.Classify(f.ParseError);
            yield return new Diagnostic(
                Codes.XmlParseFailed, Severity.Error, Scope.Offline,
                // ★ Task 2 Step 6：把 cause 写进消息**与** Evidence —— 前者给人看，后者给 --json 消费方。
                $"XML 解析失败（cause={cause}）：{f.ParseError}",
                "按 cause 区分：Encoding = 文件字节编码与 XML 声明/默认编码不符（是内容问题）；"
                + "Structure = 良构性错误。注意 XmlReader 的报错位置可能与真实问题行无关",
                FileAbs: f.FileAbs, Line: f.ErrorLine, Column: f.ErrorColumn,
                ModPackageId: f.ModPackageId,
                Evidence: new Dictionary<string, string>
                {
                    ["cause"] = cause,
                    ["rootKind"] = f.RootKind ?? "",
                });
        }
    }
}
