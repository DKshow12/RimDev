using System.Xml;

namespace RimDev.Core.Catalog;

/// <summary>
/// 解析 <c>loadFolders.xml</c>，取出**目标版本**对应的目录列表。
///
/// 实测格式普查（3984 个目录）：<c>&lt;v1.6&gt;</c> = 1374 个，<c>&lt;1.6&gt;</c> = 0 个，
/// 有 loadFolders 但没有 1.6 键 = 48 个。因此以 <c>v</c>+版本 为主，同时兼容无 <c>v</c> 写法。
/// 目录值 <c>/</c> 表示 mod 根。
/// </summary>
public static class LoadFoldersReader
{
    /// <summary>
    /// 返回目标版本对应的目录列表（**已过滤掉磁盘上不存在的目录**）。
    /// 没有 loadFolders.xml 时返回 null，调用方按回落规则处理。
    /// </summary>
    public static string[]? Read(string modRoot, string targetVersion)
    {
        var path = FindLoadFolders(modRoot);
        if (path is null) return null;

        try
        {
            var doc = new XmlDocument { XmlResolver = null };
            doc.Load(path);

            var keys = new[] { "v" + targetVersion, targetVersion };
            XmlNode? section = null;
            foreach (var k in keys)
            {
                section = doc.DocumentElement?.SelectSingleNode(k);
                if (section is not null) break;
            }
            if (section is null) return Array.Empty<string>();

            var result = new List<string>();
            // ★ 去重（大小写不敏感，Windows 路径），保持**首次出现顺序**：
            //   实测 47 个工坊 mod 的 loadFolders.xml 声明了重复 <li>（例：workshop\content\294100\2297729625
            //   把 Compatibility\Mods\DESurgeries 列了 6 次），且 <li>.</li> 也会重复出现 → 同一个 modRoot 被多次加入。
            //   不去重的后果不是「数字不好看」：同一份 XML 会被枚举/解析 N 次 → 同一 def 被索引多次
            //   → ByDefName 里同一 defName 的 List<RawDef>.Count > 1 → duplicateDefNames 报出纯粹由重复计数
            //   造出来的**假重名**（这正是 Task 6 的 RD2004 要读的数，误报比不报更糟）。
            //   注意：HashSet 只用于「挡重复」，返回值仍是逐个累积的 List（顺序影响索引稳定性）。
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlNode li in section.SelectNodes("li") ?? new XmlNodeListStub())
            {
                var v = (li.InnerText ?? "").Trim();
                if (v.Length == 0) continue;
                if (v == "/" || v == "." || v == "\\")
                {
                    if (seen.Add(modRoot)) result.Add(modRoot);
                    continue;
                }
                var abs = Path.Combine(modRoot, v.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(abs) && seen.Add(abs)) result.Add(abs);
            }
            return result.ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>工坊 mod 里两种拼写都见过（loadFolders.xml / LoadFolders.xml）。</summary>
    public static string? FindLoadFolders(string modRoot)
    {
        foreach (var n in new[] { "loadFolders.xml", "LoadFolders.xml" })
        {
            var p = Path.Combine(modRoot, n);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>没有 loadFolders.xml 时的回落：有 &lt;root&gt;\&lt;targetVersion&gt; 就用它 + 根，否则只用根。</summary>
    public static string[] Fallback(string modRoot, string targetVersion)
    {
        var versioned = Path.Combine(modRoot, targetVersion);
        return Directory.Exists(versioned)
            ? new[] { modRoot, versioned }
            : new[] { modRoot };
    }

    /// <summary>空集合占位（避免 SelectNodes 返回 null 时的判空分支）。</summary>
    private sealed class XmlNodeListStub : XmlNodeList
    {
        public override XmlNode? Item(int index) => null;

        public override int Count => 0;

        public override System.Collections.IEnumerator GetEnumerator()
            => Enumerable.Empty<XmlNode>().GetEnumerator();
    }
}
