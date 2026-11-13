using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace RimDev.Core.Defs;

/// <summary>
/// ★★ 第 3 期 Task 13：把 XML 里的**全限定元素名**归一化成它的 Def 基类名。
///
/// ── 要解决什么 ────────────────────────────────────────────────────────────
/// 引擎允许用**完整类型名**作元素名来定义 def（<see cref="Verse.DirectXmlToObject"/> 的
/// <c>ClassTypeOf</c> 走 <c>Type.GetType</c> 语义）。实测语料：
/// <code>
/// &lt;!-- workshop\294100\3560667190\1.7\Defs\AlienRace\AlienRaceSettings.xml:2 --&gt;
/// &lt;AlienRace.ThingDef_AlienRace Name="BANW_KivotosStudent"&gt;
///     &lt;defName&gt;BANW_KivotosStudent&lt;/defName&gt;
/// </code>
/// 而 <see cref="DefScanner"/> 直接把元素名当 <c>DefType</c>，于是
/// <c>AlienRace.ThingDef_AlienRace</c> 不算 <c>ThingDef</c> ⇒
/// <c>&lt;race&gt;BANW_KivotosStudent&lt;/race&gt;</c>（<c>PawnKindDef.race</c> 期望 ThingDef）
/// 被判悬空，**同一目标报了 151 次**。
///
/// ── 为什么不能「取末段」 ──────────────────────────────────────────────────
/// 项目历史上踩过：<c>VEF.Abilities.AbilityDef</c> 取末段 <c>AbilityDef</c> 会兜底命中
/// <c>RimWorld.AbilityDef</c>，实测造出 **17,623 条假阳**（记在
/// <c>ManagedAssemblyCatalog.cs:36-40</c>）。所以这里**只认真实继承链**。
///
/// ── 怎么拿到继承链（不加载程序集）────────────────────────────────────────
/// <c>System.Reflection.Metadata.PEReader</c> 直读元数据表，**不执行任何代码**。
/// 为什么不用 <c>Assembly.LoadFrom</c>：mod 的 DLL 多是 net472（1.6 时代编译），
/// 而本工具跑 net10 —— 加载有 <c>BadImageFormatException</c> / <c>ReflectionTypeLoadException</c> 风险，
/// 崩一次整个 check 就没了。<c>Docs/10</c> §7.1 记了这条判据。
///
/// ── 保守原则 ─────────────────────────────────────────────────────────────
/// 找不到类型（模组 DLL 未安装、类型名拼错、或该 DLL 不提供它）⇒ **原样返回元素名**，
/// 保持修前行为。宁可漏报（不归并），不可把无关类型硬归并到某个 Def 基类下 ——
/// 那会制造新的假阳/假阴。
/// </summary>
public sealed class XmlElementTypeResolver
{
    /// <summary>元素名 → 归一化 Def 基类名。null = 无法解析（调用方保留原值）。</summary>
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.Ordinal);

    /// <summary>类型全名 → (基类全名, 是否派生自 Def)。同一次进程内只读一遍程序集。</summary>
    private readonly ConcurrentDictionary<string, (string? BaseFullName, bool DerivesFromDef)> _types
        = new(StringComparer.Ordinal);

    /// <summary>已扫描过的程序集（避免重复打开同一个文件）。</summary>
    private readonly ConcurrentDictionary<string, bool> _scanned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ★ 中间抽象基类黑名单：以 <c>Def</c> 结尾、也派生自 <c>Def</c>，
    /// 但**不作为 XML 元素名 / <c>DefType</c>**。归并到它们身上不会解决任何问题
    /// —— 引用方字段声明的是**具体**类型（如 <c>ThingDef</c>），不是中间层。
    /// <para>来源：实测反编译链 —— <c>ThingDef → BuildableDef → Def</c>。</para>
    /// <para>宁可短不可长：漏一个只是「归并到中间层」（效果等于不归并）；
    /// 错列一个**有效基类**反而会少归并。</para>
    /// </summary>
    private static readonly HashSet<string> IntermediateDefBaseClasses = new(StringComparer.Ordinal)
    {
        "Def",             // 根
        "BuildableDef",    // ThingDef 的中间层（实测：ThingDef → BuildableDef → Def）
        "StyleItemDef",    // 风格插件的中间层
    };

    private readonly IReadOnlyList<string> _assemblyPaths;

    /// <summary>本解析器实际读到多少个类型定义（用于报告「解析能力」而不是假装全能）。</summary>
    public int TypeDefinitionsSeen => _types.Count;

    /// <summary>成功归并的元素名数（distinct）。</summary>
    public int ResolvedElementNames => _cache.Count(kv => kv.Value is not null);

    private XmlElementTypeResolver(IReadOnlyList<string> assemblyPaths) => _assemblyPaths = assemblyPaths;

    /// <summary>
    /// ★ 从一个 <see cref="Catalog.ModCatalog"/> 建解析器（官方程序集 + 每个 mod 的 <c>Assemblies/*.dll</c>）。
    /// <para>传入 <paramref name="mods"/>应是**本次扫描要用的集合**（如 <c>Catalog.Active</c> 或
    /// <c>--scope all</c> 的全部）—— 归并只需要「可能出现在语料里的类型」。</para>
    /// <para>官方 <c>Assembly-CSharp.dll</c> 一定会被加入（它提供 <c>Verse.*Def</c> / <c>RimWorld.*Def</c> 这些归并目标）。</para>
    /// </summary>
    public static XmlElementTypeResolver CreateFromCatalog(
        string? officialAssemblyPath, IEnumerable<Catalog.ModInfo> mods)
    {
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(officialAssemblyPath) && File.Exists(officialAssemblyPath))
            paths.Add(officialAssemblyPath);

        foreach (var m in mods)
        {
            // 只看声明了有程序集的 mod（避免对 4000+ 个目录做无用的磁盘探测）。
            if (!m.HasAssemblies) continue;
            var dir = Path.Combine(m.RootPath, "Assemblies");
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
                    paths.Add(dll);
            }
            catch { /* 目录不可读 ⇒ 跳过，不影响其他 mod */ }
        }

        return Create(paths);
    }

    /// <summary>
    /// 建一个解析器。<paramref name="assemblyPaths"/> 应包含**官方程序集**与
    /// **启用集里各 mod 的 <c>Assemblies/*.dll</c>**（顺序无关；后读到的同名类型不覆盖先读到的）。
    /// 传空列表 = 全部不解析（退化为修前行为），可用于 A/B 对照。
    /// </summary>
    public static XmlElementTypeResolver Create(IEnumerable<string> assemblyPaths)
    {
        var list = assemblyPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var r = new XmlElementTypeResolver(list);
        // 预扫一遍：把类型表建起来（并行度交给调用方控制；这里是只读 IO）。
        foreach (var p in list) r.ScanAssembly(p);
        return r;
    }

    /// <summary>
    /// 归一化一个元素名。
    /// <list type="bullet">
    /// <item>不含 <c>.</c> ⇒ 原样返回（<c>ThingDef</c> 这类原版元素名，零开销快路径）</item>
    /// <item>含 <c>.</c> 且能解析出「派生自 Def」的基类 ⇒ 返回**该基类简单名**（如 <c>ThingDef</c>）</item>
    /// <item>否则 ⇒ 原样返回（保守：不猜）</item>
    /// </list>
    /// </summary>
    public string Normalize(string elementName)
    {
        if (string.IsNullOrEmpty(elementName)) return elementName;
        if (!elementName.Contains('.')) return elementName;      // 快路径

        return _cache.GetOrAdd(elementName, name =>
        {
            if (!_types.TryGetValue(name, out var info))
            {
                // 元素名没在已扫程序集里出现 ⇒ 无法解析
                return null;
            }
            if (!info.DerivesFromDef || info.BaseFullName is null) return null;

            // 基类全名 → 简单名。引擎里 Def 的基类都在 Verse 命名空间（如 Verse.ThingDef），
            // 但为稳妥，统一取最后一段。
            var i = info.BaseFullName.LastIndexOf('.');
            return i >= 0 ? info.BaseFullName[(i + 1)..] : info.BaseFullName;
        }) ?? elementName;     // null（解析失败）⇒ 原样返回
    }

    /// <summary>某个元素名是否成功归并（供统计/自测用）。</summary>
    public bool WasResolved(string elementName) =>
        _cache.TryGetValue(elementName, out var v) && v is not null;

    // ── 元数据扫描 ────────────────────────────────────────────────────────

    private void ScanAssembly(string path)
    {
        if (!_scanned.TryAdd(path, true)) return;
        try
        {
            using var fs = File.OpenRead(path);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata) return;
            var md = pe.GetMetadataReader();

            foreach (var h in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(h);
                var ns = md.GetString(td.Namespace);
                var nm = md.GetString(td.Name);
                var full = string.IsNullOrEmpty(ns) ? nm : ns + "." + nm;

                // 走一层基类即可：引擎的 Def 子类都直接派生自某个 Def 基类
                // （ThingDef_AlienRace → ThingDef；Abilities.AbilityDef → RimWorld.AbilityDef）。
                var baseFull = ResolveBaseFullName(md, td.BaseType);
                var derives = baseFull is not null;
                _types.TryAdd(full, (baseFull, derives));
            }
        }
        catch
        {
            // 读不了（不是托管程序集 / 文件被占用 / 损坏）⇒ 跳过。
            // 这里**故意吞异常**：解析器是「增强」，失败应退化为修前行为，不能让整个扫描崩。
        }
    }

    /// <summary>
    /// 取一个类型定义自己的**基类全名**（可能是同程序集内的 TypeDefinition，也可能是外部 TypeReference）。
    /// 只有基类名以 <c>Def</c> 结尾、且命名空间是 <c>Verse</c>/<c>RimWorld</c> 时才认为「派生自 Def」。
    /// </summary>
    private static string? ResolveBaseFullName(MetadataReader md, EntityHandle baseType)
    {
        if (baseType.IsNil) return null;
        string full;
        switch (baseType.Kind)
        {
            case HandleKind.TypeDefinition:
            {
                var btd = md.GetTypeDefinition((TypeDefinitionHandle)baseType);
                var bns = md.GetString(btd.Namespace);
                var bnm = md.GetString(btd.Name);
                full = string.IsNullOrEmpty(bns) ? bnm : bns + "." + bnm;
                break;
            }
            case HandleKind.TypeReference:
            {
                var tr = md.GetTypeReference((TypeReferenceHandle)baseType);
                var tns = md.GetString(tr.Namespace);
                var tnm = md.GetString(tr.Name);
                full = string.IsNullOrEmpty(tns) ? tnm : tns + "." + tnm;
                break;
            }
            default:
                return null;
        }

        // 「是不是 Def 家族」的判据：简单名以 Def 结尾，且命名空间是 Verse 或 RimWorld。
        // 这个判据来自引擎自身结构（Verse.Def 是根，RimWorld.*Def 是它的子类）。
        var dot = full.LastIndexOf('.');
        var simple = dot >= 0 ? full[(dot + 1)..] : full;
        var ns = dot >= 0 ? full[..dot] : "";
        if (!simple.EndsWith("Def", StringComparison.Ordinal)) return null;
        if (ns != "Verse" && ns != "RimWorld" && !ns.StartsWith("Verse.", StringComparison.Ordinal))
            return null;

        // ★ 排除中间抽象基类：它们也以 Def 结尾、也派生自 Def，
        //   但引擎不会用它们作 DefType。实测：若把 ThingDef_AlienRace 归并成
        //   BuildableDef，它仍不等于 ThingDef ⇒ 归并无效（白做）。
        if (IntermediateDefBaseClasses.Contains(simple)) return null;

        return full;
    }
}
