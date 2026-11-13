using RimDev.Cli;
using RimDev.Core.Environment;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// ★★ 第 3 期 Task 9b：**「全局环境开关只被 env 采纳」这一类静默错答的判别性回归**。
///
/// 缺陷（主理人实测，修前可原样复现）：
/// <c>--game-root</c> / <c>--workshop-root</c> 只被 <c>env</c> 采纳，其余命令**静默忽略**，于是
/// <code>
/// rimdev.exe check --scope active --game-root C:\__no_such_rimworld__
/// </code>
/// 照常输出「扫描 9 个 mod / 561 个 XML / 6727 个 def / error 399」并退出 0/2 —— 一个错都不报；
/// 而 <c>rimdev env --game-root …</c> 会正确报 RD1001 + 退出码 4。
/// 后果是 <c>rimdev --profile 1.6 check …</c> 的整个卖点（指向另一份安装）失效，
/// 用户无法从输出里看出「我扫的不是我指定的那份」。
///
/// 本文件的用例分两半，缺一不可：
/// <list type="bullet">
/// <item><b>判别性分支（修前必红）</b>：`--game-root &lt;不存在&gt;` ⇒ 每条命令**都**要
///   RD1001 + 退出码 4，且**不产出任何扫描结果**（stdout 必须为空）。修前这 5+2 条命令
///   全部退出 0/2 并输出完整扫描结果，所以这些断言在旧实现上必然失败 —— 这不是「验证没坏」，
///   而是「能分辨修前/修后」。</item>
/// <item><b>不回归分支</b>：不传开关时 7 条命令的退出码与输出形状与今天一致
///   （尤其 <c>check</c>/<c>sim</c> 仍可能退 2 —— 那是「有 error 级诊断」的语义，不是失败）。</item>
/// </list>
///
/// ★ 探针用**必然失败的输入**：传「正确路径看结果是否合理」在「开关生效」与「开关被忽略」
///   两种情况下输出可能逐项完全相同（本机就是如此），无法判别；传一个不存在的路径才能让
///   两种实现给出**不同**的退出码与输出。
/// </summary>
public static class Phase3Task9bCases
{
    private const string MissingRoot = @"C:\__no_such_rimworld__";
    private const string MissingWorkshop = @"C:\__no_such_workshop__";
    private const string MissingVersion = "RD1001";

    /// <summary>`index` 用例用的临时缓存目录：放在 bin 下（gitignored），不碰仓库里的 cache\。</summary>
    private static string TempCacheDir
    {
        get
        {
            var dir = Path.Combine(
                Path.GetDirectoryName(typeof(Phase3Task9bCases).Assembly.Location) ?? ".",
                "tmp-task9b-cache");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static void Run()
    {
        T.Group("phase3-task9b");

        // ── ① 参数层：一处解析、两个位置等价 ─────────────────────────────────────
        T.Case("★ (a) GlobalOptions：--game-root 放在命令后 ⇒ 被摘掉、命令参数原样保留", () =>
        {
            var o = GlobalOptions.Parse(new[] { "check", "--scope", "active", "--game-root", @"C:\X" });
            T.False(o.HasError, "HasError");
            T.Eq(string.Join(" ", o.Args), "check --scope active", "摘掉开关后的参数");
            T.Eq(o.GameRoot, @"C:\X", "GameRoot");
            T.Null(o.WorkshopRoot, "WorkshopRoot（未传）");
        });

        T.Case("★ (b) GlobalOptions：两个全局开关放命令前 / 放命令后 ⇒ 结果完全等价", () =>
        {
            var after = GlobalOptions.Parse(new[]
                { "files", "--scope", "active", "--game-root", @"C:\G", "--workshop-root", @"C:\W" });
            var before = GlobalOptions.Parse(new[]
                { "--game-root", @"C:\G", "--workshop-root", @"C:\W", "files", "--scope", "active" });

            T.Eq(string.Join(" ", before.Args), string.Join(" ", after.Args), "两种位置的命令参数");
            T.Eq(before.GameRoot, after.GameRoot, "两种位置的 GameRoot");
            T.Eq(before.WorkshopRoot, after.WorkshopRoot, "两种位置的 WorkshopRoot");
            T.Eq(string.Join(" ", after.Args), "files --scope active", "开关确实被摘掉");
        });

        T.Case("★ (c) GlobalOptions：--flag=value 形式 + --profile 放命令前（shim 依赖它）", () =>
        {
            var o = GlobalOptions.Parse(new[] { "--profile", "1.7", "env", @"--game-root=C:\G" });
            T.False(o.HasError, "HasError");
            T.Eq(o.Profile, "1.7", "Profile（放命令前）");
            T.Eq(o.GameRoot, @"C:\G", "GameRoot（等号形式）");
            T.Eq(string.Join(" ", o.Args), "env", "命令参数");

            // shim 实际使用的形状：三个 --rimdev-* 追加在用户参数之后。
            var shim = GlobalOptions.Parse(new[]
                { "check", "--scope", "active", "--rimdev-profile", "1.7", "--rimdev-invoked-via", "shim" });
            T.Eq(shim.Profile, "1.7", "shim 转发的 profile");
            T.Eq(shim.InvokedVia, "shim", "shim 转发的 invokedVia");
            T.Eq(string.Join(" ", shim.Args), "check --scope active", "shim 转发后剩余的命令参数");
        });

        T.Case("★ (d) GlobalOptions：缺值 / 空值 ⇒ 参数错（点名开关 + 说明位置语义）", () =>
        {
            var missing = GlobalOptions.Parse(new[] { "check", "--game-root" });
            T.True(missing.HasError, "缺值必须报错（不许猜默认值）");
            T.Contains(missing.Error!, "--game-root", "错误信息点名开关");
            T.Contains(missing.Error!, "命令前", "错误信息说明位置语义");

            var next = GlobalOptions.Parse(new[] { "check", "--game-root", "--json" });
            T.True(next.HasError, "下一个 token 是另一个开关时不能把它当值");

            var blank = GlobalOptions.Parse(new[] { "files", "--workshop-root=" });
            T.True(blank.HasError, "空值必须报错");
            T.Contains(blank.Error!, "--workshop-root", "错误信息点名开关");
        });

        // ── ② 判别性：不存在的 --game-root ⇒ 每条命令 RD1001 + 退出码 4，且不产出结果 ──
        T.Case("★★ (e) 判别性【修前必红】：check/sim/index/files/order + 不存在的 --game-root ⇒ 4 且无扫描结果", () =>
        {
            // ★ 修前实测（同一份二进制）：五条命令全部照常输出扫描结果，退出码 2/0/0/0/0，一个错都不报。
            //   ⇒ 这组断言在旧实现上必然红，修后必须全绿（判别性用例的判据）。
            var commands = new (string Name, string[] Args)[]
            {
                ("check", new[] { "check", "--scope", "active", "--game-root", MissingRoot }),
                ("sim", new[] { "sim", "--scope", "active", "--game-root", MissingRoot }),
                ("index", new[] { "index", "--scope", "active", "--cache", TempCacheDir, "--game-root", MissingRoot }),
                ("files", new[] { "files", "--scope", "active", "--game-root", MissingRoot }),
                ("order", new[] { "order", "--game-root", MissingRoot }),
            };

            foreach (var (name, args) in commands)
            {
                var r = RunCli(args);
                T.Eq(r.Code, EnvironmentResolution.EnvExitCode, $"{name}：退出码（应 4 = 环境不满足）");
                T.Eq(r.Out.Trim(), "", $"{name}：**不得**产出任何扫描结果（stdout 必须为空）");
                T.Contains(r.Err, MissingVersion, $"{name}：stderr 必须带 RD1001");
                T.Contains(r.Err, MissingRoot, $"{name}：错误信息必须点出那个不存在路径");
            }
        });

        T.Case("★★ (f) 判别性【修前必红】：api / name 也统一 RD1001（不是 RD4002、也不静默回落）", () =>
        {
            var commands = new (string Name, string[] Args)[]
            {
                ("api", new[] { "api", "type", "ThingDef", "--game-root", MissingRoot }),
                ("name", new[] { "name", "check", "ThingDef", "--game-root", MissingRoot }),
            };

            foreach (var (name, args) in commands)
            {
                var r = RunCli(args);
                T.Eq(r.Code, EnvironmentResolution.EnvExitCode, $"{name}：退出码（应 4）");
                T.Eq(r.Out.Trim(), "", $"{name}：不得产出查询结果");
                T.Contains(r.Err, MissingVersion, $"{name}：必须报 RD1001（不是程序集加载失败那类码）");
                T.False(r.Err.Contains("RD4002", StringComparison.Ordinal),
                    $"{name}：不得把环境错误吞成程序集加载失败");
            }
        });

        T.Case("★ (g) 位置语义一致：--game-root 放在**命令前**同样 RD1001 + 4（不是「未知命令」）", () =>
        {
            var r = RunCli(new[] { "--game-root", MissingRoot, "check", "--scope", "active" });
            T.Eq(r.Code, EnvironmentResolution.EnvExitCode, "退出码（应 4，而不是参数错 3）");
            T.Eq(r.Out.Trim(), "", "不得产出扫描结果");
            T.Contains(r.Err, MissingVersion, "stderr 必须带 RD1001");
            T.False(r.Err.Contains("未知命令", StringComparison.Ordinal),
                "开关放命令前不得被当成未知命令（修前正是这个错误）");
        });

        T.Case("★ (h) 未知开关仍报退出码 3，但文案指出「全局开关可放在命令前或命令后」", () =>
        {
            var r = RunCli(new[] { "--bogus", "check" });
            T.Eq(r.Code, CommandLine.ExitArgs, "退出码（应 3 = 参数错）");
            T.Contains(r.Err, "未知命令", "仍是未知命令分支");
            T.Contains(r.Err, "命令前", "文案说明全局开关位置语义");
            T.Contains(r.Err, "命令后", "文案说明全局开关位置语义");
        });

        // ── ③ 不回归：不传开关时行为与今天一致 ──────────────────────────────────
        T.Case("★ (i) 不回归：不传开关时 7 条命令的退出码/输出形状与今天一致", () =>
        {
            // check / sim 的退出码 2 = 「有 error 级诊断」，是既有语义（不是失败）；允许 0（内容修好后）。
            var mustBeOk = new (string Name, string[] Args, string Marker)[]
            {
                ("index", new[] { "index", "--scope", "active", "--cache", TempCacheDir }, "XML 文件"),
                ("files", new[] { "files", "--scope", "active" }, "mod 数"),
                ("order", new[] { "order" }, "Ludeon.RimWorld"),
                ("api", new[] { "api", "type", "ThingDef" }, "Verse.ThingDef"),
                ("name", new[] { "name", "check", "ThingDef" }, "ThingDef"),
            };

            foreach (var (name, args, marker) in mustBeOk)
            {
                var r = RunCli(args);
                T.Eq(r.Code, 0, $"{name}：退出码（应 0）");
                T.Contains(r.Out, marker, $"{name}：输出形状（含 `{marker}`）");
            }

            foreach (var (name, args) in new (string, string[])[]
                     {
                         ("check", new[] { "check", "--scope", "active" }),
                         ("sim", new[] { "sim", "--scope", "active" }),
                     })
            {
                var r = RunCli(args);
                T.True(r.Code is 0 or 2, $"{name}：退出码（应 0 或 2=有 error 级诊断，实际 {r.Code}）");
                T.Contains(r.Out, "scope", $"{name}：仍打印 scope 行");
                T.Contains(r.Out, "扫描", $"{name}：仍打印扫描结果");
            }
        });

        // ── ④ --workshop-root 真贯通 ────────────────────────────────────────────
        T.Case("★ (j) --workshop-root 指到不存在的路径 ⇒ 4（files / index）", () =>
        {
            foreach (var (name, args) in new (string, string[])[]
                     {
                         ("files", new[] { "files", "--scope", "active", "--workshop-root", MissingWorkshop }),
                         ("index", new[] { "index", "--scope", "active", "--cache", TempCacheDir, "--workshop-root", MissingWorkshop }),
                     })
            {
                var r = RunCli(args);
                T.Eq(r.Code, EnvironmentResolution.EnvExitCode, $"{name}：退出码（应 4）");
                T.Eq(r.Out.Trim(), "", $"{name}：不得产出结果");
                T.Contains(r.Err, MissingWorkshop, $"{name}：错误信息必须点出那个不存在路径");
            }
        });

        T.Case("★★ (k) --workshop-root 指到真实工坊目录 ⇒ 与不传**逐字节相同**（证明「传了等于没传」不再成立）", () =>
        {
            // 本机的自动探测结果就是 Facts.WorkshopDir；显式传同一个路径时，工坊 mod 集合与
            // 每个 mod 的文件夹解析都必须**逐字节**一致 —— 任何「传了被忽略」或「传了但只回显」
            // 的实现都过不了这一条。
            if (!Directory.Exists(Facts.WorkshopDir))
                throw new InvalidOperationException($"本机没有工坊目录 {Facts.WorkshopDir}（这条断言依赖它存在）");

            var plain = RunCli(new[] { "files", "--scope", "active", "--json" });
            var explicitWs = RunCli(new[] { "files", "--scope", "active", "--json", "--workshop-root", Facts.WorkshopDir });

            T.Eq(plain.Code, 0, "不传 --workshop-root 的退出码");
            T.Eq(explicitWs.Code, 0, "传了 --workshop-root 的退出码");
            T.Eq(explicitWs.Out, plain.Out, "`files --json` 输出必须逐字节相同");
        });

        T.Case("★ (l) --game-root 指到真实游戏根 ⇒ 与不传逐字节相同（证明开关被采纳、且不回落到别处）", () =>
        {
            if (!GameEnvProbe.IsGameRoot(Facts.GameRoot))
                throw new InvalidOperationException($"本机游戏根不是有效安装：{Facts.GameRoot}");

            var plain = RunCli(new[] { "order", "--json" });
            var explicitRoot = RunCli(new[] { "order", "--json", "--game-root", Facts.GameRoot });

            T.Eq(plain.Code, 0, "不传 --game-root 的退出码");
            T.Eq(explicitRoot.Code, 0, "传了 --game-root 的退出码");
            T.Eq(explicitRoot.Out, plain.Out, "`order --json` 输出必须逐字节相同");
        });

        T.Case("★ (m) env 的基准行为不变：不传开关可读；传不存在的根 ⇒ RD1001 + 4", () =>
        {
            var ok = RunCli(new[] { "env" });
            T.Eq(ok.Code, 0, "env 无开关的退出码");
            T.Contains(ok.Out, "游戏根", "env 仍打印游戏根行");

            var bad = RunCli(new[] { "env", "--game-root", MissingRoot });
            T.Eq(bad.Code, EnvironmentResolution.EnvExitCode, "env 传不存在的根 ⇒ 4（修前的正确基准）");
            T.Contains(bad.Err, MissingVersion, "env 报 RD1001");
        });
    }

    /// <summary>直调 CLI 入口（不起子进程），把两路输出收进字符串。</summary>
    private static (int Code, string Out, string Err) RunCli(string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = CommandLine.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }
}
