using RimDev.Core.Catalog;

namespace RimDev.Core.Defs;

/// <summary>
/// 一次折叠的结果。三个字段都是**只读快照**（不修改输入）。
/// </summary>
/// <param name="Kept">
/// 折叠后留下的声明，**保持输入顺序**（消费者用 <c>defs[0]</c> 当首条，顺序会影响诊断文本与 JSON）。
/// </param>
/// <param name="Dropped">
/// 被折叠掉的声明（= 同一 packageId 的**其它安装根**提供的、相对路径完全相同的副本），保持输入顺序。
/// </param>
/// <param name="Packages">
/// 本次折叠**涉及**（即真丢了声明）的 packageId，按首次出现顺序、已去重。
/// </param>
public sealed record InstallReplicaCollapseResult(
    IReadOnlyList<RawDef> Kept,
    IReadOnlyList<RawDef> Dropped,
    IReadOnlyList<string> Packages)
{
    /// <summary>被折叠的声明数（= <c>Dropped.Count</c>，给报告用的别名，避免调用方各写一遍 <c>.Count</c>）。</summary>
    public int DeclarationsDropped => Dropped.Count;

    /// <summary>本组是否真的发生了折叠。</summary>
    public bool Any => Dropped.Count > 0;
}

/// <summary>
/// 折叠「同一 packageId 的多份安装」互相重复的声明（第 3 期 Task 4b，用户决策 (b)）。
///
/// <para>
/// <b>要解决的痛点（实测，不是估计）</b>：本机工坊把 Combat Extended 装了两份
/// （<c>2890901044</c> 与 <c>3495749827</c>），两份 <c>About/About.xml</c> 的 packageId 都是
/// <c>CETeam.CombatExtended</c>，<c>Defs</c> 下各 357 个 <c>*.xml</c>、**相对路径 357/357 完全相同**。
/// <see cref="ModCatalog.Build"/> 在 packageId 重复时把胜者放进 <c>byPkg</c>、**败者仍留在 <c>catalog.All</c>**
/// ⇒ <c>--scope all</c> 把两份安装**都扫描**、都打上同一个 <c>ModPackageId</c>。
/// 于是「同一个 mod 内同一 (DefType, defName) 声明两次」这条判据（RD2026，error）把一次
/// **无害双装**放大成 3,878 条 error；RD2004 同时被放大 3,719 条。
/// </para>
///
/// <para>
/// <b>★ 判别键必须是「安装根不同」，不能只用「相对路径相同」（本项目已实测过这个坑）</b>：
/// 仅按 <c>(packageId, FileRelToMod)</c> 相同去判，会把<b>同一份安装内同一文件里声明两次</b>的真缺陷
/// 一起吞掉 —— 全库实测有 <b>8 条</b>这种形态，与跨安装副本在 <c>(pkg, rel)</c> 上**不可区分**。
/// 所以折叠条件 = <b>同 packageId + 同 <see cref="RawDef.FileRelToMod"/> + <see cref="RawDef.ModRoot"/> 不同</b>。
/// </para>
///
/// <para>
/// <b>为什么不实现「只评估一份安装」（决策 (a) 被明确否决）</b>：那会改变扫描集合与所有
/// 文件/def 计数（<c>scanned.xmlFiles</c> / <c>defs</c> / <c>files</c> / 指纹 / 缓存），
/// 本期明确不做。本折叠器<b>只影响报告口径</b>，不动扫描集合。
/// </para>
///
/// <para>
/// <b>口径（三条，缺一不可）</b>：
/// <list type="number">
/// <item>按 <c>(ModPackageId OrdinalIgnoreCase, FileRelToMod Ordinal)</c> 分桶；</item>
/// <item>桶内**不同的非空 ModRoot ≥ 2** ⇒ 只保留 <c>catalog.Get(pkg)?.RootPath</c> 那一份
///   （即目录表里的胜者安装；**没有匹配时保留输入顺序最早的一份** —— 例如调用方给了合成目录表），
///   其余全部入 <c>Dropped</c>；</item>
/// <item>桶内只有 1 个根（或全是 null，如测试/外部构造的 RawDef）⇒ <b>整桶不动</b>
///   —— 这是「不许吞掉真缺陷」的闸门。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>为什么是单一 static 实现</b>：<see cref="RimDev.Core.Checks.ShadowingCheck"/>（RD2026/RD2025）
/// 与 <see cref="RimDev.Core.Checks.DuplicateDefNameCheck"/>（RD2004）都要用同一条口径。
/// 复制第二套必然分叉（本项目历史上因「两套去重口径并存」栽过），所以只留这一处。
/// </para>
/// </summary>
public static class InstallReplicaCollapse
{
    /// <summary>
    /// 同 packageId、同相对路径的声明分到同一个桶。
    /// ★ packageId 用 OrdinalIgnoreCase（引擎与本工具全程如此），相对路径用 Ordinal
    ///   （Windows 上路径大小写不敏感，但「相对键」是引擎字典的键，必须按字面比 —— 与
    ///   <see cref="DefFileEnumerator"/> 的去重键口径保持一致）。
    /// </summary>
    private sealed class BucketKeyComparer : IEqualityComparer<(string PackageId, string FileRelToMod)>
    {
        public bool Equals((string PackageId, string FileRelToMod) x, (string PackageId, string FileRelToMod) y) =>
            string.Equals(x.PackageId, y.PackageId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.FileRelToMod, y.FileRelToMod, StringComparison.Ordinal);

        public int GetHashCode((string PackageId, string FileRelToMod) o) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(o.PackageId),
                StringComparer.Ordinal.GetHashCode(o.FileRelToMod));
    }

    private static readonly InstallReplicaCollapseResult Nothing =
        new(Array.Empty<RawDef>(), Array.Empty<RawDef>(), Array.Empty<string>());

    /// <summary>
    /// 折叠一组**同 (DefType, defName)** 的声明。
    ///
    /// 输入 &lt; 2 条时直接原样返回（不可能有重复），不分配任何字典。
    /// 永远不抛异常：目录表里查不到 packageId 只是退化为「保留输入顺序最早的一份」。
    /// </summary>
    public static InstallReplicaCollapseResult Collapse(
        IReadOnlyList<RawDef> declarations, ModCatalog? catalog)
    {
        if (declarations.Count < 2) return new InstallReplicaCollapseResult(declarations, Nothing.Dropped, Nothing.Packages);

        // 桶里存的是**输入下标**（不是 RawDef 引用）：RawDef 是 record，两个字段完全相同的声明会值相等，
        // 用引用/值集合都容易出错；用下标则与「保序」天然一致。
        var buckets = new Dictionary<(string PackageId, string FileRelToMod), List<int>>(new BucketKeyComparer());
        for (var i = 0; i < declarations.Count; i++)
        {
            var d = declarations[i];
            var key = (d.ModPackageId, d.FileRelToMod);
            if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<int>();
            list.Add(i);
        }

        HashSet<int>? droppedIdx = null;
        List<string>? packages = null;

        foreach (var bucket in buckets.Values)
        {
            if (bucket.Count < 2) continue;

            // 桶内不同的**非空**安装根（首次出现顺序）。
            var roots = new List<string>();
            foreach (var i in bucket)
            {
                var root = declarations[i].ModRoot;
                if (string.IsNullOrEmpty(root)) continue;
                if (!roots.Contains(root, StringComparer.OrdinalIgnoreCase)) roots.Add(root!);
            }

            // ★ 闸门：只有一个安装根（或一个都没有）⇒ 整桶不动。
            //   「同一份安装内同一文件里声明两次」正是这一支，必须继续报 RD2026。
            if (roots.Count < 2) continue;

            // 保留目录表胜者那一份；胜者根不在桶里（合成目录表 / 命令未提供目录表）时保留输入顺序最早的一份。
            var keepIdx = bucket[0];
            var winnerRoot = catalog?.Get(declarations[keepIdx].ModPackageId)?.RootPath;
            if (!string.IsNullOrEmpty(winnerRoot))
            {
                foreach (var i in bucket)
                {
                    if (!string.Equals(declarations[i].ModRoot, winnerRoot, StringComparison.OrdinalIgnoreCase)) continue;
                    keepIdx = i;
                    break;
                }
            }

            foreach (var i in bucket)
            {
                if (i == keepIdx) continue;
                droppedIdx ??= new HashSet<int>();
                droppedIdx.Add(i);

                var pkg = declarations[i].ModPackageId;
                packages ??= new List<string>();
                if (!packages.Contains(pkg, StringComparer.OrdinalIgnoreCase)) packages.Add(pkg);
            }
        }

        if (droppedIdx is null)
            return new InstallReplicaCollapseResult(declarations, Nothing.Dropped, Nothing.Packages);

        // ★ 保序：按输入下标重建两个列表。
        var kept = new List<RawDef>(declarations.Count - droppedIdx.Count);
        var dropped = new List<RawDef>(droppedIdx.Count);
        for (var i = 0; i < declarations.Count; i++)
        {
            if (droppedIdx.Contains(i)) dropped.Add(declarations[i]);
            else kept.Add(declarations[i]);
        }

        return new InstallReplicaCollapseResult(kept, dropped, packages ?? new List<string>());
    }
}
