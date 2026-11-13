namespace RimDev.Core.Checks;

/// <summary>
/// `check` 的扫描范围。
///
/// ★★ 三种语义必须无歧义（第 3 期 Task 2 Step 1，修 F1）：
/// <list type="bullet">
/// <item><b><see cref="Target"/></b> —— 目标 mod：扫【active 全集】，但报告只保留该 mod 参与的诊断。
///   这是第 2 期的默认行为；**不能**退回「只扫目标 mod」—— 那会让 ParentName 指向 Core/DLC 基类的 def
///   全部误报 RD2005（实测 `check ThreatFleet` 会得到 error 100）。</item>
/// <item><b><see cref="Active"/></b> —— 整个【启用】集合（本机 17 个 mod / 1655 XML）。</item>
/// <item><b><see cref="All"/></b> —— 整个【全库】（3971 个 mod / 31 万 def）—— 这正是第 2 期缺的能力（F1）。</item>
/// </list>
///
/// ★ 为什么枚举与解析放在 `RimDev.Core` 而不是 CLI 里：这样**自测不需要引用 CLI 工程**就能把
///   「`--all` 等价于 `--scope active`」这条语义钉死（自测工程只引 RimDev.Core）。
///   CLI 侧只保留一层转发（CheckCommand.ParseScope 等），保证「语义的实现只有一处」。
/// </summary>
public enum CheckScope { Target, Active, All }

/// <summary>
/// `--scope` / `--all` 的**唯一**解析实现。
///
/// <list type="bullet">
/// <item>无 `--scope`、无 `--all` → <see cref="CheckScope.Target"/></item>
/// <item>`--scope active`、或 `--all` → <see cref="CheckScope.Active"/>（**完全等价**，自测钉死）</item>
/// <item>`--scope all` → <see cref="CheckScope.All"/></item>
/// <item>`--scope target` → <see cref="CheckScope.Target"/></item>
/// <item>`--scope` 缺值 / 取值未知 / `--all` 与 `--scope` 互相矛盾 → 返回非空 error，调用方必须报参数错（退出码 3），
///   **不静默降级**（静默降级属「静默错答」类）</item>
/// </list>
/// </summary>
public static class CheckScopeSelector
{
    public static CheckScope Parse(string[] args, out string? error)
    {
        error = null;
        var hasAllAlias = args.Contains("--all");
        var hasScopeFlag = args.Contains("--scope");
        var scopeArg = Option(args, "--scope")?.ToLowerInvariant();

        if (hasScopeFlag && scopeArg is null)
        {
            error = "未知或缺值的 --scope（可选 target|active|all；--all 等价于 --scope active）";
            return CheckScope.Target;
        }

        switch (scopeArg)
        {
            case null:
                return hasAllAlias ? CheckScope.Active : CheckScope.Target;

            case "target":
                if (hasAllAlias)
                {
                    error = "--all（= --scope active）与 --scope target 互相矛盾，请只用一个";
                    return CheckScope.Target;
                }
                return CheckScope.Target;

            case "active":
                return CheckScope.Active;

            case "all":
                if (hasAllAlias)
                {
                    error = "--all（= --scope active）与 --scope all 互相矛盾，请只用一个";
                    return CheckScope.All;
                }
                return CheckScope.All;

            default:
                error = $"未知的 --scope: {scopeArg}（可选 target|active|all；--all 等价于 --scope active）";
                return CheckScope.Target;
        }
    }

    /// <summary>`--json` 里 `scope` 字段的**唯一**来源（CLI 不再自己拼字符串，避免两处分叉）。</summary>
    public static string Name(CheckScope scope) => scope switch
    {
        CheckScope.Active => "active",
        CheckScope.All => "all",
        _ => "target"
    };

    /// <summary>给人看的解释（`--help` 与文本输出共用，保证三者不会被描述成同一件事）。</summary>
    public static string Explanation(CheckScope scope) => scope switch
    {
        CheckScope.Active => "全部启用 mod（--all 是它的兼容别名）",
        CheckScope.All => "全库（本地 Mods + 官方 Data + 全部工坊 mod）",
        _ => "目标 mod（全集扫描 + 按目标过滤）"
    };

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
