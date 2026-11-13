using System.Text.RegularExpressions;

namespace RimDev.Core.Logs;

/// <summary>
/// ★★ 第 3 期 Task 15：日志诊断的**规则唯一声明点**。
///
/// 规则来源：RimCrow（<c>backend/managers/mgr_game_logs.py</c> 的 <c>LogAnalyzer</c>，11 条
/// <c>known_rules</c>），已核实可移植（MIT 许可）。移植时保留它的**两条核心纪律**：
///
/// <list type="number">
/// <item><b>噪音黑名单</b>：这些框架的报错不是具体 mod 的锅，归因时应排除。</item>
/// <item><b>版本号假阳性过滤</b>：日志里 <c>[1.5.2]</c> 这类会被正则误当成 packageId。</item>
/// </list>
///
/// ★ 为什么要移植而不是自己写：这 11 条是社区长期积累的**真实日志形态**，
///   自己从零总结会漏掉不常见的写法（如 <c>Could not resolve reference to object with loadID</c>）。
/// </summary>
public static class LogDiagnosisRules
{
    /// <summary>一条诊断规则。</summary>
    public sealed record Rule(
        string Id,
        string Code,
        string Pattern,
        string Explanation)
    {
        public Regex Compiled { get; } = new(Pattern,
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// 11 条已知诊断（顺序 = 优先级：先匹配到的赢，因为 <c>NullReferenceException</c> 等
    /// 宽泛模式会「吃掉」更具体的模式）。
    /// </summary>
    public static readonly IReadOnlyList<Rule> All = new[]
    {
        new Rule("missing_def", DiagnosticCodes.LogMissingDef,
            @"\b\S+\s+\S+\s+is not defined in any loaded mods\.",
            "有 mod 正在引用不存在的定义，通常是缺少前置、版本不匹配或旧存档残留引用。"),

        new Rule("xml_unknown_field", DiagnosticCodes.LogXmlUnknownField,
            @"XML error:\s+.+?\s+doesn't correspond to any field in type\s+.+?\.",
            "某个 XML 字段不被当前游戏或目标类型识别，常见原因是 mod 版本过旧、游戏版本不匹配或配置写错。"),

        new Rule("config_error", DiagnosticCodes.LogDefConfigError,
            @"Config error in",
            "某个定义配置不符合游戏要求，通常需要检查日志提到的对象和对应 mod。"),

        new Rule("cross_reference_missing", DiagnosticCodes.LogCrossRefMissing,
            @"Could not resolve cross-reference|Could not resolve reference to object with loadID",
            "游戏无法找到被引用的对象，常见原因是缺少依赖、加载顺序不对或存档里引用了已移除内容。"),

        new Rule("could_not_load_reference", DiagnosticCodes.LogCrossRefMissing,
            @"Could not load reference to\s+.+?\s+named\s+.+",
            "日志中的对象引用没有加载成功，优先检查相关 mod 是否启用、依赖是否齐全。"),

        new Rule("missing_texture", DiagnosticCodes.LogMissingTexture,
            @"Failed to find any texture|Could not load UnityEngine\.Texture2D",
            "游戏找不到贴图资源，通常是资源路径写错、mod 文件缺失或版本包不完整。"),

        // ★ Task 15b：**本机真实日志里最有价值的一条**，RimCrow 原规则库没有。
        //   它与我们的 RD2009（离线预测「补丁打空」）完全对应 —— 真机侧实证。
        //   引擎原文：`Verse.PatchOperationAdd(xpath="..."): Failed to find a node with the given xpath`
        //   实测：本机 2.4 MB 日志里这个形态出现 62 次（Ratkin CE Patch 等）。
        new Rule("patch_xpath_no_match", DiagnosticCodes.LogPatchXpathNoMatch,
            @"Failed to find a node with the given xpath",
            "补丁的 xpath 在真机里匹配 0 个节点。常见原因：目标 defName 被改名、"
            + "前置 mod 未启用、或游戏版本升级后字段路径变了。"
            + "与离线 RD2009 对应（同一事件的两个视角）。"),

        new Rule("null_reference_exception", DiagnosticCodes.LogNullReference,
            @"NullReferenceException",
            "有代码访问了不存在的对象。单独看这类错误信息量有限，需要结合堆栈里的来源继续判断。"),

        new Rule("type_load_exception", DiagnosticCodes.LogAssemblyConflict,
            @"ReflectionTypeLoadException|MissingMethodException|TypeLoadException|MissingFieldException",
            "程序集或方法字段不匹配，常见原因是 mod 版本不兼容、依赖库版本冲突或游戏更新后旧 DLL 未适配。"),

        new Rule("translation_data_error", DiagnosticCodes.LogTranslationError,
            @"Translation data .*? has errors|translation data has errors",
            "翻译文件存在格式或字段问题，通常不一定导致崩溃，但会影响文本显示。"),

        new Rule("package_id_format", DiagnosticCodes.LogPackageIdFormat,
            @"PackageId .*? is not in valid format",
            "mod 的 packageId 格式不规范，可能影响依赖识别、加载顺序和管理器匹配。"),

        new Rule("patch_obsolete_method", DiagnosticCodes.LogPatchObsolete,
            @"patch(?:es)? on obsolete method|patched jump instruction",
            "有补丁作用在过时或不稳定的方法上，通常说明相关 mod 需要更新。"),
    };

    /// <summary>
    /// 噪音黑名单：这些 id 出现在日志里**不代表**它们有问题（它们是框架/平台，不是具体 mod）。
    /// 直接照搬 RimCrow 的清单 —— 那是社区从大量真实日志里筛出来的。
    /// </summary>
    public static readonly IReadOnlySet<string> NoiseModIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ludeon.rimworld",
        "system.reflection",
        "system.object",
        "brrainz.harmony",
        "unlimitedhugs.hugslib",
        "imranfish.xmlextensions",
        "startupprofiler.mypatches",
        "zetrith.prepatcher",
        "taranchuk.moderrorchecker",
    };

    /// <summary>
    /// 栈帧里的噪音命名空间（归因用）：这些是引擎/BCL 的帧，不是 mod 的错。
    /// </summary>
    public static readonly IReadOnlySet<string> NoiseNamespaces = new HashSet<string>(StringComparer.Ordinal)
    {
        "System", "Microsoft", "Mono", "mscorlib", "UnityEngine",
        "Verse", "RimWorld", "HarmonyLib", "Runtime",
    };

    /// <summary>从日志里提取 packageId 形态的 token（<c>[author.mod]</c>）。</summary>
    public static readonly Regex ModIdPattern = new(
        @"\[([a-zA-Z0-9\-_]{2,}\.[a-zA-Z0-9\-_]{2,})\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// ★ Task 15c：环世界**真实日志格式**的 mod 标记（本机 1.7 实测）：
    /// <code>
    /// [Verse] [FileSource] [Mod: 【显示名】 (author.mod)]
    /// [Verse] [XmlSource] Mod=【显示名】 (author.mod) File=C:\…
    /// </code>
    /// <para>不能只靠 <see cref="ModIdPattern"/>（它只认 <c>[author.mod]</c> 方括号）——
    /// 本机真实日志里大量是 <c>[Mod: 名称 (pkg)]</c> 形态。</para>
    /// </summary>
    public static readonly Regex ModTagPattern = new(
        @"\[Mod:\s*.+?\((?<pkg>[a-zA-Z0-9\-_.]{2,})\)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary><c>Mod=… (pkg) File=…</c> 形态（XmlSource 那类）。</summary>
    public static readonly Regex ModEqPattern = new(
        @"Mod=.+?\((?<pkg>[a-zA-Z0-9\-_.]{2,})\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary><c>Source file: &lt;路径&gt;</c> —— 比 <see cref="FilePattern"/> 更精确的来源指针。</summary>
    public static readonly Regex SourceFilePattern = new(
        @"Source file:\s*(?<path>[A-Za-z]:\\[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>从日志里提取 XML 文件路径。</summary>
    public static readonly Regex FilePattern = new(
        @"(?<![A-Za-z0-9_\-/\\])([A-Za-z0-9_\-/\\]+\.xml)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>栈帧里的类型全名（<c>at Some.Namespace.Type.Method()</c>）。</summary>
    public static readonly Regex NamespacePattern = new(
        @"^\s*at(?:\s+\([^)]*\))*\s+([A-Za-z_][\w]*(?:\.[A-Za-z_][\w]*)*)\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>
    /// 版本号伪装成 packageId 的形态（<c>1.4</c> / <c>1.5.2</c>）。
    /// 不排除它们，提取出来的「mod 名」会是一堆版本号。
    /// </summary>
    public static readonly Regex VersionLookalike = new(
        @"^\d+\.\d+(\.\d+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>一条日志行是否「值得诊断」（只看 ERROR / WARNING / EXCEPTION 级）。</summary>
    public static bool IsDiagnosticLevel(string? level) =>
        level is not null &&
        (level.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
         || level.Equals("WARNING", StringComparison.OrdinalIgnoreCase)
         || level.Equals("EXCEPTION", StringComparison.OrdinalIgnoreCase));

    /// <summary>按优先级匹配规则；没有命中返回 null。</summary>
    public static Rule? Match(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (var r in All)
            if (r.Compiled.IsMatch(text)) return r;
        return null;
    }
}

/// <summary>避免在 Logs 命名空间里到处写 <c>RimDev.Core.Diagnostics.</c> 前缀。</summary>
internal static class DiagnosticCodes
{
    public const string LogMissingDef = Diagnostics.Codes.LogMissingDef;
    public const string LogXmlUnknownField = Diagnostics.Codes.LogXmlUnknownField;
    public const string LogDefConfigError = Diagnostics.Codes.LogDefConfigError;
    public const string LogCrossRefMissing = Diagnostics.Codes.LogCrossRefMissing;
    public const string LogMissingTexture = Diagnostics.Codes.LogMissingTexture;
    public const string LogAssemblyConflict = Diagnostics.Codes.LogAssemblyConflict;
    public const string LogNullReference = Diagnostics.Codes.LogNullReference;
    public const string LogTranslationError = Diagnostics.Codes.LogTranslationError;
    public const string LogPackageIdFormat = Diagnostics.Codes.LogPackageIdFormat;
    public const string LogPatchObsolete = Diagnostics.Codes.LogPatchObsolete;
    public const string LogPatchXpathNoMatch = Diagnostics.Codes.LogPatchXpathNoMatch;
    public const string LogUncategorized = Diagnostics.Codes.LogUncategorized;
}
