namespace RimDev.Core.Catalog;

public sealed record ModInfo(
    string PackageId,
    string DisplayName,
    string RootPath,
    ModSource Source,
    string? PublishedFileId,
    string[] SupportedVersions,
    string[] LoadFolders,          // = Resolution.DescendingOrder（引擎语义的加载目录，**不做存在性过滤**）
    string[] Dependencies,
    string[] LoadAfter,
    string[] LoadBefore,
    bool IsActive,
    bool HasAssemblies,
    string? AboutPath,
    string? AboutError,
    // ★ 第 3 期 Task 1 追加：引擎等价的 loadFolders 求解结果（Origin / MatchedVersionKey / DescendingOrder / Issues）。
    //   加在末尾且可空 = 向后兼容（现有位置参数调用与 record 解构都不受影响）。
    //   ★ 只有 <see cref="ModCatalog.Build"/> 造出来的 ModInfo 才有值；别处 new 出来的为 null，
    //   消费方（LoadFolderCheck / FilesCommand）必须容忍 null。
    ResolvedFolders? Resolution = null,
    // ★★ Task 26：About.xml 的三个附加特性。全部**加在末尾且可空**，旧的位置参数调用不受影响。
    //   实测（4,080 个 About.xml）真实使用量：
    //     IncompatibleWith        302   ← 最常用，没它就检不出冲突
    //     DependenciesByVersion   107   ← 没它会把 1.7 版依赖全漏掉
    //     AlternativePackageIds     2   ← 没它会把「写了备选名」判成缺前置
    IReadOnlyDictionary<string, string[]>? DependenciesByVersion = null,
    string[]? AlternativePackageIds = null,
    string[]? IncompatibleWith = null)
{
    /// <summary>用于报告展示的短名。</summary>
    public string ShortName => string.IsNullOrEmpty(DisplayName) ? PackageId : DisplayName;

    /// <summary>诊断里用的定位串，如 <c>ThreatFleet</c> 或 <c>workshop\3673408015</c>。</summary>
    public string Locator => Path.GetFileName(RootPath.TrimEnd('\\', '/'));
}
