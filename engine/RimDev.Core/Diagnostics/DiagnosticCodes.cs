namespace RimDev.Core.Diagnostics;

/// <summary>
/// 错误码集中定义。完整表见 Docs\01-设计文档.md §7 与 Docs\02-功能增补-基于实战经验.md §5。
/// v1 只用到元数据与名字查证相关的码；其余在后续计划中逐步启用。
/// </summary>
public static class Codes
{
    // RD1xxx 环境与索引
    public const string GameRootNotFound = "RD1001";
    public const string ManagedDirMissing = "RD1002";
    public const string ModsConfigMissing = "RD1003";

    // RD2xxx Def 结构与加载（v1 未启用）
    // RD3xxx 引用完整性（v1 未启用）

    // RD4xxx 字段与类型（元数据引擎）
    public const string FieldNotFound = "RD4001";
    public const string FieldTypeMismatch = "RD4002";
    public const string EnumValueInvalid = "RD4003";
    public const string TypeNotFound = "RD4004";
    public const string CaseMismatch = "RD4005";
    public const string MetadataUnavailable = "RD4006";

    // RD5xxx 程序集
    public const string AssemblyLoadFailed = "RD5001";

    // RD9xxx 反模式规则包（v1 只实现可离线判定的成员相关项）
    public const string CustomXmlLoaderInfo = "RD4008";

    // ===== 第 2 期 =====
    // RD2xxx Def 结构与加载
    public const string XmlParseFailed = "RD2001";
    public const string CommentContainsDoubleDash = "RD2002";
    public const string DefNameMissing = "RD2003";
    public const string DefNameDuplicate = "RD2004";
    public const string ParentNotFound = "RD2005";
    public const string InheritanceCycle = "RD2006";
    public const string RootNotDefs = "RD2013";
    public const string UnknownDefType = "RD2017";


    // ===== 第 3 期 Task 1：loadFolders 规则族（引擎 Verse.ModLoadFolders.GetIssueList 的 issue 表）=====
    // ★ 码号预检（2026-10-14）：这 10 个常量在本文件里命中数原本为 0（RD2012 只在 Docs\01 §7 的码表里，代码里没发射点）。
    //   级别照 Docs\01 §7 的码表：RD2012 info / RD2018 warning / RD2019 warning / RD2020 warning /
    //   RD2021 info / RD2022 warning / RD2023 info / RD2024 error；RD2025/RD2026 属 Task 3（遮蔽），此处一并占位。
    /// <summary>loadFolders 声明了不存在的目录（Docs\01 §7：info，「通常无害」）。</summary>
    public const string LoadFolderDoesntExist = "RD2012";
    /// <summary>版本块里一个 &lt;li&gt; 都没有（引擎 ModLoadFolderListEmpty）。</summary>
    public const string LoadFolderListEmpty = "RD2018";
    /// <summary>同一个版本块内 &lt;li&gt; 重复（引擎 ModLoadFolderRepeatingFolder）。</summary>
    public const string LoadFolderRepeatingFolder = "RD2019";
    /// <summary>版本键格式非法（引擎 IsWellFormedVersionString 要求恰好两段；引擎 ModLoadFolderMalformedVersion）。</summary>
    public const string LoadFolderMalformedVersion = "RD2020";
    /// <summary>使用了已废弃的 default 版本键（引擎 ModLoadFolderDefaultDeprecated）。</summary>
    public const string LoadFolderDefaultDeprecated = "RD2021";
    /// <summary>版本键未升序（引擎 ModLoadFolderOutOfOrder）。</summary>
    public const string LoadFolderOutOfOrder = "RD2022";
    /// <summary>版本键不在该 mod 自己的 supportedVersions 里（引擎 ModLoadFolderDefinesUnsupportedGameVersion）。</summary>
    public const string LoadFolderUnsupportedVersion = "RD2023";
    /// <summary>解析出的加载文件夹列表为空 —— 该 mod 的 def 完全不会被加载。</summary>
    public const string LoadFolderResolvedEmpty = "RD2024";
    /// <summary>同名 (DefType, defName) 被更高优先级来源遮蔽（第 3 期 Task 3）。</summary>
    public const string DefShadowed = "RD2025";
    /// <summary>同一个 mod 内同一 (DefType, defName) 声明多次（第 3 期 Task 3）。</summary>
    public const string DefDuplicateWithinMod = "RD2026";
    public const string DuplicatePackageId = "RD1005";

    // ★ RD3001 的语义说明（预检修正 ②）：`01` §7 把 RD3001 定义为「引用悬空：目标 defName 不存在」，
    //   那是第 3 期引用解析的用法（需要元数据知道 defaultProjectile 是 ThingDef 引用，见 `01` T12/T15）。
    //   本期 `name check --kind def` 报的是【同一个条件】（某个 defName 不存在），差别只是没有引用点：
    //   诊断里不会有引用处的 FileAbs/Line。第 3 期实现引用解析时会复用同一个码，届时靠 FileAbs/Line 区分两用。
    public const string DefNameNotFound = "RD3001";

    // 索引缓存 schema 不匹配（`01` §7 的 RD1004）：报它并【当作没有索引】，不要静默用旧缓存作答。
    public const string CacheSchemaMismatch = "RD1004";

    // 目标 mod 无法确定（`01` §4 退出码 5 的对应码；`01` §7 的 RD1xxx 表已补此行）
    public const string TargetModNotFound = "RD1009";
}

// ===== 第 3 期 Task 2 =====
/// <summary>
/// RD2001（XML 解析失败）的**归因**取值 —— 让报告能区分「你的文件坏了」与「工具读不了」。
///
/// ★ 为什么要单独把它写成常量而不是散在字符串里：`failed` 单独一个数字无法回答
///   「这些失败是我的 mod 有错，还是 RimDev 不认识这种编码」。第 3 期 Task 2 Step 6 要求
///   每一个失败都带 cause，并且全库 13 条**全部**是 <see cref="Encoding"/>（实测，见验收记录）。
/// </summary>
public static class ParseCause
{
    /// <summary>文件自身的字节编码与 XML 声明/默认编码不符（GBK/无 BOM 等）⇒ 是**内容**问题。</summary>
    public const string Encoding = "Encoding";

    /// <summary>良构性错误（标签不配对、非法字符出现在名字里等）⇒ 同样是**内容**问题，但与编码无关。</summary>
    public const string Structure = "Structure";

    /// <summary>
    /// 按 XmlException 的消息归因。判据只认「引擎自己说的那句话」（`Invalid character in the given encoding`）
    /// 与 .NET 的 DecoderFallback 异常名，不做启发式猜测 —— 猜错会把「结构坏了」误报成「编码问题」，
    /// 使用者会去改编码而永远修不好。
    /// </summary>
    public static string Classify(string? parseError)
    {
        if (string.IsNullOrEmpty(parseError)) return Structure;
        if (parseError.Contains("encoding", StringComparison.OrdinalIgnoreCase)) return Encoding;
        if (parseError.Contains("DecoderFallback", StringComparison.OrdinalIgnoreCase)) return Encoding;
        if (parseError.Contains("byte order mark", StringComparison.OrdinalIgnoreCase)) return Encoding;
        return Structure;
    }
}
