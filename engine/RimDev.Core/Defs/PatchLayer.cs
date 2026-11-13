using System.Xml;
using RimDev.Core.Diagnostics;

namespace RimDev.Core.Defs;

/// <summary>
/// 补丁操作的类型。<see cref="Unknown"/> = 自定义 <c>Class=</c> 或缺少 <c>Class=</c>（→ RD2014，跳过）。
/// 取值集合与程序集里 <c>Verse.PatchOperation</c> 的实现类**一一对应**（全程序集只有 12 个可实例化的操作类，
/// 用 <c>search_types("PatchOperation")</c> 实测：19 个类型里 7 个是基类/枚举/嵌套类）。
/// </summary>
public enum PatchOpKind
{
    Add, Insert, Replace, Remove, SetName,
    AttributeAdd, AttributeSet, AttributeRemove,
    Sequence, Conditional, Test, FindMod, AddModExtension,
    Unknown,
}

/// <summary>
/// 各阶段的节点数 —— <b>Step 1「时序复刻必须实测」的载体</b>。
///
/// 阶段序（`Verse.LoadedModManager.LoadAllActiveMods`，2026-10-14 反编译逐行，非注释转写）：
/// <code>
/// XmlInheritance.Clear() → InitializeMods() → LoadModContent() → CreateModClasses()
///   → LoadModXML()            ← 阶段 1：每个 mod 的 Defs 资产
///   → CombineIntoUnifiedXML() ← 阶段 2：合成一个 &lt;Defs&gt; 文档
///   → TKeySystem.Clear()/Parse()
///   → ErrorCheckPatches()     ← 阶段 4：patch.ConfigErrors()
///   → ApplyPatches()          ← 阶段 5：★ 补丁在这里改 XML
///   → ParseAndProcessXML()    ← 阶段 6：★ XmlInheritance.TryRegister + Resolve 在这一步【内部】
///   → ClearCachedPatches() → XmlInheritance.Clear()
/// </code>
/// 这就是本层存在的意义：<b>补丁在继承解析之前</b>。所以「用 PatchOperationAttributeAdd 给 def 补
/// <c>Name=</c>，让另一个 def 的 <c>ParentName</c> 解析得开」在引擎里是合法手段（Step 5/6）。
///
/// ★ 每一格都是**本层跑出来的实测值**，不是从反编译文本抄的（Task 4 Step 1 的硬要求）。
///   各字段的名字都描述「实际动作」（项目纪律：名字描述「判定输入」的字段一到分支改道就误报）。
/// </summary>
public sealed record PatchPhaseStats(
    // 阶段 1 LoadModXML：参与合成的 def 文件数（= 资产数）。
    int Assets,
    // 阶段 2 CombineIntoUnifiedXML：合成根 <Defs> 下的一级元素（def 节点）数。
    int UnifiedDefNodes,
    // 阶段 3 TKeySystem.Parse：注册的 TKey 引用数（1.6 实测恒为 0，见 Apply 里的注释）。
    int TKeyRefs,
    // 阶段 4 ErrorCheckPatches：补丁文件数。
    int PatchFiles,
    // 阶段 5 ApplyPatches：顶层 <Operation> 数（不含嵌套）。
    int TopLevelOperations,
    // 阶段 5 之前 / 之后的 def 节点数（补丁可能增删节点，两个数都必须实测）。
    int DefNodesBeforePatches,
    int DefNodesAfterPatches,
    // 阶段 6 ParseAndProcessXML：会进入 XmlInheritance.TryRegister 的节点数。
    int RegisteredNodes,
    // 补丁真正补上的 Name 属性个数（Step 5/6 的机制量）。
    int NameAttributesAdded,
    // 自定义/缺失 Class 被跳过的操作数（= RD2014 条数）。
    int SkippedCustomClass,
    long ElapsedMs)
{
    /// <summary>给人看的一行（自测与报告直接打印它，避免两处各拼一次格式）。</summary>
    public string Describe() =>
        $"assets={Assets} unifiedDefNodes={UnifiedDefNodes} tKeyRefs={TKeyRefs} "
        + $"patchFiles={PatchFiles} topOps={TopLevelOperations} "
        + $"defNodes={DefNodesBeforePatches}->{DefNodesAfterPatches} "
        + $"registered={RegisteredNodes} nameAdded={NameAttributesAdded} "
        + $"customClassSkipped={SkippedCustomClass} elapsedMs={ElapsedMs}";
}

/// <summary>一个被求值的操作的实测结果（报告与自测的证据来源）。</summary>
public sealed record PatchOutcome(
    string FileAbs,
    string ModPackageId,
    int Line,
    PatchOpKind Kind,
    string OperationClass,
    string? XPath,
    // XPath 命中的节点数（非 pathed 操作为 -1）。
    int Matched,
    // 引擎 Apply() 的返回值语义（含 Success=Always/Never/Invert 的翻转）。
    bool Applied,
    // 是否被跳过（自定义 Class / 缺 Class）。
    bool Skipped,
    string? SkipReason,
    // 求值异常（如 XPath 语法错）；null = 正常。
    string? EvaluationError = null);

/// <summary>
/// 补丁给某个 def 补上的 <c>Name</c> 属性（<c>PatchOperationAttributeAdd</c> + <c>&lt;attribute&gt;Name&lt;/attribute&gt;</c>）。
/// Step 6 的度量就用它：这些 <c>Name</c> 让 <c>ParentName</c> 从「靠 defName 兜底」变成「Name 空间真命中」。
/// </summary>
public sealed record PatchNameAddition(
    string AddedName,
    string XPath,
    int Matched,
    // 被打上 Name 的那个 def（能取到时填；取不到为 null —— 不编造）。
    string? TargetDefType,
    string? TargetDefName,
    string FileAbs,
    int Line,
    string ModPackageId);

/// <summary>
/// 一次补丁应用的全部产物。
/// <see cref="Document"/> 是**打完补丁之后**的统一 XML（Task 5 的 <c>InheritanceLayer</c> 应直接吃它，
/// 而不是自己再读一遍盘 —— 补丁必须先于继承解析，这是引擎时序）。
/// </summary>
public sealed record PatchRunResult(
    PatchPhaseStats Stats,
    IReadOnlyList<PatchOutcome> Outcomes,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<PatchNameAddition> NameAdditions,
    XmlDocument Document);

/// <summary>
/// <c>PatchOperation</c> 家族的静态求值（第 3 期 Task 4）。
///
/// 位置说明：实施计划 `07` §1 的文件结构把本文件列在 <c>RimDev.Core/Defs/</c>（与
/// <c>DefScanner</c>/<c>DefFileEnumerator</c> 同层），Task 4 任务书写的是 <c>Catalog/</c> ——
/// 以计划为准（它才是权威口径），且本层只依赖 <see cref="DefFileEnumerator"/> 的产物与 <c>XmlDocument</c>。
///
/// ★★ 三条不能改的实现纪律（都来自反编译原文，不是推测）：
/// <list type="number">
/// <item><b>补丁只在统一文档上跑一次，且先于继承解析。</b>
///   <c>ApplyPatches(XmlDocument, assetlookup)</c> 是
///   <c>foreach (PatchOperation item in runningMods.SelectMany(rm =&gt; rm.Patches)) item.Apply(xmlDoc);</c>
///   —— 顺序 = mod 加载序 × 该 mod 内 <c>Patches/</c> 的资产顺序。</item>
/// <item><b>补丁文件的两个硬结构要求</b>（<c>ModContentPack.LoadPatches()</c>）：
///   根元素必须是 <c>Patch</c>，一级子元素必须叫 <c>Operation</c> —— 不满足时引擎 <c>Log.Error</c> 并
///   <b>跳过</b>（不是 try/catch 吞掉）。本层同样跳过，并把数量记进 <see cref="PatchPhaseStats"/>。</item>
/// <item><b>不执行 mod 代码。</b> 自定义 <c>Class=</c> 解析得到的是 mod 程序集里的类型，引擎会真的执行；
///   我们只报 <c>RD2014</c>（info）并跳过该操作（`01` §7）。</item>
/// </list>
///
/// ★ 关于 <c>value</c> 的两副面孔（踩过就懂）：<c>PatchOperationAdd/Insert/Replace/AddModExtension</c> 的
/// <c>value</c> 是 <c>Verse.XmlContainer</c>（<c>LoadDataFromXmlCustom</c> 直接取 <c>xmlRoot</c>，所以
/// <c>node.ChildNodes</c> 就是 <c>&lt;value&gt;</c> 的子元素）；而
/// <c>PatchOperationAttributeAdd/Set</c> 的 <c>value</c> 是 <c>string</c>。同名元素、不同类型，
/// 判别依据是操作类型 —— 写成「统一取 value 的 InnerText」会把 Add 的整段子树压成字符串。
/// </summary>
public static class PatchLayer
{
    /// <summary>
    /// 内置操作类（去掉命名空间后的简单名 → 类型）。
    /// 与 <c>Verse</c> 命名空间下的实现类一一对应；<c>PatchOperation</c> / <c>PatchOperationPathed</c> /
    /// <c>PatchOperationAttribute</c> 是抽象基类 —— 引擎把缺 <c>Class=</c> 的节点回落到 <c>PatchOperation</c>
    /// 并必然失败，因此这三个也走 RD2014 的 <c>missingClass</c>/<c>customClass</c> 分支。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, PatchOpKind> KnownOperations =
        new Dictionary<string, PatchOpKind>(StringComparer.Ordinal)
        {
            ["PatchOperationAdd"] = PatchOpKind.Add,
            ["PatchOperationInsert"] = PatchOpKind.Insert,
            ["PatchOperationReplace"] = PatchOpKind.Replace,
            ["PatchOperationRemove"] = PatchOpKind.Remove,
            ["PatchOperationSetName"] = PatchOpKind.SetName,
            ["PatchOperationAttributeAdd"] = PatchOpKind.AttributeAdd,
            ["PatchOperationAttributeSet"] = PatchOpKind.AttributeSet,
            ["PatchOperationAttributeRemove"] = PatchOpKind.AttributeRemove,
            ["PatchOperationSequence"] = PatchOpKind.Sequence,
            ["PatchOperationConditional"] = PatchOpKind.Conditional,
            ["PatchOperationTest"] = PatchOpKind.Test,
            ["PatchOperationFindMod"] = PatchOpKind.FindMod,
            ["PatchOperationAddModExtension"] = PatchOpKind.AddModExtension,
        };

    /// <summary>会**修改文档**的操作 —— 只有它们「0 匹配」才等于「补丁打空了」（RD2009 的口径，见 Codes.PatchXpathNoMatch）。</summary>
    private static readonly HashSet<PatchOpKind> MutatingKinds = new()
    {
        PatchOpKind.Add, PatchOpKind.Insert, PatchOpKind.Replace, PatchOpKind.Remove, PatchOpKind.SetName,
        PatchOpKind.AttributeAdd, PatchOpKind.AttributeSet, PatchOpKind.AttributeRemove, PatchOpKind.AddModExtension,
    };

    /// <summary>
    /// 跑完整条补丁链：合成统一 XML → 按序应用补丁 → 产出诊断与度量。
    /// </summary>
    /// <param name="defFiles">进入统一 XML 的 def 文件（**必须已按引擎语义去重**，即 <see cref="DefFileEnumerator"/> 的产物）。</param>
    /// <param name="patchFiles">补丁文件，**调用方必须按 mod 加载序传入**（引擎就是这个顺序）。</param>
    /// <param name="activeModNames"><c>PatchOperationFindMod</c> 的判据 = <c>ModMetaData.Name</c>（**显示名，不是 packageId**）。</param>
    public static PatchRunResult Apply(
        IReadOnlyList<(string FileAbs, string ModPackageId)> defFiles,
        IReadOnlyList<(string FileAbs, string ModPackageId)> patchFiles,
        IReadOnlySet<string>? activeModNames = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var diags = new List<Diagnostic>();
        var outcomes = new List<PatchOutcome>();
        var nameAdds = new List<PatchNameAddition>();

        // ── 阶段 2：CombineIntoUnifiedXML ────────────────────────────────────────────────
        // 引擎逐行：new XmlDocument(); AppendChild(CreateElement("Defs"));
        //           foreach asset: if root != "Defs" → Log.Error（仍然继续 AppendChild）
        //                           foreach child: ImportNode(deep) + AppendChild
        // 我们只收 <Defs> 根（非 Defs 的文件在别的规则里报 RD2013；补丁文件根本不该进这里）。
        var doc = new XmlDocument();
        doc.AppendChild(doc.CreateElement("Defs"));

        var assets = 0;
        foreach (var f in defFiles)
        {
            assets++;
            XmlDocument src;
            try
            {
                src = new XmlDocument();
                src.Load(f.FileAbs);
            }
            catch
            {
                // 解析失败的文件由 DefScanner/RD2001 负责报；本层不重复报，也不静默当成空文档破坏时序。
                continue;
            }

            if (src.DocumentElement is null || src.DocumentElement.Name != "Defs") continue;
            foreach (XmlNode child in src.DocumentElement.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element) continue;
                doc.DocumentElement!.AppendChild(doc.ImportNode(child, deep: true));
            }
        }

        var unifiedNodes = doc.DocumentElement!.ChildNodes.Count;
        var beforePatches = unifiedNodes;

        // ── 阶段 3：TKeySystem.Parse ────────────────────────────────────────────────────
        // 实测（反编译 Verse.TKeySystem）：类里 private static readonly bool ShouldUseHardcodedMapping => true，
        // 而 ParseDefNode 的**第一句**就是 `if (ShouldUseHardcodedMapping) return;` ⇒ 1.6 上这个阶段
        // 恒等于 0 条 TKey（键表由 TKeySystemHardcodedMapping 硬编码提供）。这不是「我们没实现」，是引擎现状。
        var tKeyRefs = 0;

        // ── 阶段 4 + 5：ErrorCheckPatches / ApplyPatches ───────────────────────────────
        var runner = new Runner(doc, activeModNames, diags, outcomes, nameAdds);
        var topLevelOps = 0;
        var patchFilesUsed = 0;

        foreach (var (fileAbs, mod) in patchFiles)
        {
            XmlDocument pdoc;
            try
            {
                pdoc = new XmlDocument();
                pdoc.Load(fileAbs);
            }
            catch
            {
                continue;   // 同 DefScanner：坏文件的 RD2001 归 DefScanner，本层不重复
            }

            var root = pdoc.DocumentElement;
            if (root is null || root.Name != "Patch") continue;   // 引擎：根不是 Patch → Log.Error 并 continue
            patchFilesUsed++;

            var lines = SourceLineMap.Load(fileAbs);
            runner.BeginFile(fileAbs, mod, lines);

            foreach (XmlNode child in root.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element) continue;
                if (child.Name != "Operation") continue;          // 引擎：一级子元素必须叫 Operation，否则 Error + continue
                topLevelOps++;
                runner.ApplyElement((XmlElement)child, nested: false);
            }
        }

        var afterPatches = doc.DocumentElement.ChildNodes.Count;
        sw.Stop();

        var stats = new PatchPhaseStats(
            Assets: assets,
            UnifiedDefNodes: unifiedNodes,
            TKeyRefs: tKeyRefs,
            PatchFiles: patchFilesUsed,
            TopLevelOperations: topLevelOps,
            DefNodesBeforePatches: beforePatches,
            DefNodesAfterPatches: afterPatches,
            // 阶段 6：ParseAndProcessXML 会对**当前**根下的每个 Element 子节点 TryRegister 一次
            // （补丁新增的节点也在内 —— 它们没有 assetlookup 条目，走 patchedDefs 分支）。
            RegisteredNodes: afterPatches,
            NameAttributesAdded: nameAdds.Count,
            SkippedCustomClass: outcomes.Count(o => o.Skipped),
            ElapsedMs: sw.ElapsedMilliseconds);

        return new PatchRunResult(stats, outcomes, diags, nameAdds, doc);
    }

    /// <summary>
    /// 只读地抽取「补丁给 def 补 <c>Name</c>」的操作（不应用、不起 DOM）—— Step 6 的度量入口。
    ///
    /// 口径与 <see cref="Apply"/> 相同（同一个 Class 判别、同一套行列定位），区别只是不求值；
    /// 这样「量出来的候选」与「实际执行的操作」用的是同一份代码，不会两套口径漂移。
    /// </summary>
    public static IReadOnlyList<(string AddedName, string XPath, string FileAbs, int Line, string ModPackageId)>
        CollectNameAttributeAdds(IReadOnlyList<(string FileAbs, string ModPackageId)> patchFiles)
    {
        var result = new List<(string, string, string, int, string)>();
        foreach (var (fileAbs, mod) in patchFiles)
        {
            XmlDocument pdoc;
            try { pdoc = new XmlDocument(); pdoc.Load(fileAbs); }
            catch { continue; }
            if (pdoc.DocumentElement is null || pdoc.DocumentElement.Name != "Patch") continue;

            var lines = SourceLineMap.Load(fileAbs);
            foreach (XmlNode child in pdoc.DocumentElement.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element || child.Name != "Operation") continue;
                Collect(((XmlElement)child).ChildNodes, mod, fileAbs, lines, result);
            }
        }
        return result;

        static void Collect(XmlNodeList nodes, string mod, string fileAbs, SourceLineMap lines,
            List<(string, string, string, int, string)> sink)
        {
            foreach (XmlNode n in nodes)
            {
                if (n.NodeType != XmlNodeType.Element) continue;
                var el = (XmlElement)n;
                var cls = SimpleName(el.GetAttribute("Class"));
                if (cls == "PatchOperationAttributeAdd"
                    && string.Equals(Text(el, "attribute"), "Name", StringComparison.Ordinal))
                {
                    var xpath = Text(el, "xpath") ?? "";
                    sink.Add((Text(el, "value") ?? "", xpath, fileAbs, lines.LineOf(xpath), mod));
                }

                // 嵌套（Sequence 的 operations/li、Conditional/FindMod 的 match/nomatch）
                if (cls == "PatchOperationSequence")
                {
                    var ops = el.SelectSingleNode("operations");
                    if (ops is not null) Collect(ops.ChildNodes, mod, fileAbs, lines, sink);
                }
                else if (cls is "PatchOperationConditional" or "PatchOperationFindMod")
                {
                    foreach (var branch in new[] { "match", "nomatch" })
                        if (el.SelectSingleNode(branch) is { } b) Collect(b.ChildNodes, mod, fileAbs, lines, sink);
                }
            }
        }
    }

    /// <summary>去掉命名空间的简单类型名（<c>"Verse.PatchOperationAdd"</c> → <c>"PatchOperationAdd"</c>）。</summary>
    internal static string SimpleName(string? className)
    {
        if (string.IsNullOrEmpty(className)) return "";
        var i = className!.LastIndexOf('.');
        return i < 0 ? className : className[(i + 1)..];
    }

    /// <summary>取子元素的文本（不存在返回 null；与引擎「字段绑定」最接近的静态对应）。</summary>
    internal static string? Text(XmlElement op, string childName)
    {
        var n = op.SelectSingleNode(childName);
        return n?.InnerText;
    }

    // ═══════════════════════════════════════════════════════════════════════════════════
    // 求值器
    // ═══════════════════════════════════════════════════════════════════════════════════

    private sealed class Runner
    {
        private readonly XmlDocument _doc;
        private readonly IReadOnlySet<string> _activeModNames;
        private readonly List<Diagnostic> _diags;
        private readonly List<PatchOutcome> _outcomes;
        private readonly List<PatchNameAddition> _nameAdds;

        private string _file = "";
        private string _mod = "";
        private SourceLineMap _lines = SourceLineMap.Empty;

        public Runner(XmlDocument doc, IReadOnlySet<string>? activeModNames,
            List<Diagnostic> diags, List<PatchOutcome> outcomes, List<PatchNameAddition> nameAdds)
        {
            _doc = doc;
            _activeModNames = activeModNames ?? new HashSet<string>(StringComparer.Ordinal);
            _diags = diags;
            _outcomes = outcomes;
            _nameAdds = nameAdds;
        }

        public void BeginFile(string file, string mod, SourceLineMap lines)
        {
            _file = file;
            _mod = mod;
            _lines = lines;
        }

        /// <summary>
        /// 求值一个 <c>&lt;Operation&gt;</c> 节点（顶层或嵌套）。
        /// 返回引擎 <c>Apply()</c> 的返回值（含 Success 翻转），供 Sequence/Conditional 使用。
        /// </summary>
        public bool ApplyElement(XmlElement op, bool nested)
        {
            var rawClass = op.GetAttribute("Class");
            var cls = PatchLayer.SimpleName(rawClass);
            var xpath = PatchLayer.Text(op, "xpath");
            var line = _lines.LineOf(string.IsNullOrEmpty(xpath) ? $"Class=\"{rawClass}\"" : xpath!);

            // ── Step 4：自定义 Class=（mod 自带补丁类）/ 缺 Class ──
            if (!PatchLayer.KnownOperations.TryGetValue(cls, out var kind))
            {
                var reason = string.IsNullOrEmpty(rawClass) ? "missingClass" : "customClass";
                _outcomes.Add(new PatchOutcome(_file, _mod, line, PatchOpKind.Unknown,
                    string.IsNullOrEmpty(rawClass) ? "(缺 Class=)" : rawClass, xpath,
                    Matched: -1, Applied: false, Skipped: true, SkipReason: reason));
                _diags.Add(new Diagnostic(
                    Codes.CustomPatchClass, Severity.Info, Scope.Offline,
                    reason == "missingClass"
                        ? "<Operation> 缺少 Class= 属性：引擎回落到基类 Verse.PatchOperation，其 ApplyWorker 必然失败"
                        : $"补丁操作 Class=\"{rawClass}\" 是自定义补丁类，需要执行 mod 代码 → 跳过该操作",
                    "本工具不执行 mod 代码（01 §5.1/§7 的诚实边界）；该操作的实际效果需真机验证",
                    FileAbs: _file, Line: line > 0 ? line : null, ModPackageId: _mod,
                    Evidence: new Dictionary<string, string>
                    {
                        ["reason"] = reason,
                        ["operationClass"] = rawClass,
                        ["xpath"] = xpath ?? "",
                    }));
                return false;
            }

            var applied = Dispatch(op, kind, xpath, line, out var matched, out var evalError);

            _outcomes.Add(new PatchOutcome(_file, _mod, line, kind, rawClass, xpath,
                matched, applied, Skipped: false, SkipReason: null, EvaluationError: evalError));

            // ── Step 3：XPath 0 命中 → RD2009（只对会改文档的操作；控制流不算「打空」）──
            if (evalError is null && matched == 0 && PatchLayer.MutatingKinds.Contains(kind))
            {
                _diags.Add(new Diagnostic(
                    Codes.PatchXpathNoMatch, Severity.Warning, Scope.Offline,
                    $"{cls} 的 XPath 匹配 0 个节点（补丁打空了）：{xpath}",
                    "检查 XPath 的元素名/defName 是否写错；可用 `rimdev name check <defName> --kind def` 反查名字是否存在。"
                    + "（引擎对 0 匹配只在启动末尾报一句 `Patch operation ... failed`，不指出 XPath 哪里错）",
                    FileAbs: _file, Line: line > 0 ? line : null, ModPackageId: _mod,
                    Evidence: new Dictionary<string, string>
                    {
                        ["outcome"] = "noMatch",
                        ["operationClass"] = rawClass,
                        ["xpath"] = xpath ?? "",
                        ["matched"] = "0",
                    }));
            }
            else if (evalError is not null)
            {
                _diags.Add(new Diagnostic(
                    Codes.PatchXpathNoMatch, Severity.Warning, Scope.Offline,
                    $"{cls} 的 XPath 求值失败：{xpath} —— {evalError}",
                    "XPath 1.0 语法或函数不受支持；引擎会在 ApplyPatches 里捕获异常并记 `Error in patch.Apply()`",
                    FileAbs: _file, Line: line > 0 ? line : null, ModPackageId: _mod,
                    Evidence: new Dictionary<string, string>
                    {
                        ["outcome"] = "xpathError",
                        ["operationClass"] = rawClass,
                        ["xpath"] = xpath ?? "",
                        ["error"] = evalError,
                    }));
            }

            return applied;
        }

        /// <summary>
        /// 真正执行一个操作。语义逐行对齐反编译的 <c>Verse.PatchOperation*</c>。
        /// ★ 下面每个实现都保留了引擎那几处**看起来奇怪但必须照抄**的写法（尤其 Insert 的插入顺序），
        ///   「顺手改漂亮」= 改变结果。
        /// </summary>
        private bool Dispatch(XmlElement op, PatchOpKind kind, string? xpath, int line,
            out int matched, out string? evalError)
        {
            matched = -1;
            evalError = null;
            var worker = false;

            switch (kind)
            {
                case PatchOpKind.Add:
                {
                    // order 默认 Append（`private Order order;` 无初始化器 ⇒ 0 = Append）
                    var append = ReadOrder(op, defaultAppend: true);
                    var value = op.SelectSingleNode("value");
                    matched = 0;
                    foreach (var node in SelectNodes(op, xpath, out evalError))
                    {
                        matched++;
                        if (value is null) continue;
                        if (append)
                            foreach (XmlNode c in value.ChildNodes)
                                node.AppendChild(_doc.ImportNode(c, deep: true));
                        else
                            for (var i = value.ChildNodes.Count - 1; i >= 0; i--)
                                node.PrependChild(_doc.ImportNode(value.ChildNodes[i]!, deep: true));
                    }
                    worker = matched > 0;
                    break;
                }

                case PatchOpKind.Insert:
                {
                    // order 默认 Prepend（`private Order order = Order.Prepend;`）
                    var append = ReadOrder(op, defaultAppend: false);
                    var value = op.SelectSingleNode("value");
                    matched = 0;
                    foreach (var node in SelectNodes(op, xpath, out evalError))
                    {
                        matched++;
                        var parent = node.ParentNode;
                        if (parent is null || value is null) continue;
                        if (append)
                        {
                            // ★ 引擎原文 `foreach (child in value.ChildNodes) parent.InsertAfter(Import(child), node);`
                            //   —— 多子元素时结果是**反序**的。照抄，不「修正」。
                            foreach (XmlNode c in value.ChildNodes)
                                parent.InsertAfter(_doc.ImportNode(c, deep: true), node);
                        }
                        else
                        {
                            for (var i = value.ChildNodes.Count - 1; i >= 0; i--)
                                parent.InsertBefore(_doc.ImportNode(value.ChildNodes[i]!, deep: true), node);
                        }
                        if (node.NodeType == XmlNodeType.Text) parent.Normalize();
                    }
                    worker = matched > 0;
                    break;
                }

                case PatchOpKind.Replace:
                {
                    // ★ 引擎先 ToArray() 快照：否则「替换出的新节点又命中同一 XPath」会无限循环。
                    var value = op.SelectSingleNode("value");
                    matched = 0;
                    foreach (var node in SelectNodes(op, xpath, out evalError))
                    {
                        matched++;
                        var parent = node.ParentNode;
                        if (parent is null || value is null) continue;
                        foreach (XmlNode c in value.ChildNodes)
                            parent.InsertBefore(_doc.ImportNode(c, deep: true), node);
                        parent.RemoveChild(node);
                    }
                    worker = matched > 0;
                    break;
                }

                case PatchOpKind.Remove:
                {
                    matched = 0;
                    foreach (var node in SelectNodes(op, xpath, out evalError))
                    {
                        matched++;
                        node.ParentNode?.RemoveChild(node);
                    }
                    worker = matched > 0;
                    break;
                }

                case PatchOpKind.SetName:
                {
                    // ★ 引擎用 CreateElement(name) + InnerXml 复制 —— **属性会丢**。照抄。
                    var name = PatchLayer.Text(op, "name") ?? "";
                    matched = 0;
                    foreach (var node in SelectNodes(op, xpath, out evalError))
                    {
                        matched++;
                        var parent = node.ParentNode;
                        if (parent is null) continue;
                        var created = _doc.CreateElement(name);
                        created.InnerXml = node.InnerXml;
                        parent.InsertBefore(created, node);
                        parent.RemoveChild(node);
                    }
                    worker = matched > 0;
                    break;
                }

                case PatchOpKind.AttributeAdd:
                case PatchOpKind.AttributeSet:
                case PatchOpKind.AttributeRemove:
                {
                    var attribute = PatchLayer.Text(op, "attribute");
                    var valueText = PatchLayer.Text(op, "value");
                    matched = 0;
                    foreach (var node in SelectNodes(op, xpath, out evalError))
                    {
                        matched++;
                        if (string.IsNullOrEmpty(attribute)) continue;
                        var existing = node.Attributes?[attribute!];
                        if (kind == PatchOpKind.AttributeAdd)
                        {
                            if (existing is null && node.Attributes is not null)
                            {
                                var created = _doc.CreateAttribute(attribute!);
                                created.Value = valueText ?? "";
                                node.Attributes.Append(created);
                                worker = true;
                            }
                        }
                        else if (kind == PatchOpKind.AttributeSet)
                        {
                            if (existing is not null) existing.Value = valueText ?? "";
                            else if (node.Attributes is not null)
                            {
                                var created = _doc.CreateAttribute(attribute!);
                                created.Value = valueText ?? "";
                                node.Attributes.Append(created);
                            }
                            worker = true;
                        }
                        else // AttributeRemove
                        {
                            if (existing is not null)
                            {
                                node.Attributes!.Remove(existing);
                                worker = true;
                            }
                        }

                        // ★ Step 5/6：AttributeAdd 真的补上 Name → 记一条（含它打到了哪个 def）
                        if (kind == PatchOpKind.AttributeAdd && existing is null
                            && string.Equals(attribute, "Name", StringComparison.Ordinal))
                        {
                            _nameAdds.Add(new PatchNameAddition(
                                valueText ?? "", xpath ?? "", matched,
                                node is XmlElement target ? target.Name : null,
                                node is XmlElement t2 ? PatchLayer.Text(t2, "defName") : null,
                                _file, line, _mod));
                        }
                    }
                    break;
                }

                case PatchOpKind.Sequence:
                {
                    // 引擎：operations 里逐个 Apply，**第一个 false 就停**（没有 onFail —— 见类注释与报告）。
                    var ops = op.SelectSingleNode("operations");
                    worker = true;
                    if (ops is not null)
                        foreach (XmlNode c in ops.ChildNodes)
                        {
                            if (c.NodeType != XmlNodeType.Element) continue;
                            if (!ApplyElement((XmlElement)c, nested: true)) { worker = false; break; }
                        }
                    break;
                }

                case PatchOpKind.Conditional:
                {
                    // 引擎原文（照抄，包括「命中但没有 match 就返回 nomatch != null」这条反直觉分支）：
                    //   if (SelectSingleNode(xpath) != null) { if (match != null) return match.Apply(); }
                    //   else if (nomatch != null) return nomatch.Apply();
                    //   if (match == null) return nomatch != null;
                    //   return true;
                    bool hit;
                    try { hit = _doc.SelectSingleNode(xpath ?? "") is not null; }
                    catch (Exception ex) { evalError = ex.Message; return false; }

                    var match = op.SelectSingleNode("match");
                    var nomatch = op.SelectSingleNode("nomatch");
                    if (hit)
                    {
                        if (match is not null) { worker = ApplyChild(match); break; }
                    }
                    else if (nomatch is not null) { worker = ApplyChild(nomatch); break; }
                    worker = match is null ? nomatch is not null : true;
                    break;
                }

                case PatchOpKind.Test:
                {
                    try { matched = _doc.SelectSingleNode(xpath ?? "") is not null ? 1 : 0; }
                    catch (Exception ex) { evalError = ex.Message; return false; }
                    worker = matched == 1;
                    break;
                }

                case PatchOpKind.FindMod:
                {
                    // ★ 引擎用的是 ModLister.HasActiveModWithName(name) → `mod.Active && mod.Name == name`：
                    //   比的是 **ModMetaData.Name（About.xml 的 <name>，显示名）**，不是 packageId，
                    //   而且是 **Ordinal 精确相等**（区分大小写）。很多 mod 在这里写 packageId ⇒ 恒不命中。
                    var hit = false;
                    var mods = op.SelectSingleNode("mods");
                    if (mods is not null)
                        foreach (XmlNode li in mods.ChildNodes)
                        {
                            if (li.NodeType != XmlNodeType.Element) continue;
                            if (_activeModNames.Contains(li.InnerText)) { hit = true; break; }
                        }

                    var match = op.SelectSingleNode("match");
                    var nomatch = op.SelectSingleNode("nomatch");
                    if (hit)
                    {
                        if (match is not null) { worker = ApplyChild(match); break; }
                    }
                    else if (nomatch is not null) { worker = ApplyChild(nomatch); break; }
                    worker = true;   // 引擎：FindMod 两个分支都没配也返回 true
                    break;
                }

                case PatchOpKind.AddModExtension:
                {
                    var value = op.SelectSingleNode("value");
                    matched = 0;
                    foreach (var node in SelectNodes(op, xpath, out evalError))
                    {
                        matched++;
                        if (node is not XmlElement element) continue;
                        // ★ 引擎原文用 `xmlNode["modExtensions"]`，那是 XmlElement 的索引器；
                        //   XmlNode 上没有这个成员（反编译输出把类型放宽了），所以这里必须显式取 XmlElement。
                        var ext = element["modExtensions"];
                        if (ext is null)
                        {
                            ext = _doc.CreateElement("modExtensions");
                            element.AppendChild(ext);
                        }
                        if (value is not null)
                            foreach (XmlNode c in value.ChildNodes)
                                ext.AppendChild(_doc.ImportNode(c, deep: true));
                        worker = true;
                    }
                    break;
                }

                default:
                    return false;
            }

            // ── Success 包装（引擎 Apply() 的最后几行，原样照抄）──
            var success = (PatchLayer.Text(op, "success") ?? "Normal").Trim();
            if (string.Equals(success, "Always", StringComparison.OrdinalIgnoreCase)) worker = true;
            else if (string.Equals(success, "Never", StringComparison.OrdinalIgnoreCase)) worker = false;
            else if (string.Equals(success, "Invert", StringComparison.OrdinalIgnoreCase)) worker = !worker;

            return worker;
        }

        /// <summary>
        /// <c>Conditional</c> / <c>FindMod</c> 的 <c>&lt;match&gt;</c> / <c>&lt;nomatch&gt;</c> 分支。
        ///
        /// ★★ 这里**不是**「容器里装一条操作」：引擎的字段就是 <c>private PatchOperation match;</c>，
        ///   由 DirectXmlToObject 按该元素自己的 <c>Class=</c> 反序列化成操作 ⇒ **&lt;match&gt; 元素本身**
        ///   就是那条操作（它的子元素 <c>&lt;xpath&gt;</c>/<c>&lt;value&gt;</c> 才是参数）。
        ///   第一版把它当成容器、去取它的第一个子元素当操作，于是把 <c>&lt;xpath&gt;</c> 误当成操作 ——
        ///   后果是 **match/nomatch 全都不执行**（还被误报成自定义 Class）。这是用例抓出来的。
        /// </summary>
        private bool ApplyChild(XmlNode child) =>
            child is XmlElement el && ApplyElement(el, nested: true);

        /// <summary><c>order</c> 字段的绑定：<c>Append</c>/<c>Prepend</c>；解析不到时取引擎的默认值。</summary>
        private static bool ReadOrder(XmlElement op, bool defaultAppend)
        {
            var text = PatchLayer.Text(op, "order");
            if (string.IsNullOrWhiteSpace(text)) return defaultAppend;
            return text.Trim().Equals("Append", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// <c>xml.SelectNodes(xpath)</c> 的包装：异常（XPath 语法错）转成 <c>evalError</c> 而不是冒泡，
        /// 与引擎 <c>ApplyPatches</c> 的 try/catch 语义一致。Replace/Remove/SetName 在这里做快照
        /// （引擎对这三种也是快照，Add/Insert/Attribute* 是实时列表 —— 但这三种不会删除已匹配节点，
        /// 实时枚举与快照等价，为安全统一快照**不影响语义**，因为枚举时机只在删除类操作上可观察）。
        /// </summary>
        private List<XmlNode> SelectNodes(XmlElement op, string? xpath, out string? evalError)
        {
            evalError = null;
            if (string.IsNullOrEmpty(xpath)) return new List<XmlNode>();
            try
            {
                var list = new List<XmlNode>();
                var nodes = _doc.SelectNodes(xpath!);
                if (nodes is null) return list;
                foreach (XmlNode n in nodes) list.Add(n);
                return list;
            }
            catch (Exception ex)
            {
                evalError = $"{ex.GetType().Name}: {ex.Message}";
                return new List<XmlNode>();
            }
        }
    }

    /// <summary>
    /// 每份补丁文件的「行号索引」：<c>XmlDocument</c> **不保存逐节点行号**
    /// （<c>XmlElement</c> 不实现 <c>IXmlLineInfo</c> —— 实测 <c>IsIXmlLineInfo=False</c>，只有
    /// <c>XmlDocument</c> 自身实现、返回读取器的当前位置），所以按字面量在源码行里找。
    /// 同一个字面量在一份文件里可能重复出现，因此每个字面量维护一个**单调游标**：按文档顺序查询时
    /// 命中的就是对应那一条。
    /// </summary>
    private sealed class SourceLineMap
    {
        /// <summary>超过这个大小的文件不做行索引（补丁文件都是几 KB；防御性上限）。</summary>
        private const long MaxBytes = 8 * 1024 * 1024;

        private readonly string[] _lines;
        private readonly Dictionary<string, int> _cursor = new(StringComparer.Ordinal);

        public static readonly SourceLineMap Empty = new(Array.Empty<string>());

        private SourceLineMap(string[] lines) => _lines = lines;

        public static SourceLineMap Load(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length > MaxBytes) return Empty;
                return new SourceLineMap(File.ReadAllLines(path));
            }
            catch
            {
                return Empty;
            }
        }

        /// <summary>返回 1-based 行号；找不到返回 0（诊断里就是「无行号」，不编造）。</summary>
        public int LineOf(string literal)
        {
            if (_lines.Length == 0 || string.IsNullOrEmpty(literal)) return 0;
            var start = _cursor.TryGetValue(literal, out var c) ? c : 0;
            for (var i = start; i < _lines.Length; i++)
                if (_lines[i].Contains(literal, StringComparison.Ordinal))
                {
                    _cursor[literal] = i + 1;
                    return i + 1;
                }
            for (var i = 0; i < start && i < _lines.Length; i++)
                if (_lines[i].Contains(literal, StringComparison.Ordinal)) return i + 1;
            return 0;
        }
    }
}
