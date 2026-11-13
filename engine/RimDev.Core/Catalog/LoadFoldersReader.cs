using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;

namespace RimDev.Core.Catalog;

/// <summary>
/// 目录表的门面：<c>loadFolders.xml</c> → 「按引擎语义应该加载哪些目录」。
///
/// ★ 第 3 期 Task 1 **重写**。旧实现（只找 <c>v1.6</c>/<c>1.6</c> 两个键；找不到就 <c>Fallback(root, root\1.6)</c>；
///   忽略 <c>If*</c> 属性；按 XML 顺序而非倒序）有**三条已知错误路径**，按纪律直接删除，不保留「对照用」的旧代码：
///   <list type="number">
///   <item>有 <c>loadFolders</c> 但没有 1.6 键时返回**空数组**（不是 null）⇒ 消费方的 <c>?? Fallback</c> 不会兜底
///     ⇒ 该 mod **一个文件都不枚举**。实测这类 mod 有 49 个（其中 1 个 <c>loadFolders.xml</c> 本身编码损坏）。
///     计划 §0.4.5 的对照表把它写成「回退到 根 + &lt;mod&gt;\1.6」，与代码实际行为不符。</item>
///   <item>忽略 <c>IfModActive</c> / <c>IfModActiveAll</c> / <c>IfModNotActive</c> ⇒ 多算目录
///     （Combat Extended 一个 mod 就多算 3,572 个文件）。</item>
///   <item>目录顺序按 XML 顺序 ⇒ 与引擎的**倒序追加**相反，遮蔽胜者会反过来。</item>
///   </list>
/// 真实实现见 <see cref="LoadFolderResolver"/>（逐行对齐反编译源码）。
/// </summary>
public static class LoadFoldersReader
{
    /// <summary>工坊 mod 里两种拼写都见过（Windows 上大小写不敏感，等价）。</summary>
    public static string? FindLoadFolders(string modRoot) => LoadFolderResolver.FindLoadFoldersFile(modRoot);

    /// <summary>解析 <c>loadFolders.xml</c>（不解析目录是否存在的问题，那是 <see cref="LoadFolderCheck"/> 的活）。</summary>
    public static LoadFolderDocument Read(string modRoot) => LoadFolderResolver.Load(modRoot);

    /// <summary>
    /// 引擎等价解析：五步分支（精确版本 → 向下取版本 → default → 回退 → 空），
    /// 并把 <c>If*</c> 条件按**当前启用集合**裁决。
    /// </summary>
    public static ResolvedFolders Resolve(
        string modRoot,
        string packageId,
        EngineVersionInfo version,
        IReadOnlyCollection<string> activePackageIds,
        IReadOnlyList<string>? supportedVersions = null)
    {
        var doc = LoadFolderResolver.Load(modRoot);
        return LoadFolderResolver.Resolve(modRoot, packageId, doc, version,
            LoadFolderResolver.ActiveMatcher(activePackageIds), supportedVersions);
    }

    /// <summary>文件发现（引擎等价）——本类型不再自己枚举文件，统一走 <see cref="DefFileEnumerator"/>。</summary>
    public static IReadOnlyList<EnumeratedXmlFile> EnumerateFiles(IReadOnlyList<string> foldersDescending) =>
        DefFileEnumerator.Enumerate(foldersDescending);
}
