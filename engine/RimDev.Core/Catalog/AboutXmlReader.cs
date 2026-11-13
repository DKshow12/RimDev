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
    string? Error);

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
            if (root is null || root.Name != "ModMetaData")
                return new AboutInfo("", "", Array.Empty<string>(), Array.Empty<string>(),
                    Array.Empty<string>(), Array.Empty<string>(),
                    $"根元素不是 <ModMetaData>（实际 <{root?.Name}>）");

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
                null);
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
