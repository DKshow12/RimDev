using System.Xml;

namespace RimDev.Core.Catalog;

/// <summary>&lt;About.xml&gt; 里我们需要的字段。</summary>
public sealed record AboutInfo(
    string PackageId,
    string DisplayName,
    string[] SupportedVersions,
    string[] Dependencies,
    string[] LoadAfter,
    string[] LoadBefore,
    string? Error)
{
    // ★★ Task 26：`About.xml` 的四个特性。实测（4,080 个 About.xml）的真实使用量：
    //     incompatibleWith          302
    //     modDependenciesByVersion  107
    //     alternativePackageIds       2
    //     core 别名                   0（实测不存在，但它是合法写法，仍需兼容）
    //
    //   为什么用 空数组而不是 null：消费方（依赖检查 / 冲突检查）只想 `Any()` /
    //   `foreach`，不应该处处先判 null。

    /// <summary>
    /// ★ <c>&lt;modDependenciesByVersion&gt;</c> —— **按版本归并**的依赖。
    /// <para>形状：<c>&lt;modDependenciesByVersion&gt;&lt;v1.7&gt;&lt;li&gt;&lt;packageId&gt;…&lt;/packageId&gt;&lt;/li&gt;&lt;/v1.7&gt;…&lt;/modDependenciesByVersion&gt;</c></para>
    /// <para>键是版本字符串（<c>v1.1</c> / <c>v1.7</c> / <c>default</c> 等），值是该版本下的 packageId 列表。</para>
    /// </summary>
    public IReadOnlyDictionary<string, string[]> DependenciesByVersion { get; init; }
        = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ★ <c>&lt;alternativePackageIds&gt;</c> —— 该 mod 的**备选 packageId**。
    /// <para>依赖方写了备选名时，不能判成「缺前置」。实测只有 2 个 mod 用，
    /// 但用了就能避免假阳。</para>
    /// </summary>
    public string[] AlternativePackageIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// ★ <c>&lt;incompatibleWith&gt;</c> —— 声明与哪些 mod 不兼容。实测 **302 个 mod** 用了它。
    /// <para>这是中国环世界社区最常用的一个字段，没它就检不出冲突。</para>
    /// </summary>
    public string[] IncompatibleWith { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 读取 <c>About/About.xml</c>。
///
/// ★ 核心纪律：**只取 &lt;ModMetaData&gt; 的直接子元素**，不要用「文件里第一个 &lt;packageId&gt;」。
///
/// 实测反例（工坊 2538411704 Visual Exceptions）：它的 &lt;modDependencies&gt; 排在自身
/// &lt;packageId&gt; 之前，里面有 &lt;packageId&gt;brrainz.harmony&lt;/packageId&gt;。
/// 用正则取第一个会得到 brrainz.harmony —— 实测这会让 3984 个目录里 **257 个**认错 id
/// （7%），并让 brrainz.visualexceptions 这类 mod 完全解析不到目录。
/// XML 感知提取后：解析成功 3971 个、重复 0 个、17/17 启用 mod 全部定位。
/// 代价仅 1375 ms vs 1009 ms。
/// </summary>
public static class AboutXmlReader
{
    /// <summary>读取；失败时返回 Error 非空、PackageId 为空串的对象（不抛）。</summary>
    public static AboutInfo Read(string aboutPath)
    {
        try
        {
            var doc = new XmlDocument { XmlResolver = null };
            doc.Load(aboutPath);

            var root = doc.DocumentElement;
            if (root is null)
                return new AboutInfo("", "", Array.Empty<string>(), Array.Empty<string>(),
                    Array.Empty<string>(), Array.Empty<string>(), "About.xml 是空文档");

            // ★ 刻意【不检查根元素名】（IL 级实测证据）：
            //   全程序集扫描显示，Assembly-CSharp.dll 里含字面量 "ModMetaData" 的**方法数为 0**
            //   —— 若 RimWorld 比较过根元素名（如 xmlRoot.Name == "ModMetaData"），必然存在这个字面量。
            //   它只做 Path.Combine(modRoot, "About", "About.xml")（Verse.ModMetaData.Init 里有 "About"/"About.xml"
            //   两个字符串），然后把 DocumentElement 交给字段映射——而字段映射只按【子元素名】取值。
            //   后果：实测有 **26 个**工坊 mod 的根元素写成小写 <modMetaData>（如 2122675053 shavius.yayocombat3.zh），
            //   它们能被游戏正常加载。若在此做大小写敏感校验，会把这 26 个 mod 全部丢弃（3971 → 3945）。

            // ★ 关键：在**直接子节点**上取（XPath 的 child 轴）。
            //   不要用 GetElementsByTagName / XPath "//" / 正则——它们都会命中
            //   <modDependencies> 里的 <packageId>，实测会让 257 个 mod 认错 id。
            //   也刻意不嵌套 ReadSubtree()：子树嵌套的 depth 语义很脆，About.xml 只有 1–3 KB，
            //   用 DOM 直接读更简单也更不容易错。
            var packageId = DirectText(root, "packageId");
            var displayName = DirectText(root, "name");

            if (packageId.Length == 0)
                return new AboutInfo("", displayName, Array.Empty<string>(), Array.Empty<string>(),
                    Array.Empty<string>(), Array.Empty<string>(),
                    "About.xml 里没有直接的 <packageId> 子元素");

            return new AboutInfo(
                packageId,
                displayName,
                LiValues(root, "supportedVersions"),
                DependencyPackageIds(root),
                LiValues(root, "loadAfter"),
                LiValues(root, "loadBefore"),
                null)
            {
                // ★ Task 26：四个特性。用 init 而不是构造参数 ——
                //   保持原有 7 个位置参数不变，旧调用不受影响。
                DependenciesByVersion = DependenciesByVersion(root),
                AlternativePackageIds = AlternativePackageIds(root),
                IncompatibleWith = IncompatibleWith(root),
            };
        }
        catch (Exception ex)
        {
            return new AboutInfo("", "", Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<string>(), Array.Empty<string>(), $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>取直接子元素（child 轴）的文本，不存在返回空串。</summary>
    private static string DirectText(XmlElement root, string childName)
        => root.SelectSingleNode(childName)?.InnerText?.Trim() ?? "";

    /// <summary>取 &lt;container&gt;&lt;li&gt;值&lt;/li&gt;…&lt;/container&gt;。</summary>
    private static string[] LiValues(XmlElement root, string containerName)
    {
        var container = root.SelectSingleNode(containerName);
        var nodes = container?.SelectNodes("li");
        if (nodes is null) return Array.Empty<string>();
        var list = new List<string>();
        foreach (XmlNode n in nodes)
        {
            var v = (n.InnerText ?? "").Trim();
            if (v.Length > 0) list.Add(v);
        }
        return list.ToArray();
    }

    /// <summary>
    /// ★ Task 26：解析 <c>&lt;modDependenciesByVersion&gt;</c>。
    ///
    /// <para>形状（实测工坊 1201382956）：</para>
    /// <code>
    /// &lt;modDependenciesByVersion&gt;
    ///   &lt;v1.1&gt;
    ///     &lt;li&gt;&lt;packageId&gt;brrainz.harmony&lt;/packageId&gt; …&lt;/li&gt;
    ///   &lt;/v1.1&gt;
    ///   &lt;v1.7&gt; … &lt;/v1.7&gt;
    /// &lt;/modDependenciesByVersion&gt;
    /// </code>
    ///
    /// <para>★ 键名不固定（<c>v1.1</c> / <c>v1.7</c> / <c>default</c> 都见过），
    /// 所以**不能硬编码版本列表**，必须遍历全部直接子元素。</para>
    ///
    /// <para>实测使用量：4,080 个 About.xml 里有 <b>107</b> 个用它。
    /// 没它就会把 1.7 版的依赖全漏掉。</para>
    /// </summary>
    private static Dictionary<string, string[]> DependenciesByVersion(XmlElement root)
    {
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var container = root.SelectSingleNode("modDependenciesByVersion");
        if (container is null) return result;

        foreach (XmlNode versionNode in container.ChildNodes)
        {
            if (versionNode is not XmlElement versionEl) continue;
            var ids = new List<string>();
            foreach (XmlNode li in versionEl.SelectNodes("li")!)
            {
                // 两种形状都见过：&lt;li&gt;&lt;packageId&gt;x&lt;/packageId&gt;&lt;/li&gt; 与 &lt;li&gt;x&lt;/li&gt;
                var v = li.SelectSingleNode("packageId")?.InnerText?.Trim();
                if (string.IsNullOrEmpty(v)) v = (li.InnerText ?? "").Trim();
                if (!string.IsNullOrEmpty(v)) ids.Add(v!);
            }
            if (ids.Count > 0) result[versionEl.Name] = ids.ToArray();
        }
        return result;
    }

    /// <summary>
    /// ★ Task 26：解析 <c>&lt;alternativePackageIds&gt;</c>。实测 4,080 个里只有 <b>2</b> 个用，
    /// 但用了它的 mod 在依赖里被写成备选名时，没它就会判成「缺前置」（假阳）。
    /// </summary>
    private static string[] AlternativePackageIds(XmlElement root)
        => LiValues(root, "alternativePackageIds");

    /// <summary>
    /// ★ Task 26：解析 <c>&lt;incompatibleWith&gt;</c>。实测 <b>302</b> 个 mod 用它 ——
    /// 这是中文环世界社区最常用的一个字段。
    /// </summary>
    private static string[] IncompatibleWith(XmlElement root)
        => LiValues(root, "incompatibleWith");

    /// <summary>取 &lt;modDependencies&gt;&lt;li&gt;&lt;packageId&gt;x&lt;/packageId&gt;&lt;/li&gt;…&lt;/modDependencies&gt;。</summary>
    private static string[] DependencyPackageIds(XmlElement root)
    {
        var container = root.SelectSingleNode("modDependencies");
        var nodes = container?.SelectNodes("li");
        if (nodes is null) return Array.Empty<string>();
        var list = new List<string>();
        foreach (XmlNode li in nodes)
        {
            var v = li.SelectSingleNode("packageId")?.InnerText?.Trim();
            if (!string.IsNullOrEmpty(v)) list.Add(v);
        }
        return list.ToArray();
    }
}
