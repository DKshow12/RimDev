using System.Reflection;

namespace RimDev.Core.Metadata;

/// <summary>
/// RimWorld 元数据属性的**按名字**读取器 —— 这三条规则的单一真源。
///
/// ★ 为什么不能直接 `typeof(LoadAliasAttribute)`：RimDev.Core **不引用** Assembly-CSharp.dll
///   （它是运行时用 <see cref="ManagedAssemblyCatalog.Load"/> 反射载入的，工程里零外部依赖、
///   零 ProjectReference）。所以这里一律：
///     ① `MemberInfo.GetCustomAttributes(inherit: true)` 拿 object[]；
///     ② 按 <c>GetType().Name</c> 判是不是目标属性；
///     ③ 按字段名反射读值（属性类的公共字段：<c>alias</c> / <c>allowLoading</c> / <c>elementToIgnore</c>）。
///   这与既有的 <see cref="ManagedAssemblyCatalog.HasCustomXmlLoader"/>（按名字找方法）是同一套做法。
///
/// ★ 反编译依据（Verse.XmlToObjectUtils.DoFieldSearch，1.6.4871 rev591，逐行 + IL 双证据）：
///   · 别名的比较是 <c>Verse.GenText.EqualsIgnoreCase</c> = `string.Compare(A, B, ignoreCase: true) == 0`；
///     字段名/别名都是 ASCII 标识符，Ordinal 与当前区域性比较在 ASCII 上等价 —— 本工具统一按
///     <see cref="StringComparison.OrdinalIgnoreCase"/>，以免静态分析结果随机器区域性漂移。
///   · <c>GetCustomAttributes&lt;LoadAliasAttribute&gt;(inherit: true)</c> / `GetCustomAttributes(typeof(IgnoreSavedElementAttribute), inherit: true)`
///     / <c>value.TryGetAttribute&lt;UnsavedAttribute&gt;()</c> 三个调用的 inherit 都是 true ⇒ 这里也必须用 inherit: true。
/// </summary>
public static class RimWorldAttributes
{
    /// <summary>字段级别名属性（<c>[LoadAlias("旧名")]</c>），字段名 <c>alias</c>。</summary>
    public const string LoadAlias = "LoadAliasAttribute";

    /// <summary>字段级「不参与存档/加载」属性，字段名 <c>allowLoading</c>（默认 false）。</summary>
    public const string Unsaved = "UnsavedAttribute";

    /// <summary>类型级「忽略该元素名」属性，字段名 <c>elementToIgnore</c>（AllowMultiple = true）。</summary>
    public const string IgnoreSavedElement = "IgnoreSavedElementAttribute";

    private static readonly Dictionary<string, FieldInfo?> AttributeFieldCache = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary><c>MemberInfo.GetCustomAttributes(inherit: true)</c>，异常退化为空数组。</summary>
    /// <remarks>
    /// 为什么要包 try：某些类型的属性元数据在反射时会抛（与 HasCustomXmlLoader / GetTypes 的历史教训同源）。
    /// 抛了就当「没有该属性」——方向上是**少报**，不会造出假诊断。
    /// </remarks>
    public static object[] SafeAttributes(MemberInfo member)
    {
        try { return member.GetCustomAttributes(inherit: true); }
        catch { return Array.Empty<object>(); }
    }

    private static FieldInfo? FieldOf(Type attributeType, string fieldName)
    {
        var key = (attributeType.FullName ?? attributeType.Name) + "|" + fieldName;
        lock (Gate)
        {
            if (AttributeFieldCache.TryGetValue(key, out var cached)) return cached;
            FieldInfo? f;
            try
            {
                f = attributeType.GetField(fieldName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch { f = null; }
            AttributeFieldCache[key] = f;
            return f;
        }
    }

    /// <summary>把属性实例的某个字段读成 string（读不到返回 null）。</summary>
    private static string? ReadString(object attr, string fieldName)
    {
        var f = FieldOf(attr.GetType(), fieldName);
        if (f is null) return null;
        try { return f.GetValue(attr) as string; }
        catch { return null; }
    }

    /// <summary>
    /// 该字段声明的全部别名（按属性声明顺序；引擎按 <c>GetFields</c> 顺序 + 属性顺序取第一个命中）。
    /// </summary>
    public static IReadOnlyList<string> LoadAliases(FieldInfo field)
    {
        List<string>? list = null;
        foreach (var a in SafeAttributes(field))
        {
            if (a.GetType().Name != LoadAlias) continue;
            var alias = ReadString(a, "alias");
            if (string.IsNullOrEmpty(alias)) continue;
            (list ??= new List<string>()).Add(alias!);
        }
        return list ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    /// <summary>
    /// 该字段是否带 <c>[Unsaved]</c>，以及它的 <c>allowLoading</c> 取值。
    /// 引擎语义：只有 <c>allowLoading == false</c> 才会 `Log.Error` 并返回 null。
    /// </summary>
    public static bool TryGetUnsavedAllowLoading(FieldInfo field, out bool allowLoading)
    {
        foreach (var a in SafeAttributes(field))
        {
            if (a.GetType().Name != Unsaved) continue;
            var f = FieldOf(a.GetType(), "allowLoading");
            if (f is null)
            {
                // 属性类变了（没有这个字段）就按 .NET 默认值 false 处理：false 是「会报错」那一侧，
                // 与 UnsavedAttribute 构造器默认参数 false 一致。
                allowLoading = false;
                return true;
            }
            try { allowLoading = f.GetValue(a) is true; }
            catch { allowLoading = false; }
            return true;
        }
        allowLoading = false;
        return false;
    }

    /// <summary>
    /// 该类型声明的全部 <c>IgnoreSavedElementAttribute.elementToIgnore</c>（AllowMultiple = true）。
    /// 引擎用 <c>string.Equals(..., name, StringComparison.OrdinalIgnoreCase)</c> 比元素名。
    /// </summary>
    public static IReadOnlyList<string> IgnoreSavedElements(Type type)
    {
        List<string>? list = null;
        foreach (var a in SafeAttributes(type))
        {
            if (a.GetType().Name != IgnoreSavedElement) continue;
            var v = ReadString(a, "elementToIgnore");
            if (string.IsNullOrEmpty(v)) continue;
            (list ??= new List<string>()).Add(v!);
        }
        return list ?? (IReadOnlyList<string>)Array.Empty<string>();
    }
}
