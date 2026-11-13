using System.Xml;

namespace RimDev.Core.Defs;

public sealed record DefFileResult(
    string FileAbs,
    string ModPackageId,
    IReadOnlyList<RawDef> Defs,
    string? ParseError,
    int? ErrorLine,
    int? ErrorColumn,
    bool CommentHasDoubleDash,
    string? RootKind = null);          // "Defs" | "Patch" | 其它根元素名

public sealed record ScanResult(
    IReadOnlyList<RawDef> Defs,
    IReadOnlyList<DefFileResult> Files,
    int FilesScanned,
    int FilesFailed,
    long ElapsedMs);

/// <summary>
/// 流式扫描 Defs/*.xml，只抽取 def 头，**不把 XML 装进 DOM**（全库 300 MB，必须流式）。
///
/// 必须遵守的点：
/// 1. **保留行号**：用 <see cref="IXmlLineInfo"/>（XmlReader 实现了它）。没有行号的诊断等于没定位。
/// 2. **注释含 `--` 要单独报**：XmlReader 遇到 `--` 会直接抛 XmlException，且**报错位置与真实问题行无关**
///    （项目历史：4 次踩坑，3 次是表格分隔线 `|------|`）。所以解析失败时再跑一次廉价的逐行预扫，
///    把「注释含 --」与「其它语法错」区分开（RD2002 vs RD2001）。
/// 3. **非 &lt;Defs&gt; 根不是解析失败**：`Patches/*.xml` 的根是 `&lt;Patch&gt;`（PatchOperation），
///    合法 XML 但不是 def 文件。实测启用集合里有 17 个（Royalty 2 / Ideology 1 / Biotech 5 /
///    Odyssey 6 / 工坊 3）。把它们计成 ParseError 会让「解析失败 = 0」永远不成立，并把
///    PatchOperation 当成 def 头收录。Patch 的语义应用留给第 3 期。
/// </summary>
public static class DefScanner
{
    public static ScanResult Scan(IEnumerable<(string FileAbs, string ModPackageId, string ModRoot)> files)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var allDefs = new List<RawDef>();
        var results = new List<DefFileResult>();
        int failed = 0;

        foreach (var (fileAbs, pkgId, modRoot) in files)
        {
            var r = ScanFile(fileAbs, pkgId, modRoot);
            results.Add(r);
            if (r.ParseError is not null) failed++;
            allDefs.AddRange(r.Defs);
        }

        sw.Stop();
        return new ScanResult(allDefs, results, results.Count, failed, sw.ElapsedMilliseconds);
    }

    public static DefFileResult ScanFile(string fileAbs, string modPackageId, string modRoot)
    {
        var defs = new List<RawDef>();
        string rel = Path.GetRelativePath(modRoot, fileAbs);
        string? parseError = null;
        int? errLine = null, errCol = null;
        string? rootKind = null;

        try
        {
            using var stream = File.OpenRead(fileAbs);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                IgnoreComments = true,
                IgnoreWhitespace = true,
                DtdProcessing = DtdProcessing.Prohibit
            });

            var li = (IXmlLineInfo)reader;
            string? rootSeen = null;

            // ★ 单遍深度跟踪，**不嵌套 ReadSubtree**：
            //   ReadSubtree 的 Depth 语义很容易读错（子树根到底是 0 还是原深度），而且一旦判错
            //   就会【静默产错】（defName 全为 null）而不是编译报错——这类错误最贵。
            //   这里用显式的 pending 状态：Depth==1 是 def 开始，Depth==2 的 <defName> 是它的名字，
            //   Depth==1 的 EndElement 是 def 结束。
            RawDef? pending = null;
            string? pendingDefName = null;
            bool pendingAbstract = false;

            void Emit()
            {
                if (pending is null) return;
                var name = string.IsNullOrWhiteSpace(pendingDefName) ? null : pendingDefName;
                defs.Add(pending with
                {
                    DefName = name,
                    IsAbstract = pendingAbstract || (pending.NameAttr is not null && name is null)
                });
                pending = null;
                pendingDefName = null;
                pendingAbstract = false;
            }

            while (reader.Read())
            {
                var line = li.HasLineInfo() ? li.LineNumber : 0;

                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (rootSeen is null)
                    {
                        rootSeen = reader.Name;
                        if (rootSeen != "Defs")
                        {
                            // 不是解析失败：<Patch> 是 PatchOperation 的根，合法 XML 但不是 def 文件。
                            // 实测启用集合里 17 个（Royalty 2 / Ideology 1 / Biotech 5 / Odyssey 6 / 工坊 3）。
                            // 不产生任何 RawDef（PatchOperation 不是 def 头）；Patch 的语义应用留给第 3 期。
                            //
                            // ★ 但仍要把剩余内容读完：否则「根元素对了、正文坏了」的文件会被静默放过
                            //   （实测：<Patch> 里写坏 XML 时，不读完则 failed=False，等于丢失 RD2001 能力）。
                            while (reader.Read()) { }
                            rootKind = rootSeen;
                            break;
                        }
                        continue;
                    }

                    if (reader.Depth == 1)
                    {
                        // 一个 def 的开始
                        var nameAttr = reader.GetAttribute("Name");
                        var parentName = reader.GetAttribute("ParentName");
                        var mayRequire = reader.GetAttribute("MayRequire");
                        pendingAbstract = string.Equals(reader.GetAttribute("Abstract"), "True",
                            StringComparison.OrdinalIgnoreCase);
                        pendingDefName = null;
                        pending = new RawDef(
                            DefType: reader.Name,
                            DefName: null,                        // 稍后由子元素补上
                            NameAttr: string.IsNullOrWhiteSpace(nameAttr) ? null : nameAttr,
                            ParentName: string.IsNullOrWhiteSpace(parentName) ? null : parentName,
                            MayRequire: string.IsNullOrWhiteSpace(mayRequire) ? null : mayRequire,
                            IsAbstract: pendingAbstract,           // Emit 时再按 Name=/defName 复合判断
                            ModPackageId: modPackageId,
                            FileAbs: fileAbs,
                            FileRelToMod: rel,
                            Line: line);

                        if (reader.IsEmptyElement) Emit();    // 空 def 节点：只有属性
                        continue;
                    }

                    if (pending is not null && reader.Depth == 2
                        && reader.Name == "defName" && pendingDefName is null)
                    {
                        // 只取 def 的一级子元素 <defName>；comps/li 里同名的不会命中（Depth 不同）
                        pendingDefName = reader.ReadElementContentAsString().Trim();

                        // ★ 关键：ReadElementContentAsString 会把【父元素（即本 def）的结束标签】一起消费掉
                        //   （XmlReader 语义 + 实测确认）。若 <defName> 恰是本 def 的最后一个子元素
                        //   （原版极常见：<DrawStyleCategoryDef><defName>X</defName></DrawStyleCategoryDef>），
                        //   那么 depth==1 的 EndElement 永远不会被本循环看到 → pending 被下一个 def 覆盖
                        //   → **def 头静默丢失**。实测 Core 的 DrawStyleCategories.xml 有 19 个 def，
                        //   修前只收到 5 个；全 Core 少 89 个。因此读完后若已回到 depth<=1，就地 Emit。
                        if (reader.Depth <= 1) Emit();
                        continue;
                    }

                    continue;
                }

                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == 1 && pending is not null)
                    Emit();
            }

            Emit();   // 兜底：文件末尾未见 EndElement 时也不丢最后一个 def
        }
        catch (XmlException ex)
        {
            parseError = $"XmlException: {ex.Message}";
            errLine = ex.LineNumber > 0 ? ex.LineNumber : (int?)null;
            errCol = ex.LinePosition > 0 ? ex.LinePosition : (int?)null;
        }
        catch (Exception ex)
        {
            parseError = $"{ex.GetType().Name}: {ex.Message}";
        }

        // 解析失败时判断是不是「注释含 --」——这决定了报 RD2002 还是 RD2001
        bool dash = false;
        if (parseError is not null) dash = CommentHasDoubleDash(fileAbs);

        return new DefFileResult(fileAbs, modPackageId, defs, parseError, errLine, errCol, dash, rootKind);
    }

    /// <summary>
    /// 廉价预扫：找 &lt;!-- ... --&gt; 区间内是否含 `--`。
    /// 只在解析失败时调用（全库 300 MB，不能每次都跑）。
    /// </summary>
    public static bool CommentHasDoubleDash(string fileAbs)
    {
        try
        {
            var text = File.ReadAllText(fileAbs);
            int i = 0;
            while (true)
            {
                var open = text.IndexOf("<!--", i, StringComparison.Ordinal);
                if (open < 0) return false;
                var close = text.IndexOf("-->", open + 4, StringComparison.Ordinal);
                if (close < 0) return false;
                var body = text.Substring(open + 4, close - open - 4);
                if (body.Contains("--", StringComparison.Ordinal)) return true;
                i = close + 3;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>★ 用这个：把目录列表摊平成扫描输入。</summary>
    public static List<(string FileAbs, string ModPackageId, string ModRoot)> CollectFiles(
        IEnumerable<(string PackageId, string RootPath, IReadOnlyList<string> LoadFolders)> mods)
    {
        var list = new List<(string FileAbs, string ModPackageId, string ModRoot)>();
        foreach (var m in mods)
        {
            foreach (var dir in m.LoadFolders)
            {
                foreach (var sub in new[] { "Defs", "Patches" })
                {
                    var p = Path.Combine(dir, sub);
                    if (!Directory.Exists(p)) continue;
                    foreach (var f in Directory.EnumerateFiles(p, "*.xml", SearchOption.AllDirectories))
                        list.Add((f, m.PackageId, m.RootPath));
                }
            }
        }
        return list;
    }
}
