using System.Text;
using RimDev.Cli.Commands;

namespace RimDev.Cli;

/// <summary>
/// 一个命令认得的**一个**开关。
/// <para>
/// <see cref="TakesValue"/> 只用于文案（把 `--scope` 显示成 `--scope &lt;target|active|all&gt;`），
/// **不参与判定**：取值校验仍然属于各命令自己（未知取值一律退出码 3，既有语义不变）。
/// </para>
/// </summary>
public sealed record FlagSpec(string Name, bool TakesValue = false, string? ValueHint = null)
{
    /// <summary>给人看的语法：`--json` / `--scope &lt;target|active|all&gt;`。</summary>
    public string Syntax => !TakesValue ? Name : ValueHint is null ? Name + " <值>" : $"{Name} <{ValueHint}>";
}

/// <summary>
/// ★★ 第 3 期 Task 9c：**一条命令认得的开关集合（白名单）的唯一声明点**。
///
/// <para>
/// 背景（主理人实测）：`GlobalOptions` 的设计是「不认识的开关原样留在 Args 里交给未知命令分支」，
/// 但那个分支**只在命令前的位置生效**。开关放在命令之后时，`check` 等命令把自己的 argv 当普通参数
/// 解析，未知 `--xxx` 被**静默跳过** —— 于是：
/// </para>
/// <code>
/// rimdev.exe check --scope active --game-rot C:\__typo__      ← 把 --game-root 拼错
/// scope active / 扫描 9 个 mod / 561 个 XML / 6727 个 def / error 399   （exit=2，与不带开关时一模一样）
/// </code>
/// <para>
/// 用户拿到的是一份看起来完全正常的报告，**没有一个字提示他扫错了安装**。这与 Task 9/9b 的
/// 「`--game-root` 被静默忽略」是同一类静默错答，只是入口从「命令不读开关」变成了「开关根本不属于这条命令」。
/// </para>
///
/// <para>
/// ★ 白名单**必须来自命令自己的实现**：每个命令文件里有一个 `public static readonly CommandFlagSet Flags`，
/// 紧挨着读这些开关的代码；校验器（<see cref="CommandFlags"/>）只从注册表取，不手抄第二份清单。
/// 帮助标志（`--help` / `-h` / `--?`）由本类构造器**统一追加**，任何命令都不可能漏掉（也不会出现
/// 「声明了 help 却没实现」的半截状态：打印动作就是构造时传进来的 <see cref="Help"/>）。
/// </para>
/// </summary>
public sealed class CommandFlagSet
{
    /// <summary>所有命令共有的帮助标志。由构造器追加，**命令声明里不要再写一遍**。</summary>
    public static readonly IReadOnlyList<FlagSpec> HelpFlags =
        new[] { new FlagSpec("--help"), new FlagSpec("-h"), new FlagSpec("--?") };

    private readonly Dictionary<string, FlagSpec> _byName;

    /// <param name="command">命令名（= argv[0]）。</param>
    /// <param name="help">`--help` 的打印动作（不依赖环境探测，所以帮助在没有安装时也能看）。</param>
    /// <param name="flags">**本命令自己**认得的开关（不含帮助标志）。</param>
    public CommandFlagSet(string command, Action help, params FlagSpec[] flags)
    {
        Command = command;
        Help = help;
        Flags = flags.Concat(HelpFlags).ToArray();
        // 重名 = 声明错（同一个开关写了两遍）：构造期就炸，不静默取最后一个。
        _byName = new Dictionary<string, FlagSpec>(StringComparer.Ordinal);
        foreach (var f in Flags)
        {
            if (!_byName.TryAdd(f.Name, f))
                throw new InvalidOperationException($"{command}: 开关 {f.Name} 被重复声明");
        }
    }

    /// <summary>命令名（argv[0]）。</summary>
    public string Command { get; }

    /// <summary>`--help` / `-h` / `--?` 的打印动作。</summary>
    public Action Help { get; }

    /// <summary>全部认得的开关（含帮助标志），按声明顺序。</summary>
    public IReadOnlyList<FlagSpec> Flags { get; }

    public FlagSpec? Find(string name) => _byName.TryGetValue(name, out var spec) ? spec : null;

    public bool Declares(string name) => _byName.ContainsKey(name);

    /// <summary>单行枚举（`--scope &lt;…&gt; / --all / --json / --help / …`）—— 报错文案与自测共用。</summary>
    public string SyntaxList() => string.Join(" / ", Flags.Select(f => f.Syntax));

    /// <summary>`--flag=value` → `--flag`（判定只关心开关名）；其余原样。</summary>
    public static string Head(string token)
    {
        if (token.Length > 1 && token[0] == '-' && token.IndexOf('=') is var i and > 1)
            return token[..i];
        return token;
    }

    /// <summary>形如开关的 token：`-x` / `--xxx`；单独的 `--` 不算开关（它是「字面量分界」的常见写法）。</summary>
    public static bool LooksLikeFlag(string token) =>
        token.Length > 1 && token[0] == '-' && token != "--";

    public static bool IsHelpFlag(string token) => HelpFlags.Any(f => f.Name == Head(token));
}

/// <summary>
/// ★★ 第 3 期 Task 9c：**「命令 → 它认得的开关」的注册表 + 唯一校验器**。
///
/// <para>
/// 三件事各只有一处实现：
/// <list type="number">
/// <item><b>注册表</b>：<see cref="Registered"/> 直接引用各命令自己声明的 <see cref="CommandFlagSet"/>
///   （不手抄清单；新增命令若忘了登记，自测有一条「注册表覆盖 Usage 里列出的全部命令」把它钉红）。</item>
/// <item><b>校验</b>：<see cref="FindUnknown"/> 逐 token 判定「形如开关却既不是本命令声明、也不是全局开关」，
///   返回第一个命中的 token；<see cref="CommandLine"/> 拿到它就报退出码 3 并**不执行命令**。</item>
/// <item><b>文案</b>：<see cref="UnknownFlagMessage"/> 点名那个开关 + 给出最相近的候选 + 复用
///   <c>GlobalOptions</c> 的「全局开关可放在命令前/命令后」措辞。</item>
/// </list>
/// </para>
///
/// ★ 为什么校验是**逐 token** 而不是「先取第一个以 `--` 开头的」：`--game-rot` 只是众多误用里最贵的一种；
///   `--only-officail`、`--jsno` 这类拼错若被吞掉，同样会让使用者拿着一份「看起来正常」的报告下结论。
/// ★ `--flag=value` 与 `--flag value` 由**同一份白名单**校验（<see cref="CommandFlagSet.Head"/> 只取开关名）；
///   等号形式还会在 <c>GlobalOptions.Parse</c> 里归一化成两个 token，让各命令的取值逻辑不必写两遍。
/// ★ `--` 之后不再特殊处理：本 CLI 没有「位置参数里带开关形状」的用法，故不做 `--` 语义；
///   单独的 `--` 会作为普通 token 传给命令（历史行为不变）。
/// </summary>
public static class CommandFlags
{
    /// <summary>各命令自己声明的开关集合（新增命令必须在此登记，否则它会被当成未知命令）。</summary>
    private static readonly CommandFlagSet[] Sets =
    {
        EnvCommand.Flags,
        ApiCommand.Flags,
        NameCommand.Flags,
        IndexCommand.Flags,
        FilesCommand.Flags,
        CheckCommand.Flags,
        OrderCommand.Flags,
        // ★ Task 28：依赖关系查询。
        //   忘了这一行会静默退化成「未知命令」——
        //   正是本次实测撞到的（分派写了、Sets 没登记）。
        DepsCommand.Flags,
        SimCommand.Flags,
        LogCommand.Flags,
    };

    /// <summary>全部已登记的命令（自测用来钉「注册表覆盖 Usage」）。</summary>
    public static IReadOnlyList<CommandFlagSet> Registered => Sets;

    /// <summary>
    /// ★ 防漂移：<see cref="CommandLine.Usage"/> 里 `&lt;env|api|…&gt;` 列出的命令名（命令清单的单一权威）。
    /// 自测拿它对 <see cref="Registered"/>：新增命令若忘了登记白名单，就会当场变红 ——
    /// 否则它会悄无声息地退化成「未知命令」（拿不到白名单就进不了分发）。
    /// </summary>
    public static IReadOnlyList<string> ListedCommands()
    {
        var usage = CommandLine.Usage;
        var open = usage.IndexOf('<');
        var close = open < 0 ? -1 : usage.IndexOf('>', open + 1);
        if (open < 0 || close <= open) return Array.Empty<string>();
        return usage[(open + 1)..close]
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
    /// <summary>命令 → 它认得的开关；不认识该命令时返回 null（交给未知命令分支）。</summary>
    public static CommandFlagSet? For(string command) =>
        Sets.FirstOrDefault(s => string.Equals(s.Command, command, StringComparison.Ordinal));

    /// <summary>
    /// 返回**第一个**「形如开关、却不被这条命令接受」的 token（null = 全部认得）。
    /// 全局开关此时已经由 <see cref="GlobalOptions"/> 摘掉，这里再放行一次纯属防御
    /// （万一将来有命令想把自己的 argv 直接交给校验器）。
    /// </summary>
    public static string? FindUnknown(CommandFlagSet set, IReadOnlyList<string> args)
    {
        // args[0] = 命令本身；从 1 开始逐 token 看。
        for (var i = 1; i < args.Count; i++)
        {
            var token = args[i];
            if (!CommandFlagSet.LooksLikeFlag(token)) continue;
            var head = CommandFlagSet.Head(token);
            if (CommandFlagSet.IsHelpFlag(head)) return null;              // 帮助：交给调用方打印后返回 0
            if (set.Declares(head)) continue;
            if (GlobalOptions.AllFlagNames.Contains(head, StringComparer.Ordinal)) continue;
            return token;
        }
        return null;
    }

    /// <summary>
    /// 未知开关的完整报错（**第一行**点名开关 + 候选，后面是「这条命令认得什么」与
    /// 「全局开关可放在命令前/后」）。调用方把整段打到 stderr，然后退出码 3、**不执行命令**。
    /// </summary>
    public static string UnknownFlagMessage(CommandFlagSet set, string token)
    {
        var name = CommandFlagSet.Head(token);
        var sb = new StringBuilder();
        sb.Append("未知开关: ").Append(name)
          .Append("（").Append(set.Command).Append(" 不认识它");
        if (Suggest(set, name) is { } candidate)
            sb.Append("，你是不是想写 ").Append(candidate).Append('？');
        sb.Append('）');
        sb.AppendLine();
        sb.Append("  ").Append(set.Command).Append(" 认得的开关：").Append(set.SyntaxList());
        sb.AppendLine();
        sb.Append("  全局开关（").Append(GlobalOptions.PublicFlagList)
          .Append("）**可以放在命令前，也可以放在命令后**：");
        sb.AppendLine();
        sb.Append("    rimdev --game-root <游戏根> ").Append(set.Command).Append(" …");
        sb.AppendLine();
        sb.Append("    rimdev ").Append(set.Command).Append(" … --game-root <游戏根>        （两者等价）");
        return sb.ToString();
    }

    /// <summary>在「本命令认得的开关 + 全局开关」里找最相近的一个（编辑距离 ≤ 2 才给建议，否则不猜）。</summary>
    private static string? Suggest(CommandFlagSet set, string token)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in set.Flags.Select(f => f.Name).Concat(GlobalOptions.AllFlagNames))
        {
            if (string.Equals(candidate, token, StringComparison.Ordinal)) continue;
            var d = EditDistance(token, candidate);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = candidate;
            }
        }
        return bestDistance <= 2 ? best : null;
    }

    /// <summary>Levenshtein 距离（开关名都很短，朴素 DP 足够）。</summary>
    private static int EditDistance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
