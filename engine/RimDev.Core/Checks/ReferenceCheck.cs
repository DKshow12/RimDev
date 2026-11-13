using System.Text;
using System.Xml;
using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;
using RimDev.Core.Metadata;

namespace RimDev.Core.Checks;

/// <summary>
/// 一次引用解析的**实测统计**（`check --json` 的 `referenceResolution` 与 `sim` 的摘要都从这里取，
/// **CLI 绝不另算一遍** —— 与 Task 4 的 `patchLayer` / Task 5a 的 `parentResolution` 同一个理由：
/// 两套口径必然分叉，而「0 条」与「没跑」必须能区分开）。
///
/// ★ 计数分两类，**不能混用**（混用会得到自相矛盾的报告）：
///   · 以 <c>Scope</c> 结尾的 = 在**本次扫描集合**上统计（含被报告过滤器挡掉的部分）；
///   · <see cref="Reported"/> = 真正变成诊断（受 <c>TargetModPackageId</c> 过滤）。
/// </summary>
public sealed record ReferenceStats(
    int DeclarationsTraversed,
    int DeclarationsSkippedByMayRequire,
    int DeclarationsUnknownType,
    int ReferenceSites,
    int ResolvedExact,
    int ResolvedViaFullNameKey,
    int ResolvedViaAssignableTypes,
    int MissingInScope,
    int Reported,
    int CustomLoaderSubtreesSkipped,
    int UnknownFieldElements,
    int AbstractElementWithoutClass,
    int ClassDispatchHits,
    int ClassDispatchMisses,
    int ClassTypeMismatch,
    int ParityFailures,
    /// <summary>★ Task 6b Step 3：Abstract="True" 等「引擎从不实例化」的声明数（其引用不求值）。</summary>
    int AbstractDeclarationsSkipped,
    long ElapsedMs,
    IReadOnlyList<string>? CustomLoaderSamples = null)
{
    public string Describe() =>
        $"声明 {DeclarationsTraversed}（MayRequire 跳过 {DeclarationsSkippedByMayRequire}，类型未知 {DeclarationsUnknownType}）"
        + $" / 引用点 {ReferenceSites}（exact {ResolvedExact}，fullname {ResolvedViaFullNameKey}，"
        + $"抽象槽 {ResolvedViaAssignableTypes}，悬空 {MissingInScope}）"
        + $" / 边界：自定义 loader 子树 {CustomLoaderSubtreesSkipped}，未知字段元素 {UnknownFieldElements}，"
        + $"抽象无 Class= {AbstractElementWithoutClass} / Class= 派发 命中 {ClassDispatchHits} 未命中 {ClassDispatchMisses} 类型不匹配 {ClassTypeMismatch}"
        + $" / 归属键不一致文件 {ParityFailures}";
}

/// <summary>
/// 引用解析（第 3 期 Task 6）：把 XML 里**引用型字段**的值解析成 def 引用，查
/// <see cref="SimulatedDefSet"/>，未命中即 <c>RD3001</c>（error）。
///
/// ── 口径（`07` §6.1 的规则 A/B/C/D + 两条边界）────────────────────────────────────
/// <list type="bullet">
/// <item><b>哪些字段是引用</b>：不按字段名猜，也不平扫全程序集（那会拿到 80% 噪音）——
///   用 <see cref="ReferenceFieldIndex"/> 从 255 个 Def 类型沿字段类型做闭包得到的 1748 个引用槽。</item>
/// <item><b>XML 形状</b>：标量 `&lt;f&gt;defName&lt;/f&gt;`；`List/Set/数组` `&lt;f&gt;&lt;li&gt;defName&lt;/li&gt;`；
///   `Dictionary` `&lt;f&gt;&lt;li&gt;&lt;key&gt;/&lt;value&gt;&lt;/li&gt;`（key/value 各可能是一个引用槽）。</item>
/// <item><b>规则 D 多态</b>：元素代表的对象类型是**抽象**类时按该元素的 `Class=` 派发；
///   派发不到 ⇒ **工具边界**（计数、不再下钻，**绝不报 error**，与 Task 2b 的 `ClassTypeOf` 结论同源）。</item>
/// <item><b>边界 1 · 自定义 loader 类型（65 个）</b>：`LoadDataFromXmlCustom(XmlNode)` 决定 XML 形状，
///   反射推不出 ⇒ 进入即停、不展开，并把跳过的子树计数打进统计块（防「没检查」被读成「没问题」）。</item>
/// <item><b>边界 2 · 元素类型恰为基类 `Def`（4 处）</b>：目标 DefType 未知 ⇒ 按「名字在**任意** Def 类型下存在」判定，
///   抽象 Def 槽（`BuildableDef` ×7 / `StyleItemDef` ×1）同理放宽成「可赋值的任意 Def 类型」。</item>
/// <item><b>补丁</b>：本规则吃的是**未打补丁**的声明集合。补丁对 def 存在性/引用值的改动不在本期范围
///   （`07` §6.1 明确列为边界；`sim` 会把补丁层的实测统计与「顶层 def 节点 before→after」一并报出）。</item>
/// <item><b>不在本期范围</b>：属性形态（`soundCast="X"` —— 引擎 `GetAttribute` 调用 0 次，见 §6.0 第二条）、
///   texture 路径引用、C# 类型引用（`thingClass`/`workerClass`）。</item>
/// </list>
///
/// ── 报告过滤器 ──────────────────────────────────────────────────────────────────
/// 索引/解析/统计一律在**本次扫描集合**上做；只有**诊断**受 `ctx.TargetModPackageId` 过滤。
/// 与 <see cref="CheckContext"/> 的契约一致（否则 `check ThreatFleet` 会把 Core 的问题算到它头上）。
///
/// ── 归属键 ─────────────────────────────────────────────────────────────────────
/// 本规则**不复用** `FieldNameCheck.ScanDefs` 的第二次遍历：它自己在同一次流式读取里同时产出
/// 「depth==1 的 def 起始行」与「引用值」，因此不存在「按行号取到邻居」的形态。
/// 代价是必须自己做 parity 门禁：流式读到的 def 行序必须与 `ctx.Defs`（`DefScanner` 的产物）
/// **逐个相等**，不一致就报 RD2001(error) 并跳过该文件 —— **能静默错的地方，就要让它响亮地失败**。
/// </summary>
public sealed class ReferenceCheck : ICheck
{
    public string Id => Codes.DefNameNotFound;

    /// <summary>本次跑完的实测统计（null = 元数据不可用 / 没跑）。</summary>
    public ReferenceStats? LastStats { get; private set; }

    /// <summary>本次用到的有效 def 集合（`sim` 的摘要从这里取，**不另建一套**）。</summary>
    public SimulatedDefSet? LastSet { get; private set; }

    /// <summary>本次的引用字段空间（口径自算的产物，供 `sim --json` 报出）。</summary>
    public ReferenceFieldIndex? LastIndex { get; private set; }

    private sealed class Counters
    {
        public int Traversed, SkipMayRequire, UnknownType, Sites, Exact, FullName, Assignable, Missing;
        public int Reported, CustomSkipped, UnknownFields, AbstractNoClass, ClassHits, ClassMisses, ClassTypeMismatch, ParityFailures;
        /// <summary>★ Task 6b Step 3：`Abstract="True"` 等「引擎从不实例化」的声明数（其引用不求值）。</summary>
        public int AbstractSkipped;
        /// <summary>边界 1（自定义 loader）的**前若干条定位** —— 「每一项可定位到 文件:行」是 sim 的验收要求。</summary>
        public readonly List<string> CustomSamples = new();
    }

    public IEnumerable<Diagnostic> Run(CheckContext ctx)
    {
        ManagedAssemblyCatalog? catalog = null;
        try { catalog = ManagedAssemblyCatalog.Load(ReadManagedDir()); }
        catch { /* 元数据不可用时静默跳过（api/name 有独立命令覆盖该场景） */ }
        if (catalog is null) yield break;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var index = ReferenceFieldIndex.Build(catalog);
        if (index.DefType is null) yield break;

        // ★ 解析口径的唯一来源：InheritanceLayer.Analyze（Task 5a）——
        //   本规则不重新算 ParentName，也不重写合并语义。
        var analysis = InheritanceLayer.Analyze(ctx);
        var set = SimulatedDefSet.Build(ctx, analysis, catalog);
        var c = new Counters();
        var assignableMemo = new Dictionary<Type, IReadOnlyList<string>>();
        var frames = new Frame?[64];

        foreach (var group in ctx.Defs.GroupBy(d => d.FileAbs, StringComparer.OrdinalIgnoreCase))
        {
            // GroupBy 保留组内源顺序 = 文件内文档序（DefScanner 逐文件流式产出）。
            var defsInFile = group as IList<RawDef> ?? group.ToList();
            if (defsInFile.Count == 0) continue;

            var current = (RawDef?)null;
            var defIdx = 0;
            var parityOk = true;
            var pending = new List<Diagnostic>();
            Array.Clear(frames);

            try
            {
                using var stream = File.OpenRead(group.Key);
                using var reader = XmlReader.Create(stream, new XmlReaderSettings
                {
                    IgnoreComments = true,
                    IgnoreWhitespace = true,
                    DtdProcessing = DtdProcessing.Prohibit
                });
                var li = (IXmlLineInfo)reader;

                while (reader.Read())
                {
                    var line = li.HasLineInfo() ? li.LineNumber : 0;

                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        var depth = reader.Depth;
                        if (depth == 0) continue;                       // <Defs>
                        if (depth >= frames.Length) { frames[^1] = null; continue; }   // 防御：超深

                        if (depth == 1)
                        {
                            // parity 门禁：流式读到的 def 行序必须与 DefScanner 的逐个相等。
                            if (defIdx >= defsInFile.Count || defsInFile[defIdx].Line != line)
                            {
                                parityOk = false;
                                c.ParityFailures++;
                                break;
                            }
                            current = defsInFile[defIdx++];
                            c.Traversed++;

                            // ★★ 第 3 期 Task 6b Step 3：**非 def 的声明上的引用永不解析**。
                            //   引擎只实例化「非 Abstract 且有 defName（可继承）」的节点；
                            //   `RawDef.IsAbstract` 的口径 = `Abstract="True"` 或（有 Name= 且无 defName），
                            //   正好就是「引擎从不实例化」的那一类。官方先例：
                            //   `Data\Odyssey\Defs\TileMutators\Landmarks.xml` 的
                            //   `<LandmarkDef Abstract="True" Name="AbandonedColonyLandmarkBase">` 里写着
                            //   `<nameMaker>`，引擎从不解析它 ⇒ 照报就是假阳（`08` §4f.1 第 2 类）。
                            //   ★ 但**继承它的子 def** 带着同一引用时仍要查（那才是引擎会解析的地方）。
                            if (current.IsAbstract)
                            { c.AbstractSkipped++; frames[1] = null; continue; }
                            // ★ 资格判定走 InheritanceLayer.IsEligible（单一真源）。**不能**用 analysis.Get：
                            //   它只覆盖带 ParentName 的声明，会让「无 ParentName 但 MayRequire 未满足」的
                            //   声明被照常遍历（引擎连 def 都不建 ⇒ 它的引用不该被求值）。
                            if (!InheritanceLayer.IsEligible(current, analysis.ActivePackageIds))
                            { c.SkipMayRequire++; frames[1] = null; continue; }

                            var t = catalog.ResolveXmlTypeName(current.ResolvedTypeName);
                            if (t is null) { c.UnknownType++; frames[1] = null; continue; }
                            frames[1] = new Frame(t, line) { OwnerType = t.FullName ?? t.Name };
                            continue;
                        }

                        var parent = frames[depth - 1];
                        frames[depth] = parent is null ? null : ChildFrame(parent, reader, line, index, catalog, c, current!);
                        continue;
                    }

                    if (reader.NodeType == XmlNodeType.EndElement)
                    {
                        var depth = reader.Depth;
                        if (depth >= 1 && depth < frames.Length)
                        {
                            var f = frames[depth];
                            if (f is not null && f.SelfRefs.Count > 0 && current is not null)
                            {
                                var value = f.Text?.ToString().Trim();
                                if (!string.IsNullOrEmpty(value))
                                    foreach (var slot in f.SelfRefs)
                                    {
                                        var diag = BuildDiagnostic(slot, value, f.OwnerType, current, f.StartLine,
                                            index, set, assignableMemo, c);
                                        if (diag is null) continue;
                                        if (ctx.TargetModPackageId is not null
                                            && !string.Equals(current.ModPackageId, ctx.TargetModPackageId,
                                                StringComparison.OrdinalIgnoreCase)) continue;
                                        c.Reported++;
                                        pending.Add(diag);
                                    }
                            }
                            frames[depth] = null;
                        }
                        continue;
                    }

                    // 文本节点：归属它的元素在 depth-1（XmlReader 的 Text 深度 = 所属元素深度 + 1）
                    if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
                    {
                        var pd = reader.Depth - 1;
                        if (pd >= 1 && pd < frames.Length) frames[pd]?.Text?.Append(reader.Value);
                        continue;
                    }
                }
            }
            catch (XmlException) { /* 解析失败由 RD2001 报；本规则不重复报 */ }
            catch (IOException) { /* 同上：读不了的文件不该让整条规则炸掉 */ }

            // ★ `yield return` 不能在带 catch 的 try 里（CS1626）：本文件的诊断先收进 pending，
            //   出了 try 再逐个交出。语义不变 —— 顺序仍是「文件内文档序、def 内行序」。
            foreach (var diag in pending) yield return diag;

            if (!parityOk || defIdx != defsInFile.Count)
            {
                yield return new Diagnostic(Codes.XmlParseFailed, Severity.Error, Scope.Offline,
                    $"def 归属键与 DefScanner 的 def 集合不一致（流式读到 {defIdx} / DefScanner {defsInFile.Count}），"
                    + "本文件的引用解析被跳过以免把邻居 def 的引用算到本 def 头上",
                    "这是本工具的边界错误：两侧都按 depth==1 判定 def，本应给出同一集合；把该文件与出现次数报给工具作者",
                    FileAbs: group.Key,
                    Line: defsInFile[0].Line,
                    ModPackageId: defsInFile[0].ModPackageId,
                    Evidence: new Dictionary<string, string>
                    {
                        ["streamDefs"] = defIdx.ToString(),
                        ["rawDefs"] = defsInFile.Count.ToString(),
                        ["firstMismatchRawLine"] = defIdx < defsInFile.Count ? defsInFile[defIdx].Line.ToString() : "(none)",
                    });
            }
        }

        sw.Stop();
        LastIndex = index;
        LastSet = set;
        LastStats = new ReferenceStats(
            DeclarationsTraversed: c.Traversed,
            DeclarationsSkippedByMayRequire: c.SkipMayRequire,
            DeclarationsUnknownType: c.UnknownType,
            ReferenceSites: c.Sites,
            ResolvedExact: c.Exact,
            ResolvedViaFullNameKey: c.FullName,
            ResolvedViaAssignableTypes: c.Assignable,
            MissingInScope: c.Missing,
            Reported: c.Reported,
            CustomLoaderSubtreesSkipped: c.CustomSkipped,
            UnknownFieldElements: c.UnknownFields,
            AbstractElementWithoutClass: c.AbstractNoClass,
            ClassDispatchHits: c.ClassHits,
            ClassDispatchMisses: c.ClassMisses,
            ClassTypeMismatch: c.ClassTypeMismatch,
            ParityFailures: c.ParityFailures,
            AbstractDeclarationsSkipped: c.AbstractSkipped,
            ElapsedMs: sw.ElapsedMilliseconds,
            CustomLoaderSamples: c.CustomSamples);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // 遍历帧
    // ────────────────────────────────────────────────────────────────────────────

    /// <summary>一个 XML 元素在**类型图**上的位置与它承载的引用槽。</summary>
    private sealed class Frame
    {
        public Frame(Type? current, int startLine)
        {
            Current = current;
            StartLine = startLine;
        }

        /// <summary>该元素代表的对象类型（null = 边界：类型未知/抽象无 Class=/自定义 loader）。</summary>
        public Type? Current { get; set; }
        public int StartLine { get; }
        /// <summary>声明这个字段的 owner 类型全名（诊断证据用）。</summary>
        public string OwnerType { get; init; } = "?";
        /// <summary>本元素的**文本**就是一个引用值（标量引用；字典的 key/value 走这里）。</summary>
        public IReadOnlyList<RefSlot> SelfRefs { get; init; } = Array.Empty<RefSlot>();
        /// <summary>本元素是集合（`List/Set/数组`），它的每个 `&lt;li&gt;` 都是引用值。</summary>
        public bool IsCollection { get; init; }
        public Type? ElementType { get; init; }
        public IReadOnlyList<RefSlot> ChildRefs { get; init; } = Array.Empty<RefSlot>();
        /// <summary>本元素是字典（它的 `&lt;li&gt;` 是 key/value 条目）。</summary>
        public bool IsDict { get; init; }
        public bool IsDictEntry { get; init; }
        public Type? KeyType { get; init; }
        public Type? ValueType { get; init; }
        public IReadOnlyList<RefSlot> KeyRefs { get; init; } = Array.Empty<RefSlot>();
        public IReadOnlyList<RefSlot> ValueRefs { get; init; } = Array.Empty<RefSlot>();
        public StringBuilder? Text { get; } = new();
    }

    private static Frame? ChildFrame(Frame parent, XmlReader reader, int line, ReferenceFieldIndex index,
        ManagedAssemblyCatalog catalog, Counters c, RawDef def)
    {
        var name = reader.Name;
        // 边界定位用（sim 的「每一项可定位到 文件:行」）
        var loc = $"{def.FileRelToMod}:{line}";

        // ① 集合元素：`<f><li>…</li></f>`
        if (parent.IsCollection)
        {
            // ★ 形状差异：集合元素类型带自定义 XML loader 时，XML 用的是**带名元素**而不是 `<li>`
            //   （实证：`PawnKindDef.skills` 是 `List<SkillRange>`，而 `SkillRange.LoadDataFromXmlCustom`
            //   直接把元素名当 SkillDef 用 —— `<skills><Shooting>14~18</Shooting></skills>`）。
            //   这类形状推不出来 ⇒ 归入**边界 1**（自定义 loader）并计数，而不是「未知字段」。
            if (name != "li")
            {
                if (parent.ElementType is not null && ManagedAssemblyCatalog.HasCustomXmlLoader(parent.ElementType))
                { c.CustomSkipped++; if (c.CustomSamples.Count < 16) c.CustomSamples.Add($"{loc} {parent.ElementType.Name}"); }
                else c.UnknownFields++;
                return null;
            }
            var et = parent.ElementType;
            if (et is null) { c.AbstractNoClass++; return null; }
            var f = new Frame(et, line) { OwnerType = parent.OwnerType, SelfRefs = parent.ChildRefs };
            // 引用槽非空 ⇒ 这个 li 的文本就是引用值，不需要再下钻（也避免抽象类型误报边界）
            return parent.ChildRefs.Count > 0 ? f : Dispatch(f, reader, catalog, c, loc);
        }

        // ② 字典条目：`<f><li><key>…</key><value>…</value></li></f>`
        if (parent.IsDict)
        {
            if (name != "li") { c.UnknownFields++; return null; }   // 字典条目必须是 `<li>`
            return new Frame(null, line)
            {
                OwnerType = parent.OwnerType,
                IsDictEntry = true,
                KeyType = parent.KeyType,
                ValueType = parent.ValueType,
                KeyRefs = parent.KeyRefs,
                ValueRefs = parent.ValueRefs,
            };
        }

        // ③ 字典条目的 key / value
        if (parent.IsDictEntry)
        {
            if (name == "key")
                return parent.KeyRefs.Count > 0
                    ? new Frame(parent.KeyType, line) { OwnerType = parent.OwnerType, SelfRefs = parent.KeyRefs }
                    : Dispatch(new Frame(parent.KeyType, line) { OwnerType = parent.OwnerType }, reader, catalog, c, loc);
            if (name == "value")
                return parent.ValueRefs.Count > 0
                    ? new Frame(parent.ValueType, line) { OwnerType = parent.OwnerType, SelfRefs = parent.ValueRefs }
                    : Dispatch(new Frame(parent.ValueType, line) { OwnerType = parent.OwnerType }, reader, catalog, c, loc);
            c.UnknownFields++;
            return null;
        }

        // ④ 普通对象的字段
        if (parent.Current is null) return null;
        var shape = index.ShapeOf(parent.Current);
        if (shape is null) { c.UnknownFields++; return null; }          // 不可达/自定义 loader 类型
        if (!shape.Fields.TryGetValue(name, out var fi)) { c.UnknownFields++; return null; }

        var ft = fi.FieldType;
        var kind = ReferenceFieldIndex.KindOf(ft);
        var refs = shape.Refs.TryGetValue(name, out var r) ? r : (IReadOnlyList<RefSlot>)Array.Empty<RefSlot>();

        switch (kind)
        {
            case RefContainerKind.Scalar:
            {
                var f = new Frame(ft, line) { OwnerType = parent.Current.FullName ?? parent.Current.Name, SelfRefs = refs };
                return refs.Count > 0 ? f : Dispatch(f, reader, catalog, c, loc);
            }
            case RefContainerKind.List:
            case RefContainerKind.Set:
            case RefContainerKind.Array:
                return new Frame(null, line)
                {
                    OwnerType = parent.Current.FullName ?? parent.Current.Name,
                    IsCollection = true,
                    ElementType = ReferenceFieldIndex.ElementTypeOf(ft),
                    ChildRefs = refs,
                };
            default:
            {
                var args = ft.GetGenericArguments();
                var keyRefs = refs.Where(x => x.IsDictionaryKey).ToList();
                var valRefs = refs.Where(x => !x.IsDictionaryKey).ToList();
                return new Frame(null, line)
                {
                    OwnerType = parent.Current.FullName ?? parent.Current.Name,
                    IsDict = true,
                    KeyType = args.Length > 0 ? args[0] : null,
                    ValueType = args.Length > 1 ? args[1] : null,
                    KeyRefs = keyRefs,
                    ValueRefs = valRefs,
                };
            }
        }
    }

    /// <summary>
    /// 规则 D：元素代表的对象类型是抽象类时按 `Class=` 派发。派发不到 ⇒ 工具边界
    /// （计数 + 停止下钻，**绝不报 error**）。
    /// </summary>
    private static Frame Dispatch(Frame f, XmlReader reader, ManagedAssemblyCatalog catalog, Counters c,
        string location)
    {
        // 边界 1：自定义 XML loader 类型（65 个）—— XML 形状由 loader 代码决定，反射推不出 ⇒ 进入即停。
        //   这里同时把「进入了多少个这样的子树」计数（防「没检查」被读成「没问题」）。
        if (f.Current is not null && ManagedAssemblyCatalog.HasCustomXmlLoader(f.Current))
        {
            c.CustomSkipped++;
            if (c.CustomSamples.Count < 16) c.CustomSamples.Add($"{location} {f.Current.Name}");
            f.Current = null;
            return f;
        }
        // ★★ 规则 D 的口径：**只看 `Class=`，不看「声明类型是否抽象」** ——
        //   引擎 `Verse.DirectXmlToObject.ClassTypeOf<T>` 就是无条件读 `Class=`。
        //   实证：官方内容里有 **1643 条** `<li Class="CompProperties...">`，而
        //   `Verse.CompProperties` **不是抽象类**（反射实测 IsAbstract=False）——
        //   只对抽象类型派发会把这一整批（以及它们内部的引用）全部看不见。
        var declared = f.Current;
        var cls = reader.GetAttribute("Class");
        if (cls is not null)
        {
            var t = catalog.ResolveXmlTypeName(cls);
            if (t is not null)
            {
                if (declared is null || declared.IsAssignableFrom(t))
                {
                    c.ClassHits++;
                    f.Current = t;
                    return f;
                }
                // `Class=` 解析到了、但与声明类型**不可赋值**（例：`<li Class="Verb_Shoot">` 落在
                // `List<VerbProperties>` 上 —— 官方 0 例，是 mod 写法）。引擎会在实例化处失败；
                // 我们**按声明类型继续**（保住字段覆盖），并把这条计入边界（两个方向都要可见）。
                c.ClassTypeMismatch++;
                if (declared.IsAbstract) { c.AbstractNoClass++; f.Current = null; }
                return f;
            }
            c.ClassMisses++;
            f.Current = null;
            return f;
        }
        if (declared is null || !declared.IsAbstract) return f;
        c.AbstractNoClass++;
        f.Current = null;
        return f;
    }

    // ────────────────────────────────────────────────────────────────────────────
    // 判定与诊断
    // ────────────────────────────────────────────────────────────────────────────

    private static Diagnostic? BuildDiagnostic(RefSlot slot, string value, string ownerType, RawDef def, int line,
        ReferenceFieldIndex index, SimulatedDefSet set, Dictionary<Type, IReadOnlyList<string>> memo, Counters c)
    {
        c.Sites++;
        var target = slot.TargetDefType;
        var defType = index.DefType!;

        // ★★ 两段式判定 —— 第二段是**引擎真值**，不是放宽：
        //   `Verse.DefDatabase<T>.AddAllInMods` 把**全部 def**里满足 `def is T` 的收进 `DefDatabase<T>`
        //   （IL 实证：遍历 `LoadedModManager.PatchedDefsForReading` + `isinst T`），
        //   所以 `List<CreepJoinerBaseDef>` 这种**具体基类**字段照样能引用**子类**的 def。
        //   实证（本机官方 Anomaly）：`CreepJoinerFormKindDef.excludes/requires` 声明为
        //   `List<Verse.CreepJoinerBaseDef>`，而 `<li>` 里写的 116 个名字全是
        //   `CreepJoinerBenefit\|Aggressive\|Downside` Def（`CreepJoinerBaseDef` 的子类）——
        //   只做精确类型匹配会把它们**全部误报**成悬空（本 Task 第一版实测 RD3001 = 116，全是假阳）。
        //   归档游戏日志（`logs\\archive\\20261014-1710-Player*.log`）里对这 116 条**零命中**
        //   —— 引擎自己认，所以我们也认。
        IReadOnlyList<string> keys;
        bool ok;
        string basis;

        var exact = SimulatedDefSet.CandidateTypeKeys(target);
        if (set.Contains(exact[0], value)) { ok = true; keys = exact; basis = "exact-type"; c.Exact++; }
        else if (exact.Count > 1 && set.Contains(exact[1], value)) { ok = true; keys = exact; basis = "full-name"; c.FullName++; }
        else
        {
            keys = AssignableKeys(target, index, memo);
            if (set.ContainsAnyOf(keys, value))
            {
                ok = true;
                basis = target == defType ? "any-def-type" : "assignable-subtype";
                c.Assignable++;
            }
            else { ok = false; basis = "exact-type"; c.Missing++; }
        }

        if (ok) return null;

        var suggestions = set.Suggest(keys, value, 5);
        var alsoKnown = set.TypesContaining(value, 5);

        var hint = suggestions.Count > 0
            ? $"最相近的候选：{string.Join(" / ", suggestions)}"
            : "该 DefType 下没有相近的名字";
        if (alsoKnown.Count > 0)
            hint += $"；这个名字在别的 DefType 下存在：{string.Join(" / ", alsoKnown)}（字段声明的 Def 类型可能不对）";

        return new Diagnostic(
            Codes.DefNameNotFound, Severity.Error, Scope.Offline,
            $"引用悬空：{ownerType}.{slot.FieldName} 指向不存在的 {target.Name} \"{value}\"",
            hint,
            FileAbs: def.FileAbs, Line: line,
            ModPackageId: def.ModPackageId, DefName: def.DefName, DefType: def.DefType,
            Suggestions: suggestions,
            Evidence: new Dictionary<string, string>
            {
                ["referenceAt"] = $"{def.FileRelToMod}:{line}",
                ["targetDefType"] = target.Name,
                ["targetDefName"] = value,
                ["ownerType"] = ownerType,
                ["field"] = slot.FieldName,
                ["container"] = slot.Kind.ToString().ToLowerInvariant(),
                ["lookupBasis"] = basis,
                ["candidateKeys"] = string.Join("|", keys),
                ["nearest"] = string.Join(" ", suggestions),
                ["sameNameOtherDefTypes"] = string.Join(" ", alsoKnown),
                ["declarationInScope"] = set.DeclarationCount(target.Name, value).ToString(),
            });
    }

    /// <summary>
    /// 「可赋值的任意 Def 类型」的候选键（槽类型是基类 `Def` 或抽象 Def 时的口径）。
    /// 这是**有意放宽**的方向：宁可漏报（少报），也不要把「名字确实存在于某个具体 Def 类型下」报成悬空。
    /// </summary>
    private static IReadOnlyList<string> AssignableKeys(Type broad, ReferenceFieldIndex index,
        Dictionary<Type, IReadOnlyList<string>> memo)
    {
        if (memo.TryGetValue(broad, out var cached)) return cached;
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        // 声明类型自身（含「恰为基类 Def」与抽象 Def 槽）
        if (index.DefType is { } dt && dt.IsAssignableFrom(broad))
        {
            keys.Add(broad.Name);
            if (broad.FullName is { } self) keys.Add(self);
        }
        foreach (var t in index.DefDerivedTypes)
        {
            if (!broad.IsAssignableFrom(t)) continue;
            keys.Add(t.Name);
            if (t.FullName is { } full) keys.Add(full);
        }
        var list = (IReadOnlyList<string>)keys.ToList();
        memo[broad] = list;
        return list;
    }

    private static string ReadManagedDir() =>
        RimDev.Core.Environment.GameEnvProbe.Discover().ManagedDir;
}
