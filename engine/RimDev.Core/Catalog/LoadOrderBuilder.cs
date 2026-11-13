using System.Text.RegularExpressions;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;

namespace RimDev.Core.Catalog;

/// <summary>
/// 一个 packageId 是怎么进入加载序的。
/// ★ 取值就是任务书 §2.1 的两个字面量，**不要**扩成三个：<c>Data\Core</c> 与 DLC 都属于
/// 「官方内容」（它们不在 <c>&lt;activeMods&gt;</c> 语义里 —— 即使 ModsConfig 也把它们列了一遍，
/// 引擎的官方包顺序由 <c>Data\</c> 扫描 + <c>&lt;knownExpansions&gt;</c> 决定）。
/// </summary>
public enum LoadOrigin
{
    /// <summary>官方内容：<c>Data\Core</c>（序位 0）与 <c>&lt;knownExpansions&gt;</c> 里列出的 DLC。</summary>
    OfficialData,

    /// <summary><c>ModsConfig.xml</c> 的 <c>&lt;activeMods&gt;</c> 里列出的 mod。</summary>
    ActiveMods
}

/// <summary>加载序里的一项。<see cref="Order"/> 0 = 最先加载 = 优先级最高。</summary>
public sealed record LoadOrderEntry(
    int Order,
    string PackageId,
    ModInfo Mod,
    LoadOrigin Origin)
{
    /// <summary>该 mod 的降序加载目录（引擎语义，索引 0 优先级最高）。</summary>
    public IReadOnlyList<string> Folders => Mod.Resolution?.DescendingOrder ?? Mod.LoadFolders;
}

/// <summary>
/// mod 加载顺序（引擎 <c>Verse.LoadedModManager</c> 的语义）：
/// <list type="number">
/// <item><c>&lt;游戏根&gt;\Data\Core</c> = 序位 <b>0</b>；</item>
/// <item>然后 <c>ModsConfig.xml</c> 的 <c>&lt;knownExpansions&gt;</c> 顺序（DLC）；</item>
/// <item>然后 <c>&lt;activeMods&gt;</c> 顺序。</item>
/// </list>
///
/// ★ 三条纪律（都是实测踩出来的，别改）：
/// <list type="number">
/// <item><b>不要自动注入依赖</b>（与设计文档 §4.3 一致）。引擎确实会为 <c>loadAfter</c> 做排序，
///   但「排序」不改变 <c>activeMods</c> 的集合；本 Task 只复刻「谁在前谁在后」的**配置顺序**，
///   依赖是否满足交由 <see cref="CheckOrderConsistency"/> 报 RD2011，而不是替用户改顺序。</item>
/// <item><c>&lt;activeMods&gt;</c> 段里**同时也列了 Core 与 5 个 DLC**（本机实测：17 条 <c>&lt;li&gt;</c> 里
///   <c>ludeon.rimworld</c> + 5 个 <c>ludeon.rimworld.*</c> 都在）。按本类型的规则它们先被
///   「Core → knownExpansions」放进序里，再次出现时**只跳过、不重复计数**
///   ⇒ 本机最终条目数仍是 <b>17</b>（1 Core + 5 DLC + 11 第三方），与任务书门禁 6 的 17 行吻合。
///   跳过项记录在 <see cref="SkippedAlreadyOrdered"/> 里，便于核对「不是丢了一条」。</item>
/// <item>解析不到 <c>ModInfo</c> 的 packageId 记进 <see cref="UnresolvedPackageIds"/>（不静默丢弃）。</item>
/// </list>
/// </summary>
public sealed class LoadOrder
{
    private readonly Dictionary<string, int> _rankByPackageId;

    private LoadOrder(
        IReadOnlyList<LoadOrderEntry> entries,
        IReadOnlyList<string> activePackageIds,
        IReadOnlyList<string> knownExpansions,
        IReadOnlyList<string> skippedAlreadyOrdered,
        IReadOnlyList<string> unresolvedPackageIds,
        string? corePackageId)
    {
        Entries = entries;
        ActivePackageIds = activePackageIds;
        KnownExpansions = knownExpansions;
        SkippedAlreadyOrdered = skippedAlreadyOrdered;
        UnresolvedPackageIds = unresolvedPackageIds;
        CorePackageId = corePackageId;

        _rankByPackageId = new Dictionary<string, int>(entries.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries) _rankByPackageId[e.PackageId] = e.Order;
    }

    /// <summary>加载序（只含能解析到 <see cref="ModInfo"/> 的条目）。</summary>
    public IReadOnlyList<LoadOrderEntry> Entries { get; }

    /// <summary><c>&lt;activeMods&gt;</c> 的原始顺序（含 Core/DLC 那两个「已在序里」的重复项）。</summary>
    public IReadOnlyList<string> ActivePackageIds { get; }

    /// <summary><c>&lt;knownExpansions&gt;</c> 的原始顺序。</summary>
    public IReadOnlyList<string> KnownExpansions { get; }

    /// <summary>在 <c>&lt;activeMods&gt;</c> 里出现、但已被「Core/knownExpansions」放进序里的 packageId（本机应为 6 个）。</summary>
    public IReadOnlyList<string> SkippedAlreadyOrdered { get; }

    /// <summary>配置里写了、但目录表里找不到的 packageId（本机应为空）。</summary>
    public IReadOnlyList<string> UnresolvedPackageIds { get; }

    /// <summary><c>Data\Core</c> 的 packageId；找不到 <c>Data\Core</c> 时为 null。</summary>
    public string? CorePackageId { get; }

    /// <summary>序位（0 = 最先加载）；不在启用序里返回 null。</summary>
    public int? RankOf(string? packageId) =>
        packageId is not null && _rankByPackageId.TryGetValue(packageId, out var r) ? r : null;

    public bool Contains(string? packageId) => RankOf(packageId) is not null;

    /// <summary>
    /// 给**本次扫描的 mod 子集**（<c>--scope all</c> / <c>--only-official</c> 会缩小它）分配 0..N-1 的序位：
    /// 启用序里的 mod 按<see cref="RankOf"/> 排在前，其余按 (来源, packageId) 稳定排序接在后面。
    ///
    /// ★ 为什么需要它：遮蔽胜者判定键是 <c>(modLoadOrder, folderIndex)</c>，而「本次扫描到了谁」
    ///   与「引擎真正会加载谁」是两个集合。用同一个函数给两者编号，保证
    ///   `--scope all` 下也在启用集内部保持真实相对顺序，不会因为多扫了 3954 个未启用 mod 而把胜者算反。
    /// </summary>
    public Dictionary<string, int> RankFor(IEnumerable<ModInfo> mods)
    {
        var ordered = mods
            .OrderBy(m => RankOf(m.PackageId) ?? int.MaxValue)
            .ThenBy(m => m.Source)
            .ThenBy(m => m.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var map = new Dictionary<string, int>(ordered.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < ordered.Count; i++) map[ordered[i].PackageId] = i;
        return map;
    }

    /// <summary>从 <c>ModsConfig.xml</c> 只取 <c>&lt;knownExpansions&gt;</c> 段（★ 同 activeMods：不能扫全文）。</summary>
    public static string[] ReadKnownExpansions(string modsConfigPath)
    {
        if (!File.Exists(modsConfigPath)) return Array.Empty<string>();
        try
        {
            var text = File.ReadAllText(modsConfigPath);
            var m = Regex.Match(text, @"(?s)<knownExpansions>(.*?)</knownExpansions>");
            if (!m.Success) return Array.Empty<string>();
            return Regex.Matches(m.Groups[1].Value, @"<li>([^<]+)</li>")
                .Select(x => x.Groups[1].Value.Trim())
                .Where(x => x.Length > 0)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>按 §2.1 的三步造加载序。</summary>
    public static LoadOrder Build(
        GameEnv env,
        ModCatalog catalog,
        IReadOnlyList<string> activePackageIds,
        IReadOnlyList<string> knownExpansions)
    {
        var entries = new List<LoadOrderEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<string>();
        var unresolved = new List<string>();

        void Add(string? packageId, LoadOrigin origin)
        {
            if (string.IsNullOrWhiteSpace(packageId)) return;
            var id = packageId.Trim();
            if (!seen.Add(id)) { skipped.Add(id); return; }
            if (!catalog.TryGet(id, out var mod)) { unresolved.Add(id); return; }
            entries.Add(new LoadOrderEntry(entries.Count, mod.PackageId, mod, origin));
        }

        // ① Data\Core —— 用**目录名**定位（packageId 是 ludeon.rimworld，不该硬编码进工具）。
        var coreRoot = Path.Combine(env.DataDir, "Core");
        string? corePackageId = null;
        if (Directory.Exists(coreRoot))
        {
            corePackageId = catalog.All
                .FirstOrDefault(m => string.Equals(
                    Path.GetFullPath(m.RootPath).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(coreRoot).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                ?.PackageId;
            Add(corePackageId, LoadOrigin.OfficialData);
        }

        // ② knownExpansions（DLC，按配置顺序）
        foreach (var dlc in knownExpansions) Add(dlc, LoadOrigin.OfficialData);

        // ③ activeMods（按配置顺序；Core/DLC 已在上面的步骤里入序 ⇒ 这里只跳过）
        foreach (var am in activePackageIds) Add(am, LoadOrigin.ActiveMods);

        return new LoadOrder(entries, activePackageIds, knownExpansions, skipped, unresolved, corePackageId);
    }

    /// <summary>便捷重载：直接从环境里的 <c>ModsConfig.xml</c> 读两段。</summary>
    public static LoadOrder Build(GameEnv env, ModCatalog catalog) =>
        Build(env, catalog, ModCatalog.ReadActiveMods(env.ModsConfigPath), ReadKnownExpansions(env.ModsConfigPath));

    /// <summary>
    /// 由既有条目直接构造（**不扫盘、不读 ModsConfig**）。
    ///
    /// ★ 存在意义：Task 3 Step 7 要求的反例是「A 声明 <c>loadAfter</c> B，而 activeMods 把 A 放在 B 之前」。
    ///   没有它就只能去改真实的 <c>ModsConfig.xml</c> 才能构造这个局面 —— 那是不可接受的（会动用户配置）。
    ///   生产路径一律走 <see cref="Build(GameEnv, ModCatalog, IReadOnlyList{string}, IReadOnlyList{string})"/>。
    /// </summary>
    public static LoadOrder FromEntries(
        IEnumerable<(ModInfo Mod, LoadOrigin Origin)> items,
        IReadOnlyList<string>? activePackageIds = null,
        IReadOnlyList<string>? knownExpansions = null)
    {
        var entries = items
            .Select((it, i) => new LoadOrderEntry(i, it.Mod.PackageId, it.Mod, it.Origin))
            .ToList();

        var core = entries.Count > 0 && entries[0].Mod.Source == ModSource.Official
            ? entries[0].PackageId
            : null;

        return new LoadOrder(entries,
            activePackageIds ?? Array.Empty<string>(),
            knownExpansions ?? Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            core);
    }

    // ───────────────────────── §2.4 顺序一致性（RD2011） ─────────────────────────

    /// <summary>
    /// <c>loadAfter</c> / <c>loadBefore</c> 与实际加载顺序矛盾 → <b>RD2011 info</b>。
    ///
    /// ★ 语义（任务书 §2.4）：
    ///   · <c>A.loadAfter  = [B]</c> ⇒ 期望 <c>rank(B) &lt; rank(A)</c>；`rank(B) >= rank(A)` 即矛盾。
    ///   · <c>A.loadBefore = [B]</c> ⇒ 期望 <c>rank(A) &lt; rank(B)</c>；`rank(B) <= rank(A)` 即矛盾。
    ///
    /// ★ 三条防假阳边界（都会直接决定「官方零假阳门」过不过）：
    /// <list type="number">
    /// <item><b>对方不在加载序里就不判</b>（未安装 / 未启用 / 解析不到 <c>ModInfo</c>）——
    ///   此时引擎根本不会比较两者，「顺序矛盾」这个命题不成立。</item>
    /// <item><b>自引用不判</b>（<c>&lt;li&gt;自己&lt;/li&gt;</c>；原版内容里就有这种冗余写法）。</item>
    /// <item>级别恒为 <b>Info</b>：这是**提示**（引擎会为 <c>loadAfter</c> 做排序，多数情形下无害），
    ///   报成 error 会让使用者直接关掉整份报告 —— 任务书 Step 7 专门要求断言「绝不是 error」。</item>
    /// </list>
    /// </summary>
    public IEnumerable<Diagnostic> CheckOrderConsistency(string? targetPackageId)
    {
        foreach (var e in Entries)
        {
            var mod = e.Mod;

            foreach (var dep in mod.LoadAfter)
            {
                if (string.Equals(dep, e.PackageId, StringComparison.OrdinalIgnoreCase)) continue;
                var other = RankOf(dep);
                if (other is null) continue;
                if (other.Value < e.Order) continue;   // 期望成立

                var otherMod = Entries[other.Value].Mod;
                yield return new Diagnostic(
                    Codes.LoadOrderViolation, Severity.Info, Scope.Offline,
                    $"{e.PackageId} 声明 loadAfter `{otherMod.PackageId}`，但实际加载顺序里它在对方**之前**"
                    + $"（声明：应在 {otherMod.PackageId} 之后；实际：序位 {e.Order} vs {other.Value}）",
                    "调整 ModsConfig.xml 的 activeMods 顺序，或删掉这条已失效的 loadAfter；"
                    + "引擎会为 loadAfter 做排序，多数情形下无害，故此项为 info",
                    FileAbs: mod.AboutPath, Line: null,
                    ModPackageId: e.PackageId, DefName: null,
                    Suggestions: new[] { $"{otherMod.PackageId} @ {otherMod.RootPath}" },
                    Evidence: new Dictionary<string, string>
                    {
                        ["kind"] = "loadAfter",
                        ["declaredBy"] = e.PackageId,
                        ["declaredTarget"] = otherMod.PackageId,
                        ["declared"] = $"loadAfter {otherMod.PackageId}",
                        ["actualOrder"] = $"{e.PackageId}={e.Order} ({e.Origin}) ; {otherMod.PackageId}={other.Value} ({Entries[other.Value].Origin})",
                        ["targetSource"] = otherMod.Source.ToString()
                    });
            }

            foreach (var dep in mod.LoadBefore)
            {
                if (string.Equals(dep, e.PackageId, StringComparison.OrdinalIgnoreCase)) continue;
                var other = RankOf(dep);
                if (other is null) continue;
                if (other.Value > e.Order) continue;   // 期望成立

                var otherMod = Entries[other.Value].Mod;
                yield return new Diagnostic(
                    Codes.LoadOrderViolation, Severity.Info, Scope.Offline,
                    $"{e.PackageId} 声明 loadBefore `{otherMod.PackageId}`，但实际加载顺序里它在对方**之后**"
                    + $"（声明：应在 {otherMod.PackageId} 之前；实际：序位 {e.Order} vs {other.Value}）",
                    "调整 ModsConfig.xml 的 activeMods 顺序，或删掉这条已失效的 loadBefore；此项为 info",
                    FileAbs: mod.AboutPath, Line: null,
                    ModPackageId: e.PackageId, DefName: null,
                    Suggestions: new[] { $"{otherMod.PackageId} @ {otherMod.RootPath}" },
                    Evidence: new Dictionary<string, string>
                    {
                        ["kind"] = "loadBefore",
                        ["declaredBy"] = e.PackageId,
                        ["declaredTarget"] = otherMod.PackageId,
                        ["declared"] = $"loadBefore {otherMod.PackageId}",
                        ["actualOrder"] = $"{e.PackageId}={e.Order} ({e.Origin}) ; {otherMod.PackageId}={other.Value} ({Entries[other.Value].Origin})",
                        ["targetSource"] = otherMod.Source.ToString()
                    });
            }
        }

        _ = targetPackageId;   // 过滤器由调用方（ShadowingCheck）统一施加，保持「一处口径」
    }
}
