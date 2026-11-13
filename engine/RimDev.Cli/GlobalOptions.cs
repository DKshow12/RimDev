namespace RimDev.Cli;

/// <summary>
/// ★★ 第 3 期 Task 9b：**全局开关的唯一解析点**。
///
/// 缺陷背景（主理人实测）：<c>--game-root</c> / <c>--workshop-root</c> 只被 <c>env</c> 采纳，
/// 其余命令（<c>check</c> / <c>sim</c> / <c>index</c> / <c>files</c> / <c>order</c>）**静默忽略**，
/// 照常分析「自动探测到的那份安装」并给出完全正常的输出 ——
/// <c>rimdev --profile 1.6 check …</c> 的整个卖点（指向另一份安装）就此失效，而用户无法从输出里察觉。
///
/// 本类的契约（三项，缺一即「参数层静默」）：
/// <list type="number">
/// <item><b>一处解析</b>：全局开关只在这里识别一次，其余任何地方都不许再扫 argv 找它们。</item>
/// <item><b>两个位置等价</b>：<c>rimdev --game-root X &lt;命令&gt; …</c> 与
///   <c>rimdev &lt;命令&gt; … --game-root X</c> 必须等价（<c>--workshop-root</c> / <c>--profile</c> 同理）。
///   后者是 shim（<c>rimdev.cmd</c> → <c>rimdev.ps1</c>）实际使用的形状 —— 它把
///   <c>--rimdev-profile / --rimdev-invoked-via / --workshop-root / --game-root</c> 追加在用户参数**之后**。</item>
/// <item><b>未知开关响亮失败</b>：本类只管<b>全局</b>开关；其余形如开关的 token 原样留在 <see cref="Args"/> 里，
///   由两处分别拦截 —— 写在**命令位置**时走「未知命令」分支；写在**命令之后**时由
///   <see cref="CommandFlags"/>（每条命令自己的白名单，第 3 期 Task 9c）报退出码 3。
///   两处文案都说明「全局开关可放在命令前或命令后」（否则用户会以为位置写错了）。</item>
/// </list>
///
/// ★ 冲突口径：同一开关出现多次时**首次出现优先**。理由是可判别的一侧 ——
///   shim 会在末尾追加它从 profile 里算出的 <c>--game-root</c>，而用户显式写在命令行里的那份在前；
///   「显式参数优先」比「后写的覆盖先写的」更符合直觉，也让「shim + 用户显式开关」不至于被悄悄改写。
/// </summary>
public sealed class GlobalOptions
{
    public const string GameRootFlag = "--game-root";
    public const string WorkshopRootFlag = "--workshop-root";
    public const string ProfileFlag = "--profile";
    /// <summary>shim 转发用的 profile 名（与 <see cref="ProfileFlag"/> 同义，两个都接受）。</summary>
    public const string ShimProfileFlag = "--rimdev-profile";
    public const string ShimInvokedViaFlag = "--rimdev-invoked-via";

    /// <summary>
    /// ★ 第 3 期 Task 9c：全局开关的**规范名清单**（唯一一份）。
    /// <para>
    /// 两个消费者共用它，免得再手抄第三份：① 命令白名单校验器（<see cref="CommandFlags"/>）
    /// 要把它当作「除本命令声明之外还认得的东西」；② 报错文案要数出「全局开关只有这几个」。
    /// </para>
    /// <para>
    /// 含 shim 专用的两个 `--rimdev-*`：它们是 shim（`rimdev.ps1`）追加在用户参数**之后**的，
    /// 所以校验器必须认得它们，否则每个经 shim 的调用都会变成「未知开关」。
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> AllFlagNames = new[]
    {
        GameRootFlag, WorkshopRootFlag, ProfileFlag, ShimProfileFlag, ShimInvokedViaFlag,
    };

    /// <summary>面向用户的三个全局开关（报错文案用；shim 内部的 `--rimdev-*` 不在其中）。</summary>
    public const string PublicFlagList = "--game-root / --workshop-root / --profile";

    /// <summary><c>--game-root</c>；null = 没传（由环境变量 / 自动探测决定）。</summary>
    public string? GameRoot { get; private init; }

    /// <summary><c>--workshop-root</c>；null = 没传（由环境变量 / Steam 布局推导）。</summary>
    public string? WorkshopRoot { get; private init; }

    /// <summary>生效 profile 名（命令行 <c>--profile</c> / shim 的 <c>--rimdev-profile</c> / 环境变量）。</summary>
    public string? Profile { get; private init; }

    /// <summary><c>--rimdev-invoked-via</c>（shim 用来标注「这次是经切换入口进来的」）。</summary>
    public string? InvokedVia { get; private init; }

    /// <summary>
    /// 全局开关被摘掉后的参数；<c>Args[0]</c> = 子命令。
    /// ★ 第 3 期 Task 9c：<c>--flag=value</c> 已在这里归一化为 <c>--flag value</c> 两个 token，
    ///   所以各命令的 <c>Option(args, "--flag")</c> 对两种写法同样生效（修前等号形式会被静默忽略）。
    /// </summary>
    public string[] Args { get; private init; } = Array.Empty<string>();

    /// <summary>解析错误（null = 没问题）。缺值时**必须**报参数错，不能猜一个默认值。</summary>
    public string? Error { get; private init; }

    public bool HasError => Error is not null;

    /// <summary>解析一次；永不抛异常（错误体现在 <see cref="Error"/> 上，由调用方转成退出码 3）。</summary>
    public static GlobalOptions Parse(string[] argv)
    {
        var rest = new List<string>(argv.Length);
        string? gameRoot = null, workshopRoot = null, profile = null, invokedVia = null;

        for (var i = 0; i < argv.Length; i++)
        {
            var token = argv[i];

            if (Matches(token, GameRootFlag, out var rootInline))
            {
                if (!TakeValue(argv, ref i, token, rootInline, out var value, out var error))
                    return Fail(error!);
                gameRoot ??= value;
                continue;
            }

            if (Matches(token, WorkshopRootFlag, out var wsInline))
            {
                if (!TakeValue(argv, ref i, token, wsInline, out var value, out var error))
                    return Fail(error!);
                workshopRoot ??= value;
                continue;
            }

            // `--profile` 与 shim 的 `--rimdev-profile` 同义：**都**必须能放在命令前（shim 依赖前者，
            // 而 shim 自己是把后者放在命令后的）。
            if (Matches(token, ProfileFlag, out var pInline) || Matches(token, ShimProfileFlag, out pInline))
            {
                if (!TakeValue(argv, ref i, token, pInline, out var value, out var error))
                    return Fail(error!);
                profile ??= value;
                continue;
            }

            if (Matches(token, ShimInvokedViaFlag, out var viaInline))
            {
                if (!TakeValue(argv, ref i, token, viaInline, out var value, out var error))
                    return Fail(error!);
                invokedVia ??= value;
                continue;
            }
            // ★ 第 3 期 Task 9c：`--flag=value` 在这里**归一化**成两个 token（只对开关形状的 token）。
            //   理由：各命令取值一律走 `Option(args, "--flag")`（= 找相邻两个 token），
            //   等号形式若原样传下去就会「存在但取不到值」，等于另一种静默——修前 `--scope=active`
            //   就是被静默忽略、退回默认值的。归一化后两种形式对命令完全等价，校验器也只需一份白名单。
            if (TrySplitInlineValue(token, out var head, out var inlineValue))
            {
                rest.Add(head);
                if (inlineValue is not null) rest.Add(inlineValue);
                continue;
            }

            rest.Add(token);
        }

        return new GlobalOptions
        {
            GameRoot = gameRoot,
            WorkshopRoot = workshopRoot,
            Profile = profile,
            InvokedVia = invokedVia,
            Args = rest.ToArray(),
        };
    }

    /// <summary>
    /// <c>--flag</c>（<paramref name="inline"/> 为 null，值在下一个 token）或 <c>--flag=value</c>。
    /// </summary>
    private static bool Matches(string token, string flag, out string? inline)
    {
        inline = null;
        if (token == flag) return true;
        if (token.StartsWith(flag + "=", StringComparison.Ordinal))
        {
            inline = token[(flag.Length + 1)..];
            return true;
        }
        return false;
    }

    /// <summary>
    /// ★ 第 3 期 Task 9c：把 `--flag=value` 拆成 `--flag` + `value`（等号形式与空格形式**等价**）。
    /// 只有「形如开关」的 token 才拆：单个 `-`、裸 `--`、以及不以 `-` 开头的普通参数一律原样。
    /// 空值（如 `--scope=`）只留下开关本身 —— 后续「缺值」校验会照常报参数错，**不静默当成空串**。
    /// </summary>
    private static bool TrySplitInlineValue(string token, out string head, out string? value)
    {
        head = token;
        value = null;
        if (token.Length <= 2 || token[0] != '-' || token == "--") return false;
        var eq = token.IndexOf('=', 1);
        if (eq <= 1) return false;
        head = token[..eq];
        var tail = token[(eq + 1)..];
        value = tail.Length == 0 ? null : tail;
        return true;
    }

    /// <summary>取值；缺值 / 空值 / 下一个 token 是另一个开关 ⇒ 参数错（不静默降级）。</summary>
    private static bool TakeValue(string[] argv, ref int i, string flag, string? inline,
        out string? value, out string? error)
    {
        value = null;
        error = null;

        var raw = inline;
        if (raw is null)
        {
            // ★ 不能把下一个 `--xxx` 当成值：`--game-root --json` 必须报错，而不是把 "--json" 当路径。
            if (i + 1 >= argv.Length || argv[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"{flag} 需要一个值（例如 {flag} {DefaultExample(flag)}）；"
                        + "全局开关可放在命令前，也可放在命令后。";
                return false;
            }

            i++;
            raw = argv[i];
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = $"{flag} 的值不能为空；全局开关可放在命令前，也可放在命令后。";
            return false;
        }

        value = raw;
        return true;
    }

    private static string DefaultExample(string flag) => flag switch
    {
        GameRootFlag => @"C:\Program Files (x86)\Steam\steamapps\common\RimWorld",
        WorkshopRootFlag => @"C:\Program Files (x86)\Steam\steamapps\workshop\content\294100",
        _ => "<值>",
    };

    private static GlobalOptions Fail(string error) => new() { Error = error };
}
