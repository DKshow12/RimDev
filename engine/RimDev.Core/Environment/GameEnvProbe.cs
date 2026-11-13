namespace RimDev.Core.Environment;

public sealed class GameEnvProbeException : Exception
{
    public GameEnvProbeException(string message) : base(message) { }
}

/// <summary>
/// 探测环世界安装位置。顺序：环境变量 RIMWORLD_DIR → 从工作区向上找 RimWorldWin64.exe
/// → Steam 注册表 → 常见默认路径。全部失败抛 <see cref="GameEnvProbeException"/>。
/// </summary>
public static class GameEnvProbe
{
    public const int ExpectedManagedDllCount = 108;

    private static readonly string[] DefaultRoots =
    {
        @"C:\Program Files (x86)\Steam\steamapps\common\RimWorld",
        @"C:\Program Files\Steam\steamapps\common\RimWorld",
        @"D:\Steam\steamapps\common\RimWorld",
        @"D:\SteamLibrary\steamapps\common\RimWorld",
        @"E:\SteamLibrary\steamapps\common\RimWorld",
    };

    public static GameEnv Discover(string? explicitRoot = null, string? startDir = null)
    {
        var root = explicitRoot
                   ?? System.Environment.GetEnvironmentVariable("RIMWORLD_DIR")
                   ?? FindByWalkingUp(startDir ?? Directory.GetCurrentDirectory())
                   ?? FindBySteamRegistry()
                   ?? DefaultRoots.FirstOrDefault(IsGameRoot);

        if (root is null || !IsGameRoot(root))
            throw new GameEnvProbeException(
                "找不到 RimWorld 游戏根目录。请设置环境变量 RIMWORLD_DIR，或在游戏根目录下运行。");

        return FromRoot(root);
    }

    public static bool IsGameRoot(string dir) =>
        Directory.Exists(dir) && File.Exists(Path.Combine(dir, "RimWorldWin64.exe"));

    /// <summary>从 startDir 逐级上溯；工作区是 &lt;游戏根&gt;\Mods，因此上一级即命中。</summary>
    public static string? FindByWalkingUp(string startDir)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(startDir));
        while (dir is not null)
        {
            if (IsGameRoot(dir.FullName)) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    public static string? FindBySteamRegistry()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var steamPath = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (string.IsNullOrWhiteSpace(steamPath)) return null;

            var candidate = Path.Combine(steamPath, "steamapps", "common", "RimWorld");
            if (IsGameRoot(candidate)) return candidate;

            // 库文件夹可能在别的盘
            var libraryFolders = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFolders)) return null;

            foreach (var line in File.ReadLines(libraryFolders))
            {
                var t = line.Trim();
                if (!t.StartsWith("\"path\"", StringComparison.Ordinal)) continue;
                var parts = t.Split('"', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;
                var p = parts[^1].Replace("\\\\", "\\");
                var c = Path.Combine(p, "steamapps", "common", "RimWorld");
                if (IsGameRoot(c)) return c;
            }
        }
        catch
        {
            // 注册表不可用属正常，继续走默认路径
        }
        return null;
    }

    public static GameEnv FromRoot(string gameRoot)
    {
        gameRoot = Path.GetFullPath(gameRoot);
        var managed = Path.Combine(gameRoot, "RimWorldWin64_Data", "Managed");
        var data = Path.Combine(gameRoot, "Data");
        var mods = Path.Combine(gameRoot, "Mods");

        var localLow = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "Ludeon Studios", "RimWorld by Ludeon Studios");
        var config = Path.Combine(localLow, "Config");

        var versionRaw = ReadTrimmed(Path.Combine(gameRoot, "Version.txt"));
        var modsConfigPath = Path.Combine(config, "ModsConfig.xml");
        var versionConfig = ReadModsConfigVersion(modsConfigPath);

        // 版本口径冲突（实测：Version.txt=rev590 而 ModsConfig=rev591）时以 ModsConfig 为准，
        // 因为它才是游戏自己写入、并用于选择 loadFolders 版本的那个值。
        var versionSource = string.IsNullOrWhiteSpace(versionConfig) ? versionRaw : versionConfig;

        return new GameEnv(
            GameRoot: gameRoot,
            ManagedDir: managed,
            ModsDir: mods,
            DataDir: data,
            WorkshopDir: ResolveWorkshopDir(gameRoot),
            ConfigDir: config,
            ModsConfigPath: modsConfigPath,
            PlayerLogPath: Path.Combine(localLow, "Player.log"),
            PrevPlayerLogPath: Path.Combine(localLow, "Player-prev.log"),
            VersionRaw: versionRaw,
            VersionConfig: versionConfig,
            TargetVersion: ToMajorMinor(versionSource),
            ManagedDllCount: Directory.Exists(managed)
                ? Directory.GetFiles(managed, "*.dll").Length
                : 0);
    }

    private static string ResolveWorkshopDir(string gameRoot)
    {
        var steamApps = Directory.GetParent(gameRoot)?.Parent?.FullName; // ...\steamapps
        if (steamApps is not null)
        {
            var candidate = Path.Combine(steamApps, "workshop", "content", "294100");
            if (Directory.Exists(candidate)) return candidate;
        }
        return Path.Combine(steamApps ?? gameRoot, "workshop", "content", "294100");
    }

    /// <summary>"1.6.4871 rev591" → "1.6"</summary>
    public static string ToMajorMinor(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "";
        var core = version.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        var parts = core.Split('.');
        return parts.Length >= 2 ? parts[0] + "." + parts[1] : core;
    }

    private static string ReadTrimmed(string path) =>
        File.Exists(path) ? File.ReadAllText(path).Trim() : "";

    /// <summary>从 ModsConfig.xml 取 &lt;version&gt;，避免引 XML 解析器（此处只需一个值）。</summary>
    private static string ReadModsConfigVersion(string path)
    {
        if (!File.Exists(path)) return "";
        try
        {
            var text = File.ReadAllText(path);
            var open = text.IndexOf("<version>", StringComparison.Ordinal);
            if (open < 0) return "";
            open += "<version>".Length;
            var close = text.IndexOf("</version>", open, StringComparison.Ordinal);
            if (close < 0) return "";
            return text[open..close].Trim();
        }
        catch
        {
            return "";
        }
    }
}
