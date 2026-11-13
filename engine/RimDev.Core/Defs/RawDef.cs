namespace RimDev.Core.Defs;

/// <summary>
/// 一个 def 的「头」信息——只保留结构定位所需的最小集，不保留 XML 原文
/// （原文留给第 3 期的 sim/引用解析按需重读）。
/// </summary>
public sealed record RawDef(
    string DefType,          // 元素名，如 "ThingDef"
    string? DefName,         // <defName>
    string? NameAttr,        // Name="..."（抽象基类用）
    string? ParentName,      // ParentName="..."
    string? MayRequire,      // MayRequire="..."
    bool IsAbstract,         // Abstract="True" 或 有 NameAttr 且无 DefName
    string ModPackageId,
    string FileAbs,
    string FileRelToMod,
    int Line)               // 1-based
{
    /// <summary>可用于被引用的名字（defName 优先，其次 Name=）。</summary>
    public string? ReferableName => !string.IsNullOrEmpty(DefName) ? DefName : NameAttr;
}
