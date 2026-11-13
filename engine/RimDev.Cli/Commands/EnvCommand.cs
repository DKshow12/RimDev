using RimDev.Core.Environment;
using RimDev.Core.Metadata;

namespace RimDev.Cli.Commands;

public static class EnvCommand
{
    public static int Run(string[] args)
    {
        var json = args.Contains("--json");
        var gameRoot = ValueOf(args, "--game-root");
        var workshopRoot = ValueOf(args, "--workshop-root");
        // ★ 第 3 期 Task 9：切换入口（_rimdev\rimdev.cmd）转发过来的「我现在在哪一边」。
        var profileFromShim = ValueOf(args, "--rimdev-profile");
        var invokedVia = ValueOf(args, "--rimdev-invoked-via") ?? "direct";
        // 生效 profile 名：shim 转发的名字 > 环境变量 RIMDEV_PROFILE > "(default)"。
        var envProfile = System.Environment.GetEnvironmentVariable("RIMDEV_PROFILE");
        var effectiveProfile = profileFromShim
                               ?? (string.IsNullOrWhiteSpace(envProfile) ? "(default)" : envProfile);

        GameEnv env;
        try
        {
            env = GameEnvProbe.Discover(explicitRoot: gameRoot, explicitWorkshopRoot: workshopRoot);
        }
        catch (GameEnvProbeException ex)
        {
            Console.Error.WriteLine("RD1001: " + ex.Message);
            return 4;
        }

        var envSourceGameRoot = gameRoot is not null ? "--game-root"
            : !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("RIMWORLD_DIR")) ? "RIMWORLD_DIR"
            : "auto";
        var envSourceWorkshop = workshopRoot is not null ? "--workshop-root"
            : !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("RIMWORLD_WORKSHOP_DIR")) ? "RIMWORLD_WORKSHOP_DIR"
            : "auto";

        var identity = env.Identity();
        var typeResolution = ResolveTypes(env);

        if (json)
        {
            Console.WriteLine(
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    profile = effectiveProfile,
                    invokedVia = invokedVia,
                    layout = env.Layout.ToName(),
                    layoutDescription = env.Layout.ToDescription(),
                    gameRoot = env.GameRoot,
                    gameRootSource = envSourceGameRoot,
                    managedDir = env.ManagedDir,
                    managedDllCount = env.ManagedDllCount,
                    assembliesInManaged = env.ManagedDllCount,
                    assemblyCSharpPath = env.AssemblyCSharpPath,
                    modsDir = env.ModsDir,
                    dataDir = env.DataDir,
                    workshopRoot = env.WorkshopDir,
                    workshopDir = env.WorkshopDir,
                    workshopRootSource = envSourceWorkshop,
                    workshopExists = env.WorkshopExists,
                    configDir = env.ConfigDir,
                    modsConfigPath = env.ModsConfigPath,
                    playerLogPath = env.PlayerLogPath,
                    prevPlayerLogPath = env.PrevPlayerLogPath,
                    prevPlayerLogExists = env.PrevPlayerLogExists,
                    versionRaw = env.VersionRaw,
                    versionConfig = env.VersionConfig,
                    targetVersion = env.TargetVersion,
                    runtime = ManagedAssemblyIdentity.RuntimeDescription,
                    assemblyCSharp = new
                    {
                        fileName = identity.Assembly.FileName,
                        assemblyName = identity.Assembly.AssemblyName,
                        assemblyVersion = identity.Assembly.AssemblyVersion,
                        mvid = identity.Assembly.Mvid,
                        metadataVersion = identity.Assembly.MetadataVersion,
                        fileBytes = identity.Assembly.FileBytes,
                        typeDefs = identity.Assembly.TypeDefinitionCount,
                        error = identity.Assembly.Error
                    },
                    typeResolution = typeResolution.ToDictionary(
                        r => r.LogicalName,
                        r => r.ResolvedFullName
                             ?? $"<未解析> {Truth(r)}"),
                    typeResolutionDetail = typeResolution.Select(r => new
                    {
                        logicalName = r.LogicalName,
                        candidates = r.Candidates,
                        resolvedFullName = r.ResolvedFullName,
                        resolvedCandidate = r.ResolvedCandidate,
                        failure = r.Failure
                    })
                },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Console.WriteLine("profile       " + effectiveProfile + "   （invokedVia=" + invokedVia + "）");
        Console.WriteLine("布局          " + env.Layout.ToDescription());
        Console.WriteLine("游戏根        " + env.GameRoot + $"   [{envSourceGameRoot}]");
        Console.WriteLine("托管程序集    " + env.ManagedDir + $"  ({env.ManagedDllCount} 个 dll)");
        Console.WriteLine("  Assembly-CSharp  " + identity.Assembly.ToOneLine());
        Console.WriteLine("运行时        " + ManagedAssemblyIdentity.RuntimeDescription);
        Console.WriteLine("官方内容      " + env.DataDir);
        Console.WriteLine("本地 mod      " + env.ModsDir);
        Console.WriteLine("工坊 mod      " + env.WorkshopDir + $"   [{envSourceWorkshop}]" + (env.WorkshopExists ? "" : "   [不存在]"));
        Console.WriteLine("配置目录      " + env.ConfigDir);
        Console.WriteLine("  ModsConfig  " + env.ModsConfigPath);
        Console.WriteLine("  Player.log  " + env.PlayerLogPath + (env.PrevPlayerLogExists ? "   (含 Player-prev.log)" : ""));
        Console.WriteLine("版本          Version.txt=\"" + env.VersionRaw + "\"  ModsConfig=\"" + env.VersionConfig + "\"");
        Console.WriteLine("目标版本      " + env.TargetVersion);
        Console.WriteLine("类型名解析（候选回退；命中哪支即记哪支）");

        foreach (var r in typeResolution)
            Console.WriteLine("  " + r.LogicalName.PadRight(15) + " ← " + string.Join(" | ", r.Candidates) +
                              "  ⇒ " + (r.ResolvedFullName ?? "未解析: " + Truth(r)));

        // ★ 布局相关的 dll 数基线：1.6 有 108；1.7.x 线的 Managed 只剩 0–2 个，**没有基线**。
        //   把无基线的情况也显式打出来（而不是沉默），否则「1.7 上不报警」会被读成「检查过了」。
        var expected = GameEnvProbe.ExpectedManagedDllCountFor(env.Layout);
        if (expected is { } n)
        {
            if (env.ManagedDllCount != n)
                Console.WriteLine($"RD1002 (warning): Managed dll 数为 {env.ManagedDllCount}，与 1.6 布局基线 {n} 不同");
        }
        else
        {
            Console.WriteLine($"注意：布局 {env.Layout.ToName()} 没有 Managed dll 数基线（实测 {env.ManagedDllCount} 个，仅打印不作断言）");
        }

        return 0;
    }

    private static string Truth(TypeResolution r) => r.Failure ?? "候选全名都不存在";

    /// <summary>
    /// 解析候选表。**载入失败不算 env 失败**：net8 产物遇上 1.7 的 net10 程序集会抛
    /// ReflectionTypeLoadException，那正是「布局与产物不匹配」的正常表现，必须如实打印而不是崩掉。
    /// </summary>
    private static IReadOnlyList<TypeResolution> ResolveTypes(GameEnv env)
    {
        try
        {
            var catalog = ManagedAssemblyCatalog.Load(env.ManagedDir);
            return TypeNameResolver.ResolveAll(catalog);
        }
        catch (Exception ex)
        {
            var reason = $"程序集无法载入（{ex.GetType().Name}: {Trim(ex.Message)}）";
            return TypeNameResolver.Unavailable(reason);
        }
    }

    private static string Trim(string s) => s.Length <= 160 ? s : s[..160] + "…";

    /// <summary>取 <c>--flag value</c> 的值；也支持 <c>--flag=value</c>。</summary>
    private static string? ValueOf(string[] args, string flag)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith(flag + "=", StringComparison.Ordinal))
                return args[i][(flag.Length + 1)..];
            if (args[i] == flag && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                return args[i + 1];
        }
        return null;
    }
}
