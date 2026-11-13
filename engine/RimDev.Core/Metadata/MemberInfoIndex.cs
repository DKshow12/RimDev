using System.Reflection;

namespace RimDev.Core.Metadata;

public enum MemberKind { Field, Property, Method, Constructor, Type }

/// <summary>单个成员的存在性结论。</summary>
public sealed record MemberHit(
    MemberKind Kind,
    string Name,
    string DeclaringType,
    string OwnerType,
    string TypeName,
    bool IsPublic,
    bool IsStatic,
    string? Detail);

/// <summary>
/// 一个类型的成员表。
///
/// ★ 关键设计（由实测得出，见实施计划 §0.1）：
///   1. 字段必须包含 NonPublic —— 证据：VerbProperties.forcedMissRadius 是 private 字段，
///      却是本项目炮塔 XML 正在使用的合法字段。只查 public 会把它误报成"不存在"。
///   2. 查找大小写敏感 —— 证据：RaceProperties.IsMechanoid 存在，isMechanoid 不存在。
///   3. 字段与属性分开暴露 —— XML 只从字段读；规则包需要属性（如 IsMechanoid）。
/// </summary>
public sealed class MemberInfoIndex
{
    // ★ 字段表见 FieldReflection.FieldsByName（单一真源）。
    //   为什么必须聚合基类：.NET 单次 GetFields(Public|NonPublic|Instance) 取值 275 对 ThingDef 成立，
    //   但它**不返回基类的 private 字段**，而 RimWorld 的 XML 反序列化看得见它们。实证：
    //     · RimWorld.StyleItemDef.category 是 private；单次 GetFields 在 BeardDef/HairDef 上找不到它，
    //       可原版 Core\Defs\BeardDefs\BeardDefs.xml:8-10 真的写 <category>Minimal</category>（归档日志零报错）。
    //     · ThingDef 沿 BaseType 走，旧表少收的恰好 1 个是 Verse.BuildableDef.placeWorkersInstantiatedInt（private）。
    //   不聚合 = 官方内容上 126 条 RD4001 + 12 条 RD4005 假阳。这是【把表建对】，不是放宽判据。
    //
    //   实测锚点（2026-10-14，第 2 期 Task 7b）：ThingDef public=266（不变）/ AllFieldCount=276（改前 275）。
    //   ★ 276 这个数不是「放宽」来的：PublicFieldCount 仍取 GetFields(Public|Instance).Length（公开字段本来
    //     就会被继承，所以 266 必须不变）；AllFieldCount 现在 = 去重后的字段名数（含基类 private）。
    //   语义边界：字段表【不含 Static】—— XML 反序列化只写实例字段，静态字段永远不是 XML 目标。

    // 属性枚举的标志（字段已改走 FieldReflection，不再用这个常量）。
    // 属性不做基类 private 聚合：XML 只从字段读，属性仅供规则包与候选建议用。
    private const BindingFlags PropertyFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    // ★ 方法枚举必须一次 GetMethods、不能逐层 DeclaredOnly 累加：
    //   实测 Pawn.PreApplyDamage 逐层累加得到 3 个（含被覆盖的基类版本），
    //   而单次 GetMethods 得到 1 个（只返回最派生那个）—— 后者才是调用者要的语义。
    private const BindingFlags MethodFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private readonly Type _type;
    private readonly Dictionary<string, FieldInfo> _fields;
    private readonly Dictionary<string, PropertyInfo> _properties;
    private readonly Dictionary<string, List<MethodInfo>> _methods;

    // ★★ 第 3 期 Task 2c 新增：RD4001 的两条抑制条件所需的表。
    //   ① 别名表（LoadAliasAttribute）—— 键是别名，比较 OrdinalIgnoreCase；
    //   ④ 类型级 IgnoreSavedElementAttribute 的 elementToIgnore 列表。
    private readonly Dictionary<string, FieldInfo> _aliasFields;
    private readonly IReadOnlyList<string> _ignoreSavedElements;

    public MemberInfoIndex(Type type)
    {
        _type = type;
        // ★ 字段表 = 本类型 + 全部基类的实例字段（含 private），按元素名去重、最派生优先。
        //   单一真源在 FieldReflection.FieldsByName —— 不要在这里改回单次 GetFields（那是 138 条假阳的根因）。
        //   （FieldReflection 返回 IReadOnlyDictionary；这里拷贝成 Dictionary 只为保持 FieldNames 的既有类型
        //   与 KeyCollection 语义不变，每个类型只建一次、随后由 ManagedAssemblyCatalog 缓存。）
        _fields = new Dictionary<string, FieldInfo>(FieldReflection.FieldsByName(type), StringComparer.Ordinal);

        _properties = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
        _methods = new Dictionary<string, List<MethodInfo>>(StringComparer.Ordinal);

        // ★★ 第 3 期 Task 2c：别名表 —— **照引擎实现，刻意不合并基类**。
        //
        //   引擎原文（Verse.XmlToObjectUtils.DoFieldSearch，逐行 + IL 双证据）：
        //     FieldInfo[] fields = typeBeingDeserialized.GetFields(
        //         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);   ← 单次调用，无 BaseType 循环
        //     foreach (var fieldInfo in fields)
        //       foreach (var a in fieldInfo.GetCustomAttributes<LoadAliasAttribute>(inherit: true))
        //         if (a.alias.EqualsIgnoreCase(name)) { value = fieldInfo; break; }
        //
        //   ★ 这个「单次 GetFields」与上面 _fields 用的 FieldReflection.FieldsByName（沿 BaseType 逐层 DeclaredOnly
        //     求并集）是**刻意不同**的两套规则，不是漏写：
        //       · 直接按名查找走 DirectGetFieldByName → SearchTypeHierarchy，后者**显式沿 BaseType 循环**，
        //         所以看得见基类 private 字段 —— 这正是 _fields 必须求并集的原因；
        //       · 别名查找**没有**那层循环，单次 GetFields **不返回基类 private 字段**。
        //     照引擎实现意味着：某个只以「基类 private 字段」形式存在且带 [LoadAlias] 的字段，引擎自己也匹配不到
        //     ⇒ 我们也不匹配。若擅自合并基类，就会比引擎多抑制 RD4001 = 造出**漏报**（放宽判据）。
        //   实测（1.6.4871 rev591，全程序集 16157 个类型）：41 个字段带 [LoadAlias]，**全部是 public 实例字段**，
        //     所以两套规则在当前语料上结果相同；差异只会在「未来原版把 [LoadAlias] 加到 private 字段」时显现。
        //   比较口径：引擎用 GenText.EqualsIgnoreCase = string.Compare(A, B, ignoreCase: true)（当前区域性）；
        //     字段名/别名都是 ASCII 标识符，Ordinal 与区域性在 ASCII 上等价，这里用 OrdinalIgnoreCase 以免
        //     静态分析结果随机器区域性漂移。
        _aliasFields = new Dictionary<string, FieldInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            foreach (var alias in RimWorldAttributes.LoadAliases(f))
                _aliasFields.TryAdd(alias, f);   // 先到者胜 = 与引擎「遇到第一个命中就 break」同序

        // ★ 抑制条件④的类型级半边：IgnoreSavedElementAttribute（AllowMultiple = true，inherit: true）。
        _ignoreSavedElements = RimWorldAttributes.IgnoreSavedElements(type);

        foreach (var p in type.GetProperties(PropertyFlags))
            _properties.TryAdd(p.Name, p);

        foreach (var m in type.GetMethods(MethodFlags))
        {
            if (!_methods.TryGetValue(m.Name, out var list))
                _methods[m.Name] = list = new List<MethodInfo>();
            list.Add(m);
        }
    }

    public Type Type => _type;
    public string FullName => _type.FullName ?? _type.Name;
    public string SimpleName => _type.Name;
    public int PublicFieldCount => _type.GetFields(BindingFlags.Public | BindingFlags.Instance).Length;
    /// <summary>字段表条数 = 去重后的字段名数（含基类 private 字段），恒等于 <see cref="FieldNames"/>.Count。</summary>
    public int AllFieldCount => _fields.Count;
    public int PropertyCount => _type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Length;
    public IReadOnlyCollection<string> FieldNames => _fields.Keys;
    public IReadOnlyCollection<string> PropertyNames => _properties.Keys;

    public IEnumerable<MemberHit> AllMembers()
    {
        foreach (var f in _fields.Values)
            yield return new MemberHit(MemberKind.Field, f.Name, f.DeclaringType?.Name ?? "?", _type.Name,
                f.FieldType.Name, f.IsPublic, f.IsStatic,
                f.IsInitOnly ? "readonly" : (f.IsPublic ? null : "non-public"));

        foreach (var p in _properties.Values)
            yield return new MemberHit(MemberKind.Property, p.Name, p.DeclaringType?.Name ?? "?", _type.Name,
                p.PropertyType.Name, p.GetMethod?.IsPublic ?? false,
                (p.GetMethod ?? p.SetMethod)?.IsStatic ?? false,
                "canWrite=" + p.CanWrite);

        foreach (var (name, list) in _methods)
            foreach (var m in list)
                yield return new MemberHit(MemberKind.Method, name, m.DeclaringType?.Name ?? "?", _type.Name,
                    m.ReturnType.Name, m.IsPublic, m.IsStatic, DescribeSignature(m));
    }

    public static string DescribeSignature(MethodInfo m)
    {
        var ps = m.GetParameters().Select(p =>
            (p.ParameterType.IsByRef ? "ref " : "") +
            p.ParameterType.Name.TrimEnd('&') + " " + p.Name);
        var mods = (m.IsStatic ? "static " : "") + (m.IsVirtual ? "virtual " : "") + (m.IsAbstract ? "abstract " : "");
        return $"{mods}{m.ReturnType.Name} {m.Name}({string.Join(", ", ps)})";
    }

    public FieldInfo? FindField(string name) => _fields.GetValueOrDefault(name);
    public PropertyInfo? FindProperty(string name) => _properties.GetValueOrDefault(name);

    /// <summary>
    /// 按【字段别名】查字段 —— 引擎 <c>DoFieldSearch</c> 的第二个分支（直接按名查找失败之后）。
    ///
    /// 命中 ⇒ 引擎照样把 XML 的值填进该字段 ⇒ 静态检查**不许**报 RD4001。
    /// 实测的别名语料（1.6.4871 rev591，全程序集）：41 个字段 / 26 个类型 / 28 个别名，
    /// 例如 <c>PawnKindDef.defaultFactionDef</c> 带 <c>[LoadAlias("defaultFactionType")]</c>、
    /// <c>StyleItemDef.styleGender</c> 带 <c>[LoadAlias("hairGender")]</c>、
    /// <c>StyleItemDef.styleTags</c> 带 <c>[LoadAlias("hairTags")]</c> —— 这些「旧名」引擎全都认。
    /// </summary>
    public FieldInfo? FindFieldByAlias(string name) => _aliasFields.GetValueOrDefault(name);

    /// <summary>别名表条数（一个字段有多个别名就算多条）。自测用它钉住「照引擎的别名语料」。</summary>
    public int AliasCount => _aliasFields.Count;

    /// <summary>该类型声明要忽略的元素名（<c>IgnoreSavedElementAttribute.elementToIgnore</c>）。</summary>
    public IReadOnlyList<string> IgnoreSavedElements => _ignoreSavedElements;

    /// <summary>
    /// 抑制条件④的类型级半边：元素名是否等于某个 <c>elementToIgnore</c>。
    /// 引擎用 <c>string.Equals(elementToIgnore, name, StringComparison.OrdinalIgnoreCase)</c>。
    /// </summary>
    public bool IgnoresSavedElement(string name)
    {
        foreach (var e in _ignoreSavedElements)
            if (string.Equals(e, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    public IReadOnlyList<MethodInfo> FindMethods(string name) =>
        _methods.TryGetValue(name, out var l) ? l : Array.Empty<MethodInfo>();

    /// <summary>按名字找成员，返回字段优先于属性（XML 只从字段读）。</summary>
    public MemberHit? FindMember(string name)
    {
        if (_fields.TryGetValue(name, out var f))
            return new MemberHit(MemberKind.Field, f.Name, f.DeclaringType?.Name ?? "?", _type.Name,
                f.FieldType.Name, f.IsPublic, f.IsStatic, f.IsInitOnly ? "readonly" : (f.IsPublic ? null : "non-public"));

        if (_properties.TryGetValue(name, out var p))
            return new MemberHit(MemberKind.Property, p.Name, p.DeclaringType?.Name ?? "?", _type.Name,
                p.PropertyType.Name, p.GetMethod?.IsPublic ?? false,
                (p.GetMethod ?? p.SetMethod)?.IsStatic ?? false, "canWrite=" + p.CanWrite);

        var m = FindMethods(name);
        if (m.Count > 0)
            return new MemberHit(MemberKind.Method, m[0].Name, m[0].DeclaringType?.Name ?? "?", _type.Name,
                m[0].ReturnType.Name, m[0].IsPublic, m[0].IsStatic, DescribeSignature(m[0]) + $"  (共 {m.Count} 个重载)");

        return null;
    }

    /// <summary>
    /// 给出最相近的成员名。用于把"字段不存在"变成"你是不是想写 X"。
    /// 优先级：忽略大小写全等 → 前缀 → 编辑距离。
    /// </summary>
    public IReadOnlyList<string> Suggest(string name, int max = 5)
    {
        var pool = _fields.Keys.Concat(_properties.Keys).Distinct().ToList();
        return pool
            .Select(n => (Name: n, Score: Similarity(name, n)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Name.Length)
            .Take(max)
            .Select(x => x.Name)
            .ToList();
    }

    /// <summary>粗粒度相似度：越大越像；≤0 表示不像。</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return string.Equals(a, b, StringComparison.Ordinal) ? 1000 : 900;   // 仅大小写差异最像

        var al = a.ToLowerInvariant();
        var bl = b.ToLowerInvariant();
        if (bl.StartsWith(al, StringComparison.Ordinal) || al.StartsWith(bl, StringComparison.Ordinal))
            return 500 - Math.Abs(a.Length - b.Length);

        var d = Levenshtein(al, bl);
        var maxLen = Math.Max(al.Length, bl.Length);
        var similarity = 1.0 - (double)d / maxLen;
        return similarity >= 0.6 ? similarity * 100 : 0;   // 太不像的不报，避免噪音
    }

    public static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
