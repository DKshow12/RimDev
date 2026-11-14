using RimDev.Core.Catalog;

namespace RimDev.Cli.Commands;

/// <summary>
/// <c>rimdev deps</c> —— 依赖关系查询（正向：我依赖谁 / 反向：谁依赖我）。
///
/// <para>★ 为什么补这个命令：<see cref="ModInfo.Dependencies"/> 与
/// <c>DependenciesByVersion</c> 的数据**一直在内存里**（Task 26 刚补全后者），
/// 但从未暴露给用户 —— <c>order</c> 的输出里一个依赖字段都没有。
/// 于是「我这个 mod 到底缺哪个前置」/「我改了 X，哪些 mod 会受影响」
/// 这两个最常见的问题，只能用 grep 手工找。</para>
///
/// <para>★ 与同类工具（RimDevTools 的 <c>mod dependencies</c>）的差异：
/// 我们**不限定本地 mod**，可以查工坊 mod；而且支持反向查询（dependents）。
/// 反向查询是「安全改动」的前提：改了某个 def 前，先知道谁在依赖它。</para>
///
/// <para>★ 版本语义：<c>modDependenciesByVersion</c> 是按版本块分组的
/// （<c>v1.1</c> / <c>v1.7</c> / <c>default</c>），用 <c>--version</c> 指定要看哪块；
/// 不给就只列 <c>modDependencies</c>（所有版本通用的基线）。</para>
/// </summary>
public static class DepsCommand
{
    /// <summary>
    /// ★ 开关白名单。未知开关与 <c>--help</c> 由 <c>CommandLine</c> 统一拦截，
    /// 命令内部不需要自己判 —— 这是 9c 的纪律（白名单必须来自实现本身）。
    /// </summary>
    public static readonly CommandFlagSet Flags = new("deps", PrintHelp,
        new FlagSpec("--mod", true, "packageId 片段（不区分大小写）"),
        new FlagSpec("--reverse"),
        new FlagSpec("--version", true, "v1.7"),
        new FlagSpec("--scope", true, "active|all"),
        new FlagSpec("--include-disabled"),
        new FlagSpec("--json"));

    public static void PrintHelp()
    {
        Console.WriteLine("""
            rimdev deps --mod <片段> [options]

            查一个 mod 的前置依赖（默认）或被谁依赖（--reverse）。

            两种查询：
              --mod X                X 依赖谁（正向）
              --mod X --reverse      谁依赖 X（反向）

            选项：
              --version <键>         看 modDependenciesByVersion 的哪个版本块（如 v1.7）
                                     不给则只列 modDependencies 基线
              --scope active|all     active=只查启用集（默认）｜all=含未启用的工坊 mod
              --include-disabled     列出依赖但【未启用】的前置（默认就列，此开关是冗余保险）
              --json                 机器可读输出

            退出码：0 查到；2 没查到该 mod；3 参数错；4 环境不满足

            例：
              rimdev deps --mod ratkin
              rimdev deps --mod brrainz.harmony --reverse
              rimdev deps --mod somemod --version v1.7
            """);
    }

    public static int Run(EnvironmentResolution cli, string[] args)
    {
        var needle = Option(args, "--mod");
        if (string.IsNullOrWhiteSpace(needle))
        {
            Console.Error.WriteLine("deps 需要 --mod <packageId 片段>");
            Console.Error.WriteLine("  例：rimdev deps --mod ratkin        （它依赖谁）");
            Console.Error.WriteLine("      rimdev deps --mod ratkin --reverse （谁依赖它）");
            return 3;
        }
        needle = needle.Trim();

        var reverse = args.Contains("--reverse");
        var scopeAll = args.Contains("--scope") && Option(args, "--scope") is { } s
                       && string.Equals(s, "all", StringComparison.OrdinalIgnoreCase);
        var wantVersion = Option(args, "--version");
        var wantJson = args.Contains("--json");

        // ── 环境 ────────────────────────────────────────────────────────────
        // ★ 与其他命令一致：环境不满足时返回 4（不报堆栈）。
        if (!cli.TryGetEnv(out var env)) return EnvironmentResolution.EnvExitCode;
        var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
        var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;
        var pool = scopeAll ? cat.All : cat.Active;

        // ── 解析目标 mod（片段匹配，不区分大小写）────────────────────────────
        var matches = pool
            .Where(m => m.PackageId.Contains(needle, StringComparison.OrdinalIgnoreCase)
                     || m.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            Console.Error.WriteLine($"没找到 packageId 或显示名里含 \"{needle}\" 的 mod（范围：{(scopeAll ? "全库" : "启用集")}）");
            Console.Error.WriteLine("  提示：加 --scope all 可以查未启用的工坊 mod");
            return 2;
        }

        // 精确命中优先（避免 --mod harmony 命中一堆）
        var exact = matches.FirstOrDefault(m =>
            string.Equals(m.PackageId, needle, StringComparison.OrdinalIgnoreCase));
        var target = exact ?? matches[0];

        // ── 反向：谁依赖 target ──────────────────────────────────────────────
        if (reverse)
        {
            var dependents = new List<(ModInfo Mod, string Id, string? VersionBlock)>();
            foreach (var m in pool)
            {
                foreach (var (dep, block) in EnumerateDeps(m, wantVersion))
                {
                    if (string.Equals(dep.Trim(), target.PackageId, StringComparison.OrdinalIgnoreCase))
                        dependents.Add((m, dep, block));
                }
            }

            if (wantJson)
            {
                Console.WriteLine(JsonEscape(new
                {
                    target = target.PackageId,
                    displayName = target.DisplayName,
                    active = target.IsActive,
                    direction = "dependents",
                    count = dependents.Count,
                    dependents = dependents.Select(d => new
                    {
                        packageId = d.Mod.PackageId,
                        displayName = d.Mod.DisplayName,
                        active = d.Mod.IsActive,
                        declaredAs = d.Id,
                        versionBlock = d.VersionBlock,
                    }).ToList(),
                }));
                return 0;
            }

            Console.WriteLine($"谁依赖 {target.PackageId}（{target.DisplayName}）");
            Console.WriteLine($"  范围            {(scopeAll ? "全库" : "启用集")} / {pool.Count} 个 mod");
            Console.WriteLine($"  依赖它的 mod    {dependents.Count} 个");
            if (dependents.Count > 0)
            {
                Console.WriteLine();
                foreach (var d in dependents.OrderBy(x => x.Mod.PackageId, StringComparer.OrdinalIgnoreCase))
                {
                    var act = d.Mod.IsActive ? "启用" : "未启用";
                    var blk = d.VersionBlock is null ? "" : $"  [{d.VersionBlock}]";
                    Console.WriteLine($"  {d.Mod.PackageId,-46} {act}{blk}");
                }
            }
            Console.WriteLine();
            Console.WriteLine("  提示：反向查询是「安全改动」的前提 —— 改 target 的 def 前先看这里");
            return 0;
        }

        // ── 正向：target 依赖谁 ──────────────────────────────────────────────
        var deps = EnumerateDeps(target, wantVersion).ToList();

        // 判断每个依赖是否已启用（这是我们对 RimDevTools 的一个优势：
        // 它会报「解析不到」，而我们可以直接说「这个前置在磁盘上但没启用」）
        var activeIds = new HashSet<string>(cat.Active.Select(m => m.PackageId), StringComparer.OrdinalIgnoreCase);
        var allIds = new HashSet<string>(cat.All.Select(m => m.PackageId), StringComparer.OrdinalIgnoreCase);
        var altToReal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in cat.Active)
            foreach (var alt in m.AlternativePackageIds ?? Array.Empty<string>())
                if (!string.IsNullOrWhiteSpace(alt)) altToReal[alt] = m.PackageId;

        var rows = deps.Select(d =>
        {
            var id = d.Dep.Trim();
            var norm = string.Equals(id, "core", StringComparison.OrdinalIgnoreCase) ? "Ludeon.RimWorld" : id;
            var status = activeIds.Contains(norm) ? "已启用"
                       : altToReal.TryGetValue(norm, out var real) ? $"已启用（经备选名 {real}）"
                       : allIds.Contains(norm) ? "未启用"
                       : "缺失";
            return (Id: id, Block: d.Block, Status: status);
        }).ToList();

        if (wantJson)
        {
            Console.WriteLine(JsonEscape(new
            {
                target = target.PackageId,
                displayName = target.DisplayName,
                active = target.IsActive,
                direction = "dependencies",
                versionFilter = wantVersion,
                count = rows.Count,
                dependencies = rows.Select(r => new
                {
                    packageId = r.Id,
                    versionBlock = r.Block,
                    status = r.Status,
                }).ToList(),
            }));
            return 0;
        }

        Console.WriteLine($"{target.PackageId}（{target.DisplayName}）的依赖");
        Console.WriteLine($"  状态            {(target.IsActive ? "已启用" : "未启用")}");
        if (wantVersion is not null)
            Console.WriteLine($"  版本块          {wantVersion}");
        Console.WriteLine($"  依赖数          {rows.Count} 个");
        if (rows.Count > 0)
        {
            Console.WriteLine();
            foreach (var r in rows)
            {
                var blk = r.Block is null ? "" : $"  [{r.Block}]";
                var bad = r.Status is "缺失" or "未启用";
                Console.WriteLine($"  {(bad ? "!" : " ")} {r.Id,-42} {r.Status}{blk}");
            }
            if (rows.Any(r => r.Status is "缺失" or "未启用"))
            {
                Console.WriteLine();
                Console.WriteLine("  ! 标记的是没启用的前置 —— 缺前置时本 mod 多数功能会静默失效");
            }
        }
        return 0;
    }

    /// <summary>
    /// 罗列一个 mod 声明的依赖，标注来源块。
    ///
    /// ★ <c>wantVersion</c> 语义：只列该版本块 + 基线 <c>modDependencies</c>。
    ///   —— 因为 <c>modDependencies</c> 是「所有版本通用」，任何版本都该带上；
    ///   而 <c>modDependenciesByVersion</c> 的不同块是互斥的（v1.6 的依赖不该出现在 v1.7）。
    /// </summary>
    private static IEnumerable<(string Dep, string? Block)> EnumerateDeps(ModInfo m, string? wantVersion)
    {
        foreach (var d in m.Dependencies ?? Array.Empty<string>())
            yield return (d, null);

        if (m.DependenciesByVersion is null) yield break;

        foreach (var (key, list) in m.DependenciesByVersion)
        {
            if (wantVersion is not null && !string.Equals(key, wantVersion, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var d in list ?? Array.Empty<string>())
                yield return (d, key);
        }
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string JsonEscape(object v)
        => System.Text.Json.JsonSerializer.Serialize(v,
            new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
}
