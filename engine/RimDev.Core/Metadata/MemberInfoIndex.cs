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
    // ★ 字段不含 Static（实测校准）：ThingDef public=266 / 含非公开（无 Static）=275 / 含 Static=282。
    //   RimWorld 的 XML 反序列化只写实例字段，静态字段永远不是 XML 目标；
    //   含 Static 会让 AllFieldCount 变成 282，与锚点 275 不符，且语义上是错的。
    private const BindingFlags FieldFlags =
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

    public MemberInfoIndex(Type type)
    {
        _type = type;
        _fields = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
        _properties = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
        _methods = new Dictionary<string, List<MethodInfo>>(StringComparer.Ordinal);

        foreach (var f in type.GetFields(FieldFlags))
            _fields.TryAdd(f.Name, f);

        foreach (var p in type.GetProperties(FieldFlags))
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
    public int AllFieldCount => _type.GetFields(FieldFlags).Length;
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
