using System.Reflection;
using RimDev.Core.Metadata;

namespace RimDev.Core.Defs;

/// <summary>
/// 一个引用字段的**容器形态**（第 3 期 Task 6 规则 A/B/C 的判别结果）。
/// 它同时决定 XML 的形状（&lt;field&gt; 值 / &lt;field&gt;&lt;li&gt; / &lt;li&gt;&lt;key&gt;）与 `sim` 摘要里的分桶。
/// </summary>
public enum RefContainerKind { Scalar, List, Set, Array, Dictionary }

/// <summary>
/// 一个**引用槽**：XML 里某个元素（或 `&lt;li&gt;` / `&lt;key&gt;` / `&lt;value&gt;`）的内容是一串 defName，
/// 目标 Def 类型就是 <see cref="TargetDefType"/>。
///
/// ★ 为什么是「槽」不是「字段」：`Dictionary&lt;ThingDef, ThingDef&gt;` 这类字段一个字段有两个槽
///   （key 与 value 各是一个引用），而计划 6.0 表里的 1748 是**按 (owner, field) 对**数的
///   （一个字段只算一次）—— 两个口径必须分开报，混用会得到差 1 倍的数字。
/// </summary>
public sealed record RefSlot(
    string FieldName,
    RefContainerKind Kind,
    Type FieldType,
    Type TargetDefType,
    bool IsDictionaryKey);

/// <summary>
/// 一个可达类型的形状：全部实例字段（用于顺着字段下钻）+ 其中的引用槽。
/// 下钻用的字段表与 <see cref="FieldReflection.FieldsByName"/> 同源（沿 BaseType 求并集、含 private）。
/// </summary>
public sealed class TypeShape
{
    internal TypeShape(Type type, int depth, bool isDefDerived,
        IReadOnlyDictionary<string, FieldInfo> fields,
        IReadOnlyDictionary<string, IReadOnlyList<RefSlot>> refs,
        IReadOnlyList<RefSlot> allRefSlots)
    {
        Type = type;
        Depth = depth;
        IsDefDerived = isDefDerived;
        Fields = fields;
        Refs = refs;
        AllRefSlots = allRefSlots;
    }

    public Type Type { get; }
    /// <summary>沿字段类型从 Def 出发的「跨抽象边界」次数（DFS 口径：首次访问即定型；0 = 路径上没有抽象类型）。</summary>
    public int Depth { get; }
    public bool IsDefDerived { get; }
    /// <summary>全部实例字段（含基类 private），键 = XML 元素名。</summary>
    public IReadOnlyDictionary<string, FieldInfo> Fields { get; }
    /// <summary>字段名 → 该字段承载的引用槽（字典字段可能 2 个）。非引用字段不在表里。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<RefSlot>> Refs { get; }
    public IReadOnlyList<RefSlot> AllRefSlots { get; }
}

/// <summary>
/// 引用字段空间（第 3 期 Task 6 Step 1）。
///
/// ★ 口径（与 `07` §6.0 表逐项对齐；本节每个数字都由**本实现**复算并打印，不抄任务书）：
///   从 `Verse.Def` 的**全部派生类型**（255）出发，沿**字段类型**做一次闭包遍历：
///     <list type="bullet">
///     <item>`List&lt;T&gt;` / `HashSet&lt;T&gt;` / `T[]` / `Dictionary&lt;K,V&gt;` 一律解包（规则 B/C）；</item>
///     <item>字段类型是 Def 派生（含基类 `Def` 本身）⇒ 这是一个**引用槽**（规则 A）；</item>
///     <item>字段类型是**抽象类** ⇒ 计入「跨抽象边界」（规则 D：该处 XML 需要 `Class=` 派发）；</item>
///     <item>字段类型带**自定义 XML loader**（`LoadDataFromXmlCustom(XmlNode)` 实例方法，全程序集 65 个）
///       ⇒ **进入即停、不展开**（边界 1：XML 形状由 loader 代码决定，反射推不出）；</item>
///     <item>`System*` / `Unity*` / `UnityEngine*` / `Microsoft*` / 接口 / 指针 / 基元 / 枚举 / string
///       ⇒ 不可能承载 XML 子元素，跳过。</item>
///     </list>
///
/// ★★ 必踩陷阱（任务书 6.0 预警、本实现刻意规避）：**绝不能用「字符串形态的类型名匹配」判断泛型**。
///   PowerShell 的 ``-like 'System.Collections.Generic.List`1*'`` 会把反引号当转义符吃掉 ⇒ ``List`1``
///   **永不匹配** ⇒ 所有 `List&lt;T&gt;` 字段被静默跳过（引用空间 1748 → 580 量级、「嵌套 owner」变成 0），
///   而且**不报错、退出码 0**。C# 侧用 `field.FieldType.GetGenericTypeDefinition() == typeof(List&lt;&gt;)`
///   天然安全（见 <see cref="KindOf"/>）。
///
/// ★ 深度口径（`1040 / 148` 这一行）：本类**照抄主理人侦察脚本的 DFS**（栈 + 首次访问即定型，
///   而不是「最小深度 BFS」）。两种遍历会给出不同的 depth 分布（见 <see cref="MinDepthCrossingTypeCount"/> 的说明），
///   所以 `--task6-probe` 会把两套口径都打出来，避免「数字一样」掩盖口径差异。
/// </summary>
public sealed class ReferenceFieldIndex
{
    private readonly Dictionary<Type, TypeShape> _shapes = new();
    /// <summary>
    /// **只在 `Class=` 派发时才见到的类型**的形状（按需补算）。
    /// 为什么必需：可达集只覆盖「沿字段类型能走到的类型」，而 `<li Class="CompProperties_Spawner">`
    /// 这种节点代表的具体类型**不在**任何字段类型里（字段声明的是抽象/基类 CompProperties）。
    /// 不补算 ⇒ `ShapeOf` 返回 null ⇒ 派发成功但「一看字段就停」⇒ 那批引用被**静默少报**。
    /// </summary>
    private readonly Dictionary<Type, TypeShape> _extraShapes = new();
    private HashSet<Type>? _derived;

    private ReferenceFieldIndex(ManagedAssemblyCatalog catalog, Type? defType)
    {
        Catalog = catalog;
        DefType = defType;
    }

    public ManagedAssemblyCatalog Catalog { get; }
    public Type? DefType { get; }

    // ── 6.0 表第 1、2 行 ────────────────────────────────────────────────────────────
    public int DefDerivedTypeCount => DefDerivedTypes.Count;
    public int CustomLoaderTypeCount => CustomLoaderTypes.Count;
    /// <summary>`Verse.Def` 的全部派生类型（不含 `Def` 自身）—— 「基类 Def / 抽象 Def 槽」的口径要它。</summary>
    public IReadOnlyCollection<Type> DefDerivedTypes { get; private init; } = Array.Empty<Type>();
    /// <summary>带自定义 XML loader 的类型（边界 1；引用解析进入即停）。</summary>
    public IReadOnlyCollection<Type> CustomLoaderTypes { get; private init; } = Array.Empty<Type>();


    // ── 6.0 表第 3–6 行：可达类型 ──────────────────────────────────────────────────
    public int ReachableTypeCount { get; private init; }
    /// <summary>路径上**没有**抽象类型（DFS 口径）。</summary>
    public int DepthZeroTypeCount { get; private init; }
    /// <summary>路径上跨 ≥1 个抽象类型（DFS 口径）。</summary>
    public int DepthPositiveTypeCount { get; private init; }
    public int AbstractBoundaryTypeCount { get; private init; }
    public int ContainerEdgeCount { get; private init; }

    /// <summary>
    /// ★ 同一条口径的**另一种遍历**（单调最小深度 BFS）下「跨 ≥1 抽象边界」的类型数 ——
    /// 只作对照，不参与任何判定。两个数不同**不是** bug：DFS 的「首次访问即定型」会让
    /// 先经深路径访问到的类型保留较大 depth（其子节点也随之偏移），BFS 给的才是最小深度。
    /// 把两个数并列打出来，是为了让「1040/148 到底量的是什么」可见。
    /// </summary>
    public int MinDepthCrossingTypeCount { get; private init; }
    public int MinDepthNonCrossingTypeCount { get; private init; }

    // ── 6.0 表第 7–10 行：可达引用字段 ────────────────────────────────────────────
    /// <summary>有 ≥1 个 Def 引用槽的 (owner, field) 对（**字段**口径，不是槽口径）。</summary>
    public int ReachableRefFieldCount { get; private init; }
    public int DirectOwnerFieldCount { get; private init; }
    public int NestedNoPolyFieldCount { get; private init; }
    public int NestedPolyFieldCount { get; private init; }
    /// <summary>同上的**槽**口径（同一个字段有 2 个 Def 槽时算 2）。</summary>
    public int ReachableRefSlotCount { get; private init; }
    public IReadOnlyDictionary<RefContainerKind, int> FieldCountByKind { get; private init; }
        = new Dictionary<RefContainerKind, int>();

    // ── 6.0 表第 11–13 行：边界与对照 ─────────────────────────────────────────────
    /// <summary>
    /// 全程序集（16,157 个类型）里这类引用**槽**的总数 —— 可达只占约 20%（平扫全字段 = 80% 噪音）。
    ///
    /// ★★ 与任务书 6.0 表的差异（**实测 8685 vs 任务书写 8733**，差 48）：任务书的 8733 来自侦察脚本的
    ///   **宽松口径** —— 它用 `FullName.StartsWith("System.Collections.Generic.List")` 判容器，于是把
    ///   `<List&lt;T&gt;+Enumerator` / `<Dictionary&lt;K,V&gt;+Enumerator` 这些**编译器生成的迭代器闭包字段**
    ///   （`&lt;&gt;7__wrap1` 之类）也算成了 `List&lt;T&gt;`，那些字段的内容是枚举器结构体，**永远不可能出现在
    ///   RimWorld 的 XML 里**。本实现用任务书自己指定的 C# 口径（`GetGenericTypeDefinition() == typeof(List&lt;&gt;)`）
    ///   ⇒ 8685 才是对的；48 条差异已在 `_tmp\gate6\probe-ref-slots2.ps1` 里逐条列出（两类口径并列对账）。
    ///   可达范围内的 1748 等数字**不受影响**（那些闭包类型不在从 Def 出发的可达集里）。
    /// </summary>
    public int WholeAssemblyRefSlotCount { get; private init; }
    /// <summary>自定义 loader 类型**自身**的引用槽（反射推不出形状 ⇒ 是刻意的少报方向）。</summary>
    public int CustomLoaderOwnRefSlotCount { get; private init; }
    /// <summary>元素类型恰为基类 `Def` 的引用槽数（目标 DefType 未知，需「任意 Def 类型下」查）。</summary>
    public int DefItselfSlotCount { get; private init; }
    /// <summary>上一条的逐条定位（owner/字段/形态），供人工判定。</summary>
    public IReadOnlyList<string> DefItselfSlots { get; private init; } = Array.Empty<string>();
    /// <summary>槽类型是抽象 Def 派生（非 `Def` 本身）的引用槽 —— 目标 DefType 是抽象类，需要「可赋值的任意 Def 类型」口径。</summary>
    public IReadOnlyList<string> AbstractDefSlotSlots { get; private init; } = Array.Empty<string>();

    public IReadOnlyCollection<TypeShape> Shapes => _shapes.Values;
    public TypeShape? ShapeOf(Type t)
    {
        if (_shapes.TryGetValue(t, out var s)) return s;
        if (_extraShapes.TryGetValue(t, out var e)) return e;
        if (DefType is null || _derived is null) return null;
        // 边界 1：自定义 XML loader 类型，形状由 loader 代码决定 ⇒ 不看字段（返回 null，由调用方计数）。
        if (ManagedAssemblyCatalog.HasCustomXmlLoader(t)) return null;
        var shape = BuildShape(t, int.MaxValue, DefType, _derived);
        _extraShapes[t] = shape;
        return shape;
    }

    // ────────────────────────────────────────────────────────────────────────────

    public static ReferenceFieldIndex Build(ManagedAssemblyCatalog catalog)
    {
        var defType = catalog.Find("Verse.Def") ?? catalog.FindBySimpleName("Def");
        if (defType is null) return new ReferenceFieldIndex(catalog, null);
        var shapes = new Dictionary<Type, TypeShape>();

        var all = catalog.AllTypes;

        // ① Def 派生类型（**含顺序**：DFS 复刻要按 `asm.GetTypes()` 的原始顺序压栈）
        var derived = new HashSet<Type>();
        var derivedOrder = new List<Type>();
        foreach (var t in all)
            if (t != defType && defType.IsAssignableFrom(t)) { derived.Add(t); derivedOrder.Add(t); }

        // ② 自定义 XML loader 类型
        var custom = new HashSet<Type>();
        foreach (var t in all)
            if (ManagedAssemblyCatalog.HasCustomXmlLoader(t)) custom.Add(t);

        // ③ 闭包遍历（**照抄侦察脚本的 DFS**：LIFO 栈 + 访问过就不再重扫）
        var reach = new Dictionary<Type, int>();
        var stack = new Stack<Type>();
        var visited = new HashSet<Type>();
        foreach (var d in derivedOrder) { reach[d] = 0; stack.Push(d); }

        var abstractBoundaries = new HashSet<Type>();
        long edges = 0;
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            if (!visited.Add(t)) continue;
            if (custom.Contains(t)) continue;                 // 边界 1：进入即停、不展开
            var d = reach[t];
            foreach (var f in FieldReflection.FieldsByName(t).Values)
                foreach (var slot in SlotsOf(f.FieldType))
                {
                    if (slot is null) continue;
                    if (IsRefLeaf(slot, defType, derived)) continue;
                    if (slot.IsInterface || slot.IsPointer) continue;
                    if (slot.IsPrimitive || slot.IsEnum || slot == typeof(string) || slot == typeof(decimal)) continue;
                    var ns = slot.Namespace;
                    if (ns is null) continue;
                    if (ns.StartsWith("System", StringComparison.Ordinal)
                        || ns.StartsWith("UnityEngine", StringComparison.Ordinal)
                        || ns.StartsWith("Unity", StringComparison.Ordinal)
                        || ns.StartsWith("Microsoft", StringComparison.Ordinal)) continue;

                    edges++;
                    var nd = d;
                    if (slot.IsAbstract) { nd = d + 1; abstractBoundaries.Add(slot); }
                    if (!reach.ContainsKey(slot)) { reach[slot] = nd; stack.Push(slot); }
                    else if (reach[slot] > nd) { reach[slot] = nd; stack.Push(slot); }
                }
        }

        // ④ 形状表 + 引用槽统计（按 (owner, field) 计一次；槽数另算）
        var byKind = new Dictionary<RefContainerKind, int>();
        int direct = 0, noPoly = 0, poly = 0, refSlots = 0;
        var defItselfSlots = new List<string>();
        var abstractDefSlots = new List<string>();
        foreach (var (t, depth) in reach)
        {
            var shape = BuildShape(t, depth, defType, derived, defItselfSlots, abstractDefSlots);
            shapes[t] = shape;
            refSlots += shape.AllRefSlots.Count;
            foreach (var fieldSlots in shape.Refs.Values)
            {
                byKind[fieldSlots[0].Kind] = byKind.GetValueOrDefault(fieldSlots[0].Kind) + 1;
                if (derived.Contains(t)) direct++;
                else if (depth == 0) noPoly++;
                else poly++;
            }
        }

        // ⑤ 对照：全程序集 / 自定义 loader 自身 的**槽**数
        int wholeAssembly = 0;
        foreach (var t in all)
            foreach (var f in FieldReflection.FieldsByName(t).Values)
                foreach (var slot in SlotsOf(f.FieldType))
                    if (slot is not null && IsRefLeaf(slot, defType, derived)) wholeAssembly++;

        int customOwn = 0;
        foreach (var t in custom)
            foreach (var f in FieldReflection.FieldsByName(t).Values)
                foreach (var slot in SlotsOf(f.FieldType))
                    if (slot is not null && IsRefLeaf(slot, defType, derived)) customOwn++;

        // ⑥ 最小深度 BFS（对照口径，只报数）
        var minDepth = MinDepths(defType, derived, custom, reach.Keys);
        int crossing = 0, nonCrossing = 0;
        foreach (var t in reach.Keys)
            if (!derived.Contains(t) && minDepth.TryGetValue(t, out var md))
            { if (md >= 1) crossing++; else nonCrossing++; }

        var idx = new ReferenceFieldIndex(catalog, defType)
        {
            DefDerivedTypes = derived,
            CustomLoaderTypes = custom,
            ReachableTypeCount = reach.Count,
            DepthZeroTypeCount = reach.Values.Count(v => v == 0),
            DepthPositiveTypeCount = reach.Values.Count(v => v >= 1),
            AbstractBoundaryTypeCount = abstractBoundaries.Count,
            ContainerEdgeCount = (int)edges,
            MinDepthCrossingTypeCount = crossing,
            MinDepthNonCrossingTypeCount = nonCrossing,
            ReachableRefFieldCount = direct + noPoly + poly,
            DirectOwnerFieldCount = direct,
            NestedNoPolyFieldCount = noPoly,
            NestedPolyFieldCount = poly,
            ReachableRefSlotCount = refSlots,
            FieldCountByKind = byKind,
            WholeAssemblyRefSlotCount = wholeAssembly,
            CustomLoaderOwnRefSlotCount = customOwn,
            DefItselfSlotCount = defItselfSlots.Count,
            DefItselfSlots = defItselfSlots,
            AbstractDefSlotSlots = abstractDefSlots,
        };
        // ★ 形状表必须挂在**返回的那个实例**上（早期版本填在另一个临时实例上 ⇒ 可达类型数与
        //   每一条遍历都变 0，静默且致命）。
        foreach (var kv in shapes) idx._shapes[kv.Key] = kv.Value;
        idx._derived = derived;
        return idx;
    }

    /// <summary>
    /// 单个类型的形状（实例字段表 + 引用槽）。**唯一实现** —— 可达集在建索引时用它，
    /// `Class=` 派发见到的类型在 <see cref="ShapeOf"/> 里按需用它（两处共用，避免分叉）。
    /// </summary>
    private static TypeShape BuildShape(Type t, int depth, Type defType, IReadOnlySet<Type> derived,
        ICollection<string>? defItselfSlots = null, ICollection<string>? abstractDefSlots = null)
    {
        var fields = FieldReflection.FieldsByName(t);
        var refs = new Dictionary<string, IReadOnlyList<RefSlot>>(StringComparer.Ordinal);
        var slotsAll = new List<RefSlot>();

        foreach (var f in fields.Values)
        {
            var kind = KindOf(f.FieldType);
            var slots = SlotsOf(f.FieldType);
            var fieldSlots = new List<RefSlot>();
            for (var i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                if (slot is null) continue;
                if (!IsRefLeaf(slot, defType, derived)) continue;
                var rs = new RefSlot(f.Name, kind, f.FieldType, slot,
                    IsDictionaryKey: kind == RefContainerKind.Dictionary && i == 0);
                fieldSlots.Add(rs);
                slotsAll.Add(rs);
                if (defItselfSlots is not null && slot == defType)
                    defItselfSlots.Add($"{t.FullName}.{f.Name} ({kind})");
                else if (abstractDefSlots is not null && slot.IsAbstract)
                    abstractDefSlots.Add($"{t.FullName}.{f.Name} → {slot.FullName} ({kind})");
            }

            if (fieldSlots.Count == 0) continue;
            refs[f.Name] = fieldSlots;
        }

        return new TypeShape(t, depth, derived.Contains(t), fields, refs, slotsAll);
    }

    private static Dictionary<Type, int> MinDepths(Type defType, HashSet<Type> derived, HashSet<Type> custom,
        IEnumerable<Type> universe)
    {
        var allow = new HashSet<Type>(universe);
        var dist = new Dictionary<Type, int>();
        var q = new Queue<Type>();
        foreach (var d in derived) { dist[d] = 0; q.Enqueue(d); }
        while (q.Count > 0)
        {
            var t = q.Dequeue();
            if (custom.Contains(t)) continue;
            foreach (var f in FieldReflection.FieldsByName(t).Values)
                foreach (var slot in SlotsOf(f.FieldType))
                {
                    if (slot is null || !allow.Contains(slot)) continue;
                    if (IsRefLeaf(slot, defType, derived)) continue;
                    var nd = dist[t] + (slot.IsAbstract ? 1 : 0);
                    if (!dist.TryGetValue(slot, out var cur) || nd < cur) { dist[slot] = nd; q.Enqueue(slot); }
                }
        }
        return dist;
    }

    // ── 容器解包（规则 A/B/C 的判别，**单一实现**）────────────────────────────────

    /// <summary>
    /// 字段类型 → 容器形态。<b>绝不用字符串比类型名</b>（任务书 6.0 第一条陷阱）：
    /// 一律走 <c>GetGenericTypeDefinition()</c> 与 <c>IsArray</c>。
    /// </summary>
    public static RefContainerKind KindOf(Type ft)
    {
        if (ft.IsArray) return RefContainerKind.Array;
        if (ft.IsGenericType)
        {
            var gd = ft.GetGenericTypeDefinition();
            if (gd == typeof(List<>)) return RefContainerKind.List;
            if (gd == typeof(HashSet<>)) return RefContainerKind.Set;
            if (gd == typeof(Dictionary<,>)) return RefContainerKind.Dictionary;
        }
        return RefContainerKind.Scalar;
    }

    /// <summary>
    /// 该字段类型承载的「元素类型」列表：数组取元素、标量取自身、泛型取全部实参
    /// （字典的 [0]=key、[1]=value —— 与 <see cref="RefSlot.IsDictionaryKey"/> 同序）。
    /// </summary>
    public static IReadOnlyList<Type> SlotsOf(Type ft)
    {
        switch (KindOf(ft))
        {
            case RefContainerKind.Array:
                return new[] { ft.GetElementType()! };
            case RefContainerKind.Scalar:
                return new[] { ft };
            default:
                return ft.GetGenericArguments();
        }
    }

    /// <summary>字段类型是否「承载 <c>Verse.Def</c> 引用」（含基类 <c>Def</c> 本身）。</summary>
    public static bool IsRefLeaf(Type t, Type defType, IReadOnlySet<Type> derived) =>
        t == defType || derived.Contains(t);

    /// <summary>XML 元素/子元素「代表」的 CLR 类型：标量给自身、容器给元素类型（字典给 null，按 key/value 单独走）。</summary>
    public static Type? ElementTypeOf(Type ft)
    {
        switch (KindOf(ft))
        {
            case RefContainerKind.Array: return ft.GetElementType();
            case RefContainerKind.Scalar: return ft;
            case RefContainerKind.List:
            case RefContainerKind.Set: return ft.GetGenericArguments()[0];
            default: return null;   // Dictionary：由 key/value 分别给
        }
    }
}
