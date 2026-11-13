using RimDev.Core.Environment;

namespace RimDev.SelfTest.Cases;

public static class EnvCases
{
    public static void Run()
    {
        T.Group("env");

        var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);

        T.Case("游戏根目录探测正确", () =>
            T.Eq(env.GameRoot.TrimEnd('\\'), Facts.GameRoot.TrimEnd('\\'), "GameRoot"));

        T.Case("从上溯找到游戏根（工作区=Mods 的上一级）", () =>
        {
            var modsDir = Path.Combine(Facts.GameRoot, "Mods");
            var found = GameEnvProbe.FindByWalkingUp(modsDir);
            T.Eq(found?.TrimEnd('\\'), Facts.GameRoot.TrimEnd('\\'), "WalkUp 结果");
        });

        T.Case("Managed 目录 dll 数 = 108", () =>
            T.Eq(env.ManagedDllCount, Facts.ManagedDllCount, "ManagedDllCount"));

        T.Case("版本口径取 ModsConfig 而非 Version.txt", () =>
        {
            T.Contains(env.VersionRaw, "1.6.", "VersionRaw");
            T.Eq(env.TargetVersion, "1.6", "TargetVersion");
            T.True(env.VersionConfig.Length > 0 || env.VersionRaw.Length > 0, "至少有一个版本来源");
        });

        T.Case("配置与日志路径存在", () =>
        {
            T.True(File.Exists(env.ModsConfigPath), "ModsConfig.xml 存在");
            T.True(Directory.Exists(env.ConfigDir), "Config 目录存在");
            T.True(File.Exists(env.PlayerLogPath), "Player.log 存在");
        });

        T.Case("工坊目录存在且为 294100", () =>
        {
            T.True(env.WorkshopExists, "工坊目录存在");
            T.Contains(env.WorkshopDir, "294100", "工坊目录名");
        });

        T.Case("官方内容目录 6 个（Core + 5 DLC）", () =>
        {
            var dirs = Directory.GetDirectories(env.DataDir).Select(Path.GetFileName).ToArray();
            foreach (var expected in new[] { "Core", "Royalty", "Ideology", "Biotech", "Anomaly", "Odyssey" })
                T.True(dirs.Contains(expected), $"Data\\{expected} 存在");
        });

        T.Case("ToMajorMinor 解析", () =>
        {
            T.Eq(GameEnvProbe.ToMajorMinor("1.6.4871 rev591"), "1.6", "rev 后缀");
            T.Eq(GameEnvProbe.ToMajorMinor("1.6.4871"), "1.6", "无后缀");
            T.Eq(GameEnvProbe.ToMajorMinor(""), "", "空串");
        });
    }
}
