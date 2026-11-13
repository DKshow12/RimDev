using RimDev.Core.Environment;

namespace RimDev.Cli.Commands;

public static class EnvCommand
{
    public static int Run(string[] args)
    {
        var json = args.Contains("--json");
        GameEnv env;
        try
        {
            env = GameEnvProbe.Discover();
        }
        catch (GameEnvProbeException ex)
        {
            Console.Error.WriteLine("RD1001: " + ex.Message);
            return 4;
        }

        if (json)
        {
            Console.WriteLine(
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    gameRoot = env.GameRoot,
                    managedDir = env.ManagedDir,
                    managedDllCount = env.ManagedDllCount,
                    modsDir = env.ModsDir,
                    dataDir = env.DataDir,
                    workshopDir = env.WorkshopDir,
                    workshopExists = env.WorkshopExists,
                    configDir = env.ConfigDir,
                    modsConfigPath = env.ModsConfigPath,
                    playerLogPath = env.PlayerLogPath,
                    prevPlayerLogPath = env.PrevPlayerLogPath,
                    prevPlayerLogExists = env.PrevPlayerLogExists,
                    versionRaw = env.VersionRaw,
                    versionConfig = env.VersionConfig,
                    targetVersion = env.TargetVersion
                },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Console.WriteLine("游戏根        " + env.GameRoot);
        Console.WriteLine("托管程序集    " + env.ManagedDir + $"  ({env.ManagedDllCount} 个 dll)");
        Console.WriteLine("官方内容      " + env.DataDir);
        Console.WriteLine("本地 mod      " + env.ModsDir);
        Console.WriteLine("工坊 mod      " + env.WorkshopDir + (env.WorkshopExists ? "" : "   [不存在]"));
        Console.WriteLine("配置目录      " + env.ConfigDir);
        Console.WriteLine("  ModsConfig  " + env.ModsConfigPath);
        Console.WriteLine("  Player.log  " + env.PlayerLogPath + (env.PrevPlayerLogExists ? "   (含 Player-prev.log)" : ""));
        Console.WriteLine("版本          Version.txt=\"" + env.VersionRaw + "\"  ModsConfig=\"" + env.VersionConfig + "\"");
        Console.WriteLine("目标版本      " + env.TargetVersion);

        if (env.ManagedDllCount != GameEnvProbe.ExpectedManagedDllCount)
            Console.WriteLine($"RD1002 (warning): Managed dll 数为 {env.ManagedDllCount}，与实测基线 {GameEnvProbe.ExpectedManagedDllCount} 不同");

        return 0;
    }
}
