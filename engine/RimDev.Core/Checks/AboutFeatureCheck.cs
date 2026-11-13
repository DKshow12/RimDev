using RimDev.Core.Catalog;
using RimDev.Core.Diagnostics;

namespace RimDev.Core.Checks;

/// <summary>
/// ★★ 第 3 期 Task 26：<b>About.xml 的三个附加特性</b>的消费侧检查。
///
/// <para>这三个字段长期被忽略，因为它们不在 `About.xml` 的「标准」文档里 ——
/// 但实测（4,080 个 About.xml）它们是<strong>真实且大量</strong>在用的：</para>
/// <code>
/// incompatibleWith          302 个 mod   ← 中文社区最常见
/// modDependenciesByVersion  107 个 mod   ← 不读它，1.7 版依赖整段漏掉
/// alternativePackageIds       2 个 mod   ← 不读它，「写了备选名」被判成缺前置
/// </code>
///
/// <para>本规则只做三件事，每件都对应一个具体的错误方向：</para>
/// <list type="number">
/// <item><b>RD3002</b> 声明的依赖没启用 —— 假阴的修复（该报的没报）。</item>
/// <item><b>RD3003</b> 声明的冲突同时启用 —— 这是**唯一能提前发现冲突的手段**。</item>
/// <item><b>RD3004</b> 依赖写的是备选名 —— 假阳的修复（不该报的报了）。</item>
/// </list>
///
/// <para>★ 为什么 RD3004 必须是 <c>Info</c> 而不是 error：备选名能被解析是**正常**的，
/// 我们只是想知道「这条依赖是靠备选名解析的」。用户看到它也不该恐慌。</para>
/// </summary>
public sealed class AboutFeatureCheck : ICheck
{
    public string Id => "aboutFeatures";

    /// <summary>引擎的核心包：任何 mod 声明它是冗余的（它永远在）。</summary>
    private const string CoreAlias = "core";

    public IEnumerable<Diagnostic> Run(CheckContext ctx)
    {
        var cat = ctx.Catalog;
        if (cat is null) yield break;

        // 已启用的 packageId（大小写不敏感 —— ModsConfig 里全是小写，而 About.xml 保留原始大小写）
        var activeIds = new HashSet<string>(
            cat.Active.Select(m => m.PackageId), StringComparer.OrdinalIgnoreCase);

        // ★ 备选名 → 真正的 packageId。用于 RD3004 与「依赖已满足」判定。
        var altToReal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in cat.Active)
        {
            foreach (var alt in m.AlternativePackageIds ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(alt)) altToReal[alt] = m.PackageId;
            }
        }

        // 只看本次扫描集合里的 mod（--scope 决定），但「是否启用」看全局 active。
        foreach (var mod in cat.Active)
        {
            var aboutError = mod.AboutError;
            if (!string.IsNullOrEmpty(aboutError)) continue;   // 解析失败的已由 RD2007 报

            // ── 依赖：RD3002 / RD3004 ────────────────────────────────────────
            foreach (var (dep, fromVersionBlock) in EnumerateDependencies(mod))
            {
                var depTrim = dep.Trim();
                if (depTrim.Length == 0) continue;

                // core 是 ludeon.rimworld 的别名（实测 0 个 mod 用，但它是合法写法）
                var normalized = string.Equals(depTrim, CoreAlias, StringComparison.OrdinalIgnoreCase)
                    ? "Ludeon.RimWorld"
                    : depTrim;

                // 直接命中？
                if (activeIds.Contains(normalized)) continue;

                // 靠备选名命中？⇒ 不是缺前置，但值得说一声（Info）
                if (altToReal.TryGetValue(normalized, out var realId))
                {
                    yield return new Diagnostic(Codes.DependencyViaAlternativeId, Severity.Info, Scope.Offline,
                        $"依赖声明的 `{depTrim}` 是 `{realId}` 的备选 packageId（已启用）",
                        "这条依赖已满足，不需要额外安装；把声明改成正式 packageId 更清晰",
                        FileAbs: mod.AboutPath, ModPackageId: mod.PackageId,
                        Evidence: new Dictionary<string, string>
                        {
                            ["declaredId"] = depTrim,
                            ["resolvedTo"] = realId,
                            ["source"] = fromVersionBlock is null ? "modDependencies" : $"modDependenciesByVersion/{fromVersionBlock}",
                        });
                    continue;
                }

                // 真的缺 ⇒ RD3002
                yield return new Diagnostic(Codes.DependencyMissing, Severity.Warning, Scope.Offline,
                    $"声明的前置 mod `{depTrim}` 未启用",
                    "启用该前置，或确认它已改名 / 已合并；缺前置时本 mod 多数功能会静默失效",
                    FileAbs: mod.AboutPath, ModPackageId: mod.PackageId,
                    Evidence: new Dictionary<string, string>
                    {
                        ["missing"] = depTrim,
                        ["source"] = fromVersionBlock is null ? "modDependencies" : $"modDependenciesByVersion/{fromVersionBlock}",
                    });
            }

            // ── 冲突：RD3003 ────────────────────────────────────────────────
            foreach (var inc in mod.IncompatibleWith ?? Array.Empty<string>())
            {
                var incTrim = inc.Trim();
                if (incTrim.Length == 0) continue;

                var normalized = string.Equals(incTrim, CoreAlias, StringComparison.OrdinalIgnoreCase)
                    ? "Ludeon.RimWorld"
                    : incTrim;

                // 冲突方启用了吗（含备选名）
                var activeReal = activeIds.Contains(normalized)
                    ? normalized
                    : altToReal.TryGetValue(normalized, out var r) ? r : null;
                if (activeReal is null) continue;
                if (string.Equals(activeReal, mod.PackageId, StringComparison.OrdinalIgnoreCase)) continue;

                yield return new Diagnostic(Codes.IncompatibleModActive, Severity.Warning, Scope.Offline,
                    $"声明与 `{incTrim}` 不兼容，但两者都已启用",
                    "这是 mod 作者明确写下的冲突声明 —— 优先相信它，禁用其中一个",
                    FileAbs: mod.AboutPath, ModPackageId: mod.PackageId,
                    Evidence: new Dictionary<string, string>
                    {
                        ["incompatibleWith"] = incTrim,
                        ["activeAs"] = activeReal,
                    });
            }
        }
    }

    /// <summary>
    /// 罗列出该 mod 声明的全部依赖，并标注来源（<c>modDependencies</c> 或某个版本块）。
    ///
    /// ★ 为什么要合并两个来源：<c>modDependencies</c> 是「所有版本都适用」的基线，
    ///   <c>modDependenciesByVersion</c> 是「特定版本追加」。两者都要查，否则会漏。
    ///   实测 107 个 mod 用了后者 —— 只读前者会把这些 mod 的版本化依赖整段漏掉。
    /// </summary>
    private static IEnumerable<(string Dep, string? FromVersionBlock)> EnumerateDependencies(ModInfo mod)
    {
        foreach (var d in mod.Dependencies ?? Array.Empty<string>())
            yield return (d, null);

        if (mod.DependenciesByVersion is null) yield break;
        foreach (var (versionKey, deps) in mod.DependenciesByVersion)
        {
            foreach (var d in deps ?? Array.Empty<string>())
                yield return (d, versionKey);
        }
    }
}
