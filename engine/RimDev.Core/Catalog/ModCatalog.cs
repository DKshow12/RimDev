using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;

namespace RimDev.Core.Catalog;

public sealed record CatalogBuildResult(
    ModCatalog Catalog,
    IReadOnlyList<Diagnostic> Diagnostics,
    long ElapsedMs);

/// <summary>
/// 扫描所有 mod 目录并建立 packageId → ModInfo 的目录表。
///
/// 实测规模：3984 个候选目录（本地 Mods 11 + 官方 Data 6 + 工坊 3967），
/// XML 感知扫描耗时 1375 ms，解析出 3971 个 packageId、0 个真实重复。
/// 因此**元数据层不缓存**——每次运行直接扫。
///
/// 同 packageId 冲突时按 Official &gt; Local &gt; Workshop 优先级保留先者，
/// 后者记为被遮蔽（报 RD1005）。
/// </summary>
public sealed class ModCatalog
{
    private readonly Dictionary<string, ModInfo> _byPackageId;

    private ModCatalog(IReadOnlyList<ModInfo> all, IReadOnlyList<ModInfo> active,
        Dictionary<string, ModInfo> byPackageId)
    {
        All = all;
        Active = active;
        _byPackageId = byPackageId;
    }

    public IReadOnlyList<ModInfo> All { get; }
    public IReadOnlyList<ModInfo> Active { get; }
    public bool TryGet(string packageId, out ModInfo mod) => _byPackageId.TryGetValue(packageId, out mod!);
    public ModInfo? Get(string packageId) => _byPackageId.GetValueOrDefault(packageId);

    public static CatalogBuildResult Build(GameEnv env, string targetVersion,
        IReadOnlyList<string> activePackageIds)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var diagnostics = new List<Diagnostic>();
        var candidates = EnumerateCandidates(env);

        // ★ 版本口径必须来自引擎本体（RimWorld.VersionControl.CurrentVersionString = "1.6.4871"），
        //   不能用 targetVersion（"1.6"）：引擎第 1 步查的是三段键，用 "1.6" 会把「精确分支」张冠李戴。
        //   参数 targetVersion 保留只为签名兼容，真实口径见 EngineVersionProbe。
        var versionInfo = Environment.EngineVersionProbe.Probe(env);
        var isActiveMatcher = LoadFolderResolver.ActiveMatcher(activePackageIds);
        _ = targetVersion;

        var byPkg = new Dictionary<string, ModInfo>(StringComparer.OrdinalIgnoreCase);
        var all = new List<ModInfo>();

        foreach (var (root, source) in candidates)
        {
            var about = Path.Combine(root, "About", "About.xml");
            var hasAbout = File.Exists(about);

            string packageId;
            AboutInfo aboutInfo = new("", "", Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<string>(), Array.Empty<string>(), null);

            if (hasAbout)
            {
                aboutInfo = AboutXmlReader.Read(about);
                packageId = aboutInfo.PackageId;
                if (string.IsNullOrWhiteSpace(packageId))
                {
                    // 无 packageId 的目录不进目录表（实测 13 个，多为无 About 的杂项目录）
                    continue;
                }
            }
            else
            {
                continue;   // 没有 About.xml 就不是 mod
            }

            // ★ 第 3 期 Task 1：引擎等价的 loadFolders 求解（五步分支 + If* 条件 + 倒序优先）。
            //   旧实现（Read ?? Fallback）已删除，见 LoadFoldersReader 的类注释。
            var doc = LoadFolderResolver.Load(root);
            var resolved = LoadFolderResolver.Resolve(root, packageId, doc, versionInfo,
                isActiveMatcher, aboutInfo.SupportedVersions);
            var folders = resolved.DescendingOrder.ToArray();

            var info = new ModInfo(
                PackageId: packageId,
                DisplayName: aboutInfo.DisplayName,
                RootPath: root,
                Source: source,
                PublishedFileId: source == ModSource.Workshop ? Path.GetFileName(root) : null,
                SupportedVersions: aboutInfo.SupportedVersions,
                LoadFolders: folders,
                Dependencies: aboutInfo.Dependencies,
                LoadAfter: aboutInfo.LoadAfter,
                LoadBefore: aboutInfo.LoadBefore,
                IsActive: activePackageIds.Contains(packageId, StringComparer.OrdinalIgnoreCase),
                HasAssemblies: Directory.Exists(Path.Combine(root, "Assemblies")),
                AboutPath: hasAbout ? about : null,
                AboutError: aboutInfo.Error,
                Resolution: resolved);

            if (byPkg.TryGetValue(packageId, out var existing))
            {
                // 实测本机 0 例；保留逻辑以备用户环境不同
                var winner = existing.Source.Priority() <= source.Priority() ? existing : info;
                var loser = ReferenceEquals(winner, existing) ? info : existing;
                if (!ReferenceEquals(winner, existing))
                {
                    byPkg[packageId] = winner;
                    all.Remove(existing);
                    all.Add(winner);
                    all.Add(loser);
                }
                else
                {
                    all.Add(loser);
                }
                diagnostics.Add(new Diagnostic(Codes.DuplicatePackageId, Severity.Warning, Scope.Offline,
                    $"packageId 重复：{packageId}",
                    $"保留 {winner.RootPath}，遮蔽 {loser.RootPath}",
                    TypeName: packageId,
                    Evidence: new Dictionary<string, string>
                    {
                        ["kept"] = winner.RootPath,
                        ["shadowed"] = loser.RootPath
                    }));
                continue;
            }

            byPkg[packageId] = info;
            all.Add(info);
        }

        sw.Stop();
        var active = all.Where(m => m.IsActive).ToList();
        var catalog = new ModCatalog(all, active, byPkg);
        return new CatalogBuildResult(catalog, diagnostics, sw.ElapsedMilliseconds);
    }

    /// <summary>本地 Mods\ + 官方 Data\ + 工坊。注意 <c>_rimdev</c> 也在 Mods 下，但无 About.xml 会被跳过。</summary>
    public static IEnumerable<(string Root, ModSource Source)> EnumerateCandidates(GameEnv env)
    {
        if (Directory.Exists(env.ModsDir))
            foreach (var d in Directory.GetDirectories(env.ModsDir))
                yield return (d, ModSource.Local);

        if (Directory.Exists(env.DataDir))
            foreach (var d in Directory.GetDirectories(env.DataDir))
                yield return (d, ModSource.Official);

        if (Directory.Exists(env.WorkshopDir))
            foreach (var d in Directory.GetDirectories(env.WorkshopDir))
                yield return (d, ModSource.Workshop);
    }

    /// <summary>从 ModsConfig.xml 只取 &lt;activeMods&gt; 段（★ 不能扫全文的 &lt;li&gt;，那会混进 knownExpansions）。</summary>
    public static string[] ReadActiveMods(string modsConfigPath)
    {
        if (!File.Exists(modsConfigPath)) return Array.Empty<string>();
        var text = File.ReadAllText(modsConfigPath);
        var m = System.Text.RegularExpressions.Regex.Match(text,
            @"(?s)<activeMods>(.*?)</activeMods>");
        if (!m.Success) return Array.Empty<string>();
        return System.Text.RegularExpressions.Regex.Matches(m.Groups[1].Value, @"<li>([^<]+)</li>")
            .Select(x => x.Groups[1].Value.Trim())
            .Where(x => x.Length > 0)
            .ToArray();
    }
}
