using System.Reflection;
using RimDev.Core.Metadata;
using System.Runtime.Loader;

namespace RimDev.Core.Environment;

/// <summary>
/// <see cref="RimWorld.VersionControl"/> 的实测值（Q1）。
///
/// ★ 命名空间修正（2026-10-14 反射实测）：类型全名是 **<c>RimWorld.VersionControl</c>**，
///   不是计划 §0.4.1 里写的 <c>Verse.VersionControl</c>（`Verse.VersionControl` 返回 null）。
///
/// ★ 实测值（1.6.4871 rev591，反射自 Assembly-CSharp.dll）：
///   CurrentVersionString            = "1.6.4871"     ← 【三段】，不是 "1.6"
///   CurrentVersionStringWithoutBuild= "1.6"
///   CurrentVersion                  = 1.6.4871.591
///   CurrentVersionStringWithRev     = "1.6.4871 rev591"
///   取值方式：AssemblyLoadContext 解析根指向 Managed\，Assembly.LoadFrom(Assembly-CSharp.dll)，
///   再读 <c>RimWorld.VersionControl</c> 的静态属性（静态构造器只依赖 Assembly.GetExecutingAssembly().GetName()，
///   不需要 Unity 运行时，见 VersionControl 的反编译源码）。
///
/// ★ 这条值决定 loadFolders 走哪个分支：引擎第 1 步是
///   <c>LoadFoldersForVersion(VersionControl.CurrentVersionString)</c> = 查键 <b>"1.6.4871"</b>，
///   而现实中的 mod 只写 <c>&lt;v1.6&gt;</c> → 键 "1.6" → **精确分支实际上永不命中**，
///   所有正常 mod 都走第 2 步「向下取版本」（Origin=LowerVersion）。
/// </summary>
public sealed record EngineVersionInfo(
    string CurrentVersionString,
    string CurrentVersionStringWithoutBuild,
    Version CurrentVersion,
    string Source,
    string? Error,
    // ★ Task 9：命中的是候选表里的哪一支（如 "RimWorld.VersionControl"）；未命中/降级为 null。
    string? TypeFullName = null)
{
    public const string ReflectionSource = "reflection";
    public const string EnvFallbackSource = "env-fallback";
}

/// <summary>反射探测引擎版本号；进程内只做一次。</summary>
public static class EngineVersionProbe
{
    private static readonly object Gate = new();
    private static EngineVersionInfo? _cached;
    private static bool _resolverHooked;

    public static EngineVersionInfo Probe(GameEnv env)
    {
        lock (Gate)
        {
            return _cached ??= ProbeCore(env);
        }
    }

    /// <summary>仅供自测：清掉缓存以便重复探针。</summary>
    public static void ResetForTests()
    {
        lock (Gate) { _cached = null; }
    }

    private static EngineVersionInfo ProbeCore(GameEnv env)
    {
        try
        {
            if (!_resolverHooked)
            {
                var managedDir = env.ManagedDir;
                AssemblyLoadContext.Default.Resolving += (ctx, name) =>
                {
                    // ★ 与 ManagedAssemblyCatalog.Load 同一条纪律：框架程序集交给运行时，
                    //   绝不从游戏目录载入（1.7.x 的游戏根里有整套 net10 运行时；
                    //   net8 产物从那里载入 System.Runtime 会把解析链拖进自我递归）。
                    if (ManagedAssemblyCatalog.IsFrameworkAssembly(name.Name))
                        return null;

                    var p = Path.Combine(managedDir, name.Name + ".dll");
                    return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
                };
                _resolverHooked = true;
            }

            var asm = Assembly.LoadFrom(Path.Combine(env.ManagedDir, "Assembly-CSharp.dll"));
            // ★ Task 9：候选列表逐个尝试（不要写版本分支）；命中哪支就记哪支，见 KnownTypeNames。
            var t = TypeNameResolver.ResolveInAssembly(asm, KnownTypeNames.VersionControl, out var resolvedFullName)
                    ?? asm.GetTypes().FirstOrDefault(x => x.Name == KnownTypeNames.VersionControl);
            if (t is null)
                throw new InvalidOperationException(
                    "Assembly-CSharp 里找不到 VersionControl 类型（候选：" +
                    string.Join(" | ", KnownTypeNames.Get(KnownTypeNames.VersionControl).FullNames) +
                    "），也没有简单名为 VersionControl 的类型");

            var s = Prop(t, "CurrentVersionString") as string;
            var s2 = Prop(t, "CurrentVersionStringWithoutBuild") as string;
            var v = Prop(t, "CurrentVersion") as Version;
            if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(s2) || v is null)
                throw new InvalidOperationException($"VersionControl 属性读取为 null（{s ?? "null"} / {s2 ?? "null"} / {v?.ToString() ?? "null"}）");

            return new EngineVersionInfo(s, s2, v, EngineVersionInfo.ReflectionSource, null,
                resolvedFullName ?? t.FullName);
        }
        catch (Exception ex)
        {
            // 降级：从 Version.txt / ModsConfig.xml 的 "<version>" 文本推导。
            // 注意这【不是】引擎值本身（缺 revision），因此 Source 会标成 env-fallback，
            // 让 `rimdev files` 的输出来得及告诉使用者「这次可能是猜的」。
            var raw = string.IsNullOrWhiteSpace(env.VersionConfig) ? env.VersionRaw : env.VersionConfig;
            var token = (raw ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            var withoutBuild = string.IsNullOrWhiteSpace(token) ? "" : GameEnvProbe.ToMajorMinor(token);
            Version parsed;
            try { parsed = string.IsNullOrWhiteSpace(token) ? new Version(0, 0) : Version.Parse(token); }
            catch { parsed = new Version(0, 0); }
            var rev = raw?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
                .FirstOrDefault(x => x.StartsWith("rev", StringComparison.OrdinalIgnoreCase));
            if (rev is not null && int.TryParse(rev[3..], out var revInt) && parsed.Revision < 0 && parsed.Build >= 0)
                parsed = new Version(parsed.Major, parsed.Minor, parsed.Build, revInt);

            return new EngineVersionInfo(token, withoutBuild, parsed,
                EngineVersionInfo.EnvFallbackSource, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static object? Prop(Type t, string name) =>
        t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
}
