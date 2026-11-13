using RimDev.Cli;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// ★★ 第 3 期 Task 9c：**「未知开关放在命令之后被静默吞掉」这一类参数层静默错答的判别性回归**。
///
/// 缺陷（主理人实测，修前可原样复现）：
/// <code>
/// rimdev.exe check --scope active --game-rot C:\__typo__     ← 故意把 --game-root 拼错
/// scope  active / 扫描 9 个 mod / 561 个 XML / 6727 个 def / error 399   （exit=2，与不带开关时一模一样）
/// rimdev.exe check --scope active --bogus-flag 1             ← 任何未知开关同理
/// （照常扫描，exit=2，无任何提示）
/// </code>
/// 而同一个未知开关放在**命令之前**时会被未知命令分支拦下（exit=3）——所以这是「位置不同、语义不同」的
/// 双重静默：① 未知开关没有任何提示；② 用户把 <c>--game-root</c> 拼错后，工具照常给出「9 个 mod /
/// 561 个 XML」的完整报告，**没有一个字提示他扫的是自动探测到的那份安装**。
/// 这与 Task 9/9b 的「--game-root 被静默忽略」是同一类（参数层静默），只是入口从「命令不读开关」
/// 变成了「开关根本不属于这条命令」。
///
/// 本文件的用例分两半，缺一不可：
/// <list type="bullet">
/// <item><b>判别性分支（修前必红）</b>：8 条命令 + 拼错的开关 ⇒ 退出码 3、stderr 点名该开关、
///   **stdout 不得有任何结果**。修前这 8 条**全部**退出 0/2 并输出完整结果，所以这些断言在旧实现上
///   必然失败 —— 这不是「验证没坏」，而是「能分辨修前/修后」。</item>
/// <item><b>白名单没有写窄的分支</b>：同一批命令 + 它们**真实需要**的开关 ⇒ 仍然照常工作。
///   这一半防的是「把校验写得太紧，把好开关也拦掉」——那是本类修复最可能的失败方向。</item>
/// </list>
///
/// ★ 探针选的是**拼错的全局开关名**（--game-rot）而不是随便一个 --zzz：它同时覆盖「用户拼错」与
///   「命令不认全局开关」两种最真实的误用，也让「文案必须提示正确的全局开关名与位置语义」可断言。
/// </summary>
public static class Phase3Task9cCases
{
    private const string Typo = "--game-rot";
    private const string Bogus = "--bogus-flag";
    private const string TypoValue = @"C:\__typo__";

    /// <summary>`index` 的临时缓存目录（放在 bin 下，gitignored，不碰仓库里的 cache\）。</summary>
    private static string TempCacheDir
    {
        get
        {
            var dir = Path.Combine(
                Path.GetDirectoryName(typeof(Phase3Task9cCases).Assembly.Location) ?? ".",
                "tmp-task9c-cache");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>8 条命令各带一个**拼错的全局开关**（放命令之后 —— 缺陷的原始形状）。</summary>
    private static (string Name, string[] Args)[] TypoFlagCases() =>
        new (string, string[])[]
        {
            ("env", new[] { "env", Typo, TypoValue }),
            ("api", new[] { "api", "probe", "ThingDef", Typo, TypoValue }),
            ("name", new[] { "name", "check", "ThingDef", Typo, TypoValue }),
            ("index", new[] { "index", "--scope", "active", "--cache", TempCacheDir, Typo, TypoValue }),
            ("files", new[] { "files", "--scope", "active", Typo, TypoValue }),
            ("check", new[] { "check", "--scope", "active", Typo, TypoValue }),
            ("order", new[] { "order", Typo, TypoValue }),
            ("sim", new[] { "sim", "--scope", "active", Typo, TypoValue }),
        };

    /// <summary>同上，但用「任意未知开关 + 一个值」的形状（任务书要求的第二个探针）。</summary>
    private static (string Name, string[] Args)[] BogusFlagCases() =>
        new (string, string[])[]
        {
            ("env", new[] { "env", Bogus, "1" }),
            ("api", new[] { "api", "probe", "ThingDef", Bogus, "1" }),
            ("name", new[] { "name", "check", "ThingDef", Bogus, "1" }),
            ("index", new[] { "index", "--scope", "active", "--cache", TempCacheDir, Bogus, "1" }),
            ("files", new[] { "files", "--scope", "active", Bogus, "1" }),
            ("check", new[] { "check", "--scope", "active", Bogus, "1" }),
            ("order", new[] { "order", Bogus, "1" }),
            ("sim", new[] { "sim", "--scope", "active", Bogus, "1" }),
        };

    public static void Run()
    {
        T.Group("phase3-task9c");

        // ── ① 判别性：拼错的开关（修前 8/8 全绿 = 全部被静默吞掉） ──────────────────
        T.Case("★★ (a) 判别性【修前必红】：8 条命令 + 拼错的 --game-rot ⇒ 3 / 点名 / stdout 无结果", () =>
        {
            // 修前实测（同一份二进制，net10.0）：env 0 / api 0 / name 0 / index 0 / files 0 /
            //   check 2 / order 0 / sim 2 —— 八条全部照常产出结果，stderr 一个字都没有。
            foreach (var (name, args) in TypoFlagCases())
            {
                var r = RunCli(args);
                T.Eq(r.Code, CommandLine.ExitArgs, $"{name} --game-rot：退出码（应 3 = 参数错，不是 0/2）");
                T.Contains(r.Err, Typo, $"{name}：stderr 必须点名那个拼错的开关");
                T.Eq(r.Out.Trim(), "", $"{name}：**不得**继续执行命令（stdout 必须为空）");
                T.Contains(r.Err, "--game-root", $"{name}：文案必须提示正确的全局开关名");
                T.Contains(r.Err, "命令前", $"{name}：文案必须说明全局开关的位置语义（可放在命令前）");
                T.Contains(r.Err, "命令后", $"{name}：文案必须说明全局开关的位置语义（也可放在命令后）");
            }
        });

        T.Case("★★ (b) 判别性【修前必红】：8 条命令 + 任意未知开关 --bogus-flag 1 ⇒ 3 / 点名 / stdout 无结果", () =>
        {
            foreach (var (name, args) in BogusFlagCases())
            {
                var r = RunCli(args);
                T.Eq(r.Code, CommandLine.ExitArgs, $"{name} --bogus-flag：退出码（应 3）");
                T.Contains(r.Err, Bogus, $"{name}：stderr 必须点名那个未知开关");
                T.Eq(r.Out.Trim(), "", $"{name}：不得产出结果（stdout 必须为空）");
            }
        });

        // ── ② 位置语义一致：命令前 / 命令后都是 3 ────────────────────────────────
        T.Case("★★ (c) 判别性：未知开关放命令前 / 命令后 ⇒ 行为一致（都是 3，且都不产出结果）", () =>
        {
            var before = RunCli(new[] { Bogus, "1", "check", "--scope", "active" });
            var after = RunCli(new[] { "check", "--scope", "active", Bogus, "1" });

            // 修前：before=3（未知命令分支拦下）、after=2（静默吞掉、照常扫描）—— 这就是「两个位置语义不同」。
            T.Eq(before.Code, CommandLine.ExitArgs, "命令前：退出码（应 3）");
            T.Eq(after.Code, CommandLine.ExitArgs, "命令后：退出码（应 3）");
            T.Eq(after.Code, before.Code, "两个位置的退出码必须一致");
            T.Eq(after.Out.Trim(), "", "命令后：不得产出扫描结果");
            T.Eq(before.Out.Trim(), "", "命令前：不得产出扫描结果");
            T.Contains(before.Err, Bogus, "命令前：stderr 点名该开关");
            T.Contains(after.Err, Bogus, "命令后：stderr 点名该开关");
        });

        // ── ③ 白名单没有写窄：每条命令带**真实需要**的开关仍然正常 ──────────────────
        T.Case("★★ (d) 白名单不写窄：8 条命令各带自己真实需要的开关 ⇒ 仍然正常（防「好开关被误拦」）", () =>
        {
            // 这一半是本次修复最可能的失败方向：把校验写得太紧，真的被支持的开关也被拦掉。
            // 每条命令都把「实现里真正读的那些开关」全带上（files/check 各两条以覆盖全部）。
            AssertWorks("env --json", new[] { "env", "--json" }, "\"gameRoot\"");
            AssertWorks("api probe --json", new[] { "api", "probe", "ThingDef", "--json" }, "schemaVersion");
            AssertWorks("api type --filter --json", new[] { "api", "type", "ThingDef", "--filter", "defName", "--json" }, "\"members\"");
            AssertWorks("name check --kind --json", new[] { "name", "check", "ThingDef", "--kind", "type", "--json" }, "\"exists\"");
            AssertWorks("name check --owner --index --json",
                new[] { "name", "check", "defName", "--kind", "field", "--owner", "ThingDef", "--index", "--json" }, "schemaVersion");
            AssertWorks("index --scope --cache --no-workshop --rebuild --json",
                new[] { "index", "--scope", "active", "--cache", TempCacheDir, "--no-workshop", "--rebuild", "--json" },
                "\"command\": \"index\"");
            AssertWorks("files --scope --explain --filter --limit --json",
                new[] { "files", "--scope", "active", "--explain", "all", "--filter", "RimWorld", "--limit", "2", "--json" },
                "\"command\": \"files\"");
            AssertWorks("check --scope --only-official --json",
                new[] { "check", "--scope", "active", "--only-official", "--json" }, "\"onlyOfficial\": true", 0, 2);
            AssertWorks("check --all --shadowing --severity --json",
                new[] { "check", "--all", "--shadowing", "--severity", "error", "--json" }, "\"shadowingOnly\": true", 0, 2);
            AssertWorks("order --json --explain", new[] { "order", "--json", "--explain" }, "\"command\": \"order\"");

            // sim：--parallel / --timings 一起验（一次真扫描覆盖两个开关；--timings 走 stderr 以保住 stdout 稳定）
            var sim = RunCli(new[] { "sim", "--scope", "active", "--parallel", "1", "--timings", "--json" });
            T.True(sim.Code is 0 or 2, $"sim --parallel --timings：退出码（应 0 或 2，实际 {sim.Code}）");
            T.Contains(sim.Out, "\"command\": \"sim\"", "sim：stdout 仍是 --json 结果");
            T.Contains(sim.Err, "[timings]", "sim：--timings 仍打到 stderr");
        });

        // ── ④ --flag=value 与 --flag value 按同一白名单校验 ───────────────────────
        T.Case("★★ (e) 判别性：--flag=value 形式按同一白名单校验（拼错 ⇒ 3）", () =>
        {
            var eqTypo = RunCli(new[] { "check", "--scope=active", "--game-rot=" + TypoValue });
            T.Eq(eqTypo.Code, CommandLine.ExitArgs, "--game-rot=值：退出码（应 3）");
            T.Contains(eqTypo.Err, Typo, "--game-rot=值：stderr 点名该开关");
            T.Eq(eqTypo.Out.Trim(), "", "--game-rot=值：不得产出扫描结果");

            var eqBogus = RunCli(new[] { "sim", "--scope=active", "--bogus-flag=1" });
            T.Eq(eqBogus.Code, CommandLine.ExitArgs, "--bogus-flag=值：退出码（应 3）");
            T.Contains(eqBogus.Err, Bogus, "--bogus-flag=值：stderr 点名该开关");
        });

        T.Case("★ (f) --flag=value 与 --flag value 等价（--scope=active 真的生效，不是被静默忽略）", () =>
        {
            // 修前 `--scope=active` 不是「等价写法」而是「被静默忽略」：命令用 Array.IndexOf 找 "--scope"，
            // 等号形式永远找不到 ⇒ scope 退回默认值（check 的默认是 target）。两种实现下退出码可能相同，
            // 所以要断言 JSON 里的 scope 字段本身就是判别性证据。
            var plain = RunCli(new[] { "check", "--scope", "active", "--json" });
            var inline = RunCli(new[] { "check", "--scope=active", "--json" });

            T.True(plain.Code is 0 or 2, $"空格形式：退出码（应 0 或 2，实际 {plain.Code}）");
            T.Eq(inline.Code, plain.Code, "等号形式与空格形式的退出码必须相同");
            T.Contains(plain.Out, "\"scope\": \"active\"", "空格形式：scope 字段");
            T.Contains(inline.Out, "\"scope\": \"active\"", "等号形式：scope 字段（不得静默退回 target）");
        });

        // ── ⑤ 既有 --help 用法不得被白名单拦掉 ────────────────────────────────────
        T.Case("★ (g) 既有 --help / -h / --? 用法不被拦（check / sim / order）", () =>
        {
            var helps = new (string Name, string[] Args)[]
            {
                ("check --help", new[] { "check", "--help" }),
                ("check -h", new[] { "check", "-h" }),
                ("sim --help", new[] { "sim", "--help" }),
                ("sim --?", new[] { "sim", "--?" }),
                ("order --help", new[] { "order", "--help" }),
            };
            foreach (var (name, args) in helps)
            {
                var r = RunCli(args);
                T.Eq(r.Code, 0, $"{name}：退出码（应 0 = 打印帮助）");
                T.True(r.Out.Trim().Length > 0, $"{name}：stdout 必须有帮助正文");
                T.Eq(r.Err.Trim(), "", $"{name}：不得报错");
            }
        });

        // ── ⑥ 白名单是「注册表 + 命令自己的声明」，不是手抄清单 ─────────────────────
        T.Case("★ (h) 白名单注册表覆盖 Usage 里列出的全部命令，且每条命令都接受 --help（防漂移）", () =>
        {
            var listed = CommandFlags.ListedCommands();
            T.True(listed.Count > 0, "从 Usage 里解析出的命令清单不得为空（正则/格式写坏时会变 0，静默通过）");
            foreach (var name in listed)
            {
                var set = CommandFlags.For(name);
                T.NotNull(set, $"{name}：必须在 CommandFlags 注册表里登记开关白名单");
                T.Eq(set!.Command, name, $"{name}：白名单声明的命令名");
                T.True(set.Declares("--help"), $"{name}：必须认得 --help（帮助由 CommandFlagSet 构造器统一追加）");
                T.True(set.Declares("-h"), $"{name}：必须认得 -h");
                T.True(set.Declares("--?"), $"{name}：必须认得 --?");
            }

            // 反向：不存在的命令拿不到白名单（否则它会绕过校验、退化成"什么都认"）。
            T.Null(CommandFlags.For("bogus-command"), "未登记的命令必须返回 null");
        });

        // ── ⑦ 9c 收尾：`api list` 不需要类型名（帮助与设计文档都这么写，旧实现恒 exit 3） ────────
        T.Case("★★ (i) 判别性【修前必红】：`api list --filter` 不需要类型名（帮助/设计文档承诺的用法必须真能跑）", () =>
        {
            // 修前实测：`api list --json` ⇒ exit 3「缺少类型名」；`api list --filter ThingDef --json`
            //   同样 3 —— 而本命令 `--help` 与 `03-实施计划.md` 的示例 `& $cli api list --filter MechWeight`
            //   都是**不带类型名**的写法。给一个占位类型名反而能跑（说明类型名对 list 完全无用）。
            var bare = RunCli(new[] { "api", "list", "--json" });
            T.Eq(bare.Code, 0, "api list --json：退出码（应 0；修前 3「缺少类型名」）");
            T.Contains(bare.Out, "types", "api list --json：必须列出类型名");
            T.Eq(bare.Err.Trim(), "", "api list --json：不得报错");

            var filtered = RunCli(new[] { "api", "list", "--filter", "ThingDef", "--json" });
            T.Eq(filtered.Code, 0, "api list --filter：退出码（应 0）");
            T.Contains(filtered.Out, "ThingDef", "api list --filter：过滤结果里必须有 ThingDef");

            // 占位类型名必须与「不带类型名」结果等价（证明类型名对 list 确实无关，不是被静默忽略的另一种）。
            var withJunk = RunCli(new[] { "api", "list", "_", "--filter", "ThingDef", "--json" });
            T.Eq(withJunk.Code, 0, "api list <占位> --filter：退出码（应 0）");
            T.Eq(withJunk.Out, filtered.Out, "api list：带占位类型名与不带类型名的输出必须逐字相同");

            // 需要类型名的子命令仍然必须报缺（别把校验一起放宽了）。
            foreach (var sub in new[] { "probe", "type", "member", "ctor" })
            {
                var r = RunCli(new[] { "api", sub, "--json" });
                T.Eq(r.Code, 3, $"api {sub} <无类型名>：退出码（应 3 = 参数错）");
                T.Contains(r.Err, "缺少类型名", $"api {sub} <无类型名>：必须报「缺少类型名」");
            }

            // 未知子命令要报「未知」而不是「缺少类型名」（修前会被类型名检查抢先）。
            var bogus = RunCli(new[] { "api", "bogus-sub" });
            T.Eq(bogus.Code, 3, "api bogus-sub：退出码（应 3）");
            T.Contains(bogus.Err, "bogus-sub", "api bogus-sub：stderr 必须点名那个子命令");
        });

        // ── ⑧ 9c 收尾：`--scope` 的**取值域**也要响亮失败（修前 index 是唯一静默回落的） ────────────
        T.Case("★★ (j) 判别性【修前必红】：--scope 拼错值 ⇒ 退出码 3（不许静默当成 active 跑一次索引）", () =>
        {
            // 修前实测：`index --scope bogus` ⇒ exit 0、stdout 1882 B（**照常跑完整索引**！），
            //   而同批的 files / check / sim 对同一个错值都是 exit 3 —— 同一类参数错一个响亮一个静默。
            foreach (var name in new[] { "index", "files", "check", "sim" })
            {
                var r = RunCli(new[] { name, "--scope", "bogus", "--json" });
                T.Eq(r.Code, 3, $"{name} --scope bogus：退出码（应 3；静默回落 = 用户拿着一份 scope 不对的报告下结论）");
                T.Contains(r.Err, "bogus", $"{name} --scope bogus：stderr 必须点名那个错值");
                T.Eq(r.Out.Trim(), "", $"{name} --scope bogus：不得产出结果");
            }

            // 合法的两个值仍必须照常工作（别把取值域校验写窄了）。
            foreach (var v in new[] { "active", "all" })
            {
                var ok = RunCli(new[] { "files", "--scope", v, "--json", "--limit", "1" });
                T.True(ok.Code is 0 or 2, $"files --scope {v}：退出码（应 0/2，实际 {ok.Code}）");
            }
        });
    }

    /// <summary>「带真实开关仍然正常」的通用断言：退出码在允许集合内、stdout 命中预期标记、stderr 为空。</summary>
    /// <param name="allowedCodes">
    /// 允许的退出码（缺省只有 0）。`check` / `sim` 的 <c>2</c> = 「有 error 级诊断」，是本机语料的既有语义、
    /// **不是**参数错；把它们写成 3 或拒绝执行才是回归。
    /// </param>
    private static void AssertWorks(string label, string[] args, string marker, params int[] allowedCodes)
    {
        var codes = allowedCodes.Length == 0 ? new[] { 0 } : allowedCodes;
        var r = RunCli(args);
        T.True(codes.Contains(r.Code), $"{label}：退出码（应 {string.Join("/", codes)}，实际 {r.Code}）");
        T.Contains(r.Out, marker, $"{label}：输出形状（含 `{marker}`）");
        T.Eq(r.Err.Trim(), "", $"{label}：stderr 不得有参数错提示");
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
