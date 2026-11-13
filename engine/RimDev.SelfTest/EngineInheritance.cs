using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using RimDev.Core.Environment;
using RimDev.Core.Metadata;

namespace RimDev.SelfTest;

/// <summary>
/// 引擎对照台（第 3 期 Task 5b Step 1 / Step 3）：用**反射**调 <c>Verse.XmlInheritance</c>，
/// 把「引擎真实的 XML 继承合并语义」搬进 RimDev 自测进程当**真值**。
///
/// ★★ 为什么必须反射：`RimDev.Core` **不引用** `Assembly-CSharp`（零外部依赖 + 不漏出 Unity 类型），
///    所以引擎的合并语义只能在自测进程里用反射取；生产代码里那套语义由
///    <c>RimDev.Core.Defs.InheritanceLayer.Merge</c> 复刻。
///
/// ── 离线调用配方（本文件在 .NET 8 进程内跑通）──────────────────────────────────────
/// 【1】按序载入 `UnityEngine.CoreModule.dll` → `Assembly-CSharp.dll`
///      （用既有的 <see cref="ManagedAssemblyCatalog"/>，它的 `Resolving` 钩子按 managed 目录解析依赖）。
/// 【2】Unity-free shim：克隆路径首句要读 `Prefs.LogVerbose`，而 `Prefs.data` 离线为 **null**
///      ⇒ 先给 `Prefs.data` 塞一个**未初始化实例**（<c>RuntimeHelpers.GetUninitializedObject</c>）。
///      ★ **不要**用 `FormatterServices.GetUninitializedObject`：.NET 8 起它标了 `[Obsolete]`，
///        `SYSLIB0050` 会直接破坏本项目的「0 警告」门禁。
/// 【3】`XmlInheritance.Clear()` → 对顶层元素 `TryRegister(node, null)` → `Resolve()`
///      → `GetResolvedNodeFor(node)` 取合并结果。
///      · `TryRegister` 的 `ModContentPack` 参数可传 `null`；
///      · 带 `MayRequire` / `MayRequireAnyOf` 的节点**必须跳过**（`ModLister` 需要 Unity）；
///      · 按 `Name` 属性去重（引擎 `nodesByName` 是**先到者胜**，重复会让它走 `Log.Error`）。
///
/// ── ★★ 最关键的一条离线约束：`Verse.Log.Error` 离线会抛异常（本 Task 实测）──────────────
/// 引擎在三条路径上会 <c>Log.Error</c>，而 <c>Verse.Log.Error</c> 最终走
/// <c>UnityEngine.Debug.LogError</c>（Unity 的 **InternalCall/ECall**），在纯 .NET 8 进程里必然抛
/// <c>TargetInvocationException: ECall methods must be packaged into a system module.</c>
/// 而 `Resolve()` 是**全量循环** —— 任何一个节点触发日志，整次解析都会中断、什么都拿不到。
/// 三条触发路径：
/// <list type="number">
/// <item>`GetBestParentFor` 找不到父名（`Could not find parent node named ...`）；</item>
/// <item>`ResolveXmlNodes` 末尾 / `ResolveXmlNodesRecursively` 对**没能解析**的节点
///   （`Cyclic inheritance hierarchy detected`，这条文案同时覆盖真环与断链）；</item>
/// <item>`CheckForDuplicateNodes`（在 `ResolveXmlNodeFor` 里对**子节点自身**的原始子树检查
///   「同一父元素下两个非列表同名子元素」）。</item>
/// </list>
/// ⇒ 本类在注册**之前**先把这三条条件**预测**出来（见 <see cref="IsSafelyResolvable"/> 与
///   只注册「引擎保证不会打日志」的节点。对没注册的节点，
///   <c>GetResolvedNodeFor</c> **不能调**（它自己也会 `Log.Error`），
///   引擎的答案是 `GetResolvedNodeFor` 末尾那句 <c>return originalNode;</c> —— 即**原节点**，
///   所以 <see cref="ResolvedNode.Xml"/> 记 null、差分侧按「原节点」解释。
/// ★ 例外：`CheckForDuplicateNodes` 触发的节点**离线无解**（引擎在真机里会打日志后继续，
///   在我们这里会整次中断），因此单独计成 <see cref="Resolution.SkippedDuplicateNodeName"/>，
///   差分测试对它**显式断言为 0**（若不为 0 就必须停下上报，不许悄悄吞掉）。
///
/// ★★ 探针跑不通时**必须显式报出来**，不许静默跳过（假阳比漏报更贵）：
///    <see cref="Available"/> 为 false 时 <see cref="Status"/> 给出**具体原因**，
///    Step 5 有一条用例专门钉住「探针不可用 ⇒ 用例 FAIL」。
/// </summary>
public static class EngineInheritance
{
    /// <summary>没被注册的节点，分别因为哪一条「引擎会打日志」的条件。</summary>
    public static class SkipReason
    {
        /// <summary>带 `MayRequire` / `MayRequireAnyOf`（`ModLister` 需要 Unity）。</summary>
        public const string MayRequire = "MayRequire";

        /// <summary>`Name` 撞名：引擎 `TryRegister` 会 `Log.Error` 后早退，不注册。</summary>
        public const string DuplicateName = "DuplicateName";

        /// <summary>父名不存在 / 上游断链 / 成环 ⇒ `Resolve()` 末尾会打 `Cyclic...` 日志。</summary>
        public const string Unresolvable = "Unresolvable";

        /// <summary>自身原始子树会触发 `CheckForDuplicateNodes` 的 `Log.Error`（离线无解）。</summary>
        public const string DuplicateNodeName = "DuplicateNodeName";
    }

    /// <summary>一个顶层元素的解析结果。</summary>
    /// <param name="Label">来源标签（文件名或夹具名）。</param>
    /// <param name="Index">在该文档顶层元素里的下标（0-based）。</param>
    /// <param name="ElementName">元素名（`ThingDef` / `Def` / …）。</param>
    /// <param name="DefName">`&lt;defName&gt;`（可能为 null）。</param>
    /// <param name="ParentName">`ParentName` 属性值（可能为 null）。</param>
    /// <param name="Node">**原始节点对象**（我方实现要用同一个节点走链）。</param>
    /// <param name="Xml">引擎解析结果的规范化 XML（可能为 null，见 <paramref name="SkipReason"/>）。</param>
    /// <param name="ParentFound">父名是否在引擎的 `nodesByName` 表里（同一份注册输入下）。</param>
    /// <param name="SkipReason">
    /// null = 已注册（`Xml` 有值时是真值）；非 null = 未注册，引擎的答案是**原节点**。
    /// </param>
    public sealed record ResolvedNode(
        string Label, int Index, string ElementName, string? DefName, string? ParentName,
        XmlNode Node, string? Xml, bool ParentFound, string? SkipReason)
    {
        /// <summary>该节点是否真的被 <c>TryRegister</c> 注册过。</summary>
        public bool Registered => SkipReason is null;
    }

    /// <summary>一次解析的全部产物 + 引擎自己用的「Name → 节点」表。</summary>
    public sealed class Resolution
    {
        internal Resolution(
            IReadOnlyList<ResolvedNode> nodes,
            IReadOnlyDictionary<string, XmlNode> nameMap,
            IReadOnlyList<string> failures,
            int registered, int skippedMayRequire, int skippedDuplicateName,
            int skippedUnresolvable, int skippedDuplicateNodeName)
        {
            Nodes = nodes;
            NameMap = nameMap;
            Failures = failures;
            Registered = registered;
            SkippedMayRequire = skippedMayRequire;
            SkippedDuplicateName = skippedDuplicateName;
            SkippedUnresolvable = skippedUnresolvable;
            SkippedDuplicateNodeName = skippedDuplicateNodeName;
        }

        /// <summary>所有顶层元素的结果（含没有 `ParentName` 的）。</summary>
        public IReadOnlyList<ResolvedNode> Nodes { get; }

        /// <summary>
        /// 引擎 `nodesByName` 的等价物（= 同一份注册输入下，引擎**自己能查到**的父节点表）。
        /// 只按 `Name` 属性建表、**先到者胜** —— 这正是引擎的口径（`&lt;defName&gt;` 不参与继承解析）。
        /// </summary>
        public IReadOnlyDictionary<string, XmlNode> NameMap { get; }

        /// <summary>读不了的文件/夹具（不是「解析结果为 null」——那是另一回事）。</summary>
        public IReadOnlyList<string> Failures { get; }

        public int Registered { get; }
        public int SkippedMayRequire { get; }
        public int SkippedDuplicateName { get; }
        public int SkippedUnresolvable { get; }
        public int SkippedDuplicateNodeName { get; }

        /// <summary>带 `ParentName` 的顶层元素数（不分注册与否）。</summary>
        public int WithParentName => Nodes.Count(n => !string.IsNullOrEmpty(n.ParentName));
    }

    // ─────────────────────────── 反射句柄 ───────────────────────────

    private sealed record Api(
        Type Type, MethodInfo Clear, MethodInfo TryRegister, MethodInfo Resolve, MethodInfo GetResolvedNodeFor);

    private static bool _initialized;
    private static Api? _api;
    private static HashSet<string>? _allowDuplicateNodeFieldNames;
    private static string _status = "未初始化";

    /// <summary>探针是否可用。</summary>
    public static bool Available
    {
        get { EnsureInitialized(); return _api is not null; }
    }

    /// <summary>探针状态：可用时给配方摘要，不可用时给**具体失败原因**。</summary>
    public static string Status
    {
        get { EnsureInitialized(); return _status; }
    }

    /// <summary>引擎 `XmlInheritance.allowDuplicateNodesFieldNames`（`IsListElement` 的第二条判据）。</summary>
    public static IReadOnlySet<string> EngineAllowDuplicateNodeFieldNames
    {
        get { EnsureInitialized(); return _allowDuplicateNodeFieldNames ?? (IReadOnlySet<string>)new HashSet<string>(); }
    }

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            var managed = env.ManagedDir;

            // 【1】按序载入。UnityEngine.CoreModule 先落盘，Assembly-CSharp 由既有目录表设施载入。
            var unity = Path.Combine(managed, "UnityEngine.CoreModule.dll");
            if (!File.Exists(unity))
            {
                _status = $"探针不可用：找不到 {unity}";
                return;
            }
            Assembly.LoadFrom(unity);
            var cat = ManagedAssemblyCatalog.Load(managed);

            var xiType = cat.Find("XmlInheritance");
            if (xiType is null)
            {
                _status = "探针不可用：Assembly-CSharp 里没有 Verse.XmlInheritance（类型名或程序集变了）";
                return;
            }

            // 【2】Unity-free shim：Prefs.data 离线为 null，克隆路径首句会读 Prefs.LogVerbose。
            var prefsType = cat.Find("Prefs");
            if (prefsType is null)
            {
                _status = "探针不可用：Assembly-CSharp 里没有 Verse.Prefs";
                return;
            }
            var dataField = prefsType.GetField("data", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (dataField is null)
            {
                _status = "探针不可用：Verse.Prefs.data 字段不存在（Unity-free shim 无法建立）";
                return;
            }
            if (dataField.GetValue(null) is null)
            {
                // ★ RuntimeHelpers，不是 FormatterServices（后者 SYSLIB0050，破坏 0 警告门禁）。
                dataField.SetValue(null, RuntimeHelpers.GetUninitializedObject(dataField.FieldType));
            }

            // 【3】方法句柄（按名字 + 参数个数/类型挑，不假设重载唯一）。
            var clear = xiType.GetMethod("Clear", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
            var resolve = xiType.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
            var tryRegister = xiType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "TryRegister" && m.GetParameters().Length == 2);
            var getResolved = xiType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "GetResolvedNodeFor"
                                     && m.GetParameters().Length == 1
                                     && typeof(XmlNode).IsAssignableFrom(m.GetParameters()[0].ParameterType));
            var allowDupField = xiType.GetField("allowDuplicateNodesFieldNames",
                BindingFlags.Public | BindingFlags.Static);

            if (clear is null || resolve is null || tryRegister is null || getResolved is null || allowDupField is null)
            {
                _status = "探针不可用：Verse.XmlInheritance 的成员与配方不符（"
                          + $"Clear={clear is not null} TryRegister={tryRegister is not null} "
                          + $"Resolve={resolve is not null} GetResolvedNodeFor={getResolved is not null} "
                          + $"allowDuplicateNodesFieldNames={allowDupField is not null}）";
                return;
            }

            var api = new Api(xiType, clear, tryRegister, resolve, getResolved);

            // 触发静态构造（allowDuplicateNodesFieldNames 由它填充），再取那张表。
            api.Clear.Invoke(null, null);
            _allowDuplicateNodeFieldNames = allowDupField.GetValue(null) as HashSet<string> ?? new HashSet<string>();

            // 冒烟：探针必须**真的能产出合并结果**，否则视为不可用（不许「接上了但结果恒为原节点」）。
            var smoke = ResolveCore(new[] { ("smoke", SmokeDocument()) }, api, out var smokeError);
            if (smoke is null)
            {
                _status = $"探针不可用：冒烟失败 —— {smokeError}";
                return;
            }
            var smokeChild = smoke.Nodes.FirstOrDefault(n => n.DefName == "SynSmokeChild");
            if (smokeChild?.Xml is null || !smokeChild.Xml.Contains("<Mass>5</Mass>", StringComparison.Ordinal))
            {
                _status = "探针不可用：冒烟结果里看不到继承来的 <Mass>5</Mass>（注册/解析没生效）"
                          + (smokeChild?.Xml is null ? "（GetResolvedNodeFor 返回 null）" : $"，实际 = {smokeChild.Xml}");
                return;
            }

            _api = api;
            _status = $"探针可用（{xiType.FullName}；registered={smoke.Registered}；"
                      + $"allowDuplicateNodesFieldNames={_allowDuplicateNodeFieldNames.Count} 个）";
        }
        catch (Exception ex)
        {
            _api = null;
            _status = $"探针不可用：{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static XmlDocument SmokeDocument()
    {
        var doc = new XmlDocument();
        doc.LoadXml(
            "<Defs>"
            + "<ThingDef Name=\"SynSmokeBase\" Abstract=\"True\"><defName>SynSmokeBase</defName>"
            + "<statBases><Mass>5</Mass></statBases></ThingDef>"
            + "<ThingDef ParentName=\"SynSmokeBase\"><defName>SynSmokeChild</defName></ThingDef>"
            + "</Defs>");
        return doc;
    }

    // ─────────────────────────── 公开入口 ───────────────────────────

    /// <summary>
    /// 解析一批**磁盘文件**（真语料抽样用）。
    /// 返回 null 表示探针不可用/引擎中途抛异常 —— 调用方**必须报错**，不许当成「没有差异」。
    /// </summary>
    public static Resolution? ResolveFiles(IReadOnlyList<string> files, out string? error)
    {
        EnsureInitialized();
        if (_api is null) { error = Status; return null; }

        var docs = new List<(string Label, XmlDocument Doc)>();
        var failures = new List<string>();
        foreach (var f in files)
        {
            try
            {
                var doc = new XmlDocument();
                doc.Load(f);
                docs.Add((f, doc));
            }
            catch (Exception ex)
            {
                failures.Add($"{f}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        var res = ResolveCore(docs, _api, out error);
        if (res is not null && failures.Count > 0)
            return new Resolution(res.Nodes, res.NameMap, res.Failures.Concat(failures).ToList(),
                res.Registered, res.SkippedMayRequire, res.SkippedDuplicateName,
                res.SkippedUnresolvable, res.SkippedDuplicateNodeName);
        return res;
    }

    /// <summary>解析一段内存里的 <c>&lt;Defs&gt;</c> 夹具（Step 1 的 8 条口径用）。</summary>
    public static Resolution? ResolveFixture(string fixtureXml, out string? error)
    {
        EnsureInitialized();
        if (_api is null) { error = Status; return null; }

        XmlDocument doc;
        try { doc = new XmlDocument(); doc.LoadXml(fixtureXml); }
        catch (Exception ex) { error = $"夹具 XML 不良构：{ex.GetType().Name}: {ex.Message}"; return null; }

        return ResolveCore(new[] { ("fixture", doc) }, _api, out error);
    }

    /// <summary>
    /// 把一段夹具交给引擎解析，返回 <c>&lt;defName&gt;</c> 等于 <paramref name="nodeName"/>
    /// （或 `Name` 属性等于它）的那个节点的**规范化** XML。找不到则返回 null。
    /// </summary>
    public static string? ResolveToXml(string fixtureXml, string nodeName, out string? error)
    {
        var res = ResolveFixture(fixtureXml, out error);
        if (res is null) return null;

        var hit = res.Nodes.FirstOrDefault(n =>
            string.Equals(n.DefName, nodeName, StringComparison.Ordinal)
            || string.Equals(FindNameAttr(n.Node), nodeName, StringComparison.Ordinal));
        if (hit is null)
        {
            error = $"夹具里找不到 defName/Name = {nodeName} 的顶层元素";
            return null;
        }
        return hit.Xml;
    }

    private static string? FindNameAttr(XmlNode n) => n.Attributes?["Name"]?.Value;

    // ─────────────────────────── 核心 ───────────────────────────

    private static Resolution? ResolveCore(
        IReadOnlyList<(string Label, XmlDocument Doc)> docs, Api api, out string? error)
    {
        error = null;
        try
        {
            api.Clear.Invoke(null, null);

            // ── 1) 收集顶层元素 + 建 Name 表（先过 MayRequire，再占 Name 槽位）────────────────
            var nameMap = new Dictionary<string, XmlNode>(StringComparer.Ordinal);
            var ordered = new List<(string Label, int Index, XmlNode Node, string? Skip)>();
            var skippedMayRequire = 0;
            var skippedDuplicateName = 0;

            foreach (var (label, doc) in docs)
            {
                var root = doc.DocumentElement;
                if (root is null) continue;
                for (var i = 0; i < root.ChildNodes.Count; i++)
                {
                    if (root.ChildNodes[i] is not XmlElement node) continue;

                    // ★ 顺序照引擎：`TryRegister` 的早退 `return` 在 `nodesByName.Add` **之前**，
                    //   所以被 MayRequire 挡掉的节点不占名字槽位。
                    string? skip = null;
                    if (node.GetAttributeNode("MayRequire") is not null
                        || node.GetAttributeNode("MayRequireAnyOf") is not null)
                    {
                        skippedMayRequire++;
                        skip = SkipReason.MayRequire;
                    }
                    else
                    {
                        var nameAttr = node.GetAttributeNode("Name");
                        if (nameAttr is not null && !nameMap.TryAdd(nameAttr.Value, node))
                        {
                            skippedDuplicateName++;
                            skip = SkipReason.DuplicateName;   // 同名先到者胜；引擎此处 Log.Error 后早退
                        }
                    }
                    ordered.Add((label, i, node, skip));
                }
            }

            // ── 2) 预测引擎的三条 Log.Error 路径，算出「可安全注册」的节点集 ────────────────
            var allowDup = _allowDuplicateNodeFieldNames ?? new HashSet<string>();
            var memo = new Dictionary<XmlNode, bool>();
            var visiting = new HashSet<XmlNode>();
            var skippedUnresolvable = 0;
            var skippedDupNodeName = 0;
            var registeredCount = 0;

            var final = new List<(string Label, int Index, XmlNode Node, string? Skip)>(ordered.Count);
            foreach (var (label, i, node, skip) in ordered)
            {
                if (skip is not null) { final.Add((label, i, node, skip)); continue; }

                var parentName = node.Attributes?["ParentName"]?.Value;
                // `CheckForDuplicateNodes` 只在**要解析的节点**（有 ParentName）上跑。
                if (!string.IsNullOrEmpty(parentName) && WouldTriggerDuplicateNodeName(node, allowDup))
                {
                    skippedDupNodeName++;
                    final.Add((label, i, node, SkipReason.DuplicateNodeName));
                    continue;
                }

                if (!IsSafelyResolvable(node, nameMap, allowDup, memo, visiting))
                {
                    skippedUnresolvable++;
                    final.Add((label, i, node, SkipReason.Unresolvable));
                    continue;
                }

                // ★ 参数显式转回 System.Xml.XmlNode 再 Invoke（C# 下本就是 XmlNode，这里显式化以防包装类型）。
                api.TryRegister.Invoke(null, new object?[] { (XmlNode)node, null });
                registeredCount++;
                final.Add((label, i, node, null));
            }

            api.Resolve.Invoke(null, null);

            // ── 3) 取结果。只对**注册过**的节点调 GetResolvedNodeFor（否则它自己也会 Log.Error）──
            var nodes = new List<ResolvedNode>(final.Count);
            foreach (var (label, index, node, skip) in final)
            {
                var parentName = node.Attributes?["ParentName"]?.Value;
                string? xml = null;
                if (skip is null)
                {
                    try
                    {
                        var r = (XmlNode?)api.GetResolvedNodeFor.Invoke(null, new object?[] { (XmlNode)node });
                        if (r is not null) xml = Normalize(r);
                    }
                    catch (Exception ex)
                    {
                        error ??= $"GetResolvedNodeFor 抛异常：{Unwrap(ex).Message}";
                    }
                }

                var defName = node.SelectSingleNode("defName")?.InnerText?.Trim();
                nodes.Add(new ResolvedNode(
                    label, index, node.Name, defName, parentName, node, xml,
                    ParentFound: !string.IsNullOrEmpty(parentName) && nameMap.ContainsKey(parentName!),
                    SkipReason: skip));
            }

            return new Resolution(nodes, nameMap, Array.Empty<string>(),
                registeredCount, skippedMayRequire, skippedDuplicateName,
                skippedUnresolvable, skippedDupNodeName);
        }
        catch (Exception ex)
        {
            var inner = Unwrap(ex);
            var frames = (inner.StackTrace ?? string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Take(4)
                .Select(s => s.Trim());
            error = $"{ex.GetType().Name}: {inner.Message}"
                    + (frames.Any() ? "\n            " + string.Join("\n            ", frames) : "");
            return null;
        }
    }

    /// <summary>
    /// 预测「该节点会不会被引擎判成解析不出来」——判成的话 `Resolve()` 末尾会打
    /// `Cyclic inheritance hierarchy detected`（那条文案同时覆盖真环与断链），离线必抛。
    ///
    /// 规则与引擎 `ResolveParentsAndChildNodesLinks` + `ResolveXmlNodesRecursively` 对齐：
    /// 父名必须在 `nodesByName` 里；父节点自己也必须能解析（链式传播）；
    /// 环上任何节点都解析不出来。
    /// ★ 还把 `CheckForDuplicateNodes` 的预测纳入了父节点判定 —— 父节点被排除时子节点也解析不出来。
    /// </summary>
    private static bool IsSafelyResolvable(
        XmlNode node, IReadOnlyDictionary<string, XmlNode> nameMap,
        HashSet<string> allowDup, Dictionary<XmlNode, bool> memo, HashSet<XmlNode> visiting)
    {
        if (memo.TryGetValue(node, out var cached)) return cached;
        if (!visiting.Add(node)) return false;              // 环 ⇒ 引擎判未解析

        try
        {
            var parentName = node.Attributes?["ParentName"]?.Value;
            bool result;
            if (string.IsNullOrEmpty(parentName))
            {
                result = true;                              // 无父 ⇒ 引擎 `resolvedXmlNode = xmlNode`
            }
            else if (!nameMap.TryGetValue(parentName!, out var parent))
            {
                result = false;                             // 父名不存在
            }
            else if (ReferenceEquals(parent, node))
            {
                result = false;                             // 自环
            }
            else if (!string.IsNullOrEmpty(parent.Attributes?["ParentName"]?.Value)
                     && WouldTriggerDuplicateNodeName(parent, allowDup))
            {
                // 父节点不注册 ⇒ 子节点也解析不出来。
                // ★ 这里必须复刻「`CheckForDuplicateNodes` 只在**要解析的节点**上跑」这一前提：
                //   无 `ParentName` 的父节点引擎从不做这个检查，对它套用会**多排除**一整支子树。
                result = false;
            }
            else
            {
                result = IsSafelyResolvable(parent, nameMap, allowDup, memo, visiting);
            }

            memo[node] = result;
            return result;
        }
        finally
        {
            visiting.Remove(node);
        }
    }

    /// <summary>
    /// 逐行复刻引擎 <c>CheckForDuplicateNodes</c>（它在 <c>ResolveXmlNodeFor</c> 里对**子节点自身**的
    /// 原始子树跑），返回「会不会打 `XML error: Duplicate XML node name ...`」。
    ///
    /// ★★ 必须照抄的引擎缺陷：`tempUsedNodeNames` 是**静态共享集合**，而每层递归开头 `Clear()` ——
    ///   于是**递归返回后**集合里装的是**子树的**名字，同一层后续的兄弟元素会拿它去做重复判定。
    ///   本方法因此**不改成正确实现**（否则会低估触发集合，`Resolve()` 照样抛异常）。
    /// </summary>
    private static bool WouldTriggerDuplicateNodeName(XmlNode node, HashSet<string> allowDup)
    {
        var temp = new HashSet<string>(StringComparer.Ordinal);
        return Walk(node, temp, allowDup, depth: 0);
    }

    private static bool Walk(XmlNode node, HashSet<string> temp, HashSet<string> allowDup, int depth)
    {
        if (depth > 200) return false;                      // 防御：正常 XML 不会这么深
        temp.Clear();
        var hit = false;
        foreach (XmlNode child in node.ChildNodes)
        {
            if (child.NodeType != XmlNodeType.Element) continue;
            if (!IsListElement(child, allowDup) && !temp.Add(child.Name)) hit = true;
            if (Walk(child, temp, allowDup, depth + 1)) hit = true;
        }
        return hit;
    }

    /// <summary>引擎 `IsListElement`：`&lt;li&gt;` 无条件；否则看**父元素名**是否在允许重复的字段名表里。</summary>
    private static bool IsListElement(XmlNode node, HashSet<string> allowDup)
    {
        if (node.Name == "li") return true;
        var parent = node.ParentNode;
        return parent is not null && allowDup.Contains(parent.Name);
    }

    private static Exception Unwrap(Exception ex) => ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;

    // ─────────────────────────── 规范化 ───────────────────────────

    /// <summary>
    /// 差分测试的规范化口径：**去掉纯空白文本节点**后重新序列化。
    ///
    /// 为什么必须做：引擎克隆父节点时会把源码里的缩进/换行文本节点一起带过来
    /// （`&lt;statBases&gt;\n  &lt;Mass&gt;` 里的 `\n  `），而「空白」在本语义下不承载信息；
    /// 不归一化会让逐字符比对被纯格式噪声淹没。
    /// ★ 两侧**必须共用**本方法（否则比对的是方法差异，不是合并语义差异）。
    /// </summary>
    public static string Normalize(XmlNode node)
    {
        var doc = new XmlDocument { PreserveWhitespace = false };
        var clone = doc.ImportNode(node, deep: true);
        doc.AppendChild(clone);
        StripWhitespace(clone);

        var sb = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = false,
            ConformanceLevel = ConformanceLevel.Document,
            NewLineHandling = NewLineHandling.None,
        };
        using (var w = XmlWriter.Create(sb, settings)) doc.WriteTo(w);
        return sb.ToString();
    }

    private static void StripWhitespace(XmlNode node)
    {
        for (var i = node.ChildNodes.Count - 1; i >= 0; i--)
        {
            var child = node.ChildNodes[i]!;
            if (child.NodeType == XmlNodeType.Whitespace
                || (child.NodeType == XmlNodeType.Text && string.IsNullOrWhiteSpace(child.Value)))
            {
                node.RemoveChild(child);
                continue;
            }
            if (child.NodeType == XmlNodeType.Element) StripWhitespace(child);
        }
    }
}
