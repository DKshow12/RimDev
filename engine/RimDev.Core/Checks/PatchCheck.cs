using RimDev.Core.Catalog;
using RimDev.Core.Diagnostics;
using RimDev.Core.Defs;

namespace RimDev.Core.Checks;

/// <summary>
/// 补丁层的体检规则（第 3 期 Task 4）：
/// <list type="bullet">
/// <item><c>RD2009</c>（warning）= 补丁操作的 XPath 在合并后的 DOM 上匹配 0 个节点（「补丁打空了」）；</item>
/// <item><c>RD2014</c>（info）= 自定义 <c>Class=</c>（mod 自带补丁类）或缺少 <c>Class=</c> —— 不执行 mod 代码，跳过。</item>
/// </list>
///
/// ★★ 求值范围（**这是本规则最重要的一条口径，必须写在报告里**）：
///   参与求值的 mod 集 = <b>本次扫描集合 ∩ 启用集合</b>。
///
///   为什么是「∩ 启用集合」而不是「整个扫描集合」：
///   <list type="number">
///   <item><b>引擎语义</b>：补丁只存在于**具体的启用配置**里 ——
///     <c>LoadedModManager.ApplyPatches</c> 遍历的是 <c>runningMods</c>（= 本机 17 个启用 mod）。
///     `--scope all` 的 3971 个 mod 是一个**引擎永远不会实现的假想配置**（同一 mod 的多个版本
///     不可能同时加载），把全部工坊 mod 的补丁打进同一棵树只会**凭空造出**引擎里不存在的诊断
///     —— 正是项目纪律里「假阳比漏报更贵」最忌讳的那一类。</item>
///   <item><b>成本实测</b>：全库补丁约 <b>18.8 万个</b> <c>&lt;Operation&gt;</c>，而
///     <c>XmlDocument.SelectNodes</c> 在一个 30 万节点的 DOM 上实测 **22.7 ms/次**
///     （300k 子节点 + <c>Count</c> 强制求值，200 次取平均）⇒ 一次全库求值约 <b>38 分钟</b>，
///     且 DOM 本身约 1 GB。⇒ 全库补丁求值不是「没做」，是**在 check 里不可行**；
///     它属于 Task 6 的 <c>sim</c>（带 XPath 快路径/缓存），本期不承诺。</item>
///   </list>
///   为了不产生「因为没跑所以是 0」的假 0，每条诊断的 evidence 都带
///   <c>evaluatedConfiguration=active∩scope</c>，<c>check --json</c> 顶层另有 <c>patchEvaluation</c> 字段说明这一点。
///
/// ★ 副作用纪律：本规则**不修改任何输入**，只是读文件建 DOM（PatchLayer 的 <c>Apply</c> 在内存里改）。
/// </summary>
public sealed class PatchCheck : ICheck
{
    public string Id => Codes.PatchXpathNoMatch;

    /// <summary>
    /// 本次跑完后补丁层的**实测统计**（PatchLayer 自己算的，不是 CLI 另算一遍）。
    ///
    /// ★ 为什么要有它：`RD2009 = 0` 必须能与「补丁层根本没跑」区分开 —— 否则就是项目纪律里
    ///   最忌讳的**假 0**。`check` 把这一整块打进 `--json` 的 <c>patchEvaluation</c> 与人类可读输出，
    ///   让「求值了几个文件 / 几条操作 / 补了几条 Name」始终可见。
    ///   nil 表示本规则因「扫描集合 ∩ 启用集合里没有任何补丁文件」而直接返回（那也是一种必须报出来的状态）。
    /// </summary>
    public PatchPhaseStats? LastRun { get; private set; }

    /// <summary>本次进入求值的 def / 补丁文件数（同上，nil = 没跑）。</summary>
    public (int DefFiles, int PatchFiles)? LastInputs { get; private set; }

    public IEnumerable<Diagnostic> Run(CheckContext ctx)
    {
        // ── 1) 求值范围：扫描集合 ∩ 启用集合 ──────────────────────────────────────────
        //    用 ctx.Files 的 packageId 作为「扫描集合」的真源（`--only-official` 会让它变窄，
        //    这一点很关键：用 ctx.Catalog.Active 直接算会把非官方的补丁漏进 `--only-official` 报告，
        //    与 RD2011 那条「报告范围」回归钉子是同一类陷阱）。
        var scopeMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in ctx.Files) scopeMods.Add(f.ModPackageId);

        var activeMods = ctx.Catalog.Active;
        var activeIds = new HashSet<string>(activeMods.Select(m => m.PackageId), StringComparer.OrdinalIgnoreCase);
        scopeMods.IntersectWith(activeIds);
        if (scopeMods.Count == 0) yield break;

        // ── 2) 文件分类：根 <Defs> = def 资产；根 <Patch> = 补丁 ─────────────────────
        //    DefScanner 只在「根不是 Defs」时填 RootKind（实测：Patches/*.xml 的 RootKind == "Patch"），
        //    所以 RootKind is null ⇔ 根是 <Defs>。解析失败的文件不参与（RD2001 已经报了它）。
        var defs = new List<(string FileAbs, string ModPackageId)>();
        var patches = new List<(string FileAbs, string ModPackageId, int Rank)>();
        foreach (var f in ctx.Files)
        {
            if (f.ParseError is not null) continue;
            if (!scopeMods.Contains(f.ModPackageId)) continue;

            if (string.Equals(f.RootKind, "Patch", StringComparison.Ordinal))
            {
                var rank = ctx.ModRank is not null && ctx.ModRank.TryGetValue(f.ModPackageId, out var r) ? r : int.MaxValue;
                patches.Add((f.FileAbs, f.ModPackageId, rank));
            }
            else if (f.RootKind is null)
            {
                defs.Add((f.FileAbs, f.ModPackageId));
            }
        }

        if (patches.Count == 0)
        {
            LastInputs = (defs.Count, 0);   // 明确记「跑过但没补丁可求值」，不是沉默
            yield break;
        }

        // ── 3) 顺序：引擎 `runningMods.SelectMany(rm => rm.Patches)` = mod 加载序 × 文件顺序 ──
        //    顺序会改变结果（同一个 XPath 被前后两个补丁改过时，「先被删掉」会让后一个打空）。
        var ordered = patches
            .OrderBy(p => p.Rank)
            .ThenBy(p => p.FileAbs, StringComparer.OrdinalIgnoreCase)
            .Select(p => (p.FileAbs, p.ModPackageId))
            .ToList();

        // ── 4) FindMod 的判据 = ModMetaData.Name（**显示名**，Ordinal 精确相等）────────
        //    反编译：`ModLister.HasActiveModWithName(name)` → `mod.Active && mod.Name == name`。
        //    注意引擎比的是 **Name** 不是 packageId（很多 mod 在 <mods> 里写 packageId ⇒ 恒不命中）。
        var activeNames = new HashSet<string>(activeMods.Select(m => m.DisplayName), StringComparer.Ordinal);

        LastInputs = (defs.Count, ordered.Count);
        var result = PatchLayer.Apply(defs, ordered, activeNames);
        LastRun = result.Stats;
        // ── 5) 报告过滤（CheckContext 契约：索引/求值在全集上做，报告只留目标 mod 参与的）──
        foreach (var d in result.Diagnostics)
        {
            if (ctx.TargetModPackageId is not null
                && !string.Equals(d.ModPackageId, ctx.TargetModPackageId, StringComparison.OrdinalIgnoreCase))
                continue;

            var evidence = d.Evidence is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(d.Evidence);
            evidence["evaluatedConfiguration"] = "active∩scope";
            evidence["defFiles"] = defs.Count.ToString();
            evidence["patchFiles"] = ordered.Count.ToString();

            yield return d with { Evidence = evidence };
        }
    }
}
