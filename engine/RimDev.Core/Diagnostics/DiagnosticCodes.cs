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
    // RD1xxx 补充
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
