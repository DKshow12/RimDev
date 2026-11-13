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
        var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
        var active = ModCatalog.ReadActiveMods(env.ModsConfigPath);
        var cat = ModCatalog.Build(env, env.TargetVersion, active).Catalog;
        var tf = cat.Get("dsh.threatfleet.fragments")!;

        // ThreatFleet 自身的文件与 def（只用于"字段名检查能解析出顶层元素"那一条用例）
        var tfFiles = DefScanner.CollectFiles(
            new[] { (tf.PackageId, tf.RootPath, (IReadOnlyList<string>)tf.LoadFolders) });
        var tfScan = DefScanner.Scan(tfFiles);

        // ★ 索引/defs/files 必须是【全集】（active 作用域实测 1649 文件 / 14480 defs），
        //   只把 TargetModPackageId 当【报告过滤器】——否则 ParentName 指向 Core 基类的 def
        //   会被全部误报成 RD2005（ThreatFleet 自身只有 ~185 个 def，父类大多在 Core/DLC 里）。
        var allFiles = DefScanner.CollectFiles(
            cat.Active.Select(m => (m.PackageId, m.RootPath, (IReadOnlyList<string>)m.LoadFolders)));
        var allScan = DefScanner.Scan(allFiles);
        var idx = DefIndex.Build(allScan.Defs);
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
        var diagsFull = CheckRunner.Default().Run(ctxFull);
        var diagsTf = CheckRunner.Default().Run(ctx);

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

        T.Case("★ 防假阳②：官方内容（Core + 5 DLC）按同 DefType 解析 ParentName 必须 0 未解析", () =>
        {
            var diags = new ParentChainCheck().Run(ctxOfficial).ToList();
            var unresolved = diags.Where(d => d.Code == Codes.ParentNotFound).ToList();
            var cycles = diags.Where(d => d.Code == Codes.InheritanceCycle).ToList();
            var withParent = officialDefs.Count(d => !string.IsNullOrEmpty(d.ParentName));
            Console.WriteLine(
                $"       [防假阳②] 官方 mod={official.Count} / 官方 def={officialDefs.Count} / " +
                $"其中带 ParentName={withParent} / RD2005={unresolved.Count} / RD2006={cycles.Count}");

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
            var elems = FieldNameCheck.ReadTopLevelElements(d.FileAbs, d.Line);
            Console.WriteLine($"       [字段名] {d.DefName} 顶层元素 {elems.Count} 个 @ {Path.GetFileName(d.FileAbs)}:{d.Line}");

            T.True(elems.Count > 5, $"应读到多个顶层元素，实际 {elems.Count}");
            T.True(elems.Any(e => e.Name == "defName"), "应含 defName");
            T.True(elems.All(e => e.Line > 0), "每个元素都应有行号");

            // ★ 每个文件只读一次（修正 ⑤）的等价性：按文件一次读出与按单 def 读出必须一致
            var byDefLine = FieldNameCheck.ReadElementsByDef(d.FileAbs);
            T.True(byDefLine.TryGetValue(d.Line, out var grouped), "按文件分组的读取应能定位该 def");
            T.Eq(grouped!.Count, elems.Count, "按文件一次读出的元素数应与单 def 读出的一致");

            var members = ManagedAssemblyCatalog.Load(env.ManagedDir).MembersOf("ThingDef")!;
            T.Null(members.FindField("canBeDeconstructed"), "ThingDef 不应有 canBeDeconstructed");
            T.NotNull(members.FindField("resourcesFractionWhenDeconstructed"), "替代字段存在");
        });
    }
}
