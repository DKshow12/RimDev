using RimDev.Core.Catalog;
using RimDev.Core.Checks;
using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// 第 3 期 Task 5a：<c>InheritanceLayer</c> —— `ParentName` 解析口径与可观测性。
///
/// ★ 用例分工（照「**能证伪它**的用例」原则写，不是照「能验证它」的用例）：
/// <list type="bullet">
/// <item><b>(a)</b> `NameSpace` 直接命中 ⇒ 不报 RD2005；</item>
/// <item><b>★★ (b)(b2)</b> **补丁补的 `Name` 让 `ParentName` 解析得开** —— 夹具照抄 CE 的形状。
///   两条缺一不可：有补丁 ⇒ `viaPatchLiteral`；**抽掉补丁** ⇒ 退化为 `DefNameFallback`。
///   只写前者就证伪不了任何东西；</item>
/// <item><b>(c)</b> 只命中 defName ⇒ 记为 `DefNameFallback`、**不报 error**（有意降级，不是漏报）；</item>
/// <item><b>(d)</b> 两空间皆无 ⇒ 报 RD2005，且 evidence 带 `basis`；</item>
/// <item><b>(e)(e2)(f)</b> `MayRequire*` 不满足/满足。**★ (e2) 与 (e4) 合起来钉的是那条分水岭**：
///   被挡掉的**父类**会让**仍会被加载的子节点**变 `Unresolved`（RD2005 = 1，与引擎对齐，不是假阳）；
///   但**子节点自己也被挡**时，引擎的建 def 循环连它都不处理（IL 实证，见 `InheritanceLayer` 类注释【3b】）
///   ⇒ 必须**不发** RD2005（`01` §11.1 T17「不产生悬空引用误报」）。旧断言在 (e4) 上写 1，属假阳口径；</item>
/// <item><b>(e3)(e4)(e5)</b> 反编译实证的语义：`MayRequire` 是**与**（`AllModsActiveNoSuffix`）、
///   `MayRequireAnyOf` 是**或**（`AnyFromListActive`）、**逗号**才是分隔符（空格分隔是错的）、
///   判据是 **Active** 而不是「已安装」、id 走 `ToLowerInvariant` 且 `-后缀` 被剥掉；</item>
/// <item><b>(g)(h)(g2)(g3)</b> 同名两处声明**不**算环（回归）/ 真自环照报 /
///   **★ (g2)** 同一行的两处声明是**两处**（身份键 = 对象引用，不是 `FileAbs+Line`：按行号会把
///   同行的父子当成同一个 ⇒ **假 RD2006**，并静默丢掉一半诊断）/ **★ (g3)** 链式断链的文案必须落在
///   **真正断的那一跳**上（旧文案对全库 144 条说「直接父名不存在」，而那个父名明明存在）；</item>
/// <item><b>(i)(i2)</b> 补丁字面量集：`<operations><li>` 与 `<match>` 都要下探，且只认根为 `<Patch>` 的文件；</item>
/// <item><b>(j)(m)</b> 真语料（官方 6 包与启用集）：`Unresolved == 0`、环 `0`、RD2005 `0`（**现状不变差**）；
///   **★ (m)** 分桶**不变式**：`withParentName == skippedByMayRequire + 四数之和`。</item>
/// </list>
///
/// ★ 夹具纪律（**原因已在第 3 期 Task 5a 被修掉，纪律保留为可读性约定**）：
///   声明身份键是 **`RawDef` 对象引用**，不是 `(FileAbs, Line)` —— 同一行写多条声明现在是安全的
///   （(g2) 就故意这么写来钉住修复）。既有夹具仍**每条声明独占一行**，只为好读、好定位。
///   另外**自闭合标签里不能写子元素**（`<ThingDef ... />` 里的 `<defName>` 只是文本，不会被采集）。
/// </summary>
public static class Phase3Task5aCases
{
    // ───────────────────────── 夹具 ─────────────────────────

    private sealed class TempMod : IDisposable
    {
        public string Root { get; }
        public string PackageId { get; }

        public TempMod(string packageId)
        {
            PackageId = packageId;
            Root = Path.Combine(Path.GetTempPath(), "rimdev-t5a-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public void Write(string relPath, string content)
        {
            var full = Path.Combine(Root, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { /* 临时目录，忽略 */ }
        }
    }

    private static readonly EngineVersionInfo TestVersion =
        new("1.6.4871", "1.6", new Version(1, 6, 4871, 591), "test", null);

    /// <summary>合成 mod 集合 → 真扫盘 → 真建索引 → 真建上下文（与生产路径同一条链路）。</summary>
    private static CheckContext BuildContext(IReadOnlyList<TempMod> mods, IReadOnlySet<string>? activeIds = null)
    {
        var modInfos = mods.Select(m => new ModInfo(
            PackageId: m.PackageId,
            DisplayName: m.PackageId,
            RootPath: m.Root,
            Source: ModSource.Local,
            PublishedFileId: null,
            SupportedVersions: new[] { "1.6" },
            LoadFolders: new[] { m.Root },
            Dependencies: Array.Empty<string>(),
            LoadAfter: Array.Empty<string>(),
            LoadBefore: Array.Empty<string>(),
            IsActive: true,
            HasAssemblies: false,
            AboutPath: null,
            AboutError: null,
            Resolution: LoadFolderResolver.Resolve(m.Root, m.PackageId, LoadFolderResolver.Load(m.Root),
                TestVersion, LoadFolderResolver.ActiveMatcher(Array.Empty<string>()), new[] { "1.6" }))).ToList();

        var files = DefScanner.CollectFiles(
            modInfos.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)));
        var scan = DefScanner.Scan(files);
        var idx = DefIndex.Build(scan.Defs);
        var active = activeIds
            ?? new HashSet<string>(modInfos.Select(m => m.PackageId.ToLowerInvariant()), StringComparer.Ordinal);
        return new CheckContext(idx, scan.Files, ModCatalog.FromMods(modInfos), scan.Defs, null,
            ActivePackageIds: active);
    }

    private static (int Rd2005, int Rd2006, int Rd2008) RunRules(CheckContext ctx)
    {
        var runner = CheckRunner.Default();
        var diags = runner.Run(ctx).ToList();
        return (diags.Count(d => d.Code == Codes.ParentNotFound),
                diags.Count(d => d.Code == Codes.InheritanceCycle),
                diags.Count(d => d.Code == Codes.MayRequireModMissing));
    }

    private static IReadOnlySet<string> Active(params string[] ids) =>
        new HashSet<string>(ids, StringComparer.Ordinal);

    // ───────────────────────── 真语料上下文 ─────────────────────────

    private static CheckContext? _active;
    private static CheckContext? _official;

    /// <summary>启用集（与 `check --scope active` 同一条链路）。</summary>
    private static CheckContext ActiveContext()
    {
        if (_active is not null) return _active;

        var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
        var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
        var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;
        var order = LoadOrder.Build(env, cat, active, LoadOrder.ReadKnownExpansions(env.ModsConfigPath));
        var mods = cat.Active.ToList();

        var files = DefScanner.CollectFiles(mods.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)));
        var scan = DefScanner.Scan(files);
        _active = new CheckContext(DefIndex.Build(scan.Defs), scan.Files, cat, scan.Defs, null,
            LoadOrder: order, ModRank: order.RankFor(mods),
            // 与 CLI 同口径：已启用 packageId 的小写全集
            ActivePackageIds: new HashSet<string>(active.Select(p => p.Trim().ToLowerInvariant()), StringComparer.Ordinal));
        return _active;
    }

    /// <summary>官方 6 个包（Core + 5 DLC）：本口径最重要的反证材料。</summary>
    private static CheckContext OfficialContext()
    {
        if (_official is not null) return _official;

        var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
        var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
        var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;
        var order = LoadOrder.Build(env, cat, active, LoadOrder.ReadKnownExpansions(env.ModsConfigPath));
        var mods = cat.All.Where(m => m.Source == ModSource.Official).ToList();

        var files = DefScanner.CollectFiles(mods.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)));
        var scan = DefScanner.Scan(files);
        _official = new CheckContext(DefIndex.Build(scan.Defs), scan.Files, ModCatalog.FromMods(mods), scan.Defs, null,
            LoadOrder: order, ModRank: order.RankFor(mods),
            ActivePackageIds: new HashSet<string>(
                active.Concat(mods.Select(m => m.PackageId)).Select(p => p.Trim().ToLowerInvariant()),
                StringComparer.Ordinal));
        return _official;
    }

    // ───────────────────────── 用例 ─────────────────────────

    public static void Run()
    {
        T.Group("phase3-task5a");

        // ═══════════ (a) NameSpace 直接命中 ═══════════

        T.Case("★ (a) ParentName 命中 Name 空间 ⇒ basis=NameSpace、不报 RD2005", () =>
        {
            using var m = new TempMod("syn.t5a.a");
            m.Write(Path.Combine("Defs", "A.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Defs>\n"
                + "  <ThingDef Name=\"SynBaseA\" Abstract=\"True\">\n    <defName>SynBaseA</defName>\n  </ThingDef>\n"
                + "  <ThingDef ParentName=\"SynBaseA\">\n    <defName>SynChildA</defName>\n  </ThingDef>\n"
                + "</Defs>\n");

            var ctx = BuildContext(new[] { m });
            var a = InheritanceLayer.Analyze(ctx);
            T.Eq(a.NameSpace, 1, "1 条声明由 Name 空间解析");
            T.Eq(a.Unresolved, 0, "没有未解析");
            T.Eq(a.WithParentName, 1, "带 ParentName 的声明数 = 四个桶之和");
            T.Eq(RunRules(ctx).Rd2005, 0, "RD2005 = 0");
        });

        // ═══════════ ★★ (b)(b2) 补丁补的 Name ═══════════

        // CE 的形状：目标 def 只有 defName；补丁给它补 Name=；另一个 def 写 ParentName=这个名字。
        var ceShapeDefs =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Defs>\n"
            + "  <ThingDef>\n    <defName>SynPatchTarget</defName>\n  </ThingDef>\n"
            + "  <ThingDef ParentName=\"SynPatchTarget\">\n    <defName>SynPatchChild</defName>\n  </ThingDef>\n"
            + "</Defs>\n";
        var ceShapePatch =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Patch>\n"
            + "  <Operation Class=\"PatchOperationAttributeAdd\">\n"
            + "    <xpath>Defs/ThingDef[defName=\"SynPatchTarget\"]</xpath>\n"
            + "    <attribute>Name</attribute>\n"
            + "    <value>SynPatchTarget</value>\n"
            + "  </Operation>\n"
            + "</Patch>\n";

        T.Case("★★ (b) 补丁补的 Name 让 ParentName 解析得开（basis=NameSpaceViaPatchLiteral、RD2005=0）", () =>
        {
            using var m = new TempMod("syn.t5a.b");
            m.Write(Path.Combine("Defs", "A.xml"), ceShapeDefs);
            m.Write(Path.Combine("Patches", "N.xml"), ceShapePatch);

            var ctx = BuildContext(new[] { m });
            var a = InheritanceLayer.Analyze(ctx);

            T.Eq(a.PatchFilesScanned, 1, "扫到 1 个补丁文件（根是 <Patch>）");
            T.Eq(a.PatchNameLiterals.Count, 1, "补丁字面量 distinct = 1");
            T.True(a.PatchNameLiterals.Contains("SynPatchTarget"), "字面量就是 SynPatchTarget");
            T.Eq(a.ViaPatchLiteral, 1, "1 条声明由【补丁字面量】解析");
            T.Eq(a.DefNameFallback, 0, "★ 不许退化成 defName 兜底");
            T.Eq(a.Unresolved, 0, "没有未解析");

            var res = a.ByDeclaration.Values.Single();
            T.Eq(res.DirectBasis, InheritanceLayer.ResolutionBasis.NameSpaceViaPatchLiteral, "直接依据");
            T.Eq(res.ChainBasis, InheritanceLayer.ResolutionBasis.NameSpaceViaPatchLiteral, "链依据");
            // ★ 设计要点：走补丁字面量时 Terminal 为 null —— 没有求值 XPath，「补丁补在哪个 def 上」是未知的，
            //   能确定的只是「这个名字有人补」。不编造一个 Terminal 是本口径的诚实性要求。
            T.Null(res.Terminal, "补丁字面量族不产出 Terminal（未求值 XPath）");

            var (rd2005, _, _) = RunRules(ctx);
            T.Eq(rd2005, 0, "★ 这条正是会被「只认字面 Name」的实现误报成 RD2005 的形状 —— 必须 0 条");
        });

        T.Case("★★ (b2) 同夹具**抽掉补丁** ⇒ 退化为 DefNameFallback、仍然不报 error（证明 (b) 是补丁的功劳）", () =>
        {
            using var m = new TempMod("syn.t5a.b2");
            m.Write(Path.Combine("Defs", "A.xml"), ceShapeDefs);   // 只少一个补丁文件

            var ctx = BuildContext(new[] { m });
            var a = InheritanceLayer.Analyze(ctx);

            T.Eq(a.PatchFilesScanned, 0, "没有补丁文件");
            T.Eq(a.PatchNameLiterals.Count, 0, "没有补丁 ⇒ 空集");
            T.Eq(a.ViaPatchLiteral, 0, "不是补丁救回的");
            T.Eq(a.DefNameFallback, 1, "★ 退化为 defName 兜底（近似）");
            T.Eq(a.Unresolved, 0, "兜底仍然解析成功");
            T.Eq(RunRules(ctx).Rd2005, 0, "DefNameFallback 不报 error（只有 Unresolved 才报）");
        });

        // ═══════════ (c)(d) 兜底与未解析 ═══════════

        T.Case("★ (c) 只命中 defName ⇒ basis=DefNameFallback、**不报 error**（有意的降级，不是漏报）", () =>
        {
            using var m = new TempMod("syn.t5a.c");
            m.Write(Path.Combine("Defs", "A.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Defs>\n"
                + "  <HediffDef>\n    <defName>SynBruise</defName>\n  </HediffDef>\n"
                + "  <HediffDef ParentName=\"SynBruise\">\n    <defName>SynBruiseChild</defName>\n  </HediffDef>\n"
                + "</Defs>\n");

            var ctx = BuildContext(new[] { m });
            var a = InheritanceLayer.Analyze(ctx);
            T.Eq(a.NameSpace, 0, "Name 空间没有这个名字");
            T.Eq(a.DefNameFallback, 1, "走 defName 兜底");
            var (rd2005, _, _) = RunRules(ctx);
            T.Eq(rd2005, 0, "★ DefNameFallback 不报 RD2005（否则 96/497 会被补丁字面量救回的那批全成假阳）");
        });

        T.Case("★ (d) 两空间皆无 ⇒ Unresolved，报 RD2005 且 evidence 带 basis", () =>
        {
            using var m = new TempMod("syn.t5a.d");
            m.Write(Path.Combine("Defs", "A.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Defs>\n"
                + "  <ThingDef ParentName=\"SynNowhere\">\n    <defName>SynOrphan</defName>\n  </ThingDef>\n"
                + "</Defs>\n");

            var ctx = BuildContext(new[] { m });
            var a = InheritanceLayer.Analyze(ctx);
            T.Eq(a.Unresolved, 1, "1 条未解析");

            var runner = CheckRunner.Default();
            var d = runner.Run(ctx).Single(x => x.Code == Codes.ParentNotFound);
            T.Eq(d.Severity, Severity.Error, "RD2005 是 error");
            T.NotNull(d.Evidence, "必须有 evidence");
            T.Eq(d.Evidence!["basis"], "Unresolved", "evidence.basis");
            T.Eq(d.Evidence!["chainBasis"], "Unresolved", "evidence.chainBasis");
            T.Eq(d.Evidence!["nameSpaceHit"], "False", "evidence.nameSpaceHit");
            T.Eq(d.Evidence!["defNameHit"], "False", "evidence.defNameHit");
            T.Eq(d.Evidence!["patchLiteralHit"], "False", "evidence.patchLiteralHit");
        });

        // ═══════════ (e)(e2)(f) MayRequire 的判定与方向 ═══════════

        T.Case("★ (e) MayRequire 不满足（指定的 mod 未启用）⇒ 发 RD2008（info，逐节点一条）", () =>
        {
            using var m = new TempMod("syn.t5a.e");
            m.Write(Path.Combine("Defs", "A.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Defs>\n"
                + "  <ThingDef MayRequire=\"syn.not.installed\" Name=\"SynMayBase\" Abstract=\"True\">\n    <defName>SynMayBase</defName>\n  </ThingDef>\n"
                + "  <ThingDef MayRequire=\"syn.not.installed\">\n    <defName>SynMayNode</defName>\n  </ThingDef>\n"
                + "</Defs>\n");

            var ctx = BuildContext(new[] { m });
            var runner = CheckRunner.Default();
            var rd2008 = runner.Run(ctx).Where(x => x.Code == Codes.MayRequireModMissing).ToList();
            T.Eq(rd2008.Count, 2, "逐节点一条（两个节点都带 MayRequire 且都没被满足）");
            T.Eq(rd2008[0].Severity, Severity.Info, "RD2008 是 info（上万条也不该是 error）");
            T.Eq(rd2008[0].Evidence!["mayRequire"], "syn.not.installed", "evidence 带 MayRequire 原文");
            T.Eq(rd2008[0].Evidence!["missingMods"], "syn.not.installed", "evidence 带未启用的 mod");
        });

        T.Case("★★ (e2) 分水岭之一：父类被挡、子节点**仍会加载** ⇒ 子节点变 Unresolved（RD2005 = 1，与引擎对齐）", () =>
        {
            using var m = new TempMod("syn.t5a.e2");
            m.Write(Path.Combine("Defs", "A.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Defs>\n"
                + "  <ThingDef MayRequire=\"syn.not.installed\" Name=\"SynBlockedBase\" Abstract=\"True\">\n    <defName>SynBlockedBase</defName>\n  </ThingDef>\n"
                + "  <ThingDef ParentName=\"SynBlockedBase\">\n    <defName>SynBlockedChild</defName>\n  </ThingDef>\n"
                + "</Defs>\n");

            var ctx = BuildContext(new[] { m });
            var a = InheritanceLayer.Analyze(ctx);
            T.Eq(a.NameSpace, 0, "★ 被挡掉的声明不作为父类候选（引擎 TryRegister 会直接 return）");
            T.Eq(a.Unresolved, 1, "★ 于是子节点从「可解析」变成「未解析」——与引擎对齐的 RD2005（**不是**假阳：引擎里那个父类确实不存在）");
            T.Eq(RunRules(ctx).Rd2005, 1, "RD2005 = 1（与引擎对齐，不是假阳）");
            // ★ 归因可见性（`--scope all` 上 RD2005 比「字面 Name 空间口径」多出来的那一大批就是这一类）：
            //   两个计数器必须把它单独报出来，否则使用者只会看到「RD2005 比预期多 3 倍」而无法归因。
            T.Eq(a.NameSpaceBlockedByMayRequire, 1, "★ 父名在 Name 空间有候选、但候选全被 MayRequire* 挡掉 ⇒ 计数 1");
            T.Eq(a.UnresolvedMayRequireBlocked, 1, "★ 其中判成 Unresolved 的 1 条（RD2005 的来源之一）");
        });

        T.Case("★ (e3) 真语料形状：官方 Core 的 MayRequireAnyOf=\"A,B\"（逗号分隔、任一命中）⇒ 不跳过", () =>
        {
            using var m = new TempMod("syn.t5a.e3");
            m.Write(Path.Combine("Defs", "A.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Defs>\n"
                + "  <StatDef Name=\"SynMechStatBase\" Abstract=\"True\" MayRequireAnyOf=\"ludeon.rimworld.biotech,ludeon.rimworld.anomaly\">\n"
                + "    <defName>SynMechStatBase</defName>\n  </StatDef>\n"
                + "  <StatDef ParentName=\"SynMechStatBase\">\n    <defName>SynBandwidthCost</defName>\n  </StatDef>\n"
                + "</Defs>\n");

            // 只启用 Biotech（Anomaly 未启用）—— 走的是 `AnyFromListActive` = 任一命中
            var ctx = BuildContext(new[] { m }, Active("syn.t5a.e3", "ludeon.rimworld.biotech"));
            var a = InheritanceLayer.Analyze(ctx);

            T.Eq(RunRules(ctx).Rd2008, 0, "任一命中即满足 ⇒ 不发 RD2008");
            T.Eq(a.NameSpace, 1, "★ 父类可用 ⇒ 由 Name 空间解析（与官方 Stats_Pawns_General.xml:653 同形）");
            T.Eq(a.Unresolved, 0, "不是 Unresolved");
        });

        T.Case("★★ (e4) 语义差异：MayRequire=\"A,B\" 是【与】(全部都要)，MayRequireAnyOf=\"A,B\" 是【或】", () =>
        {
            using var a1 = new TempMod("syn.t5a.e4a");
            a1.Write(Path.Combine("Defs", "A.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Defs>\n"
                + "  <ThingDef Name=\"SynAndBase\" Abstract=\"True\" MayRequire=\"ludeon.rimworld.biotech,ludeon.rimworld.anomaly\">\n"
                + "    <defName>SynAndBase</defName>\n  </ThingDef>\n"
                + "  <ThingDef ParentName=\"SynAndBase\" MayRequire=\"ludeon.rimworld.biotech,ludeon.rimworld.anomaly\">\n"
                + "    <defName>SynAndChild</defName>\n  </ThingDef>\n"
                + "</Defs>\n");
            using var a2 = new TempMod("syn.t5a.e4b");
            a2.Write(Path.Combine("Defs", "B.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Defs>\n"
                + "  <ThingDef Name=\"SynOrBase\" Abstract=\"True\" MayRequireAnyOf=\"ludeon.rimworld.biotech,ludeon.rimworld.anomaly\">\n"
                + "    <defName>SynOrBase</defName>\n  </ThingDef>\n"
                + "  <ThingDef ParentName=\"SynOrBase\" MayRequireAnyOf=\"ludeon.rimworld.biotech,ludeon.rimworld.anomaly\">\n"
                + "    <defName>SynOrChild</defName>\n  </ThingDef>\n"
                + "</Defs>\n");

            var active = Active("syn.t5a.e4a", "syn.t5a.e4b", "ludeon.rimworld.biotech");

            var c1 = BuildContext(new[] { a1 }, active);
            T.Eq(RunRules(c1).Rd2008, 2, "★ MayRequire 是「与」：Anomaly 没启用 ⇒ 父模板与子节点各一条 RD2008");
            // ★★ 第 3 期 Task 5a 修正（原断言写 1，**属假阳口径**）：本夹具的**子节点自己也带同一串
            //   `MayRequire`** ⇒ 引擎 `ParseAndProcessXML` 的建 def 循环也不会处理它（IL 实证）
            //   ⇒ 它既不建 def、也不报 RD2005。与 (e2) 的差异正是那条分水岭：
            //     · 父挡、子**未**挡 ⇒ 引擎真会报（(e2)：RD2005 = 1）；
            //     · 父挡、子**也**挡 ⇒ 引擎两处都跳，报它就是假阳（本例：RD2005 = 0）。
            var an1 = InheritanceLayer.Analyze(c1);
            T.Eq(an1.Unresolved, 0, "★ 父与子都被挡 ⇒ 子节点引擎里不加载 ⇒ 不是 Unresolved（旧断言 1 = 假阳口径）");
            T.Eq(an1.SkippedByMayRequire, 1, "★ 被挡且**带 ParentName** 的只有子节点 1 条（父模板没有 ParentName，不进这个计数）");
            T.Eq(an1.WithParentName, 1, "★ 带 ParentName 的声明共 1 条");
            T.Eq(RunRules(c1).Rd2005, 0, "★★ 不发 RD2005（`01` §11.1 T17：不产生悬空引用误报）");

            var c2 = BuildContext(new[] { a2 }, active);
            T.Eq(RunRules(c2).Rd2008, 0, "★ MayRequireAnyOf 是「或」：Biotech 已启用即满足（同一批 mod，结果相反）");
            T.Eq(InheritanceLayer.Analyze(c2).Unresolved, 0, "父类可用 ⇒ 子节点解析成功");
        });

        T.Case("★ (e5) id 的匹配与引擎一致：大小写归一 + `_steam` 后缀归一 + 未启用 ≠ 已安装", () =>
        {
            using var m1 = new TempMod("syn.t5a.e5a");
            m1.Write(Path.Combine("Defs", "A.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Defs>\n"
                + "  <ThingDef Name=\"SynCaseBase\" Abstract=\"True\" MayRequire=\"Ludeon.Rimworld.Biotech\">\n"
                + "    <defName>SynCaseBase</defName>\n  </ThingDef>\n"
                + "</Defs>\n");
            using var m2 = new TempMod("syn.t5a.e5b");
            m2.Write(Path.Combine("Defs", "B.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Defs>\n"
                + "  <ThingDef Name=\"SynSuffixBase\" Abstract=\"True\" MayRequire=\"syn.suffixed\">\n"
                + "    <defName>SynSuffixBase</defName>\n  </ThingDef>\n"
                + "</Defs>\n");

            // m1 的 id 大小写与真值不同 ⇒ 验引擎的 `ToLowerInvariant()`；
            // m2：活动集里只有带后缀的那一份（工坊/本地撞 packageId 时 `appendPackageIdSteamPostfix`），
            //     而引擎建表用的是 `packageIdLowerCase`（About 里那个 id，不带后缀）⇒ 必须命中。
            //     ★ 后缀是 **`_steam`**（IL：`Verse.ModMetaData..cctor` 的 `ldstr "_steam"` →
            //       `stsfld SteamModPostfix`，token 0x060024DF），**不是** `-steam`。
            var active = Active("syn.t5a.e5a", "syn.t5a.e5b", "ludeon.rimworld.biotech", "syn.suffixed_steam");
            var ctx = BuildContext(new[] { m1, m2 }, active);
            T.Eq(RunRules(ctx).Rd2008, 0, "★ 大小写归一 + `_steam` 后缀归一 ⇒ 两条都算满足");

            // 同一份定义，但两个 mod 都「未启用」⇒ 必须被挡（证明判据是 Active，而不是「装了什么」）
            var ctxNotActive = BuildContext(new[] { m1, m2 }, Active("syn.t5a.e5a", "syn.t5a.e5b"));
            T.Eq(RunRules(ctxNotActive).Rd2008, 2, "★ 两个 mod 未启用 ⇒ 两条 RD2008（判据是 Active）");

            // ★ 证伪型（方向）：引擎**不剥查询侧**的后缀 —— `GetActiveModWithIdentifier` 只做
            //   `identifier.ToLowerInvariant().Trim()` 然后查表，而表键是不带 `_steam` 的
            //   `packageIdLowerCase` ⇒ 活动集只有 `syn.suffixed` 时，`MayRequire="syn.suffixed_steam"`
            //   必须**不命中**。（把后缀剥在查询侧的实现会在这条上失败。）
            using var m3 = new TempMod("syn.t5a.e5c");
            m3.Write(Path.Combine("Defs", "C.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Defs>\n"
                + "  <ThingDef Name=\"SynPostfixBase\" Abstract=\"True\" MayRequire=\"syn.suffixed_steam\">\n"
                + "    <defName>SynPostfixBase</defName>\n  </ThingDef>\n"
                + "</Defs>\n");
            var ctxPostfixQuery = BuildContext(new[] { m3 }, Active("syn.suffixed", "syn.t5a.e5c"));
            T.Eq(RunRules(ctxPostfixQuery).Rd2008, 1, "★ 查询侧不剥后缀：活动集只有 base 时写 `_steam` 不算满足");
        });

        T.Case("★ (f) MayRequire 满足 ⇒ 节点**照常参与**解析（不是一律跳过）", () =>
        {
            using var m = new TempMod("syn.t5a.f");
            m.Write(Path.Combine("Defs", "A.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Defs>\n"
                + "  <ThingDef MayRequire=\"syn.t5a.f\" Name=\"SynOkBase\" Abstract=\"True\">\n    <defName>SynOkBase</defName>\n  </ThingDef>\n"
                + "  <ThingDef MayRequire=\"syn.t5a.f\" ParentName=\"SynOkBase\">\n    <defName>SynOkNode</defName>\n  </ThingDef>\n"
                + "</Defs>\n");

            var ctx = BuildContext(new[] { m });
            var a = InheritanceLayer.Analyze(ctx);
            T.Eq(RunRules(ctx).Rd2008, 0, "满足 ⇒ 不发 RD2008");
            T.Eq(a.NameSpace, 1, "父类照常可解析");
            T.Eq(a.Unresolved, 0, "未解析 0");
            T.Eq(a.NameSpaceBlockedByMayRequire, 0, "MayRequire 满足 ⇒ 一个候选都没被挡（反证上一条不是恒真）");
        });

        // ═══════════ (g)(h) 环：按声明身份 ═══════════

        T.Case("★★ (g) 同名两处声明**不**算环（回归：官方 PawnKinds_Mercenary.xml:198 vs :232 形态）", () =>
        {
            using var m = new TempMod("syn.t5a.g");
            m.Write(Path.Combine("Defs", "A.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Defs>\n"
                // ① 抽象模板：**Name 与 defName 不同串** —— 官方就是这个形状：
                //    `PawnKinds_Mercenary.xml:198` 是 `Name="MercenarySlasherBase"`，
                //    而它内部的装饰性 `<defName>Mercenary_Slasher</defName>` 与 :232 的真 def 同串。
                //    ★ 旧夹具把抽象模板的 Name 也写成 "SynSlasher"，那条 ParentName 就落进了
                //      **Name 空间**（DefNameFallback=0），「靠 defName 兜底也不判环」这层根本没被验到。
                //      这是**夹具错、断言对** ⇒ 改夹具，不改断言。
                + "  <ThingDef Name=\"SynSlasherBase\" Abstract=\"True\">\n    <defName>SynSlasher</defName>\n  </ThingDef>\n"
                // ② 真 def：没有 Name=，于是引用它的 ParentName 只能靠 defName 兜底
                + "  <ThingDef>\n    <defName>SynSlasher</defName>\n  </ThingDef>\n"
                + "  <ThingDef ParentName=\"SynSlasher\">\n    <defName>SynSlasherChild</defName>\n  </ThingDef>\n"
                + "</Defs>\n");

            var ctx = BuildContext(new[] { m });
            var a = InheritanceLayer.Analyze(ctx);
            T.Eq(a.ByDeclaration.Count, 1, "只有一处声明带 ParentName");
            T.Eq(a.NameSpace, 0, "★ 抽象模板的 Name 与它不同串 ⇒ 这一跳只能靠 defName 兜底（钉死下一条的前提）");
            T.Eq(a.DefNameFallback, 1, "那条声明落在 defName 兜底口径上");
            var (rd2005, rd2006, _) = RunRules(ctx);
            T.Eq(rd2006, 0, "★ 不许报环：两处同名声明是两个不同对象");
            T.Eq(rd2005, 0, "也不是未解析");
        });

        T.Case("★ (h) 真自环（Name 与 ParentName 同串）仍要报 RD2006", () =>
        {
            using var m = new TempMod("syn.t5a.h");
            m.Write(Path.Combine("Defs", "A.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Defs>\n"
                + "  <ThingDef Name=\"SynSelf\" ParentName=\"SynSelf\" Abstract=\"True\">\n    <defName>SynSelf</defName>\n  </ThingDef>\n"
                + "</Defs>\n");

            var ctx = BuildContext(new[] { m });
            var (rd2005, rd2006, _) = RunRules(ctx);
            T.Eq(rd2006, 1, "真自环报 1 条 RD2006");
            T.Eq(rd2005, 0, "环优先，不重复报 RD2005");
        });

        // ═══════════ (i)(i2) 补丁字面量集的收集规则 ═══════════

        T.Case("★ (i) 字面量集：Sequence 的 `<operations><li>` 与 Conditional 的 `<match>` 都要下探，SetName 不收", () =>
        {
            using var m = new TempMod("syn.t5a.i");
            m.Write(Path.Combine("Defs", "A.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Defs>\n"
                + "  <ThingDef>\n    <defName>SynL1</defName>\n  </ThingDef>\n"
                + "  <ThingDef>\n    <defName>SynL2</defName>\n  </ThingDef>\n"
                + "  <ThingDef>\n    <defName>SynL3</defName>\n  </ThingDef>\n"
                + "  <ThingDef>\n    <defName>SynL4</defName>\n  </ThingDef>\n"
                + "</Defs>\n");
            m.Write(Path.Combine("Patches", "P.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Patch>\n"
                // ① 顶层 AttributeAdd
                + "  <Operation Class=\"PatchOperationAttributeAdd\">\n"
                + "    <xpath>Defs/ThingDef[defName=\"SynL1\"]</xpath>\n"
                + "    <attribute>Name</attribute><value>SynL1Name</value>\n"
                + "  </Operation>\n"
                // ② Sequence 的操作子节点：元素名是 li（Class 在 li 上）
                + "  <Operation Class=\"PatchOperationSequence\">\n"
                + "    <operations>\n"
                + "      <li Class=\"PatchOperationAttributeSet\">\n"
                + "        <xpath>Defs/ThingDef[defName=\"SynL2\"]</xpath>\n"
                + "        <attribute>Name</attribute><value>SynL2Name</value>\n"
                + "      </li>\n"
                + "    </operations>\n"
                + "  </Operation>\n"
                // ③ Conditional 的 match：元素本身就是一条操作
                + "  <Operation Class=\"PatchOperationConditional\">\n"
                + "    <xpath>Defs/ThingDef[defName=\"SynL3\"]</xpath>\n"
                + "    <match Class=\"PatchOperationAttributeAdd\">\n"
                + "      <xpath>Defs/ThingDef[defName=\"SynL3\"]</xpath>\n"
                + "      <attribute>Name</attribute><value>SynL3Name</value>\n"
                + "    </match>\n"
                + "  </Operation>\n"
                // ④ 干扰项 1：attribute 不是 Name
                + "  <Operation Class=\"PatchOperationAttributeAdd\">\n"
                + "    <xpath>Defs/ThingDef[defName=\"SynL4\"]</xpath>\n"
                + "    <attribute>Class</attribute><value>ShouldNotAppear1</value>\n"
                + "  </Operation>\n"
                // ⑤ 干扰项 2：SetName 是「用新名字重建元素」，旧 Name 随之消失 ⇒ 不提供继承名，必须排除
                + "  <Operation Class=\"PatchOperationSetName\">\n"
                + "    <xpath>Defs/ThingDef[defName=\"SynL4\"]</xpath><name>ShouldNotAppear2</name>\n"
                + "  </Operation>\n"
                + "</Patch>\n");

            var ctx = BuildContext(new[] { m });
            var a = InheritanceLayer.Analyze(ctx);

            Console.WriteLine("    [t5a.i] literals=" + string.Join("|", a.PatchNameLiterals.OrderBy(x => x)));
            T.True(a.PatchNameLiterals.Contains("SynL1Name"), "① 顶层 AttributeAdd");
            T.True(a.PatchNameLiterals.Contains("SynL2Name"), "★ ② Sequence 里的 <li Class=…>（只认 Operation 的实现会漏这条）");
            T.True(a.PatchNameLiterals.Contains("SynL3Name"), "★ ③ Conditional 的 <match Class=…> 元素本身就是操作");
            T.False(a.PatchNameLiterals.Contains("ShouldNotAppear1"), "attribute 不是 Name ⇒ 不收");
            T.False(a.PatchNameLiterals.Contains("ShouldNotAppear2"),
                "★ PatchOperationSetName 不收（它用新名字重建元素，旧 Name 随之消失，反而取消一个继承名）");
            T.Eq(a.PatchNameLiterals.Count, 3, "distinct 字面量恰好 3 个（两个干扰项一个都不许进来）");
        });

        T.Case("★ (i2) 只有根是 `<Patch>` 的文件才算补丁（`<Defs>` 根里的同名元素不算）", () =>
        {
            using var m = new TempMod("syn.t5a.i2");
            m.Write(Path.Combine("Defs", "A.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Defs>\n"
                + "  <ThingDef>\n    <defName>SynR1</defName>\n  </ThingDef>\n"
                + "</Defs>\n");
            m.Write(Path.Combine("Defs", "Fake.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Defs>\n"
                + "  <Operation Class=\"PatchOperationAttributeAdd\">\n"
                + "    <attribute>Name</attribute><value>ShouldNotAppear</value>\n"
                + "  </Operation>\n"
                + "</Defs>\n");

            var ctx = BuildContext(new[] { m });
            var a = InheritanceLayer.Analyze(ctx);
            T.False(a.PatchNameLiterals.Contains("ShouldNotAppear"), "根不是 <Patch> ⇒ 不是补丁文件");
            T.Eq(a.PatchFilesScanned, 0, "扫过的补丁文件数 = 0");
        });

        T.Case("★★ (g2) 同一行的两处声明 = 两处（身份键是对象引用，不是 FileAbs+Line）", () =>
        {
            using var m = new TempMod("syn.t5a.g2");
            // ★ 父与子**写在同一物理行**。按 (FileAbs, Line) 做身份键会把它们折叠成同一个节点：
            //   ① `ByDeclaration` 只剩 1 条（另一半的诊断被静默丢掉）；
            //   ② `Walk` 走到父时判成「回到已访问」⇒ 报**假 RD2006**。本用例同时钉住这两点。
            m.Write(Path.Combine("Defs", "SameLine.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Defs>\n"
                + "  <ThingDef Name=\"SynSameLineBase\" ParentName=\"SynSameLineMissing\" Abstract=\"True\"><defName>SynSameLineBase</defName></ThingDef>"
                + "<ThingDef ParentName=\"SynSameLineBase\"><defName>SynSameLineChild</defName></ThingDef>\n"
                + "</Defs>\n");

            var ctx = BuildContext(new[] { m });
            var a = InheritanceLayer.Analyze(ctx);
            Console.WriteLine($"    [t5a/g2] withParent={a.WithParentName} unresolved={a.Unresolved}");

            T.Eq(a.WithParentName, 2, "★ 同一行的两条声明必须各算一条（旧口径折叠成 1）");
            // ★ 注意 `Unresolved` 是**直接依据**分桶：父模板自己缺父名 ⇒ 1；子节点的**直接**父名
            //   （SynSameLineBase）是解析得开的，进 ChainBroken 而不是 Unresolved。
            T.Eq(a.Unresolved, 1, "★ 直接父名解析不开的只有父模板 1 条");
            T.Eq(a.ChainBroken, 1, "★ 子节点是「父名解析得开、链断在上游」那 1 条");
            T.Eq(a.Unresolved + a.ChainBroken, 2, "★ RD2005 的条数 = 两者之和（本例环为 0）");
            var (rd2005, rd2006, _) = RunRules(ctx);
            T.Eq(rd2006, 0, "★★ 同一行**不是**环 —— 报 RD2006 就是假阳（旧口径在这里报 1 条）");
            T.Eq(rd2005, 2, "★★ 两处声明各一条 RD2005（旧口径被折叠 ⇒ 只剩 1）");
        });

        T.Case("★★ (g3) 链式断链：文案与 File/Line 必须落在**真正断的那一跳**上", () =>
        {
            using var m = new TempMod("syn.t5a.g3");
            m.Write(Path.Combine("Defs", "Chain.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Defs>\n"
                + "  <ThingDef Name=\"SynChainMid\" ParentName=\"SynChainMissing\" Abstract=\"True\">\n    <defName>SynChainMid</defName>\n  </ThingDef>\n"
                + "  <ThingDef ParentName=\"SynChainMid\">\n    <defName>SynChainLeaf</defName>\n  </ThingDef>\n"
                + "</Defs>\n");

            var ctx = BuildContext(new[] { m });
            var a = InheritanceLayer.Analyze(ctx);
            var leaf = a.Get(ctx.Defs.First(d => d.DefName == "SynChainLeaf"))!;
            T.Eq(leaf.DirectBasis, InheritanceLayer.ResolutionBasis.NameSpace, "直接父名（SynChainMid）**解析得开**");
            T.Eq(leaf.ChainBasis, InheritanceLayer.ResolutionBasis.Unresolved, "但整条链断在更上游");

            var rd = CheckRunner.Default().Run(ctx).Where(x => x.Code == Codes.ParentNotFound).ToList();
            T.Eq(rd.Count, 2, "两处声明各一条 RD2005");
            var leafDiag = rd.Single(x => x.DefName == "SynChainLeaf");
            T.True(leafDiag.Message.Contains("继承链在"), "★ 链式断链的文案要说「继承链在…处断开」，不能说直接父名不存在");
            T.True(leafDiag.Message.Contains("SynChainMissing"), "★ 文案要点出**真正缺的那个父名**（SynChainMissing）");
            T.True(leafDiag.Evidence!["chainBrokenAt"].EndsWith("Chain.xml:3"), "★ evidence 带断点声明定位（FileRelToMod:Line）");
            T.Eq(leafDiag.Evidence!["chainBrokenParentName"], "SynChainMissing", "★ evidence 带断点缺的父名");
            T.Eq(leafDiag.Line, 6, "★ File/Line 仍是**引用者**那一行（leaf 自己），不是断点那一行");
        });

        T.Case("★ (m) 分桶不变式：withParentName == skippedByMayRequire + 四数之和", () =>
        {
            using var m = new TempMod("syn.t5a.m");
            m.Write(Path.Combine("Defs", "Partition.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Defs>\n"
                + "  <ThingDef Name=\"SynPartBase\" Abstract=\"True\">\n    <defName>SynPartBase</defName>\n  </ThingDef>\n"
                + "  <ThingDef ParentName=\"SynPartBase\">\n    <defName>SynPartOk</defName>\n  </ThingDef>\n"
                + "  <ThingDef MayRequire=\"syn.not.installed\" ParentName=\"SynPartBase\">\n    <defName>SynPartSkipped</defName>\n  </ThingDef>\n"
                + "  <ThingDef ParentName=\"SynPartMissing\">\n    <defName>SynPartBad</defName>\n  </ThingDef>\n"
                + "</Defs>\n");

            var ctx = BuildContext(new[] { m });
            var a = InheritanceLayer.Analyze(ctx);
            T.Eq(a.WithParentName, 3, "带 ParentName 的声明 3 条");
            T.Eq(a.SkippedByMayRequire, 1, "其中 1 条引擎不加载（不计入四桶）");
            T.Eq(a.NameSpace, 1, "NameSpace 1");
            T.Eq(a.Unresolved, 1, "Unresolved 1");
            T.Eq(a.SkippedByMayRequire + a.NameSpace + a.ViaPatchLiteral + a.DefNameFallback + a.Unresolved,
                 a.WithParentName, "★★ 五路分桶必须闭合（可断言的不变式）");
        });

        // ═══════════ (j) 真语料：官方与启用集 0 未解析（现状不变差） ═══════════

        T.Case("★★ (j) 真语料：官方 6 包与启用集上 Unresolved == 0、环 0、RD2005 == 0", () =>
        {
            var ctx = ActiveContext();
            var a = InheritanceLayer.Analyze(ctx);
            Console.WriteLine($"    [t5a] active: defs={ctx.Defs.Count} withParent={a.WithParentName} "
                              + $"nameSpace={a.NameSpace} viaPatchLiteral={a.ViaPatchLiteral} "
                              + $"defNameFallback={a.DefNameFallback} unresolved={a.Unresolved} "
                              + $"patchFiles={a.PatchFilesScanned} literals={a.PatchNameLiterals.Count}");
            T.Eq(a.Unresolved, 0, "★ 启用集上未解析必须为 0（现状不变差）");
            T.Eq(a.NameSpace, a.WithParentName, "★ 启用集上应当**全部**落在 Name 空间（关键前提 1）");

            var official = OfficialContext();
            var ao = InheritanceLayer.Analyze(official);
            Console.WriteLine($"    [t5a] official: defs={official.Defs.Count} withParent={ao.WithParentName} "
                              + $"unresolved={ao.Unresolved}");
            T.Eq(ao.Unresolved, 0, "★ 官方内容 0 未解析（这是本口径的反证）");

            var (rd2005, rd2006, _) = RunRules(ctx);
            T.Eq(rd2005, 0, "启用集 RD2005 = 0");
            T.Eq(rd2006, 0, "启用集环 = 0");
            // ★ 等式（可断言的不变式）：RD2005 == Unresolved + ChainBroken − 环。
            T.Eq(rd2005, a.Unresolved + a.ChainBroken - rd2006, "★ RD2005 == unresolved + chainBroken - 环");
        });
    }
}
