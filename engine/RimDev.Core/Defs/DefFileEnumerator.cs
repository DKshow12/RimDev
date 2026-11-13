namespace RimDev.Core.Defs;

/// <summary>一个被枚举到的 XML 文件（引擎 <c>LoadableXmlAsset</c> 的输入）。</summary>
public sealed record EnumeratedXmlFile(
    string FileAbs,
    string FolderRoot,      // 该文件是在哪个「加载文件夹」下被枚举到的（= foldersToLoadDescendingOrder[i]）
    int FolderIndex,        // 该文件夹在列表里的下标（0 = 优先级最高）
    string RelativeKey);    // ★ 去重键 = 相对该文件夹根的路径（引擎字典的 key）

/// <summary>
/// 文件发现（引擎等价）：<c>Verse.DirectXmlLoader.XmlAssetsInModFolder</c>。
///
/// <code>
/// Dictionary&lt;string, FileInfo&gt; dict = new();
/// for (int i = 0; i &lt; list.Count; i++) {
///     var di = new DirectoryInfo(Path.Combine(list[i], folderPath));   // folderPath = "Defs" / "Patches"
///     if (!di.Exists) continue;
///     foreach (var f in di.GetFiles("*.xml", SearchOption.AllDirectories))
///         if (!f.Name.StartsWith("._") &amp;&amp; !f.Name.StartsWith("."))
///             dict.TryAdd(f.FullName.Substring(list[i].Length + 1), f);   // ★ 键 = 相对【该文件夹根】的路径
/// }
/// </code>
///
/// 三条必须记住的语义：
/// ① 去重是 <c>TryAdd</c> **先到先得** ⇒ <c>list[0]</c> 赢（而 <c>list[0]</c> 是 <c>&lt;li&gt;</c> 里**最后**声明的那条）；
/// ② 键是**相对文件夹根**的路径 ⇒ <c>&lt;mod&gt;\1.6\Defs\A.xml</c> 与 <c>&lt;mod&gt;\Defs\A.xml</c> 是两个键，
///    **会同时被加载**（同一个 mod 出现两遍同名 def）；
/// ③ 字典用的是默认比较器（<b>ordinal</b>，大小写敏感）——与引擎的 <c>Dictionary&lt;string, FileInfo&gt;</c> 一致。
/// </summary>
public static class DefFileEnumerator
{
    /// <summary>引擎只认这两个子目录名（<c>XmlAssetsInModFolder(mod, "Defs")</c> / <c>("Patches")</c>）。</summary>
    public static readonly string[] KnownSubfolders = { "Defs", "Patches" };

    /// <summary>枚举一个 mod 的全部 def/patches 文件（按引擎语义去重）。</summary>
    public static IReadOnlyList<EnumeratedXmlFile> Enumerate(IReadOnlyList<string> foldersDescending)
    {
        var dict = new Dictionary<string, EnumeratedXmlFile>(StringComparer.Ordinal);
        for (var i = 0; i < foldersDescending.Count; i++)
        {
            var folder = foldersDescending[i];
            foreach (var sub in KnownSubfolders)
            {
                var di = Path.Combine(folder, sub);
                if (!Directory.Exists(di)) continue;

                // ★ 用 GetFiles（materialize）而不是 EnumerateFiles：后者是惰性的，
                //   权限异常会在 foreach 里抛出而绕过这里的 try/catch。
                string[] files;
                try { files = Directory.GetFiles(di, "*.xml", SearchOption.AllDirectories); }
                catch { continue; }

                foreach (var full in files)
                {
                    var name = Path.GetFileName(full);
                    // ★ 与引擎一致：先判 "._"，再判 "."。两条都在，任何以点开头的文件都会被跳过。
                    if (name.StartsWith("._", StringComparison.Ordinal)) continue;
                    if (name.StartsWith(".", StringComparison.Ordinal)) continue;

                    // ★ 引擎原样是 fullName.Substring(folder.Length + 1) —— 保留这一写法（含它在
                    //   folder 以分隔符结尾时的偏移行为），否则就不是「逐行等价」了。
                    if (full.Length <= folder.Length + 1) continue;
                    var key = full.Substring(folder.Length + 1);
                    dict.TryAdd(key, new EnumeratedXmlFile(full, folder, i, key));
                }
            }
        }
        return dict.Values.ToList();
    }

    /// <summary>把「mod → 加载目录列表」摊平成扫描输入（<see cref="DefScanner.CollectFiles"/> 的唯一实现）。</summary>
    public static List<(string FileAbs, string ModPackageId, string ModRoot)> Collect(
        IEnumerable<(string PackageId, string RootPath, IReadOnlyList<string> LoadFolders)> mods)
    {
        var list = new List<(string FileAbs, string ModPackageId, string ModRoot)>();
        foreach (var m in mods)
        {
            foreach (var f in Enumerate(m.LoadFolders))
                list.Add((f.FileAbs, m.PackageId, m.RootPath));
        }
        return list;
    }
}
