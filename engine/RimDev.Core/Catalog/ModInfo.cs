namespace RimDev.Core.Catalog;

public sealed record ModInfo(
    string PackageId,
    string DisplayName,
    string RootPath,
    ModSource Source,
    string? PublishedFileId,
    string[] SupportedVersions,
    string[] LoadFolders,          // 针对目标版本，已过滤存在的目录
    string[] Dependencies,
    string[] LoadAfter,
    string[] LoadBefore,
    bool IsActive,
    bool HasAssemblies,
    string? AboutPath,
    string? AboutError)
{
    /// <summary>用于报告展示的短名。</summary>
    public string ShortName => string.IsNullOrEmpty(DisplayName) ? PackageId : DisplayName;

    /// <summary>诊断里用的定位串，如 <c>ThreatFleet</c> 或 <c>workshop\3673408015</c>。</summary>
    public string Locator => Path.GetFileName(RootPath.TrimEnd('\\', '/'));
}
