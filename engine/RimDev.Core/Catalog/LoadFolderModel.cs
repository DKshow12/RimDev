using RimDev.Core.Diagnostics;

namespace RimDev.Core.Catalog;

/// <summary>
/// 解析出的加载文件夹来自哪一条分支 —— 对应 <c>Verse.ModContentPack.InitLoadFolders</c> 的五步。
/// 打印它是为了让人能一眼看出「这个 mod 走的是哪条路」，因为分支不同 ⇒ 语义完全不同。
/// </summary>
public enum ResolutionOrigin
{
    /// <summary>第 1 步：<c>LoadFoldersForVersion(VersionControl.CurrentVersionString)</c> 精确命中且非空。
    /// ★ 实测 CurrentVersionString = "1.6.4871"，所以只有写 <c>&lt;v1.6.4871&gt;</c> 的 mod 会走这里（现实中几乎为零）。</summary>
    ExactVersion,

    /// <summary>第 2 步：向下取版本（`orderby x descending` 后第一个 ≤ 当前版本的键）。**正常 mod 都走这条**。</summary>
    LowerVersion,

    /// <summary>第 3 步：<c>default</c> 键。引擎只判「键存在」，不判是否为空。</summary>
    Default,

    /// <summary>第 4 步：回退分支（没有 loadFolders，或一个可用版本键都没有）：
    /// <c>&lt;mod&gt;\&lt;CurrentVersionStringWithoutBuild&gt;</c> → 否则版本目录 → <c>Common</c> → 永远最后加 mod 根。</summary>
    FallbackDir,

    /// <summary>解析结果为空（引擎的 <c>foldersToLoadDescendingOrder.Count == 0</c> ⇒ 该 mod 什么都不加载）。</summary>
    Empty
}

/// <summary>
/// 一条 <c>&lt;li&gt;</c>，等价于引擎的 <c>Verse.LoadFolder</c>。
/// <paramref name="FolderName"/> 已按 <c>LoadDataFromXmlCustom</c> 归一：<c>/</c> 与 <c>\</c> → <c>""</c>（表示 mod 根），
/// 其余把 <c>AltDirectorySeparatorChar</c> 换成 <c>DirectorySeparatorChar</c>；**不 Trim**（引擎也不 Trim）。
/// </summary>
public sealed record LoadFolderEntry(
    string FolderName,
    string[]? Any,      // IfModActive      —— 任一启用即可
    string[]? All,      // IfModActiveAll   —— 全部启用
    string[]? Not,      // IfModNotActive   —— 任一都不启用
    int Line);

/// <summary>一个版本块（<c>&lt;v1.6&gt;</c> / <c>&lt;1.6&gt;</c> / <c>&lt;default&gt;</c>），键已归一（ToLower + 剥前导 v）。</summary>
public sealed record LoadFolderBlock(string Key, string ElementName, int Line, IReadOnlyList<LoadFolderEntry> Entries);

/// <summary>
/// 一份 <c>LoadFolders.xml</c> 的解析结果。
/// ★ 同名键出现两次时会**合并**进同一个键（引擎是
///   `if (!foldersForVersion.ContainsKey(text)) Add(text, new List&lt;LoadFolder&gt;()); foldersForVersion[text].Add(...)`），
///   所以 <see cref="BlockFor"/> 把同键的所有块按声明顺序拼起来；<see cref="Blocks"/> 仍保留逐块视图（含行号）。
/// </summary>
public sealed class LoadFolderDocument
{
    private readonly Dictionary<string, List<LoadFolderEntry>> _byKey;
    private readonly Dictionary<string, int> _firstLineByKey;

    private LoadFolderDocument(string rootPath, string? filePath, string? parseError,
        IReadOnlyList<LoadFolderBlock> blocks)
    {
        RootPath = rootPath;
        FilePath = filePath;
        ParseError = parseError;
        Blocks = blocks;

        _byKey = new Dictionary<string, List<LoadFolderEntry>>(StringComparer.Ordinal);
        _firstLineByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var b in blocks)
        {
            if (!_byKey.TryGetValue(b.Key, out var list))
            {
                _byKey[b.Key] = list = new List<LoadFolderEntry>();
                _firstLineByKey[b.Key] = b.Line;
            }
            list.AddRange(b.Entries);
        }
        DefinedVersions = _byKey.Keys.ToList();
    }

    public string RootPath { get; }
    public string? FilePath { get; }
    public string? ParseError { get; }

    /// <summary>文件是否存在（存在但解析失败时也有 FilePath，ParseError 非空）。</summary>
    public bool HasFile => FilePath is not null;

    /// <summary>逐块视图（声明顺序，含行号）。</summary>
    public IReadOnlyList<LoadFolderBlock> Blocks { get; }

    /// <summary>键列表（声明顺序；同名键只出现一次）。引擎的 <c>DefinedVersions()</c> 等价物。</summary>
    public IReadOnlyList<string> DefinedVersions { get; }

    /// <summary>键对应的条目。<b>键不存在返回 null</b>（= 引擎 `FoldersForVersion` 无命中返回 null）；
    /// 键存在但一个 <c>&lt;li&gt;</c> 都没有则返回**空列表**（= 引擎的空 List，这个区别决定 RD2024）。</summary>
    public IReadOnlyList<LoadFolderEntry>? BlockFor(string key) =>
        _byKey.TryGetValue(key, out var list) ? list : null;

    public int? FirstLineOf(string key) =>
        _firstLineByKey.TryGetValue(key, out var line) ? line : null;

    public static LoadFolderDocument None(string rootPath) =>
        new(rootPath, null, null, Array.Empty<LoadFolderBlock>());

    public static LoadFolderDocument Failed(string rootPath, string filePath, string error) =>
        new(rootPath, filePath, error, Array.Empty<LoadFolderBlock>());

    public static LoadFolderDocument Parsed(string rootPath, string filePath, IReadOnlyList<LoadFolderBlock> blocks) =>
        new(rootPath, filePath, null, blocks);
}

/// <summary>
/// 一次「解析出哪些目录」的完整结果 —— 对应引擎的 <c>ModContentPack.foldersToLoadDescendingOrder</c> 加元信息。
/// <paramref name="DescendingOrder"/> **不做存在性过滤**（引擎的 <c>AddFolders</c> 也不过滤）：
/// 不存在的目录照样进列表，由文件枚举时 `if (!di.Exists) continue;` 跳过 —— 这样 RD2012 才有依据。
/// </summary>
public sealed record ResolvedFolders(
    IReadOnlyList<string> DescendingOrder,
    ResolutionOrigin Origin,
    string? MatchedVersionKey,
    IReadOnlyList<LoadFolderEntry> Entries,
    IReadOnlyList<Diagnostic> Issues)
{
    public static ResolvedFolders Empty(ResolutionOrigin origin) =>
        new(Array.Empty<string>(), origin, null, Array.Empty<LoadFolderEntry>(), Array.Empty<Diagnostic>());

    /// <summary>解析结果为空 ⇒ 该 mod 的 def 完全不会被加载（RD2024 的判据）。</summary>
    public bool IsEmpty => DescendingOrder.Count == 0;
}
