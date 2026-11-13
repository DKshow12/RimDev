using RimDev.Core.Catalog;
using RimDev.Core.Diagnostics;

namespace RimDev.Core.Checks;

/// <summary>
/// loadFolders 规则族：<b>RD2012</b>（<c>&lt;li&gt;</c> 指向的目录不存在，info）+
/// <b>RD2018–RD2024</b>（逐条对应引擎 <c>Verse.ModLoadFolders.GetIssueList</c> 的 issue 表）。
///
/// ★ 规则口径以**反编译源码**为准，不以计划 §0.4.4 的表格为准（下表给出三处实测差异）：
/// <list type="table">
/// <item><term>RD2018</term><description><c>foldersForVersion[key].Count == 0</c>（版本块里一个 <c>&lt;li&gt;</c> 都没有）——与 §0.4.4 一致。</description></item>
/// <item><term>RD2019</term><description>引擎是 <c>group f by f</c>，而 <c>LoadFolder.Equals</c> 只比较 <c>hashCodeCached</c>
///   （folderName + 三个 List 的 **引用** hash）⇒ 只有「同名且三个属性全缺」的重复 <c>&lt;li&gt;</c> 才会被引擎报出来。
///   我们按「同名 + 三个属性的**内容**完全相同」判重：既能报出引擎会报的那一类，也能报出属性相同但实例不同的那一类
///   （引擎因引用 hash 不同而漏报）；对「同名但条件不同」（合法的互斥写法）不报，避免假阳。</description></item>
/// <item><term>RD2020</term><description><c>!IsWellFormedVersionString(key) &amp;&amp; key != "default"</c>；
///   <c>IsWellFormedVersionString</c> 要求**恰好两段**，所以 <c>1.6.1</c> 也算「格式非法」——比 §0.4.4 的「不含点等」严格得多。</description></item>
/// <item><term>RD2021</term><description>key == "default"（解析时已 ToLower，故大小写不敏感）——与 §0.4.4 一致。</description></item>
/// <item><term>RD2022</term><description>**按声明顺序**比较相邻两键，当前键版本 &lt; 前一个键版本 ⇒ 未升序。
///   注意向下取版本用的是 <c>orderby x descending</c>（字符串序），与声明顺序无关 ⇒ 这条**不会改变解析结果**，故为 warning 而非 error。</description></item>
/// <item><term>RD2012</term><description><c>!Directory.Exists(Path.Combine(mod.RootDir, folderName))</c> —— 引擎对**每一个版本块的每一条**都查，
///   不只查生效的那个块；<c>folderName == ""</c>（<c>&lt;li&gt;/&lt;/li&gt;</c>）Path.Combine 后就是 mod 根，必然存在。</description></item>
/// <item><term>RD2023</term><description>★ **实测差异**：引擎的 <c>ModLoadFolderDefinesUnsupportedGameVersion</c> 判的是
///   「该版本键解析出的 Version **不在该 mod 自己的 About.xml `&lt;supportedVersions&gt;` 里**」，
///   **不是** §0.4.4 表格写的「声明的版本 &gt; 当前游戏版本」。</description></item>
/// <item><term>RD2024</term><description>解析结果为空（<c>foldersToLoadDescendingOrder.Count == 0</c>）⇒ 该 mod 的 def 完全不会被加载。error。</description></item>
/// </list>
///
/// ★ 额外形态（引擎会打日志但我们原先没有码）：<c>LoadFolders.xml</c> 解析失败 →
///   报 <see cref="Codes.XmlParseFailed"/>（RD2001），级别取 <b>Warning</b>：引擎
///   <c>DirectXmlLoader.ItemFromXmlFile</c> 会 catch 全部异常并返回**空的 ModLoadFolders** ⇒ 退回根目录逻辑，mod 仍能加载。
///   （与 def 文件的 RD2001 error 区分：靠 Evidence 的 <c>kind=loadFolders</c>。）
/// </summary>
public sealed class LoadFolderCheck : ICheck
{
    public string Id => "loadFolders";

    public IEnumerable<Diagnostic> Run(CheckContext ctx)
    {
        // 报告范围：本次扫描到的 mod（= ctx.Files 里出现的 packageId），再按 TargetModPackageId 过滤。
        // 这样 `check <mod>`（全集扫描 + 报告过滤）只产出目标 mod 的条目，而 `--all` 产出全部启用 mod 的。
        var packageIds = ctx.Files.Select(f => f.ModPackageId).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var pkg in packageIds)
        {
            if (ctx.TargetModPackageId is not null
                && !string.Equals(pkg, ctx.TargetModPackageId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!ctx.Catalog.TryGet(pkg, out var mod)) continue;

            var resolved = mod.Resolution;
            if (resolved is null)
            {
                // 兜底：ModInfo 不是 ModCatalog.Build 造的（用例/外部调用）时现场算一次，保证规则不会静默失效。
                var doc = LoadFolderResolver.Load(mod.RootPath);
                resolved = LoadFolderResolver.Resolve(mod.RootPath, mod.PackageId, doc,
                    Environment.EngineVersionProbe.Probe(Environment.GameEnvProbe.Discover(startDir: mod.RootPath)),
                    LoadFolderResolver.ActiveMatcher(ctx.Catalog.Active.Select(m => m.PackageId)),
                    mod.SupportedVersions);
            }

            foreach (var d in resolved.Issues) yield return d;
        }
    }

    /// <summary>
    /// 单 mod 的引擎 issue 分析（<b>唯一实现</b>：<see cref="LoadFolderResolver.Resolve"/> 通过
    /// <see cref="AttachIssues"/> 调它填 <c>ResolvedFolders.Issues</c>，本 Check 只做转发）。
    /// </summary>
    public static IReadOnlyList<Diagnostic> Analyze(
        string modRoot,
        string packageId,
        LoadFolderDocument doc,
        IReadOnlyList<string>? supportedVersions,
        IReadOnlyList<string> resolvedFolders)
    {
        var list = new List<Diagnostic>();
        string? file = doc.FilePath;

        if (doc.ParseError is not null)
        {
            list.Add(new Diagnostic(Codes.XmlParseFailed, Severity.Warning, Scope.Offline,
                $"LoadFolders.xml 解析失败（引擎会改用「根目录」回退逻辑，mod 仍能加载）：{doc.ParseError}",
                "检查文件编码与 XML 良构性。DirectXmlLoader.ItemFromXmlFile 捕获异常后返回空 ModLoadFolders，"
                + "因此 loadFolders 的全部声明（含 If* 条件）都会失效",
                FileAbs: file, ModPackageId: packageId,
                // ★ Task 2 Step 6：与 def 文件的 RD2001 用同一套 cause 口径（kind 用来区分两者）
                Evidence: new Dictionary<string, string>
                {
                    ["kind"] = "loadFolders",
                    ["cause"] = ParseCause.Classify(doc.ParseError),
                }));
        }

        var supported = new HashSet<Version>();
        if (supportedVersions is not null)
        {
            foreach (var s in supportedVersions)
                if (LoadFolderResolver.TryParseVersionString(s, out var sv)) supported.Add(sv);
        }

        // ★ 引擎按字典插入顺序遍历**所有**版本块做校验（不只生效的那块）。
        Version? prev = null;
        foreach (var block in doc.Blocks)
        {
            var key = block.Key;
            var line = block.Line;

            // RD2018：版本块里一个 <li> 都没有
            if (block.Entries.Count == 0)
            {
                list.Add(new Diagnostic(Codes.LoadFolderListEmpty, Severity.Warning, Scope.Offline,
                    $"loadFolders 的版本块 `{key}` 里没有任何 <li>（该版本下不加载任何目录）",
                    "补上 <li>，或删掉这个空块；空 <default> 块会让该 mod 一个目录都不加载（见 RD2024）",
                    FileAbs: file, Line: line, ModPackageId: packageId,
                    Evidence: new Dictionary<string, string> { ["versionKey"] = key }));
            }

            // RD2019：同名 + 同条件重复
            foreach (var g in block.Entries
                         .GroupBy(e => (e.FolderName, Sig(e)))
                         .Where(g => g.Count() > 1))
            {
                list.Add(new Diagnostic(Codes.LoadFolderRepeatingFolder, Severity.Warning, Scope.Offline,
                    $"loadFolders 的版本块 `{key}` 里重复声明了同一个目录（{Describe(g.Key.FolderName)}，{g.Count()} 次）",
                    "删掉多余的条目。注意引擎的重复检测是按 hashCode 分组的（LoadFolder.Equals 只比较 hashCode），"
                    + "属性完全相同的重复 <li> 才会被它报出来；重复本身通常无害（文件枚举按相对路径去重）",
                    FileAbs: file, Line: g.First().Line, ModPackageId: packageId,
                    Evidence: new Dictionary<string, string>
                    {
                        ["versionKey"] = key,
                        ["folder"] = Describe(g.Key.FolderName),
                        ["count"] = g.Count().ToString(),
                        ["lines"] = string.Join(",", g.Select(x => x.Line)),
                    }));
            }

            // RD2020：版本键格式非法（恰好两段才算合法）
            if (!LoadFolderResolver.IsWellFormedVersionString(key)
                && !key.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new Diagnostic(Codes.LoadFolderMalformedVersion, Severity.Warning, Scope.Offline,
                    $"loadFolders 的版本键 `{key}` 格式非法（引擎 IsWellFormedVersionString 要求恰好两段数字，如 1.6）",
                    "改成 <v1.6> 这种两段写法；三段（1.6.1）在引擎眼里同样非法",
                    FileAbs: file, Line: line, ModPackageId: packageId,
                    Evidence: new Dictionary<string, string> { ["versionKey"] = key }));
            }

            // RD2021：default 键已废弃
            if (key.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new Diagnostic(Codes.LoadFolderDefaultDeprecated, Severity.Info, Scope.Offline,
                    "loadFolders 使用了已废弃的 `default` 版本键",
                    "改为显式版本键（如 <v1.6>）；引擎只在没有可用版本键时才回落到 default",
                    FileAbs: file, Line: line, ModPackageId: packageId,
                    Evidence: new Dictionary<string, string> { ["versionKey"] = key }));
            }

            // RD2022：版本键未升序（按声明顺序比较相邻两键）
            if (LoadFolderResolver.TryParseVersionString(key, out var cur))
            {
                if (prev is not null && cur < prev)
                {
                    list.Add(new Diagnostic(Codes.LoadFolderOutOfOrder, Severity.Warning, Scope.Offline,
                        $"loadFolders 的版本键未升序：`{key}` 排在更大的版本之后",
                        "按升序排列版本块。注意这条**不会改变解析结果**（向下取版本用的是字符串序排序，与声明顺序无关），"
                        + "但会让引擎的 GetIssueList 报 ModLoadFolderOutOfOrder",
                        FileAbs: file, Line: line, ModPackageId: packageId,
                        Evidence: new Dictionary<string, string> { ["versionKey"] = key }));
                }
                prev = cur;
            }

            // RD2012：<li> 指向的目录不存在（引擎对每个块的每条都查）
            foreach (var e in block.Entries)
            {
                var abs = Path.Combine(modRoot, e.FolderName);
                if (Directory.Exists(abs)) continue;
                list.Add(new Diagnostic(Codes.LoadFolderDoesntExist, Severity.Info, Scope.Offline,
                    $"loadFolders（版本块 `{key}`）声明了不存在的目录：{Describe(e.FolderName)}",
                    "删掉该 <li> 或补上目录；引擎会跳过不存在的目录（无害，但会让目录列表与预期不符）",
                    FileAbs: file, Line: e.Line, ModPackageId: packageId,
                    Evidence: new Dictionary<string, string>
                    {
                        ["versionKey"] = key,
                        ["folder"] = Describe(e.FolderName),
                        ["path"] = abs,
                    }));
            }

            // RD2023：版本键不在该 mod 自己的 supportedVersions 里
            if (LoadFolderResolver.TryParseVersionString(key, out var kv) && !supported.Contains(kv))
            {
                list.Add(new Diagnostic(Codes.LoadFolderUnsupportedVersion, Severity.Info, Scope.Offline,
                    $"loadFolders 声明了 About.xml 未列出的版本 `{key}`（引擎 ModLoadFolderDefinesUnsupportedGameVersion）",
                    supportedVersions is { Count: > 0 }
                        ? $"该 mod 的 <supportedVersions> 只有：{string.Join(", ", supportedVersions)}"
                        : "该 mod 的 <supportedVersions> 为空或缺失",
                    FileAbs: file, Line: line, ModPackageId: packageId,
                    Evidence: new Dictionary<string, string>
                    {
                        ["versionKey"] = key,
                        ["supportedVersions"] = string.Join(",", supportedVersions ?? Array.Empty<string>()),
                    }));
            }
        }

        // RD2024：解析结果为空 ⇒ 该 mod 的 def 完全不会被加载
        if (resolvedFolders.Count == 0)
        {
            list.Add(new Diagnostic(Codes.LoadFolderResolvedEmpty, Severity.Error, Scope.Offline,
                "loadFolders 解析出的加载目录列表为空 —— 该 mod 的 def 完全不会被加载",
                "常见原因：<li> 的 IfModActive/IfModActiveAll/IfModNotActive 条件全部不满足，或版本块为空。"
                + "引擎此时 foldersToLoadDescendingOrder.Count == 0，不加载任何目录",
                FileAbs: file, ModPackageId: packageId,
                Evidence: new Dictionary<string, string>
                {
                    ["versions"] = string.Join(",", doc.DefinedVersions),
                }));
        }

        return list;
    }

    /// <summary>把 issue 列表挂到解析结果上（<see cref="LoadFolderResolver"/> 的唯一调用点）。</summary>
    public static ResolvedFolders AttachIssues(
        ResolvedFolders resolved,
        string modRoot,
        string packageId,
        LoadFolderDocument doc,
        IReadOnlyList<string>? supportedVersions) =>
        resolved with
        {
            Issues = Analyze(modRoot, packageId, doc, supportedVersions, resolved.DescendingOrder)
        };

    /// <summary>属性内容签名：三项都相同才算「同条件」（用于 RD2019 的判重）。</summary>
    private static string Sig(LoadFolderEntry e) =>
        $"{Join(e.Any)}|{Join(e.All)}|{Join(e.Not)}";

    private static string Join(string[]? a) => a is null ? "<null>" : string.Join(",", a);

    /// <summary><c>folderName == ""</c> 表示 mod 根（<c>&lt;li&gt;/&lt;/li&gt;</c>），打印时要说清。</summary>
    private static string Describe(string folderName) =>
        folderName.Length == 0 ? "(mod 根, <li>/</li>)" : folderName;
}
