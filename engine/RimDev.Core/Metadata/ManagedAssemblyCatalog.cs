using System.Reflection;
using System.Runtime.Loader;

namespace RimDev.Core.Metadata;

public sealed class ManagedAssemblyLoadException : Exception
{
    public ManagedAssemblyLoadException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// 把 RimWorld 的托管程序集载入当前进程并建立类型查找表。
///
/// 实测（实施计划 §0）：.NET 8 进程内 Assembly.LoadFrom(Assembly-CSharp.dll) 可行——
/// 12 ms 载入、26 个引用、GetTypes() 直接返回 16,157 个类型、只需解析 8 个依赖。
/// 因此 v1 不需要子进程隔离；但所有反射调用仍必须包 try/catch（某些类型元数据会抛 TypeLoadException）。
///
/// ★ 命名空间不可假设：BuildingProperties 在 RimWorld.*，ThingDef 在 Verse.*。
///   一律按简单名查找。
/// </summary>
public sealed class ManagedAssemblyCatalog
{
    private readonly Dictionary<string, List<Type>> _bySimpleName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MemberInfoIndex> _memberCache = new(StringComparer.Ordinal);
    private readonly Type[] _types;

    private ManagedAssemblyCatalog(string managedDir, Type[] types,
        IReadOnlyList<string> resolved, IReadOnlyList<string> resolveFailures,
        long loadMs, string? loaderWarning)
    {
        ManagedDir = managedDir;
        _types = types;
        ResolvedFromManaged = resolved;
        ResolveFailures = resolveFailures;
        LoadMilliseconds = loadMs;
        LoaderWarning = loaderWarning;

        foreach (var t in types)
        {
            var name = t.Name;
            if (!_bySimpleName.TryGetValue(name, out var list))
                _bySimpleName[name] = list = new List<Type>();
            list.Add(t);
        }
    }

    public string ManagedDir { get; }
    public int TypeCount => _types.Length;
    public IReadOnlyList<string> ResolvedFromManaged { get; }
    public IReadOnlyList<string> ResolveFailures { get; }
    public long LoadMilliseconds { get; }
    public string? LoaderWarning { get; }
    public IReadOnlyList<Type> AllTypes => _types;

    public static ManagedAssemblyCatalog Load(string managedDir, string assemblyName = "Assembly-CSharp.dll")
    {
        if (!Directory.Exists(managedDir))
            throw new ManagedAssemblyLoadException($"Managed 目录不存在: {managedDir}", new DirectoryNotFoundException(managedDir));

        var resolved = new List<string>();
        var failures = new List<string>();

        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var path = Path.Combine(managedDir, name.Name + ".dll");
            if (!File.Exists(path))
            {
                failures.Add(name.Name ?? "?");
                return null;
            }
            resolved.Add(name.Name ?? "?");
            return ctx.LoadFromAssemblyPath(path);
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assembly asm;
        var asmPath = Path.Combine(managedDir, assemblyName);
        try
        {
            asm = Assembly.LoadFrom(asmPath);
        }
        catch (Exception ex)
        {
            // 实测这条路径不会触发；保留以便版本变化时能给出可操作报错
            throw new ManagedAssemblyLoadException(
                $"载入 {assemblyName} 失败（{ex.GetType().Name}）: {ex.Message}", ex);
        }

        string? loaderWarning = null;
        Type[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // 历史教训：GetTypes() 只抛一次聚合异常，而 GetType(name) 会静默返回 null，
            // 极易被误判成"类型不存在"。这里显式降级并留下告警。
            types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
            loaderWarning = $"ReflectionTypeLoadException：返回 {types.Length} 个类型，" +
                            $"加载器异常 {ex.LoaderExceptions.Length} 条。首条：{ex.LoaderExceptions.FirstOrDefault()?.Message}";
        }
        sw.Stop();

        return new ManagedAssemblyCatalog(managedDir, types, resolved, failures,
            sw.ElapsedMilliseconds, loaderWarning);
    }

    /// <summary>按简单名精确查找（不假设命名空间）。命中多个时返回第一个。</summary>
    public Type? FindBySimpleName(string simpleName) =>
        _bySimpleName.TryGetValue(simpleName, out var list) && list.Count > 0 ? list[0] : null;

    public IReadOnlyList<Type> FindAllBySimpleName(string simpleName) =>
        _bySimpleName.TryGetValue(simpleName, out var list) ? list : Array.Empty<Type>();

    /// <summary>支持 "Verse.ThingDef" 全名与 "ThingDef" 简单名两种写法。</summary>
    public Type? Find(string typeName)
    {
        var simple = typeName.Contains('.') ? typeName[(typeName.LastIndexOf('.') + 1)..] : typeName;
        if (_bySimpleName.TryGetValue(simple, out var list))
        {
            if (list.Count == 1) return list[0];
            var exact = list.FirstOrDefault(t => t.FullName == typeName);
            return exact ?? list[0];
        }
        return null;
    }

    public MemberInfoIndex MembersOf(Type type) =>
        _memberCache.TryGetValue(type.FullName ?? type.Name, out var idx)
            ? idx
            : _memberCache[type.FullName ?? type.Name] = new MemberInfoIndex(type);

    public MemberInfoIndex? MembersOf(string typeName)
    {
        var t = Find(typeName);
        return t is null ? null : MembersOf(t);
    }

    /// <summary>类型名字的候选建议（用于"类型不存在"时给相近项）。</summary>
    public IReadOnlyList<string> SuggestTypeNames(string typeName, int max = 5) =>
        _bySimpleName.Keys
            .Select(n => (Name: n, Score: MemberInfoIndex.Similarity(typeName, n)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Name.Length)
            .Take(max)
            .Select(x => x.Name)
            .ToList();

    /// <summary>
    /// 判断某类型是否带"自定义 XML 加载器"（实例方法 LoadDataFromXmlCustom(XmlNode)）。
    /// 实测：全程序集 65 个类型带这个实例方法，静态方法 0 个 —— 所以必须查实例，不能查静态。
    /// 这是 shape 顾问（F2）判定"该写裸元素还是 &lt;li&gt;"的唯一依据。
    /// </summary>
    public static bool HasCustomXmlLoader(Type type)
    {
        try
        {
            var m = type.GetMethod("LoadDataFromXmlCustom",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return m is not null && m.GetParameters().Length == 1;
        }
        catch
        {
            return false;
        }
    }
}
