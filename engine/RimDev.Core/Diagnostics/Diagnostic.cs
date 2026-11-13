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
    IReadOnlyDictionary<string, string>? Evidence = null)
{
    /// <summary>稳定指纹，用于 baseline 差分与去重（诊断模型设计见 01 §4.7）。</summary>
    public string Fingerprint =>
        $"{Code}|{TypeName}|{MemberName}|{Message}";
}
