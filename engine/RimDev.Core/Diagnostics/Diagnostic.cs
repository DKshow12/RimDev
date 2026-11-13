namespace RimDev.Core.Diagnostics;

public enum Severity { Info, Warning, Error }

/// <summary>诊断范围：offline = 离线可判；needsGame = 必须真机。</summary>
public enum Scope { Offline, NeedsGame }

public sealed record Diagnostic(
    string Code,
    Severity Severity,
    Scope Scope,
    string Message,
    string? Hint = null,
    string? TypeName = null,
    string? MemberName = null,
    IReadOnlyList<string>? Suggestions = null,
    IReadOnlyDictionary<string, string>? Evidence = null,
    // ★ 第 2 期 Task 6 追加的【定位字段】。全部加在末尾且可选 = 向后兼容：
    //   NameOracle / ModCatalog / ApiCommand 等既有调用都是命名参数或位置参数前 9 个，不受影响。
    //   没有这些字段，Task 6 的五条 check 规则（全部用 FileAbs:/Line:/ModPackageId:/DefName:/DefType:
    //   命名参数构造诊断）会【每一条都编译不过】。
    string? FileAbs = null,
    int? Line = null,
    int? Column = null,
    string? ModPackageId = null,
    string? DefName = null,
    string? DefType = null)
{
    /// <summary>稳定指纹，用于 baseline 差分与去重（诊断模型设计见 01 §4.7）。</summary>
    public string Fingerprint =>
        // ★ 必须含位置（FileAbs/Line）。XML 类诊断的 TypeName/MemberName 都是 null，
        //   Message 形如 "XML 解析失败：XmlException: …"，撞车概率极高；
        //   不含位置时 CheckRunner 的指纹去重会把【不同文件】的同一条错误合并成一条。
        $"{Code}|{TypeName}|{MemberName}|{FileAbs}|{Line}|{Message}";
}
