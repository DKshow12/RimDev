using RimDev.Core.Catalog;
using RimDev.Core.Checks;
using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;
using RimDev.Core.Metadata;

namespace RimDev.SelfTest.Cases;

public static class CheckCases
{
    /// <summary>只用于验证 CheckRunner 的指纹去重行为，不参与任何真实规则。</summary>
    private sealed class StubCheck : ICheck
    {
        private readonly IReadOnlyList<Diagnostic> _d;
        public StubCheck(params Diagnostic[] d) => _d = d;
        public string Id => "RD9998";
        public IEnumerable<Diagnostic> Run(CheckContext ctx) => _d;
    }

    public static void Run()
    {
        T.Group("check");
        T.Mark("check init: discover env");
        var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
        var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
        T.Mark("check init: build catalog");
        var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;
        var tf = cat.Get("dsh.threatfleet.fragments")!;

        // ThreatFleet 自身的文件与 def（只用于"字段名检查能解析出顶层元素"那一条用例）
        var tfFiles = DefScanner.CollectFiles(
            new[] { (tf.PackageId, tf.RootPath, (IReadOnlyList<string>)tf.LoadFolders) });
        T.Mark($"check init: scan ThreatFleet ({tfFiles.Count} files)");
        var tfScan = DefScanner.Scan(tfFiles);
        T.Mark($"check init: scanned ThreatFleet ({tfScan.FilesScanned} files / {tfScan.Defs.Count} defs)");

        // ★ 索引/defs/files 必须是【全集】（active 作用域实测 1649 文件 / 14480 defs），
        //   只把 TargetModPackageId 当【报告过滤器】——否则 ParentName 指向 Core 基类的 def
        //   会被全部误报成 RD2005（ThreatFleet 自身只有 ~185 个 def，父类大多在 Core/DLC 里）。
        var allFiles = DefScanner.CollectFiles(
            cat.Active.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)));
        T.Mark($"check init: scan active ({allFiles.Count} files)");
        var allScan = DefScanner.Scan(allFiles);
        T.Mark($"check init: scanned active ({allScan.FilesScanned} files / {allScan.Defs.Count} defs)");
        var idx = DefIndex.Build(allScan.Defs);
        T.Mark("check init: index built");
        var ctx = new CheckContext(idx, allScan.Files, cat, allScan.Defs, tf.PackageId);
        var ctxFull = new CheckContext(idx, allScan.Files, cat, allScan.Defs, null);

        // 官方内容（Core + 5 DLC）：用于「按类型解析 ParentName」的反证。
        // 索引与 def 都只用官方集合 —— 这是最紧的口径：官方自身必须自洽。
        var official = cat.Active.Where(m => m.Source == ModSource.Official).ToList();
        var officialPkgs = official.Select(m => m.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var officialDefs = allScan.Defs.Where(d => officialPkgs.Contains(d.ModPackageId)).ToList();
        var officialIdx = DefIndex.Build(officialDefs);
        var ctxOfficial = new CheckContext(officialIdx, Array.Empty<DefFileResult>(), cat, officialDefs, null);

        // 两条全量运行只做一次，供下面的用例复用
        T.Mark("check init: CheckRunner ctxFull");
        var diagsFull = CheckRunner.Default().Run(ctxFull);
        T.Mark($"check init: ctxFull done ({diagsFull.Count} diags)");
        var diagsTf = CheckRunner.Default().Run(ctx);
        T.Mark($"check init: ctx done ({diagsTf.Count} diags)");

        T.Case("★ 防假阳①：active 全集上 RD2004 只有 1 条（同 DefType 真重名），绝不报 1202 条", () =>
        {
            var dup = diagsFull.Where(d => d.Code == Codes.DefNameDuplicate).ToList();
            var globalGroups = idx.ByDefName.Count(kv => kv.Value.Count > 1);
            var typeGroups = idx.ByTypeAndDefName.Count(kv => kv.Value.Count > 1);
            Console.WriteLine(
                $"       [防假阳①] active 全集：ByDefName 同名组(跨类型，合法)={globalGroups} / " +
                $"ByTypeAndDefName 真重名组={typeGroups} / RD2004 条数={dup.Count} / 诊断总数={diagsFull.Count}");
            foreach (var d in dup)
            {
                var decl = d.Evidence is not null && d.Evidence.TryGetValue("declarations", out var dd) ? dd : "?";
                Console.WriteLine($"         RD2004: {d.DefType}|{d.DefName}  [{decl}]");
            }

            T.Eq(typeGroups, 1, "同 (DefType, defName) 真重名组数");
            // ★ 这条就是防假阳的钉子：若有人把判重改回 ByDefName，这里会立刻变成 1202
            T.Eq(dup.Count, 1, "RD2004 条数（改回按 defName 判重会立刻变成 1202）");
            T.True(globalGroups > 1000, $"跨类型同名组应远大于 1（这才是 1202 的来源，实际 {globalGroups}）");
            if (dup.Count == 1)
            {
                T.Eq($"{dup[0].DefType}|{dup[0].DefName}",
                    "ThingSetMakerDef|MapGen_AncientComplexRoomLoot_Default", "唯一真重名的身份");
                T.True(dup[0].Severity == Severity.Warning,
                    "真重名的严重级别必须是 Warning（原版 Core/Ideology 之间就有合法覆盖先例）");
            }
        });

        T.Case("★ 防假阳②：官方内容（Core + 5 DLC）RD2005 与 RD2006 都必须为 0", () =>
        {
            var diags = new ParentChainCheck().Run(ctxOfficial).ToList();
            var unresolved = diags.Where(d => d.Code == Codes.ParentNotFound).ToList();
            var cycles = diags.Where(d => d.Code == Codes.InheritanceCycle).ToList();
            // ★ 只在 defName 空间命中（抽象名空间没有）的 ParentName 条数 —— 期望 0。
            //   引擎的 nodesByName 只认 Name 属性；这个数是 0 就说明「Name 空间优先」对 RD2005 判定零影响。
            var defNameOnlyHit = officialDefs.Count(x => !string.IsNullOrEmpty(x.ParentName)
                && !officialIdx.ByAbstractName.ContainsKey(x.ParentName!)
                && officialIdx.ByDefName.ContainsKey(x.ParentName!));
            var withParent = officialDefs.Count(d => !string.IsNullOrEmpty(d.ParentName));
            Console.WriteLine(
                $"       [防假阳②] 官方 mod={official.Count} / 官方 def={officialDefs.Count} / " +
                $"其中带 ParentName={withParent} / RD2005={unresolved.Count} / RD2006={cycles.Count} / " +
                $"只在 defName 空间命中的 ParentName={defNameOnlyHit}（期望 0）");

            // 反向守卫：官方集合若为空，下面的 0 就是空谈
            T.True(official.Count >= 6, $"官方 mod 数应 ≥ 6（Core + 5 DLC），实际 {official.Count}");
            T.True(officialDefs.Count > 10_000, $"官方 def 数应 > 10000，实际 {officialDefs.Count}");
            T.True(withParent > 1_000, $"官方带 ParentName 的 def 应 > 1000，实际 {withParent}");

            // ★ 纯证据（不改断言）：同一批官方 def 在「全局 name-only」口径下还有多少未解析。
            //   这才是判断「类型口径是否选错」的反证材料。
            var perTypeMiss = officialDefs.Where(x => !string.IsNullOrEmpty(x.ParentName)
                && !officialIdx.IsDefNameTaken(x.DefType, x.ParentName!)
                && !(officialIdx.ByAbstractName.TryGetValue(x.ParentName!, out var ab)
                     && ab.Any(y => y.DefType == x.DefType))).ToList();
            var globalMiss = officialDefs.Where(x => !string.IsNullOrEmpty(x.ParentName)
                && !officialIdx.CanBeReferenced(x.ParentName!)).ToList();
            Console.WriteLine(
                $"       [防假阳②] 按类型未解析={perTypeMiss.Count} / 全局(name-only)未解析={globalMiss.Count} / " +
                $"全局 defName 数={officialIdx.ByDefName.Count} / 抽象名数={officialIdx.ByAbstractName.Count}");
            foreach (var d in perTypeMiss.Take(40))
                Console.WriteLine($"         按类型未解析: {d.DefType}|{d.DefName} -> {d.ParentName}");

            T.Eq(unresolved.Count, 0, "官方内容按 (DefType, defName) 解析 ParentName 必须 0 未解析；实际：" +
                string.Join("; ", unresolved.Take(40).Select(d => $"{d.DefType}|{d.DefName}->{d.Message}")));

            // ★ 防假阳第二根钉子（Task 6c）：RD2006 必须 0。
            //   旧实现（defName 优先解析 + 按名字判环）在官方内容上报 5 条假自环；实测【只改一处】
            //   只能降到 2 / 3 —— 所以这条断言同时钉死两处修改（解析顺序 + 判环键）。
            foreach (var c in cycles)
                Console.WriteLine($"         RD2006 假阳: {c.DefType}|{c.DefName} @{c.FileAbs}:{c.Line}");
            var offending = cycles.Select(c => $"{c.DefType}|{c.DefName}").ToList();
            var knownFalsePositives = new[]
            {
                "PawnKindDef|Mercenary_Slasher",                  // 抽象模板的装饰 defName 与真 def 同串
                "PawnKindDef|Tribal_ChiefMelee",
                "PawnKindDef|TradersGuild_Slasher",
                "SoundDef|Designate_DragStandard_Changed",        // 抽象 Name 与真 def 的 defName 同串
                "SoundDef|Designate_DragStandard_Changed_NoCam",
            };
            var regressed = knownFalsePositives.Where(offending.Contains).ToList();
            T.Eq(cycles.Count, 0,
                "官方内容上 RD2006 必须 0；命中：" +
                string.Join("; ", cycles.Select(c => $"{c.DefType}|{c.DefName}@{Path.GetFileName(c.FileAbs)}:{c.Line}")) +
                (regressed.Count > 0 ? "（其中已知假阳回归：" + string.Join(", ", regressed) + "）" : ""));
        });

        T.Case("★ Task 2 Step 8：官方 6 个包上跑【全部】默认规则 → error 必须 0（假阳比漏报更贵）", () =>
        {
            // 与 CLI 的 `rimdev check --scope active --only-official --severity error` 走同一条规则链，
            // 只是把 mod 集合收窄到官方 6 个包。它是本期「零假阳」的机械保障：官方内容上出现的
            // 任何一条 error 都先当成【我们的规则错了】来查（计划 §5 风险表）。
            var officialMods = cat.Active.Where(m => m.Source == ModSource.Official).ToList();
            var officialFiles = DefScanner.CollectFiles(
                officialMods.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)));
            var officialScan = DefScanner.Scan(officialFiles);
            var offIdx = DefIndex.Build(officialScan.Defs);
            var offCtx = new CheckContext(offIdx, officialScan.Files, cat, officialScan.Defs, null);
            var offDiags = CheckRunner.Default().Run(offCtx);
            var offErrs = offDiags.Where(d => d.Severity == Severity.Error).ToList();

            Console.WriteLine($"       [官方零假阳] mod={officialMods.Count} files={officialFiles.Count} "
                              + $"defs={officialScan.Defs.Count} failed={officialScan.FilesFailed} "
                              + $"诊断={offDiags.Count}（error={offErrs.Count} / "
                              + $"warning={offDiags.Count(d => d.Severity == Severity.Warning)} / "
                              + $"info={offDiags.Count(d => d.Severity == Severity.Info)}）");
            foreach (var d in offDiags.Take(20))
                Console.WriteLine($"         {d.Severity} [{d.Code}] {Path.GetFileName(d.FileAbs ?? "?")}:{d.Line} {d.Message}");

            T.Eq(officialMods.Count, 6, "官方包数（Core + 5 DLC）");
            T.True(officialFiles.Count > 0, "官方文件数不得为 0（否则「0 error」是空谈）");
            T.True(officialScan.Defs.Count > 10_000, $"官方 def 数应 > 10000，实际 {officialScan.Defs.Count}");
            // ★ Task 6b 改口径（`08` §4f.4）：判据从「error = 0」改成「**误报 = 0**」。
            //   实测的那 1 条 error 是**官方 Anomaly 自己的真缺陷**（`WeatherDef.ambientSounds` →
            //   不存在的 `SoundDef Ambient_MetalHell`；引擎级证据见 AssertOfficialErrorBaseline 注释）。
            //   该条被**逐条钉死**（条数恰好 1 + 身份精确匹配），**不是白名单**。
            Phase3Task6Cases.AssertOfficialErrorBaseline(offErrs, "check/Task 2 Step 8 官方零假阳");
        });

        T.Case("★ 防假阳③：active 全集上 RD4001 与 RD4005 都必须为 0（改前 126 + 12）", () =>
        {
            // 本用例就是 Task 7b 的核心判据的单元版：这两族错误改前 100% 落在原版/DLC/Anomaly 内容上，
            // 根因是字段表用单次 GetFields 建、看不见基类 private 字段（详见 FieldReflection 类注释）。
            // ★ 故意写成【等值 0】而不是「不报原版」「要 < 10」之类：任何放宽都会把假阳重新放回来。
            var rd4001 = diagsFull.Where(d => d.Code == Codes.FieldNotFound).ToList();
            var rd4005 = diagsFull.Where(d => d.Code == Codes.CaseMismatch).ToList();
            Console.WriteLine($"       [防假阳③] active 全集：RD4001={rd4001.Count} / RD4005={rd4005.Count} / " +
                $"诊断总数={diagsFull.Count}（改前 126 + 12）");
            foreach (var d in rd4001.Take(20))
                Console.WriteLine($"         RD4001: {d.DefType}|{d.DefName} {d.Message} @{Path.GetFileName(d.FileAbs)}:{d.Line}");
            foreach (var d in rd4005.Take(20))
                Console.WriteLine($"         RD4005: {d.DefType}|{d.DefName} {d.Message} @{Path.GetFileName(d.FileAbs)}:{d.Line}");

            T.Eq(rd4001.Count, 0, "RD4001 必须为 0（改前 126 条全在原版内容上；非 0 即字段表又看不见基类 private 字段）" +
                System.Environment.NewLine + string.Join(System.Environment.NewLine,
                    rd4001.Take(10).Select(d => $"   [{d.Code}] {d.DefType}|{d.DefName} {d.Message} @{d.FileAbs}:{d.Line}")));
            T.Eq(rd4005.Count, 0, "RD4005 必须为 0（改前 12 条全在原版内容上）" +
                System.Environment.NewLine + string.Join(System.Environment.NewLine,
                    rd4005.Take(10).Select(d => $"   [{d.Code}] {d.DefType}|{d.DefName} {d.Message} @{d.FileAbs}:{d.Line}")));
        });

        // ★ Task 6c 的合成 def 最小运行器：只喂 DefIndex.Build 出来的 def，不碰磁盘。
        List<Diagnostic> RunSynthetic(params RawDef[] defs)
        {
            var list = defs.ToList();
            var sIdx = DefIndex.Build(list);
            var sCtx = new CheckContext(sIdx, Array.Empty<DefFileResult>(), cat, list, null);
            return new ParentChainCheck().Run(sCtx).ToList();
        }

        T.Case("★ RD2006 仍能抓真环（合成 A↔B，两个不同声明互相 ParentName）", () =>
        {
            var d = RunSynthetic(
                new RawDef("ThingDef", null, "SynCycA", "SynCycB", null, true, "syn.test",
                    @"C:\syn\a.xml", @"Defs\a.xml", 10),
                new RawDef("ThingDef", null, "SynCycB", "SynCycA", null, true, "syn.test",
                    @"C:\syn\b.xml", @"Defs\b.xml", 20));
            var cyc = d.Where(x => x.Code == Codes.InheritanceCycle).ToList();
            Console.WriteLine($"       [合成 A↔B] RD2006={cyc.Count}：" +
                string.Join(" | ", cyc.Select(c => c.Message + "  [" + c.Evidence?["cycle"] + "]")));
            // ★ 计划勘误（Task 6c 实测；不是放宽判据，是计划 Step 5 的数字与真实不符）：
            //   计划写「断言 RD2006 == 1」，实测恒为 **2** —— Run() 是【逐个声明】检查，A 与 B 各自都能从
            //   自己出发走出一圈，各报一条。这与计划自己的口径一致：官方 5 条假阳被记为
            //   「3 条 PawnKind + 2 条 SoundDef」而不是「3 个环组」。
            //   这里按实测的确切值断言（比 >= 1 更严格），并额外钉死「两条描述的是同一个环组」。
            T.Eq(cyc.Count, 2, "A↔B 的两个参与声明各报一条（Run 逐个声明检查；官方 5 条假阳也是这个口径）");
            T.True(cyc.All(c => c.Evidence!["cycle"].Contains(@"Defs\a.xml:10", StringComparison.Ordinal)
                             && c.Evidence!["cycle"].Contains(@"Defs\b.xml:20", StringComparison.Ordinal)),
                "两条诊断都必须覆盖同一对声明（同一个环组）：" +
                string.Join(" | ", cyc.Select(c => c.Evidence?["cycle"] ?? "<null>")));
        });

        T.Case("★ 装饰 defName 撞名不得判成环（合成 shape A：抽象模板 defName 与真 def 同串）", () =>
        {
            // 官方原形：Core\Defs\PawnKindDefs_Humanlikes\PawnKinds_Mercenary.xml
            //   :198 抽象模板 Name="MercenarySlasherBase" 上写了装饰性的 <defName>Mercenary_Slasher</defName>
            //   :232 真 def 也叫 Mercenary_Slasher，且 ParentName="MercenarySlasherBase"
            var d = RunSynthetic(
                new RawDef("PawnKindDef", "SynMerc", "SynMercBase", null, null, true, "syn.test",
                    @"C:\syn\merc.xml", @"Defs\merc.xml", 198),
                new RawDef("PawnKindDef", "SynMerc", null, "SynMercBase", null, false, "syn.test",
                    @"C:\syn\merc.xml", @"Defs\merc.xml", 232));
            var cyc = d.Where(x => x.Code == Codes.InheritanceCycle).ToList();
            Console.WriteLine($"       [合成 shape A] RD2006={cyc.Count}");
            T.Eq(cyc.Count, 0, "抽象模板上的装饰 defName 与真 def 同串是官方合法模式，不得判成环");
        });

        T.Case("★ defName 与抽象 Name 同串不得判成环（合成 shape B）", () =>
        {
            // 官方原形：Core\Defs\SoundDefs\UI_Oneshots_Designation.xml
            //   :3  <SoundDef Name="Designate_DragStandard_Changed" Abstract="True">（只有 Name）
            //   :19 真 def 的 <defName> 与它同串 —— defName 优先解析会指到它自己（旧实现报假自环）
            var d = RunSynthetic(
                new RawDef("SoundDef", null, "SynSnd", null, null, true, "syn.test",
                    @"C:\syn\snd.xml", @"Defs\snd.xml", 3),
                new RawDef("SoundDef", "SynSnd", null, "SynSnd", null, false, "syn.test",
                    @"C:\syn\snd.xml", @"Defs\snd.xml", 19));
            var cyc = d.Where(x => x.Code == Codes.InheritanceCycle).ToList();
            Console.WriteLine($"       [合成 shape B] RD2006={cyc.Count}");
            T.Eq(cyc.Count, 0, "真 def 的 defName 与抽象模板 Name 同串时，父节点应解析到抽象模板（不得判成自环）");
        });

        T.Case("★ RD2005 口径不变：defName 兜底仍算存在（防「改成 Name-only」的倒退钉子）", () =>
        {
            // 理由（见 ParentChainCheck 类注释）：引擎在 ApplyPatches 之后才 TryRegister，
            // 补丁补上的 Name 属性在静态索引里看不到 —— CE 的 PatchOperationAttributeAdd
            // （CombatExtended\Patches\ThingDef_Guns.xml:904-908 给 OSNGun_HMG 补 Name="OSNGun_HMG"）
            // 就是官方形态，其 Defs\MechGuns.xml:54 的 <ThingDef ParentName="OSNGun_HMG"> 必须继续算「存在」。
            // 全库同形 61 条 / 11 个 mod：删掉 defName 兜底会凭空造出这 61 条假阳。
            var d = RunSynthetic(
                new RawDef("ThingDef", "SynOnlyDefName", null, null, null, false, "syn.test",
                    @"C:\syn\only.xml", @"Defs\only.xml", 5),
                new RawDef("ThingDef", "SynChild", null, "SynOnlyDefName", null, false, "syn.test",
                    @"C:\syn\only.xml", @"Defs\only.xml", 9));
            var miss = d.Where(x => x.Code == Codes.ParentNotFound).ToList();
            Console.WriteLine($"       [合成 defName 兜底] RD2005={miss.Count} / " +
                $"RD2006={d.Count(x => x.Code == Codes.InheritanceCycle)}");
            T.Eq(miss.Count, 0, "只在 defName 空间命中的 ParentName 必须继续算存在（改成 Name-only 会造出全库 61 条假阳）");
        });

        T.Case("★ ThreatFleet 的静态体检：0 个 error（历史结论：游戏内零 mod 错误）", () =>
        {
            var errors = diagsTf.Where(d => d.Severity == Severity.Error).ToList();
            Console.WriteLine($"       [体检] target=ThreatFleet 诊断数={diagsTf.Count}（error={errors.Count}）");
            T.True(errors.Count == 0,
                "ThreatFleet 不应有 error，实际：" + System.Environment.NewLine +
                string.Join(System.Environment.NewLine,
                    errors.Take(10).Select(d => $"   [{d.Code}] {d.Message} @{d.FileAbs}:{d.Line}")));
        });

        T.Case("★ 指纹去重：同文件同位置合并并记 occurrences，不同文件必须分开", () =>
        {
            // ★ 指纹必须含 FileAbs/Line —— XML 类诊断的 TypeName/MemberName 都是 null，
            //   Message 形如 "XML 解析失败：XmlException: …"，不含位置会把不同文件合并成一条。
            var runner = new CheckRunner().Add(new StubCheck(
                new Diagnostic(Codes.DefNameDuplicate, Severity.Warning, Scope.Offline, "same",
                    FileAbs: "a.xml", Line: 1, DefName: "X"),
                new Diagnostic(Codes.DefNameDuplicate, Severity.Warning, Scope.Offline, "same",
                    FileAbs: "a.xml", Line: 1, DefName: "X"),
                new Diagnostic(Codes.DefNameDuplicate, Severity.Warning, Scope.Offline, "same",
                    FileAbs: "b.xml", Line: 1, DefName: "X")));

            var merged = runner.Run(ctxFull);
            T.Eq(merged.Count, 2, "同指纹（同文件同位置）归一条；不同文件必须分开");
            var a = merged.First(d => d.FileAbs == "a.xml");
            T.Eq(a.Evidence?["occurrences"], "2", "合并后应记录 occurrences=2");

            // 规则产物也必须带错误码与来源
            foreach (var d in diagsTf.Take(20))
            {
                T.True(d.Code.StartsWith("RD", StringComparison.Ordinal), "错误码格式");
                T.True(d.FileAbs is not null || d.DefName is not null, "至少有文件或 def 名");
            }
        });

        T.Case("★ 字段名检查：能解析 ThreatFleet 的顶层元素；坏字段 canBeDeconstructed 确实不存在", () =>
        {
            var d = tfScan.Defs.First(x => x.DefName == "TF_Wreck_OnslaughtMkI");
            var scanned = FieldNameCheck.ScanDefs(d.FileAbs);
            Console.WriteLine($"       [字段名] {Path.GetFileName(d.FileAbs)}：元素扫描到 {scanned.Count} 个 def"
                + $"（其中 {scanned.Count(x => x.DefLine == d.Line)} 个在 d.Line={d.Line}）");

            // ★ 第 3 期 Task 5c：归属键 = (def 起始行, 该行上第几个 def)。
            //   本用例**刻意不依赖任何声明的身份**（踩过两次测试侧的坑，记下来免得后人再踩）：
            //   ① `tfScan.Defs` 里存在**字段完全相同**的两份 RawDef，而 RawDef 是 record ⇒ `List.IndexOf(d)`
            //      按【值相等】找会命中另一行上那个值相同的声明（实测下标 230，而真身在 0 位）⇒ 行内序号算成 3 ⇒
            //      取不到桶、读到 0 个元素；② 另起一次 `DefScanner.ScanFile` 再按 ReferenceEquals 找同样不行
            //      —— 那是**另一遍扫描**，对象不是同一批。⇒ 改成直接与 **DOM 真值**对账（身份无关）。
            T.True(scanned.Count > 5, $"应扫描到多个 def，实际 {scanned.Count}");
            T.True(scanned.All(x => x.Children.Count > 0), "每个 def 都应读到一级子元素（读到 0 说明归属键取错了）");

            // ① 行内序号必须逐行从 0 连续编号（不是全 0 —— 全 0 就是「同一行多 def 共享一个桶」的旧口径）
            foreach (var g in scanned.GroupBy(x => x.DefLine))
                T.True(g.Select(x => x.OrdinalWithinLine).SequenceEqual(Enumerable.Range(0, g.Count())),
                    $"line {g.Key} 上的行内序号必须是 0..N-1（实际 [{string.Join(",", g.Select(x => x.OrdinalWithinLine))}]）");

            // ② 桶的键集合与扫描到的 def 一一对应，且**越界序号取不到**（不许静默落到邻居）
            var buckets = FieldNameCheck.ReadElementsByDef(d.FileAbs);
            T.Eq(buckets.Count, scanned.Count, "桶数必须等于 def 数（每个 def 恰好一个桶）");
            foreach (var s in scanned)
                T.True(buckets.ContainsKey((s.DefLine, s.OrdinalWithinLine)), $"桶必须存在 (line={s.DefLine}, ord={s.OrdinalWithinLine})");
            foreach (var g in scanned.GroupBy(x => x.DefLine))
                T.False(buckets.ContainsKey((g.Key, g.Count())), $"越界序号 (line={g.Key}, ord={g.Count()}) 必须不存在");

            // ③ ★★ 与 DOM 真值对账：逐个 def 的直接子元素必须与它自己那份完全一致（归属正确 = 不多不少）
            var dom = new System.Xml.XmlDocument();
            dom.Load(d.FileAbs);
            var domDefs = dom.DocumentElement!.ChildNodes.OfType<System.Xml.XmlElement>().ToList();
            T.Eq(scanned.Count, domDefs.Count, "元素扫描的 def 数必须等于 DOM 的 depth-1 元素数");
            int domMismatch = 0, domChildren = 0;
            for (int i = 0; i < domDefs.Count; i++)
            {
                var want = domDefs[i].ChildNodes.OfType<System.Xml.XmlElement>().Select(x => x.Name).ToList();
                var got = scanned[i].Children.Select(x => x.Name).ToList();
                domChildren += want.Count;
                if (!want.SequenceEqual(got))
                {
                    domMismatch++;
                    if (domMismatch <= 3)
                        Console.WriteLine($"         [DOM 不符] #{i} <{domDefs[i].Name}> dom=[{string.Join("/", want)}] scan=[{string.Join("/", got)}]");
                }
            }
            Console.WriteLine($"       [DOM 对账] defs={domDefs.Count} 一级子元素合计={domChildren} 不符={domMismatch}");
            T.Eq(domMismatch, 0, "★ 每个 def 的扫描子元素必须与 DOM 真值逐个一致（这同时钉死「不是并集」与「不是整段偏移」）");

            // ④ d 自己那一行必须读到子元素（旧口径在这里要么读到并集、要么读到邻居的空桶）
            var dLineBuckets = buckets.Where(kv => kv.Key.Line == d.Line).ToList();
            T.True(dLineBuckets.Count > 0, $"d.Line={d.Line} 上必须有桶");
            T.True(dLineBuckets.All(kv => kv.Value.Count > 0), "该行的每个桶都必须有子元素");

            // ⑤ 两条入口必须一致：ReadTopLevelElements(line, ordinal) 与 ReadElementsByDef 的同一个桶
            foreach (var s in scanned.Where(x => x.DefLine == d.Line))
            {
                var oneShot = FieldNameCheck.ReadTopLevelElements(d.FileAbs, s.DefLine, s.OrdinalWithinLine);
                T.Eq(oneShot.Count, s.Children.Count,
                    $"ReadTopLevelElements(line={s.DefLine}, ord={s.OrdinalWithinLine}) 的元素数必须等于该桶的元素数");
            }


            var members = ManagedAssemblyCatalog.Load(env.ManagedDir).MembersOf("ThingDef")!;
            T.Null(members.FindField("canBeDeconstructed"), "ThingDef 不应有 canBeDeconstructed");
            T.NotNull(members.FindField("resourcesFractionWhenDeconstructed"), "替代字段存在");
        });
        // ===== Task 7 Step 5b：NameOracle.CheckDefName 的三条用例 =====
        // 为什么放在 CheckCases 而不是 NameCases：
        //   ① 本文件已经为「五条规则」建好了 active 全集的 DefIndex `idx`（1649 文件 / 14480 def）
        //      与 ModCatalog。NameCases 若要做同样的事，必须把这一整套再跑一遍
        //      （ModCatalog.Build 实测 ~1.4 s + 全量扫描 ~200 ms），而这里只多加载一次程序集
        //      目录（ManagedAssemblyCatalog.Load，实测 12 ms）。
        //   ② 缓存的索引就是 `rimdev index --scope active` 建的，用 idx 做用例与 CLI 的
        //      `name check --kind def --index` 走的是同一份数据，覆盖的是同一条路径。
        var oracle = new NameOracle(ManagedAssemblyCatalog.Load(env.ManagedDir));

        T.Case("★ Def 名命中索引（TF_ThreatHive）：报存在且是 Info", () =>
        {
            var v = oracle.Check("TF_ThreatHive", NameKind.DefName, defIndex: idx);
            Console.WriteLine($"       [def 命中] Exists={v.Exists} / Kind={v.Kind} / {v.Diagnostic.Severity} / {v.Diagnostic.Message}");
            T.True(v.Exists, "TF_ThreatHive 应在 active 索引里存在（CLI：name check TF_ThreatHive --kind def --index → exit=0）");
            T.Eq(v.Kind, NameKind.DefName, "命中时的 Kind");
            T.Eq(v.Diagnostic.Severity, Severity.Info, "命中时应是 Info（不得报 Error）");

            // ★ Task 7b Step 5（主理人裁定）：命中时必须列出【全部声明】，不能只报 ByDefName[...][0]。
            //   TF_ThreatHive 有【两处】合法声明（ThingDef 与 PawnKindDef），两种 DefType 都出现在消息里才算合格。
            var declCount = idx.ByDefName["TF_ThreatHive"].Count;
            T.Eq(declCount, 2, "TF_ThreatHive 在 active 索引里的声明数（ThingDef + PawnKindDef）");
            T.Contains(v.Diagnostic.Message, "ThingDef", "消息里必须出现 ThingDef 那处声明");
            T.Contains(v.Diagnostic.Message, "PawnKindDef", "消息里必须出现 PawnKindDef 那处声明");
            T.Contains(v.Diagnostic.Message, $"共 {declCount} 处声明", "消息必须报出声明数量（与索引真值一致）");
        });

        T.Case("★ Def 名未命中（TF_ThreatHiveX）：Error + 候选必须含真名", () =>
        {
            var v = oracle.Check("TF_ThreatHiveX", NameKind.DefName, defIndex: idx);
            var cands = v.Diagnostic.Suggestions ?? Array.Empty<string>();
            Console.WriteLine($"       [def 未命中] Exists={v.Exists} / {v.Diagnostic.Code} / 候选({cands.Count})={string.Join(", ", cands)}");
            T.False(v.Exists, "TF_ThreatHiveX 不存在");
            T.Eq(v.Diagnostic.Severity, Severity.Error, "未命中应是 Error（CLI 退出码 2）");
            T.Eq(v.Diagnostic.Code, Codes.DefNameNotFound, "错误码应为 RD3001");
            // ★ 钉住「给不出候选＝不合格」：只报不存在而没有候选，等于把查错拼写的成本丢回给用户
            T.True(cands.Any(c => string.Equals(c, "TF_ThreatHive", StringComparison.Ordinal)),
                "候选必须包含 TF_ThreatHive，实际: " + string.Join(", ", cands));
        });

        T.Case("★ 无 Def 索引时如实报「未建立索引」（Warning），不猜", () =>
        {
            var v = oracle.Check("TF_ThreatHive", NameKind.DefName, defIndex: null);
            Console.WriteLine($"       [def 无索引] Exists={v.Exists} / {v.Diagnostic.Severity} / {v.Diagnostic.Message}");
            T.False(v.Exists, "没有索引就不能说存在");
            T.Eq(v.Diagnostic.Severity, Severity.Warning, "应为 Warning（如实说需要索引，而不是 Error/Info）");
        });

    }
}