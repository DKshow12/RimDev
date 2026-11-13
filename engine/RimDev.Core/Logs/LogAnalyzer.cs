using System.Text.RegularExpressions;
using RimDev.Core.Diagnostics;

namespace RimDev.Core.Logs;

/// <summary>归因置信层级（`Docs/01` §5.5 已定义，此处照抄语义，不另造）。</summary>
public enum Attribution
{
    /// <summary>日志里带了文件路径，能定位到 mod 目录 —— 可信度最高。</summary>
    ByPath,
    /// <summary>只有 defName，靠索引反查所属 mod。</summary>
    ByDefName,
    /// <summary>只有 mod 显示名（<c>[author.mod]</c> 形态）。</summary>
    ByModName,
    /// <summary>什么都没提取到 —— 可信度最低，<b>不参与 baseline 新增判定</b>。</summary>
    Unattributed,
}

/// <summary>一条日志诊断（比 <see cref="Diagnostic"/> 多带归因与原文）。</summary>
public sealed record LogFinding(
    string Code,
    Severity Severity,
    int LineNumber,
    string? Timestamp,
    string? Level,
    string RuleId,
    string Message,
    string Explanation,
    Attribution Attribution,
    string? ModPackageId,
    IReadOnlyList<string> RelatedFiles,
    IReadOnlyList<string> RelatedModIds,
    IReadOnlyList<string> RelatedNamespaces,
    string RawText);

/// <summary>一次日志分析的结果。</summary>
public sealed record LogAnalysisResult(
    string Path,
    long Bytes,
    int LinesScanned,
    int BlocksScanned,
    IReadOnlyList<LogFinding> Findings,
    IReadOnlyDictionary<string, int> ByRule,
    IReadOnlyDictionary<Attribution, int> ByAttribution)
{
    public int ErrorCount => Findings.Count(f => f.Severity == Severity.Error);
    public int WarningCount => Findings.Count(f => f.Severity == Severity.Warning);
    public int InfoCount => Findings.Count(f => f.Severity == Severity.Info);

    public string Describe() =>
        $"扫描 {LinesScanned} 行 / {BlocksScanned} 块 → error {ErrorCount} warning {WarningCount} info {InfoCount}"
        + $"（已归因 {Findings.Count(f => f.Attribution != Attribution.Unattributed)}，"
        + $"未归因 {Findings.Count(f => f.Attribution == Attribution.Unattributed)}）";
}

/// <summary>
/// ★★ 第 3 期 Task 15：日志分析器。
///
/// 职责：把 <see cref="PlayerLogReader.ReadBlocks"/> 产出的日志块变成结构化的 <see cref="LogFinding"/> 列表。
/// 规则来自 <see cref="LogDiagnosisRules"/>（移植自 RimCrow）。
///
/// ── 三条移植过来的纪律（每一条都防一类具体假阳）──────────────────────────
/// <list type="number">
/// <item><b>噪音黑名单</b>：<c>[brrainz.harmony]</c> 这类框架名不该被归因成「有问题的 mod」。</item>
/// <item><b>版本号过滤</b>：<c>[1.5.2]</c> 会被 <c>ModIdPattern</c> 提取成 packageId 形态，
///   必须排除，否则「相关 mod」里会出现一堆版本号。</item>
/// <item><b>活跃集交叉比对</b>：提取到的 id 在 activeMods 里 = 确定是某个 mod；
///   不在 = 可能是「缺失的前置」（游戏报找不到该 mod），也可能是正则误伤 ——
///   这两种要区分开，不能混为一谈。</item>
/// </list>
///
/// ★ 第四层 <see cref="Attribution.Unattributed"/> **不参与 baseline 新增判定**：
///   否则游戏版本一变，所有通用报错都算「新增缺陷」，门禁变成噪音源。
/// </summary>
public static class LogAnalyzer
{
    /// <summary>
    /// 分析日志文件。
    /// </summary>
    /// <param name="path">Player.log 路径。</param>
    /// <param name="activeMods">当前启用的 packageId（小写）；null = 不做活跃集交叉比对。</param>
    /// <param name="maxLines">最多读多少行（0 = 不限，用于快速采样）。</param>
    public static LogAnalysisResult Analyze(
        string path,
        IReadOnlySet<string>? activeMods = null,
        int maxLines = 0)
    {
        var findings = new List<LogFinding>();
        var byRule = new Dictionary<string, int>(StringComparer.Ordinal);
        var byAttr = new Dictionary<Attribution, int>();

        var lines = 0;
        var blocks = 0;

        foreach (var b in PlayerLogReader.ReadBlocks(path, maxLines))
        {
            blocks++;
            lines += 1 + b.StackLines.Count;

            // 只看诊断级别（ERROR / WARNING / EXCEPTION）；INFO 一行不看，省 90% 时间。
            if (!LogDiagnosisRules.IsDiagnosticLevel(b.Level)) continue;

            var fullText = b.Text + "\n" + string.Join("\n", b.StackLines);
            var rule = LogDiagnosisRules.Match(fullText);
            var attr = Extract(b, fullText, activeMods, out var pkg, out var files, out var mods, out var nss);

            var code = rule?.Code ?? Diagnostics.Codes.LogUncategorized;
            var ruleId = rule?.Id ?? "uncategorized";
            var explanation = rule?.Explanation
                              ?? "未匹配任何已知规则；保留原文供人工判读（此类不参与「比基线新增」的判定）。";
            // RD7030（未归类）固定为 info：它不参与新增判定，级别必须与之匹配。
            var sev = rule is null ? Severity.Info : (rule.Id == "patch_obsolete_method" ? Severity.Warning : Severity.Error);

            findings.Add(new LogFinding(
                code, sev, b.LineNumber, b.Timestamp, b.Level, ruleId,
                b.Text.Length > 300 ? b.Text[..300] + "…" : b.Text,
                explanation, attr, pkg, files, mods, nss, fullText));

            byRule[ruleId] = byRule.GetValueOrDefault(ruleId) + 1;
            byAttr[attr] = byAttr.GetValueOrDefault(attr) + 1;
        }

        var bytes = 0L;
        try { bytes = new FileInfo(path).Length; } catch { }

        return new LogAnalysisResult(path, bytes, lines, blocks, findings, byRule, byAttr);
    }

    /// <summary>四层归因。顺序 = 可信度从高到低。</summary>
    private static Attribution Extract(
        LogBlock b, string fullText, IReadOnlySet<string>? activeMods,
        out string? pkg, out IReadOnlyList<string> files,
        out IReadOnlyList<string> mods, out IReadOnlyList<string> nss)
    {
        pkg = null;

        // ① by-path：日志里出现 .xml 路径，且能定位到具体 mod
        var fileHits = LogDiagnosisRules.FilePattern.Matches(fullText)
            .Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // ★ Task 15c：「Source file: C:\\…」是引擎自己报的来源，比通用 .xml 匹配更精确。
        foreach (Match m in LogDiagnosisRules.SourceFilePattern.Matches(fullText))
        {
            var p = m.Groups["path"].Value.Trim();
            if (!fileHits.Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase))) fileHits.Add(p);
        }
        files = fileHits;

        // ② by-defname：从常见句式里抓 defName（`<X> is not defined` / `Config error in <X>`）
        var defName = ExtractDefName(fullText);

        // ③ by-modname / 活跃集交叉
        // ★ Task 15c：三种形态都要认 —— 环世界真实日志主要用后两种，
        //   只认第一种（RimCrow 原规则）会产生巨量「未归因」。
        var modHits = new List<string>();
        void AddId(string raw)
        {
            var low = raw.Trim().ToLowerInvariant();
            if (low.Length == 0) return;
            if (LogDiagnosisRules.NoiseModIds.Contains(low)) return;
            if (LogDiagnosisRules.VersionLookalike.IsMatch(low)) return;
            if (low.EndsWith("_steam", StringComparison.Ordinal)) low = low[..^6];
            modHits.Add(low);
        }
        // ① [author.mod]
        foreach (Match m in LogDiagnosisRules.ModIdPattern.Matches(fullText))
            AddId(m.Groups[1].Value);
        // ② [Mod: 显示名 (author.mod)]  —— 本机 FileSource 行就是这个形态
        foreach (Match m in LogDiagnosisRules.ModTagPattern.Matches(fullText))
            AddId(m.Groups["pkg"].Value);
        // ③ Mod=… (author.mod) File=…  —— XmlSource 行
        foreach (Match m in LogDiagnosisRules.ModEqPattern.Matches(fullText))
            AddId(m.Groups["pkg"].Value);
        mods = modHits.Distinct(StringComparer.Ordinal).ToList();

        // ④ 命名空间（栈帧归因）
        var nsHits = new List<string>();
        foreach (Match m in LogDiagnosisRules.NamespacePattern.Matches(fullText))
        {
            var root = m.Groups[1].Value.Split('.')[0];
            if (LogDiagnosisRules.NoiseNamespaces.Contains(root)) continue;
            nsHits.Add(m.Groups[1].Value);
        }
        nss = nsHits.Distinct(StringComparer.Ordinal).Take(5).ToList();

        // 定级：路径 > defName > modName > 未归因
        if (fileHits.Count > 0)
        {
            // 活跃集里如果有匹配到路径的 mod，用它；否则只报路径（ByPath 仍算强证据）
            if (mods.Count > 0) pkg = mods[0];
            return Attribution.ByPath;
        }
        if (!string.IsNullOrEmpty(defName)) return Attribution.ByDefName;
        if (mods.Count > 0)
        {
            pkg = mods[0];
            return Attribution.ByModName;
        }
        return Attribution.Unattributed;
    }

    private static readonly Regex MissingDefPattern = new(
        @"\b(?<type>\S+)\s+(?<slot>\S+)\s+is not defined in any loaded mods\.",
        RegexOptions.Compiled);

    private static readonly Regex ConfigErrorPattern = new(
        @"Config error in (?<name>[A-Za-z0-9_]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FailedToFindPattern = new(
        @"Failed to find (?<type>\S+) named (?<name>\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string? ExtractDefName(string text)
    {
        var m = MissingDefPattern.Match(text);
        if (m.Success) return m.Groups["slot"].Value.Trim('.', ',', ':', ';');
        m = ConfigErrorPattern.Match(text);
        if (m.Success) return m.Groups["name"].Value;
        m = FailedToFindPattern.Match(text);
        if (m.Success) return m.Groups["name"].Value.Trim('.', ',', ':', ';');
        return null;
    }
}
