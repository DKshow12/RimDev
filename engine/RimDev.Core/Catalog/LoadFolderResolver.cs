using System.Xml;
using RimDev.Core.Checks;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;

namespace RimDev.Core.Catalog;

/// <summary>
/// <c>loadFolders.xml</c> 的引擎等价解析与「加载目录」求解。
///
/// ★ 权威来源（2026-10-14 反编译，mvid 61e4173561894da49d210260257b5097，与计划 §0.4 同源）：
///   · <c>Verse.ModContentPack.InitLoadFolders()</c>      —— 五步分支 + 回退
///   · <c>Verse.ModLoadFolders.LoadDataFromXmlCustom()</c> —— 版本块/li 解析
///   · <c>RimWorld.VersionControl</c>                      —— TryParseVersionString / VersionFromString / IsWellFormedVersionString
///   · <c>Verse.LoadFolder.ShouldLoad</c>                  —— 三个 If* 属性的合取
///
/// ★ 与计划 §0.4 的**实测差异**（已按反编译实现，见 Report）：
///   1. 类型在 <c>RimWorld.VersionControl</c>，不是 <c>Verse.VersionControl</c>；
///   2. 回退分支「取 mod 根下版本号最大者」是**错的**：真实代码是升序遍历 + 条件
///      `(item &gt; version || version &gt; CurrentVersion) &amp;&amp; (item &lt;= CurrentVersion || version.Major == 0)`
///      ⇒ 实际语义 = 「≤ 当前版本的最大者；若一个都没有，则取**最小**者」（见 <see cref="PickFallbackVersionDir"/>）；
///   3. 版本目录名只用 <c>TryParseVersionString</c> 解析（只取 major.minor，忽略第三段），
///      加进列表时用的是**归一化后的** <c>version.ToString()</c>，不是磁盘上的目录名；
///   4. 回退分支的「版本目录」判定用的是 <c>CurrentVersionStringWithoutBuild</c>="1.6" 存在与否，与 §0.4 一致。
/// </summary>
public static class LoadFolderResolver
{
    public const string DefaultVersionName = "default";
    public const string CommonFolderName = "Common";

    // ───────────────────────── 文件查找 / 解析 ─────────────────────────

    /// <summary>引擎用 <c>GenFile.ResolveCaseInsensitiveFilePath(RootDir, "LoadFolders.xml")</c>；
    /// 在大小写不敏感的 Windows 文件系统上两种拼写等价，两种都试只为可读性。</summary>
    public static string? FindLoadFoldersFile(string modRoot)
    {
        foreach (var name in new[] { "LoadFolders.xml", "loadFolders.xml" })
        {
            var p = Path.Combine(modRoot, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>
    /// 解析 <c>loadFolders.xml</c>。语义严格对齐 <c>ModLoadFolders.LoadDataFromXmlCustom</c>：
    ///   · 顶层子节点是版本块；只跳过 <c>XmlComment</c>；键 = <c>Name.ToLower()</c> 再去掉前导 <c>v</c>；
    ///   · 同名键**合并**到同一个 List；
    ///   · 版本块的每个**元素**子节点都是一条 LoadFolder（引擎不检查元素名是不是 <c>li</c>）；
    ///   · 三个属性按 <c>,</c> 切分后逐项 <c>Trim()</c>；<c>InnerText</c> 为 <c>/</c> 或 <c>\</c> → folderName = <c>""</c>；
    ///   · 其余把 <c>AltDirectorySeparatorChar</c> 换成 <c>DirectorySeparatorChar</c>（仅当两者不同）；
    ///   · **不 Trim** folderName。
    /// 解析失败时返回 <see cref="LoadFolderDocument.Failed"/>：引擎的 <c>ItemFromXmlFile</c> 会 catch 全部异常并
    /// 返回一个**空的 ModLoadFolders** ⇒ <c>DefinedVersions().Count == 0</c> ⇒ 走回退分支（这一点必须复刻）。
    /// </summary>
    public static LoadFolderDocument Load(string modRoot)
    {
        var path = FindLoadFoldersFile(modRoot);
        if (path is null) return LoadFolderDocument.None(modRoot);

        try
        {
            return ParseFile(modRoot, path);
        }
        catch (Exception ex)
        {
            return LoadFolderDocument.Failed(modRoot, path, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static LoadFolderDocument ParseFile(string modRoot, string path)
    {
        var settings = new XmlReaderSettings
        {
            IgnoreComments = true,
            // ★ 与引擎的 XmlDocument（PreserveWhitespace 默认 false，空白文本节点被剥掉）对齐：
            //   IgnoreWhitespace=true 只丢「纯空白」文本节点，`<li> 1.6 </li>` 里的空格仍在。
            IgnoreWhitespace = true,
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
        };

        var blocks = new List<LoadFolderBlock>();
        using var stream = File.OpenRead(path);
        using var reader = XmlReader.Create(stream, settings);
        var li = reader as IXmlLineInfo;

        int Line() => li is not null && li.HasLineInfo() ? li.LineNumber : 0;

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;

            // 版本块：深度 1 的任意节点（引擎不区分元素/文本，只跳过注释）
            if (reader.Depth != 1) continue;
            var elementName = reader.Name;
            var key = elementName.ToLowerInvariant();
            if (key.StartsWith("v", StringComparison.Ordinal)) key = key.Substring(1);
            var blockLine = Line();

            var entries = new List<LoadFolderEntry>();
            if (!reader.IsEmptyElement)
            {
                // 逐个读版本块的子元素。**不使用 ReadElementContentAsString**：
                // 它在元素是最后一个子节点时会把父结束标签一并消费（详见 DefScanner 的同类注释），
                // 这里手工累加文本，行为与引擎的 XmlNode.InnerText 一致（含后代文本）。
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == 1) break;
                    if (reader.NodeType != XmlNodeType.Element || reader.Depth != 2) continue;

                    var entryLine = Line();
                    var any = SplitAttr(reader.GetAttribute("IfModActive"));
                    var all = SplitAttr(reader.GetAttribute("IfModActiveAll"));
                    var not = SplitAttr(reader.GetAttribute("IfModNotActive"));

                    var inner = "";
                    if (!reader.IsEmptyElement)
                    {
                        var depth = reader.Depth;
                        while (reader.Read())
                        {
                            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
                            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA
                                or XmlNodeType.SignificantWhitespace)
                                inner += reader.Value;
                        }
                    }

                    var name = inner;
                    if (name == "/" || name == "\\")
                    {
                        name = "";
                    }
                    else if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
                    {
                        name = name.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                    }

                    entries.Add(new LoadFolderEntry(name, any, all, not, entryLine));
                }
            }

            blocks.Add(new LoadFolderBlock(key, elementName, blockLine, entries));
        }

        return LoadFolderDocument.Parsed(modRoot, path, blocks);
    }

    private static string[]? SplitAttr(string? value) =>
        value is null ? null : value.Split(',').Select(s => s.Trim()).ToArray();

    // ───────────────────────── 引擎的三个版本字符串 helper ─────────────────────────

    /// <summary><c>VersionControl.TryParseVersionString</c> 逐行转写：按 <c>.</c> 切，要求 ≥ 2 段，
    /// 前两段是 ≥ 0 的整数，**只取前两段**（第三段及以后被忽略）⇒ <c>Version(major, minor)</c>。</summary>
    public static bool TryParseVersionString(string? str, out Version version)
    {
        version = new Version(0, 0);
        if (str is null) return false;
        var parts = str.Split('.');
        if (parts.Length < 2) return false;
        for (var i = 0; i < 2; i++)
        {
            if (!int.TryParse(parts[i], out var v) || v < 0) return false;
        }
        version = new Version(int.Parse(parts[0]), int.Parse(parts[1]));
        return true;
    }

    /// <summary><c>VersionControl.IsWellFormedVersionString</c>：**恰好两段**且都是 ≥ 0 的整数。
    /// 注意 <c>"1.6.1"</c> 在这里是**非法**的（这正是 RD2020 的判据，比「不含点」严格得多）。</summary>
    public static bool IsWellFormedVersionString(string str)
    {
        var parts = str.Split('.');
        if (parts.Length != 2) return false;
        for (var i = 0; i < 2; i++)
        {
            if (!int.TryParse(parts[i], out var v) || v < 0) return false;
        }
        return true;
    }

    /// <summary><c>VersionControl.VersionFromString</c>：最多 3 段，每段必须是 ≥ 0 的整数。
    /// 引擎在非法输入时**抛 ArgumentException**（调用点用 try/catch 吃掉）——这里返回 false 等价表达。</summary>
    public static bool TryVersionFromString(string? str, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrEmpty(str)) return false;
        var parts = str.Split('.');
        if (parts.Length > 3) return false;
        var major = 0; var minor = 0; var build = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out var v) || v < 0) return false;
            switch (i)
            {
                case 0: major = v; break;
                case 1: minor = v; break;
                case 2: build = v; break;
            }
        }
        version = new Version(major, minor, build);
        return true;
    }

    // ───────────────────────── 向下取版本（Q4） ─────────────────────────

    /// <summary>
    /// 逐行照抄引擎的 LINQ：
    /// <code>
    /// (from x in DefinedVersions()
    ///  where x != "default" &amp;&amp; !x.NullOrEmpty() &amp;&amp; x.Contains(".")
    ///     &amp;&amp; VersionFromString(x) &lt;= VersionControl.CurrentVersion
    ///  orderby x descending select x).FirstOrDefault()
    /// </code>
    ///
    /// ★ <c>orderby</c> 用的是 <see cref="Comparer{T}.Default"/>（即 <c>string.CompareTo</c>）——**字符串序**，不是版本序。
    ///   与 RD2022 的关系：`"1.5" &gt; "1.10"`、`"1.5.3" &gt; "1.5.10"`，所以同时定义 `1.5` 与 `1.10` 的 mod 会拿到 **1.5**。
    ///
    ///   但要注意**本机（当前版本 1.6.4871.591）**下 `"1.10"` 会先被 `Version(1,10,0) &lt;= 1.6.4871.591` 判否而排除，
    ///   所以 `["1.5","1.10"]` 这个例子里「字符串序」并不是决定因素（两种序都会选 1.5）。
    ///   真正能区分字符串序与版本序的是**两个都 ≤ 当前版本、且字符串序与版本序相反**的键，
    ///   例如 `["1.5.3","1.5.10"]` → 选 `"1.5.3"`（版本序会选 1.5.10）。自测里两条都有。
    /// </summary>
    public static string? PickVersionKey(IEnumerable<string> defined, Version current)
    {
        return defined
            .Where(x => x != DefaultVersionName && !string.IsNullOrEmpty(x) && x.Contains('.'))
            .Where(x => TryVersionFromString(x, out var v) && v <= current)
            .OrderByDescending(x => x, Comparer<string>.Default)
            .FirstOrDefault();
    }

    // ───────────────────────── 回退分支的版本目录（★ 与 §0.4 的差异 2/3） ─────────────────────────

    /// <summary>
    /// 回退分支里「取 mod 根下版本目录」的**引擎精确**语义（§0.4.1 把它简化成了「最大者」，是错的）：
    /// <code>
    /// Version version = new Version(0, 0);
    /// 升序遍历可解析目录名（TryParseVersionString ⇒ 只看 major.minor）:
    ///     if ((item &gt; version || version &gt; CurrentVersion) &amp;&amp; (item &lt;= CurrentVersion || version.Major == 0))
    ///         version = item;
    /// if (version.Major &gt; 0) add(Path.Combine(RootDir, version.ToString()));
    /// </code>
    /// 化简后的行为：**≤ 当前版本的最大者**；若一个都没有，则取**最小**者（因为首项在 version.Major==0 时必然被接受，
    /// 之后 `version &gt; CurrentVersion` 成立而使 `item &lt;= CurrentVersion` 成为唯一通道 ⇒ 再无替换）。
    /// 返回值是归一化后的 <c>version.ToString()</c>（如 "1.5"），**不是磁盘目录名**。
    /// </summary>
    public static Version? PickFallbackVersionDir(string modRoot, Version current)
    {
        var versions = new List<Version>();
        try
        {
            foreach (var dir in Directory.GetDirectories(modRoot))
            {
                if (TryParseVersionString(Path.GetFileName(dir), out var v)) versions.Add(v);
            }
        }
        catch
        {
            return null;   // 目录不可枚举 ⇒ 引擎这里会抛（外部 try 会整块失败），我们保守地当作没有版本目录
        }

        versions.Sort();
        var pick = new Version(0, 0);
        foreach (var item in versions)
        {
            if ((item > pick || pick > current) && (item <= current || pick.Major == 0))
                pick = item;
        }
        return pick.Major > 0 ? pick : null;
    }

    // ───────────────────────── ShouldLoad（三个条件合取） ─────────────────────────

    /// <summary>
    /// <c>Verse.LoadFolder.ShouldLoad</c> 的等价实现：
    /// <code>
    /// (Any.NullOrEmpty() || ModLister.AnyModActiveNoSuffix(Any))
    /// &amp;&amp; (All.NullOrEmpty() || ModLister.AllModsActiveNoSuffix(All))
    /// &amp;&amp; (Not.NullOrEmpty() || !ModLister.AnyModActiveNoSuffix(Not))
    /// </code>
    /// 注意 <c>NullOrEmpty()</c> 把「属性缺失（null）」与「空列表」同等对待；
    /// 而 <c>IfModActive=""</c> 不是空列表（Split(',') 得到 <c>[""]</c>）⇒ 不满足。
    /// 匹配用 packageId **小写后忽略大小写**比较（<c>GetActiveModWithIdentifier(id.Trim(), ignorePostfix: true)</c>
    /// 查的是 <c>packageIdLowerCase</c> 字典，即**不含 steam 后缀**的声明 id）。
    /// </summary>
    public static bool ShouldLoad(LoadFolderEntry entry, Func<string, bool> isModActive)
    {
        bool AnyHit(string[]? ids) => ids is null || ids.Length == 0 || ids.Any(isModActive);
        bool AllHit(string[]? ids) => ids is null || ids.Length == 0 || ids.All(isModActive);

        if (!AnyHit(entry.Any)) return false;
        if (!AllHit(entry.All)) return false;
        if (entry.Not is { Length: > 0 } && entry.Not.Any(isModActive)) return false;
        return true;
    }

    // ───────────────────────── 主解析：五步分支 ─────────────────────────

    /// <summary>按 <c>Verse.ModContentPack.InitLoadFolders()</c> 的五步求加载目录（**不过滤存在性**）。</summary>
    public static ResolvedFolders Resolve(
        string modRoot,
        string packageId,
        LoadFolderDocument doc,
        EngineVersionInfo version,
        Func<string, bool> isModActive,
        IReadOnlyList<string>? supportedVersions = null)
    {
        var order = new List<string>();
        var used = new List<LoadFolderEntry>();
        ResolutionOrigin origin;
        string? matchedKey = null;

        void AddFolders(IReadOnlyList<LoadFolderEntry> folders)
        {
            // ★★ 倒序遍历：foldersToLoadDescendingOrder[0] 优先级最高，而 AddFolders 从尾部往前加
            //    ⇒ `<li>` 列表里越靠后的条目优先级越高（反直觉，Q2 就是钉这条）。
            for (var i = folders.Count - 1; i >= 0; i--)
            {
                if (!ShouldLoad(folders[i], isModActive)) continue;
                order.Add(Path.Combine(modRoot, folders[i].FolderName));
                used.Add(folders[i]);
            }
        }

        // 引擎的条件是 `m?.loadFolders != null && m.loadFolders.DefinedVersions().Count > 0`
        // —— 有文件但解析失败（DefinedVersions 为空）时**不**进这个 if（等价于没有 loadFolders）。
        if (doc.HasFile && doc.DefinedVersions.Count > 0)
        {
            var exact = doc.BlockFor(version.CurrentVersionString);
            if (exact is { Count: > 0 })
            {
                AddFolders(exact);
                origin = ResolutionOrigin.ExactVersion;
                matchedKey = version.CurrentVersionString;
            }
            else
            {
                var best = PickVersionKey(doc.DefinedVersions, version.CurrentVersion);
                IReadOnlyList<LoadFolderEntry>? lower = best is null ? null : doc.BlockFor(best);
                if (lower is not null)
                {
                    AddFolders(lower);
                    origin = ResolutionOrigin.LowerVersion;
                    matchedKey = best;
                }
                else
                {
                    var def = doc.BlockFor(DefaultVersionName);
                    if (def is not null)
                    {
                        // ★ 引擎这里只判 != null（不判 Count>0）⇒ 空 <default> 块 = 该 mod 什么都不加载。
                        AddFolders(def);
                        origin = ResolutionOrigin.Default;
                        matchedKey = DefaultVersionName;
                    }
                    else
                    {
                        return Fallback(modRoot, doc, version, packageId, supportedVersions);
                    }
                }
            }

            var resolved = new ResolvedFolders(order, order.Count == 0 ? ResolutionOrigin.Empty : origin,
                matchedKey, used, Array.Empty<Diagnostic>());
            return LoadFolderCheck.AttachIssues(resolved, modRoot, packageId, doc, supportedVersions);
        }

        // ── 回退分支 ──
        return Fallback(modRoot, doc, version, packageId, supportedVersions);
    }

    private static ResolvedFolders Fallback(
        string modRoot,
        LoadFolderDocument doc,
        EngineVersionInfo version,
        string packageId,
        IReadOnlyList<string>? supportedVersions)
    {
        var order = new List<string>();

        // 引擎：`if (foldersToLoadDescendingOrder.Count != 0) return;` —— 只有 loadFolders 分支的
        // AddFolders 什么都不加时才可能非零，而那条路径已经在上面 return 了，因此这条判据在此恒假。
        // 保留注释以对照反编译源码。

        var versioned = Path.Combine(modRoot, version.CurrentVersionStringWithoutBuild);
        if (Directory.Exists(versioned))
        {
            order.Add(versioned);
        }
        else
        {
            var pick = PickFallbackVersionDir(modRoot, version.CurrentVersion);
            if (pick is not null) order.Add(Path.Combine(modRoot, pick.ToString()));
        }

        var common = Path.Combine(modRoot, CommonFolderName);
        if (Directory.Exists(common)) order.Add(common);

        // ★ 永远最后加 mod 根（回退分支下根目录优先级最低；而 loadFolders 分支根本不自动加根）
        order.Add(modRoot);

        var resolved = new ResolvedFolders(order, ResolutionOrigin.FallbackDir, null,
            Array.Empty<LoadFolderEntry>(), Array.Empty<Diagnostic>());
        return LoadFolderCheck.AttachIssues(resolved, modRoot, packageId, doc, supportedVersions);
    }

    /// <summary>把 <c>ModInfo</c> 的字段摊平成一次解析（供 ModCatalog 使用）。</summary>
    public static ResolvedFolders ResolveForMod(
        ModInfo mod, EngineVersionInfo version, Func<string, bool> isModActive, LoadFolderDocument doc) =>
        Resolve(mod.RootPath, mod.PackageId, doc, version, isModActive, mod.SupportedVersions);

    /// <summary>由 packageId 集合构造 ShouldLoad 用的匹配器（小写 + Trim，与引擎的 packageIdLowerCase 字典一致）。</summary>
    public static Func<string, bool> ActiveMatcher(IEnumerable<string> activePackageIds)
    {
        var set = new HashSet<string>(
            activePackageIds.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().ToLowerInvariant()),
            StringComparer.Ordinal);
        return id => set.Contains((id ?? "").Trim().ToLowerInvariant());
    }
}
