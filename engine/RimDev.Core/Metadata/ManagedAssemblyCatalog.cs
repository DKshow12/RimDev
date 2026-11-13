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

    // ★ 第 3 期 Task 2b：全名精确表。`Find()` 的「取最后一段当简单名」兜底对
    //   `VEF.Abilities.AbilityDef` 这类【带命名空间的元素名】是灾难：它兜底命中 RimWorld.AbilityDef，
    //   于是拿 VEF 自定义类型里写的字段去查原版类型的字段表 → 全库 17623 条 RD4001 假阳。
    //   含 '.' 的名字必须只认全名精确匹配（见 FindExactFullName 与 FieldNameCheck 的机制 B）。
    private readonly Dictionary<string, Type> _byFullName = new(StringComparer.Ordinal);

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

            // ★ 第 3 期 Task 2b：另建一张【全名】精确表（重名/嵌套情形由 TryAdd 取先到者，
            //   与 _bySimpleName 的「取第一个」语义一致）。
            if (t.FullName is { } full)
                _byFullName.TryAdd(full, t);
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

    /// <summary>
    /// 支持 "Verse.ThingDef" 全名与 "ThingDef" 简单名两种写法。
    ///
    /// ★★ 第 3 期 Task 2b 的警告：本方法对**含 `.` 的名字**会退化成简单名兜底
    ///   （`VEF.Abilities.AbilityDef` → 取末段 `AbilityDef` → 命中 `RimWorld.AbilityDef`）。
    ///   那条退化路径对「元素名/Class= 就是完整类型名」的场景是错的（实测 17623 条假阳）。
    ///   **XML 元素名/Class= 的解析请用 <see cref="ResolveXmlTypeName"/>，不要直接调本方法。**
    ///   本方法保留给 api/name 等「按名字查类型」的交互场景（那里兜底是好用的）。
    /// </summary>
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

    /// <summary>全名精确查找（<b>无任何兜底</b>）。用于含 `.` 的类型名（元素名或 Class=）。</summary>
    public Type? FindExactFullName(string fullName) =>
        _byFullName.TryGetValue(fullName, out var t) ? t : null;

    /// <summary>
    /// ★★ 第 3 期 Task 2b 的**唯一**入口：解析 XML 里的类型名（def 元素名 或 Class= 属性）。
    ///
    /// 规则（机制 B）：
    /// <list type="bullet">
    /// <item>名字含 `.`（看起来是完整类型名 / 带命名空间）→ <b>只认全名精确匹配</b>，
    ///   解析不到就返回 null（调用方报 RD2017 info），<b>绝不</b>退化到简单名。</item>
    /// <item>名字不含 `.`（`ThingDef` / `HediffDef` 这类原版元素名）→ 走 <see cref="Find"/> 的既有逻辑。
    ///   这条路径必须保留：它是抓真缺陷（如 <c>ThingDef</c> 上写 <c>renderNodeProperties</c>）的能力所在。</item>
    /// </list>
    /// </summary>
    public Type? ResolveXmlTypeName(string typeName) =>
        typeName.Contains('.') ? FindExactFullName(typeName) : Find(typeName);

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
