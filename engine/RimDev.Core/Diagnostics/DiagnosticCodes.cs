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
}
