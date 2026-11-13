using System.Reflection;

namespace RimDev.Core.Metadata;

/// <summary>
/// 一个「逻辑类型」的**候选全名列表**，按 1.6 口径在前、1.7 口径在后的顺序逐个尝试。
///
/// ★ 为什么是候选列表而不是 <c>if (version &gt;= 1.7)</c>：
///   版本分支在下次搬命名空间时会**静默失效**（代码仍能编译，只是永远走错那支）；
///   候选列表不需要知道版本，只要命中哪支就记哪支，并把命中的全名打进输出。
///
/// ★ 实测（第 3 期 Task 9 任务书）：1.7.x 线把 <c>Verse.DefGenerator</c> 搬到了 <c>RimWorld.DefGenerator</c>，
///   <c>IncidentWorker</c> 同族。凡是在代码里「按名字找原版类型」的地方都应走这张表。
/// </summary>
public sealed record TypeNameCandidate(string LogicalName, params string[] FullNames)
{
    public string Describe() => $"{LogicalName} ← {string.Join(" | ", FullNames)}";
}

/// <summary>代码里按名字解析的原版类型（其余类型一律走 XML 元素名/Class= 的通用解析路径）。</summary>
public static class KnownTypeNames
{
    public const string VersionControl = "VersionControl";
    public const string DefGenerator = "DefGenerator";
    public const string IncidentWorker = "IncidentWorker";
    public const string Def = "Def";

    /// <summary>1.6 口径在前的候选表。</summary>
    public static readonly IReadOnlyList<TypeNameCandidate> All = new[]
    {
        // 1.6 实测就是 RimWorld.VersionControl；Verse.VersionControl 从来不存在，
        // 但保留为候选可以让「将来又搬回去」不再需要改代码。
        new TypeNameCandidate(VersionControl, "RimWorld.VersionControl", "Verse.VersionControl"),

        // ★ 真正被 1.7 搬过的那一个。
        new TypeNameCandidate(DefGenerator, "Verse.DefGenerator", "RimWorld.DefGenerator"),

        // 同族（任务书点名）：1.6 = Verse.IncidentWorker，1.7 = RimWorld.IncidentWorker。
        new TypeNameCandidate(IncidentWorker, "Verse.IncidentWorker", "RimWorld.IncidentWorker"),

        new TypeNameCandidate(Def, "Verse.Def", "RimWorld.Def"),
    };

    public static TypeNameCandidate Get(string logicalName) =>
        All.First(c => c.LogicalName == logicalName);
}

/// <summary>一次解析的结果（进 <c>env --json</c> 的 <c>typeResolution</c>）。</summary>
public sealed record TypeResolution(
    string LogicalName,
    IReadOnlyList<string> Candidates,
    string? ResolvedFullName,
    string? ResolvedCandidate,
    string? Failure)
{
    public bool Resolved => ResolvedFullName is not null;
}

public static class TypeNameResolver
{
    /// <summary>解析成 Type（命中返回类型，未命中返回 null）。</summary>
    public static Type? ResolveType(ManagedAssemblyCatalog catalog, string logicalName) =>
        Resolve(catalog, logicalName) is { Resolved: true } r
            ? catalog.FindExactFullName(r.ResolvedFullName!)
            : null;

    /// <summary>在已载入的 ManagedAssemblyCatalog 上按候选顺序解析（命中即停）。</summary>
    public static TypeResolution Resolve(ManagedAssemblyCatalog catalog, string logicalName)
    {
        var c = KnownTypeNames.Get(logicalName);
        foreach (var full in c.FullNames)
        {
            var t = catalog.FindExactFullName(full);
            if (t is not null)
                return new TypeResolution(logicalName, c.FullNames, t.FullName, full, null);
        }

        // 候选全落空时给出可操作信息：简单名存在但全名不是候选之一（说明命名空间又搬了）
        var simple = catalog.FindBySimpleName(logicalName);
        var hint = simple is null
            ? $"候选全名都不存在，且没有简单名为 {logicalName} 的类型"
            : $"简单名存在但全名是 {simple.FullName}（不在候选表里 ⇒ 命名空间可能又搬了，请补进 KnownTypeNames.All）";
        return new TypeResolution(logicalName, c.FullNames, null, null, hint);
    }

    public static Type? ResolveInAssembly(Assembly asm, string logicalName, out string? resolvedFullName)
    {
        resolvedFullName = null;
        var c = KnownTypeNames.Get(logicalName);
        foreach (var full in c.FullNames)
        {
            var t = asm.GetType(full, throwOnError: false);
            if (t is not null)
            {
                resolvedFullName = t.FullName;
                return t;
            }
        }
        return null;
    }

    /// <summary>一次性解析整张候选表并返回报告（env 用；任何一步抛异常都降级成 Failure，不中断 env）。</summary>
    public static IReadOnlyList<TypeResolution> ResolveAll(ManagedAssemblyCatalog catalog) =>
        KnownTypeNames.All.Select(c => Resolve(catalog, c.LogicalName)).ToList();

    /// <summary>程序集载入失败时（典型：net8 产物遇上 1.7 的 net10 程序集）的报告。</summary>
    public static IReadOnlyList<TypeResolution> Unavailable(string reason) =>
        KnownTypeNames.All
            .Select(c => new TypeResolution(c.LogicalName, c.FullNames, null, null, reason))
            .ToList();
}
