using RimDev.Core.Catalog;
using RimDev.Core.Checks;
using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// 第 3 期 Task 1：loadFolders 引擎等价语义 + 文件发现 + 规则族（RD2012/RD2018–RD2024）。
///
/// 用例里的 <c>TestVersion</c> 是**实测值的合成替身**：<see cref="EngineVersionProbe"/> 从
/// <c>RimWorld.VersionControl</c> 反射得到 <c>CurrentVersionString="1.6.4871"</c>、
/// <c>CurrentVersionStringWithoutBuild="1.6"</c>、<c>CurrentVersion=1.6.4871.591</c>。
/// 有一条用例专门断言「真探针 == 合成替身」，所以这里既确定又不会与引擎脱节。
/// </summary>
public static class LoadFolderCases
{
    private static readonly EngineVersionInfo TestVersion =
        new("1.6.4871", "1.6", new Version(1, 6, 4871, 591), "test", null);

    private static Func<string, bool> Active(params string[] ids) => LoadFolderResolver.ActiveMatcher(ids);

    private sealed class TempMod : IDisposable
    {
        public string Root { get; }

        public TempMod(string? loadFoldersXml, params string[] dirs)
        {
            Root = Path.Combine(Path.GetTempPath(), "rimdev-lf-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            foreach (var d in dirs) Directory.CreateDirectory(Path.Combine(Root, d));
            if (loadFoldersXml is not null)
                File.WriteAllText(Path.Combine(Root, "LoadFolders.xml"), loadFoldersXml);
        }

        public ResolvedFolders Resolve(Func<string, bool>? isActive = null,
            IReadOnlyList<string>? supported = null)
        {
            var doc = LoadFolderResolver.Load(Root);
            return LoadFolderResolver.Resolve(Root, "test.mod", doc, TestVersion,
                isActive ?? Active(), supported);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { /* 临时目录，忽略 */ }
        }
    }

    /// <summary>把路径列表变成可读短名；<paramref name="root"/> 用 <c>&lt;root&gt;</c> 占位（临时目录名是 GUID）。</summary>
    private static string Names(IReadOnlyList<string> paths, string? root = null) =>
        string.Join("|", paths.Select(p =>
        {
            var n = Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar));
            return root is not null && string.Equals(n, Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)),
                StringComparison.OrdinalIgnoreCase) ? "<root>" : n;
        }));

    public static void Run()
    {
        T.Group("loadfolders");

        // ───────── Q1：引擎真实版本口径 ─────────
        T.Case("★ Q1 引擎版本口径：反射取 RimWorld.VersionControl（三段串！）", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            EngineVersionProbe.ResetForTests();
            var v = EngineVersionProbe.Probe(env);

            T.Eq(v.Source, EngineVersionInfo.ReflectionSource, "来源必须是反射（不是 env-fallback）");
            T.Null(v.Error, "不应有反射错误");
            // 这三条就是分支语义的根：精确分支查的是三段串，而现实里的 mod 只写 <v1.6>。
            T.True(v.CurrentVersionString.Length > v.CurrentVersionStringWithoutBuild.Length,
                $"CurrentVersionString({v.CurrentVersionString}) 应比 WithoutBuild({v.CurrentVersionStringWithoutBuild}) 长（= 三段）");
            T.Eq(v.CurrentVersionStringWithoutBuild, env.TargetVersion, "WithoutBuild 应等于 env.TargetVersion(1.6)");
            T.True(v.CurrentVersionString != v.CurrentVersionStringWithoutBuild,
                "两串相同就意味着精确分支会命中 <v1.6> —— 与实测不符，必须重新核对");

            // ★ 合成替身必须与真探针一致，否则上面那些用例就与引擎脱节了。
            T.Eq(TestVersion.CurrentVersionString, v.CurrentVersionString, "合成替身的 CurrentVersionString");
            T.Eq(TestVersion.CurrentVersionStringWithoutBuild, v.CurrentVersionStringWithoutBuild, "合成替身的 WithoutBuild");
            T.Eq(TestVersion.CurrentVersion.ToString(), v.CurrentVersion.ToString(), "合成替身的 CurrentVersion");
        });

        // ───────── Q2：倒序 + 先到先得 ─────────
        T.Case("★ Q2 `<li>A</li><li>B</li>` → DescendingOrder[0] 是 B（倒序追加 + 先到先得）", () =>
        {
            using var m = new TempMod("<loadFolders><v1.6><li>A</li><li>B</li></v1.6></loadFolders>");
            var r = m.Resolve();
            T.Eq(r.Origin, ResolutionOrigin.LowerVersion, "Origin（精确串 1.6.4871 不命中 → 向下取版本）");
            T.Eq(r.MatchedVersionKey, "1.6", "MatchedVersionKey");
            T.Eq(r.DescendingOrder.Count, 2, "目录数");
            T.Eq(Path.GetFileName(r.DescendingOrder[0]), "B", "第 0 位应是**最后**声明的 B");
            T.Eq(Path.GetFileName(r.DescendingOrder[1]), "A", "第 1 位是 A");
        });

        // ───────── Q3：Path.Combine(root, "") == root ─────────
        T.Case("★ Q3 Path.Combine(root, \"\") == root（`<li>/</li>` 表示 mod 根的依据）", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "rimdev-q3-root");
            T.Eq(Path.Combine(root, ""), root, "Path.Combine(root, \"\") 的实测值");

            using var m = new TempMod("<loadFolders><v1.6><li>/</li><li>\\</li></v1.6></loadFolders>");
            var r = m.Resolve();
            T.Eq(r.DescendingOrder.Count, 2, "两条 <li> 都归一成空名 → 两条都指向 mod 根（引擎不去重目录）");
            T.Eq(r.DescendingOrder[0], m.Root, "第 0 位就是 mod 根本身");
            T.Eq(r.DescendingOrder[1], m.Root, "第 1 位也是 mod 根");
            T.Eq(r.Entries[0].FolderName, "", "解析出的 folderName 必须是空串（引擎的 LoadFolder(\"\")）");
        });

        // ───────── Q4：向下取版本的字符串序 ─────────
        T.Case("★ Q4 向下取版本：定义 [1.5, 1.10] → 选 1.5（字符串序 / 版本序在这个输入下同结论）", () =>
        {
            var pick = LoadFolderResolver.PickVersionKey(new[] { "1.5", "1.10" }, TestVersion.CurrentVersion);
            T.Eq(pick, "1.5", "选中键");
        });

        T.Case("★ Q4b 能真正区分字符串序与版本序的输入：[1.5.3, 1.5.10] → 1.5.3", () =>
        {
            // 两个都 ≤ 1.6.4871.591（eligible），而 "1.5.3" > "1.5.10" 是**字符串序**；
            // 版本序会选 1.5.10。所以这条才真正钉住 orderby x descending 的字符串语义（RD2022 的依据）。
            var pick = LoadFolderResolver.PickVersionKey(new[] { "1.5.3", "1.5.10" }, TestVersion.CurrentVersion);
            T.Eq(pick, "1.5.3", "字符串序 `orderby x descending` 的结果");

            // 反面对照：版本序会给出不同答案，证明上面那条不是「两种序巧合相同」。
            T.True(new Version(1, 5, 10) > new Version(1, 5, 3), "版本序下 1.5.10 > 1.5.3（与字符串序相反）");
        });

        T.Case("★ Q4c 版本大于当前版本的键被排除（[1.7] + 当前 1.6 → null）", () =>
        {
            T.Null(LoadFolderResolver.PickVersionKey(new[] { "1.7" }, TestVersion.CurrentVersion),
                "1.7 > 1.6.4871.591 → 不可用");
            T.Eq(LoadFolderResolver.PickVersionKey(new[] { "default", "1.6" }, TestVersion.CurrentVersion),
                "1.6", "default 不参与挑选");
            T.Null(LoadFolderResolver.PickVersionKey(new[] { "notaversion" }, TestVersion.CurrentVersion),
                "非法版本串 → 不可用（引擎那里是 VersionFromString 抛异常被 catch）");
        });

        // ───────── 版本键归一 ─────────
        T.Case("版本块名归一：v1.6 / V1.6 同键，default / DEFAULT 同键（★ <1.6> 不可能存在）", () =>
        {
            // ★ 实测更正：计划 §0.4.3 说「同键 = <v1.6> 与 <1.6>」，但 **<1.6> 不是良构 XML**
            //   （XML 元素名不能以数字开头，XmlReader 直接抛 XmlException）=> 现实中根本不存在这种写法，
            //   这也解释了「<1.6> 实测 0 个」。这里用 V1.6（大写 V）验证 ToLower 那一半语义。
            using var m = new TempMod(
                "<loadFolders><v1.6><li>A</li></v1.6><V1.6><li>B</li></V1.6>"
                + "<default><li>C</li></default><DEFAULT><li>D</li></DEFAULT></loadFolders>");
            var doc = LoadFolderResolver.Load(m.Root);
            T.Null(doc.ParseError, "这份 XML 必须是良构的");
            T.Eq(doc.DefinedVersions.Count, 2, "同键合并后只应有 2 个键（1.6 与 default）");
            T.True(doc.DefinedVersions.Contains("1.6"), "键必须是 1.6（剥掉前导 v、且 ToLower）");
            T.True(doc.DefinedVersions.Contains("default"), "键必须是 default（DEFAULT -> ToLower）");
            T.Eq(doc.BlockFor("1.6")!.Count, 2, "两个同键块的条目必须**合并**（引擎 foldersForVersion[text] 追加）");
            T.Eq(doc.BlockFor("1.6")![0].FolderName, "A", "合并保序：第 0 条来自第一个块");
            T.Eq(doc.BlockFor("1.6")![1].FolderName, "B", "合并保序：第 1 条来自第二个块");

            // 反面实证：<1.6> 连 XML 都过不了。
            using var bad = new TempMod("<loadFolders><1.6><li>A</li></1.6></loadFolders>");
            T.NotNull(LoadFolderResolver.Load(bad.Root).ParseError, "<1.6> 必须是解析错误（元素名不能以数字开头）");
        });

        // ───────── If* 解析 ─────────
        T.Case("If* 属性解析：按 , 切分 + Trim；/ 与 \\ → 空名；AltDirectorySeparator 归一", () =>
        {
            using var m = new TempMod(
                "<loadFolders><v1.6>"
                + "<li IfModActive=\" a.mod , b.mod \">A</li>"
                + "<li IfModActiveAll=\"a.mod, b.mod\">B</li>"
                + "<li IfModNotActive=\" c.mod \">C</li>"
                + "<li>sub/dir</li>"
                + "</v1.6></loadFolders>");
            var doc = LoadFolderResolver.Load(m.Root);
            var e = doc.BlockFor("1.6")!;
            T.Eq(string.Join(",", e[0].Any!), "a.mod,b.mod", "IfModActive 切分 + Trim");
            T.Eq(string.Join(",", e[1].All!), "a.mod,b.mod", "IfModActiveAll 切分 + Trim");
            T.Eq(string.Join(",", e[2].Not!), "c.mod", "IfModNotActive 切分 + Trim");
            T.Null(e[0].All, "未写的属性必须是 null（NullOrEmpty 与「空列表」同等对待，但这里要保住 null）");
            T.Eq(e[3].FolderName, Path.Combine("sub", "dir"), "AltDirectorySeparatorChar 归一成 DirectorySeparatorChar");
            T.True(e[0].Line > 0, "行号必须可定位");
        });

        // ───────── ShouldLoad 三条件合取 ─────────
        T.Case("ShouldLoad = IfModActive(任一) ∧ IfModActiveAll(全部) ∧ IfModNotActive(任一都不)", () =>
        {
            using var m = new TempMod(
                "<loadFolders><v1.6>"
                + "<li IfModActive=\"x.mod\">AnyMiss</li>"
                + "<li IfModActive=\"a.mod,b.mod\">AnyHit</li>"
                + "<li IfModActiveAll=\"a.mod,b.mod\">AllHit</li>"
                + "<li IfModActiveAll=\"a.mod,z.mod\">AllMiss</li>"
                + "<li IfModNotActive=\"z.mod\">NotOk</li>"
                + "<li IfModNotActive=\"a.mod\">NotBlocked</li>"
                + "</v1.6></loadFolders>");
            var r = m.Resolve(Active("a.mod", "b.mod"));
            T.Eq(Names(r.DescendingOrder, m.Root), "NotOk|AllHit|AnyHit", "倒序 + 条件过滤后的最终列表");
        });

        // ───────── 回退分支 ─────────
        T.Case("★ 回退分支：无 loadFolders + 有 1.6 与 Common → [1.6, Common, root]（root 永远最后）", () =>
        {
            using var m = new TempMod(null, "1.6", "Common");
            var r = m.Resolve();
            T.Eq(r.Origin, ResolutionOrigin.FallbackDir, "Origin");
            T.Eq(Names(r.DescendingOrder, m.Root), "1.6|Common|<root>",
                "顺序必须是 1.6 → Common → root（root 最后加 = 优先级最低）");
            T.Eq(Path.GetFileName(r.DescendingOrder[2]), Path.GetFileName(m.Root), "最后一项是 mod 根");
        });

        T.Case("★ 有 loadFolders 时 **不**自动加 Common，也不自动加 root", () =>
        {
            using var m = new TempMod("<loadFolders><v1.6><li>1.6</li></v1.6></loadFolders>", "1.6", "Common");
            var r = m.Resolve();
            T.Eq(Names(r.DescendingOrder), "1.6", "只有 <li> 里写了的目录才加载（Common 不在其中）");
        });

        T.Case("★ 回退分支的版本目录：引擎精确语义（不是「取最大者」）", () =>
        {
            // (a) 有 ≤ 当前版本的目录 → 取其中最大者
            using (var m = new TempMod(null, "1.4", "1.7"))
            {
                var r = m.Resolve();
                T.Eq(Names(r.DescendingOrder, m.Root), "1.4|<root>", "1.4 是 ≤ 1.6 的最大者（1.7 被排除）");
            }

            // (b) 一个都不 ≤ 当前版本 → 引擎取**最小**者（§0.4「取最大者」会给出 1.8）
            using (var m = new TempMod(null, "1.7", "1.8"))
            {
                var r = m.Resolve();
                T.Eq(Names(r.DescendingOrder, m.Root), "1.7|<root>",
                    "没有 ≤ 当前版本的目录时，引擎的循环只接受**第一个**（= 最小者）1.7，而不是最大者 1.8");
            }

            // (c) 无可解析目录名 → 只剩 root
            using (var m = new TempMod(null, "abc", "1.6"))
            {
                var r = m.Resolve();
                T.Eq(Names(r.DescendingOrder, m.Root), "1.6|<root>", "有 1.6 目录时优先用它");
            }
            using (var m = new TempMod(null, "abc", "Defs"))
            {
                var r = m.Resolve();
                T.Eq(Names(r.DescendingOrder, m.Root), "<root>", "没有版本目录也没有 Common → 只有 mod 根");
            }
        });

        T.Case("★ 有 loadFolders 但没有 1.6 键 → 向下取 1.5，且**不含** mod 根（旧实现会错误地退回 root+1.6）", () =>
        {
            using var m = new TempMod("<loadFolders><v1.5><li>1.5</li></v1.5></loadFolders>", "1.5", "1.6");
            var r = m.Resolve();
            T.Eq(r.Origin, ResolutionOrigin.LowerVersion, "Origin");
            T.Eq(r.MatchedVersionKey, "1.5", "MatchedVersionKey");
            T.Eq(Names(r.DescendingOrder), "1.5", "只有 1.5（root 与 1.6 都不在列表里）");
        });

        T.Case("★ default 键：有空条目时解析出空列表 → RD2024 error（该 mod 什么都不加载）", () =>
        {
            using var m = new TempMod("<loadFolders><default></default></loadFolders>", "Defs");
            var r = m.Resolve();
            // Origin 的语义：**结果为空时记 Empty**（这是 RD2024 的信号），走的是哪条分支看 MatchedVersionKey。
            T.Eq(r.Origin, ResolutionOrigin.Empty, "列表为空 => Origin=Empty");
            T.Eq(r.MatchedVersionKey, "default", "分支仍是 default（引擎只判键存在，不判是否为空）");
            T.Eq(r.DescendingOrder.Count, 0, "空 <default> 块 -> 空目录列表");
            T.True(r.Issues.Any(d => d.Code == Codes.LoadFolderListEmpty), "应报 RD2018（版本块里没有 <li>）");
            T.True(r.Issues.Any(d => d.Code == Codes.LoadFolderResolvedEmpty && d.Severity == Severity.Error),
                "应报 RD2024 error");
        });

        T.Case("★ If* 条件全部不满足 → 空列表 + RD2024 error", () =>
        {
            using var m = new TempMod("<loadFolders><v1.6><li IfModActive=\"nope.mod\">1.6</li></v1.6></loadFolders>", "1.6");
            var r = m.Resolve(Active("a.mod"));
            T.Eq(r.DescendingOrder.Count, 0, "条件不满足 → 一个目录都不加载");
            T.True(r.Issues.Any(d => d.Code == Codes.LoadFolderResolvedEmpty), "应报 RD2024");
        });

        T.Case("★ 解析失败/注释：注释跳过、坏 XML 不抛异常（引擎 catch 后返回空 ModLoadFolders → 回退分支）", () =>
        {
            using (var m = new TempMod(
                "<loadFolders><!-- <v1.5><li>X</li></v1.5> --><v1.6><li>1.6</li><!-- 注释 --></v1.6></loadFolders>", "1.6"))
            {
                var r = m.Resolve(supported: new[] { "1.6" });
                T.Eq(Names(r.DescendingOrder), "1.6", "注释里的版本块与注释条目都不算数");
                T.Eq(r.Issues.Count, 0, "合法文件（且 supportedVersions 含 1.6）不该有任何 issue");

                // 引擎口径：supportedVersions 里没有该键时**会**报 ModLoadFolderDefinesUnsupportedGameVersion，
                // 所以「没写 <supportedVersions> 的 mod」每个版本键都会得到一条 RD2023（info）。
                var rNoSupported = m.Resolve();
                T.True(rNoSupported.Issues.Any(d => d.Code == Codes.LoadFolderUnsupportedVersion),
                    "不给 supportedVersions 时 RD2023 必须命中（引擎的 mod.SupportedVersionsReadOnly.Contains 为 false）");
            }

            using (var m = new TempMod("<loadFolders><v1.6><li>1.6</li></loadFolders>", "1.6", "Common"))
            {
                var doc = LoadFolderResolver.Load(m.Root);
                T.NotNull(doc.ParseError, "坏 XML 必须有 ParseError");
                var r = m.Resolve();
                T.Eq(r.Origin, ResolutionOrigin.FallbackDir, "解析失败 ⇒ DefinedVersions 为空 ⇒ 回退分支（引擎行为）");
                T.Eq(Names(r.DescendingOrder, m.Root), "1.6|Common|<root>", "回退分支的三个目录");
                T.True(r.Issues.Any(d => d.Code == Codes.XmlParseFailed && d.Severity == Severity.Warning),
                    "应报 RD2001（Warning：引擎会兜底，mod 仍会加载）");
            }
        });

        // ───────── 规则族 ─────────
        T.Case("★ 规则族 RD2012/RD2018/RD2020/RD2022/RD2023 各命中一次", () =>
        {
            // 1.6.1 → IsWellFormedVersionString 要求**恰好两段** ⇒ RD2020
            // 1.5 排在 1.6.1 之后 ⇒ RD2022（1.5 < 1.6.1）
            // <v1.5> 空块 ⇒ RD2018；且 TryParseVersionString("1.5")=1.5 不在 supportedVersions{1.6} ⇒ RD2023
            // <li>Nope</li> 目录不存在 ⇒ RD2012
            // 向下取版本选中 "1.6.1"（字符串序 1.6.1 > 1.5），其 <li> 目录不存在但列表非空 ⇒ 不报 RD2024
            using var m = new TempMod(
                "<loadFolders><v1.6.1><li>Nope</li></v1.6.1><v1.5></v1.5></loadFolders>");
            var r = m.Resolve(supported: new[] { "1.6" });
            var codes = r.Issues.Select(d => d.Code).OrderBy(x => x).ToList();
            T.True(codes.Contains(Codes.LoadFolderMalformedVersion), $"RD2020 命中，实际 {string.Join(",", codes)}");
            T.True(codes.Contains(Codes.LoadFolderOutOfOrder), "RD2022 命中");
            T.True(codes.Contains(Codes.LoadFolderListEmpty), "RD2018 命中");
            T.True(codes.Contains(Codes.LoadFolderDoesntExist), "RD2012 命中");
            T.True(codes.Contains(Codes.LoadFolderUnsupportedVersion), "RD2023 命中");
            T.False(codes.Contains(Codes.LoadFolderResolvedEmpty), "列表非空 ⇒ 不应报 RD2024");
            T.False(codes.Contains(Codes.LoadFolderDefaultDeprecated), "没有 default 键 ⇒ 不应报 RD2021");

            // ★ 引擎用 TryParseVersionString 只取前两段 ⇒ "1.6.1" 与 "1.6" 是**同一个 Version**，
            //   所以 supportedVersions=[1.6] 时 1.6.1 **不会**触发 RD2023（这是引擎的真实口径，别「修正」它）。
            T.True(LoadFolderResolver.TryParseVersionString("1.6.1", out var v161) && v161 == new Version(1, 6),
                "TryParseVersionString(\"1.6.1\") 只取前两段 = Version(1,6)");
        });

        T.Case("★ RD2021 default 键（info）命中一次", () =>
        {
            using var m = new TempMod("<loadFolders><default><li>1.6</li></default></loadFolders>", "1.6");
            var r = m.Resolve();
            var d = r.Issues.SingleOrDefault(x => x.Code == Codes.LoadFolderDefaultDeprecated);
            T.NotNull(d, "应报 RD2021");
            T.Eq(d!.Severity, Severity.Info, "RD2021 是 info（Docs\\01 §7 码表）");
            T.Eq(r.Origin, ResolutionOrigin.Default, "Origin=Default");
        });

        T.Case("★ RD2019 重复 <li>：同条件才报，互斥条件的同名写法不报（防假阳）", () =>
        {
            using (var m = new TempMod("<loadFolders><v1.6><li>1.6</li><li>1.6</li></v1.6></loadFolders>", "1.6"))
            {
                var r = m.Resolve();
                var d = r.Issues.SingleOrDefault(x => x.Code == Codes.LoadFolderRepeatingFolder);
                T.NotNull(d, "同条件重复应报 RD2019");
                T.Eq(d!.Severity, Severity.Warning, "RD2019 是 warning");
            }

            using (var m = new TempMod(
                "<loadFolders><v1.6>"
                + "<li IfModActive=\"a.mod\">1.6</li>"
                + "<li IfModNotActive=\"a.mod\">1.6</li>"
                + "</v1.6></loadFolders>", "1.6"))
            {
                var r = m.Resolve();
                T.False(r.Issues.Any(x => x.Code == Codes.LoadFolderRepeatingFolder),
                    "同名但条件互斥是**合法**写法，不得报 RD2019（假阳比漏报更贵）");
            }
        });

        // ───────── 文件发现 ─────────
        T.Case("★ DefFileEnumerator：相对键去重（list[0] 先到先得）+ 跳过点文件 + root 与 1.6 并存时两份都算", () =>
        {
            using var m = new TempMod("<loadFolders><v1.6><li>/</li><li>1.6</li></v1.6></loadFolders>",
                "Defs", Path.Combine("1.6", "Defs"));
            File.WriteAllText(Path.Combine(m.Root, "Defs", "Same.xml"), "<Defs/>");
            File.WriteAllText(Path.Combine(m.Root, "1.6", "Defs", "Same.xml"), "<Defs/>");
            File.WriteAllText(Path.Combine(m.Root, "Defs", "OnlyRoot.xml"), "<Defs/>");
            File.WriteAllText(Path.Combine(m.Root, "1.6", "Defs", ".Hidden.xml"), "<Defs/>");
            File.WriteAllText(Path.Combine(m.Root, "1.6", "Defs", "._Resource.xml"), "<Defs/>");

            var r = m.Resolve();
            T.Eq(Names(r.DescendingOrder, m.Root), "1.6|<root>", "loadFolders 分支顺序：后声明的 1.6 优先");

            var files = DefFileEnumerator.Enumerate(r.DescendingOrder);
            var keys = files.Select(f => f.RelativeKey).OrderBy(x => x, StringComparer.Ordinal).ToList();
            // 去重键相对**各自文件夹根**：Defs\Same.xml 与 Defs\Same.xml 是**同一个键** ⇒ 只留 1.6 那份（list[0] 赢）；
            // OnlyRoot.xml 只在 root 下 ⇒ 保留；两个点文件被跳过。
            T.Eq(string.Join(" | ", keys), "Defs\\OnlyRoot.xml | Defs\\Same.xml", "相对键集合");
            T.Eq(files.Count, 2, "文件数（5 个磁盘文件 − 2 个点文件 − 1 个被胜者遮蔽）");
            var same = files.Single(f => f.RelativeKey == "Defs\\Same.xml");
            T.True(same.FileAbs.Contains(Path.Combine("1.6", "Defs")), $"Same.xml 的胜者应来自 list[0]（1.6），实际 {same.FileAbs}");
            T.Eq(same.FolderIndex, 0, "胜者的归属文件夹下标 = 0");
        });

        T.Case("★ DefFileEnumerator：引擎口径下 root 与 1.6 下的**不同**相对路径会同时加载", () =>
        {
            using var m = new TempMod(null, "1.6", "Defs", Path.Combine("1.6", "Defs"));
            File.WriteAllText(Path.Combine(m.Root, "Defs", "A.xml"), "<Defs/>");
            File.WriteAllText(Path.Combine(m.Root, "1.6", "Defs", "B.xml"), "<Defs/>");

            var r = m.Resolve();
            T.Eq(Names(r.DescendingOrder, m.Root), "1.6|<root>", "回退分支：1.6 优先，root 在后");
            var files = DefFileEnumerator.Enumerate(r.DescendingOrder);
            T.Eq(files.Count, 2, "两个不同相对键 ⇒ 两份都加载（引擎不会因为都在 Defs 下就吞掉一份）");
        });

        // ───────── 官方内容零假阳（结构性） ─────────
        T.Case("★ 官方 6 个包上 loadFolders 规则族 0 命中（结构性：它们没有 loadFolders.xml）", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
            var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;

            var official = cat.All.Where(m => m.Source == ModSource.Official).ToList();
            T.Eq(official.Count, 6, "官方包数（Core + 5 DLC）");
            foreach (var m in official)
            {
                T.Null(LoadFolderResolver.FindLoadFoldersFile(m.RootPath),
                    $"{m.PackageId} 不应有 loadFolders.xml（否则本用例的前提不成立）");
                T.Eq(m.Resolution!.Issues.Count, 0, $"{m.PackageId} 不应有 loadFolders issue");
            }
            // ★ 诚实边界：官方内容**没有** loadFolders.xml，所以「官方 0 假阳」是结构性必然，不构成规则正确性的证据。
            //   真正的假阳防线是全库口径（见下一条）与 RD2019/RD2023 的反例用例。
        });

        T.Case("★ 全库每个有 loadFolders 的 mod 都能跑完规则族且不抛异常（规则健壮性）", () =>
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
            var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;

            int withLf = 0, failed = 0, issueTotal = 0;
            foreach (var m in cat.All)
            {
                var doc = LoadFolderResolver.Load(m.RootPath);
                if (!doc.HasFile) continue;
                withLf++;
                try
                {
                    var r = LoadFolderResolver.Resolve(m.RootPath, m.PackageId, doc, TestVersion,
                        LoadFolderResolver.ActiveMatcher(active), m.SupportedVersions);
                    issueTotal += r.Issues.Count;
                }
                catch { failed++; }
            }
            T.Eq(failed, 0, $"规则族在全库 {withLf} 个有 loadFolders 的 mod 上不得抛异常");
            T.True(withLf > 1000, $"有 loadFolders 的 mod 数应 > 1000，实际 {withLf}");
            Console.WriteLine($"    [loadFolders 全库] 有 loadFolders 的 mod={withLf}，issue 总数={issueTotal}");
        });
    }
}
