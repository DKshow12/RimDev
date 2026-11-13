namespace RimDev.Core.Environment;

public sealed class GameEnvProbeException : Exception
{
    public GameEnvProbeException(string message) : base(message) { }
}

/// <summary>
/// 探测环世界安装位置与**布局**。
///
/// 顺序：显式根（<c>--game-root</c> / 环境变量 RIMWORLD_DIR）→ 从工作区向上找 → Steam 注册表 → 常见默认路径。
/// 全部失败抛 <see cref="GameEnvProbeException"/>（CLI 报 RD1001）。
///
/// ★★ 布局判据（第 3 期 Task 9）：**只看 Assembly-CSharp.dll 落在哪**，不看 exe 文件名。
///   · <c>&lt;root&gt;\RimWorldWin64_Data\Managed\Assembly-CSharp.dll</c> 存在 ⇒ <see cref="RimWorldLayout.Classic16"/>，
///     ManagedDir = 该 Managed 目录；
///   · 否则 <c>&lt;root&gt;\Assembly-CSharp.dll</c> 存在 ⇒ <see cref="RimWorldLayout.Waylith17"/>，ManagedDir = 游戏根；
///   · 两者都无 ⇒ 抛 RD1001（**不许退化性地把根目录当 Managed 用**：那会把「环境不满足」伪装成「能跑」）。
///
/// ★ 为什么不能用 exe 名（实测，2026-10-24 本机两份 1.7 副本）：
///   Steam 副本只有 <c>RimWorld.exe</c>；桌面副本 <c>RimWorldWin64.exe</c> 与 <c>RimWorld.exe</c> 都在。
///   任何「靠文件名」的判据在这两份副本上结论相反。
///
/// ★ 「Managed 里应有 108 个 dll」这条基线**已废掉**（原 <c>ExpectedManagedDllCount = 108</c>）：
///   1.6 有 108 个，1.7.x 线的 Managed 只剩 0–2 个（本机实测 2：`0Harmony.dll` 与 `0Harmony .dll`），
///   死守 108 会把 1.7 判成环境异常。改为按布局给基线，见 <see cref="ExpectedManagedDllCountFor"/>。
/// </summary>
public static class GameEnvProbe
{
    /// <summary>
    /// ★★ 第 3 期 Task 9b：**环境解析的唯一来源（ambient）**。
    ///
    /// CLI 层把全局选项（<c>--game-root</c> / <c>--workshop-root</c>）解析一次、构造出 <see cref="GameEnv"/>，
    /// 然后 <see cref="SetResolved"/> 设在这里；此后**所有**调用点（包括 Core 内部那些历史遗留的
    /// <c>GameEnvProbe.Discover()</c>）拿到的都是同一份 —— 这正是「开关被静默忽略、命令照常分析自动探测到的那份安装」
    /// 那类缺陷的封堵点。
    ///
    /// 为什么不靠「每个命令自己把参数传下来」：Core 的检查器（FieldNameCheck / ReferenceCheck /
    /// LoadFolderCheck）也各自 <c>Discover()</c> 一次，任何一处漏传就又是一次静默错答。
    /// </summary>
    public static GameEnv? Resolved { get; private set; }

    /// <summary>设置/清除 ambient 解析结果（<c>null</c> = 回到现场探测；自测靠它做可重入的还原）。</summary>
    public static void SetResolved(GameEnv? env) => Resolved = env;

    /// <summary>
    /// 按布局取「Managed 里 dll 数量」的参考基线；<c>null</c> = 该布局**没有**这条基线（不做断言）。
    /// 1.6 的 108 是第 1/2 期实测值，保留它才能继续抓「Managed 少了一半 dll」这类环境损坏。
    /// </summary>
    public static int? ExpectedManagedDllCountFor(RimWorldLayout layout) => layout switch
    {
        RimWorldLayout.Classic16 => 108,
        _ => null,
    };

    public const string AssemblyCSharpFileName = "Assembly-CSharp.dll";

    private static readonly string[] DefaultRoots =
    {
        @"C:\Program Files (x86)\Steam\steamapps\common\RimWorld",
        @"C:\Program Files\Steam\steamapps\common\RimWorld",
        @"D:\Steam\steamapps\common\RimWorld",
        @"D:\SteamLibrary\steamapps\common\RimWorld",
        @"E:\SteamLibrary\steamapps\common\RimWorld",
    };

    /// <summary>布局判据（纯函数，不触碰注册表/默认路径列表，便于自测直接打表）。</summary>
    public static RimWorldLayout DetectLayout(string gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
            return RimWorldLayout.Unknown;

        if (File.Exists(Path.Combine(gameRoot, "RimWorldWin64_Data", "Managed", AssemblyCSharpFileName)))
            return RimWorldLayout.Classic16;

        if (File.Exists(Path.Combine(gameRoot, AssemblyCSharpFileName)))
            return RimWorldLayout.Waylith17;

        return RimWorldLayout.Unknown;
    }

    /// <summary>按布局给 ManagedDir（Unknown 时返回 1.6 形状的路径，仅供报错信息使用）。</summary>
    public static string ManagedDirFor(string gameRoot, RimWorldLayout layout) => layout switch
    {
        RimWorldLayout.Waylith17 => Path.GetFullPath(gameRoot),
        _ => Path.Combine(Path.GetFullPath(gameRoot), "RimWorldWin64_Data", "Managed"),
    };

    /// <summary>
    /// 是不是「游戏根」。判据 = 目录存在 且（有任一已知的启动 exe 或 任一布局的 Assembly-CSharp.dll）。
    /// 仍然接受 exe 名，但**只用于「这是不是根」**，绝不用它区分布局。
    /// </summary>
    public static bool IsGameRoot(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        if (File.Exists(Path.Combine(dir, "RimWorldWin64.exe"))) return true;
        if (File.Exists(Path.Combine(dir, "RimWorld.exe"))) return true;
        return DetectLayout(dir) != RimWorldLayout.Unknown;
    }

    /// <param name="explicitRoot"><c>--game-root</c>（优先级最高；实际来源由调用方另行回显）。</param>
    /// <param name="startDir">向上找根的起点（缺省 = 当前目录）。</param>
    /// <param name="explicitWorkshopRoot"><c>--workshop-root</c>（缺省 = 环境变量 RIMWORLD_WORKSHOP_DIR → 按 Steam 布局推导）。</param>
    public static GameEnv Discover(string? explicitRoot = null, string? startDir = null,
        string? explicitWorkshopRoot = null)
    {
        // ★★ 第 3 期 Task 9b：**ambient 唯一来源优先**。
        //   CLI 把关卡解析一次后 SetResolved(...)，此后所有调用点（含 Core 内部）都拿到同一份。
        if (Resolved is { } ambient) return ambient;

        // ★★ 显式给了根就**必须是有效安装**：不许静默回落到自动探测。
        //   旧行为：`--game-root C:\__no_such__` 时这里回落成「从 cwd 向上找」，于是命令照常
        //   分析**自动探测到的那份安装**并给出完全正常的输出 —— 用户无法从输出里看出「我扫的不是我指定的那份」。
        if (explicitRoot is not null)
        {
            if (!Directory.Exists(explicitRoot))
                throw new GameEnvProbeException(
                    $"--game-root 指定的路径不存在或不是目录：{explicitRoot}（不会回落到自动探测；请修正路径或去掉该开关）");

            if (!IsGameRoot(explicitRoot))
                throw new GameEnvProbeException(
                    $"--game-root 指定的目录不是有效的环世界安装：{explicitRoot}"
                    + $"（既没有 RimWorldWin64_Data\\Managed\\{AssemblyCSharpFileName}，也没有根目录的 {AssemblyCSharpFileName}）");
        }

        var root = explicitRoot
                   ?? System.Environment.GetEnvironmentVariable("RIMWORLD_DIR")
                   ?? FindByWalkingUp(startDir ?? Directory.GetCurrentDirectory())
                   ?? FindBySteamRegistry()
                   ?? DefaultRoots.FirstOrDefault(IsGameRoot);

        if (root is null || !IsGameRoot(root))
            throw new GameEnvProbeException(
                "找不到 RimWorld 游戏根目录。请设置环境变量 RIMWORLD_DIR / 用 --game-root 指定，或在游戏根目录下运行。");

        return FromRoot(root, explicitWorkshopRoot);
    }

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

    public static GameEnv FromRoot(string gameRoot, string? workshopRoot = null)
    {
        gameRoot = Path.GetFullPath(gameRoot);

        // ★★ 第 3 期 Task 9b：`--workshop-root` 也必须在**解析期**校验。
        //   旧行为：它只被 env 回显、其余命令连读都不读；即便读了也不校验 —— 指到不存在的目录时
        //   工坊扫描会静默变成「0 个工坊 mod」，而输出看起来和「没传」一模一样。
        if (workshopRoot is not null)
        {
            if (!Directory.Exists(workshopRoot))
                throw new GameEnvProbeException(
                    $"--workshop-root 指定的路径不存在或不是目录：{workshopRoot}（不会回落到自动探测；请修正路径或去掉该开关）");

            workshopRoot = Path.GetFullPath(workshopRoot);
        }

        var layout = DetectLayout(gameRoot);
        if (layout == RimWorldLayout.Unknown)
            throw new GameEnvProbeException(
                "RD1001: 游戏根目录里既没有 RimWorldWin64_Data\\Managed\\Assembly-CSharp.dll（1.6 布局），" +
                "也没有根目录的 Assembly-CSharp.dll（1.7 布局）。请确认 --game-root / RIMWORLD_DIR 指向游戏根。");

        var managed = ManagedDirFor(gameRoot, layout);
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
            WorkshopDir: workshopRoot ?? ResolveWorkshopDir(gameRoot),
            ConfigDir: config,
            ModsConfigPath: modsConfigPath,
            // ★ Task 15（Docs/10 §6.2 的裁决）：1.7 的日志在 <c>&lt;localLow&gt;\Logs\</c> 子目录，
            //   不是 <c>&lt;localLow&gt;\</c> 根目录。本机实测：
            //     &lt;localLow&gt;\Player.log          → 不存在
            //     &lt;localLow&gt;\Logs\Player.log     → 2,470,288 B（2026-11-11）1
            //   旧代码只拼根目录 ⇒ 不带 <c>--path</c> 的日志命令永远报「文件不存在」。
            //   做**二级回退**：优先根目录（旧版本行为，也许某些环境确实如此），
            //   不存在则用 <c>Logs\</c> 子目录（不管文件是否存在都拼对那个）。
            PlayerLogPath: ResolvePlayerLogPath(localLow, "Player.log"),
            PrevPlayerLogPath: ResolvePlayerLogPath(localLow, "Player-prev.log"),
            VersionRaw: versionRaw,
            VersionConfig: versionConfig,
            TargetVersion: ToMajorMinor(versionSource),
            ManagedDllCount: Directory.Exists(managed)
                ? Directory.GetFiles(managed, "*.dll").Length
                : 0,
            Layout: layout,
            AssemblyCSharpPath: Path.Combine(managed, AssemblyCSharpFileName));
    }

    /// <summary>
    /// ★ Task 15：解析 Player.log 路径。
    /// <para>优先 <c>&lt;localLow&gt;\&lt;file&gt;</c>（旧版行为）；不存在时用
    /// <c>&lt;localLow&gt;\Logs\&lt;file&gt;</c>（1.7 实测位置）。</para>
    /// <para>注意：即使两个都不存在，也返回 <c>Logs\</c> 那个 —— 让上层报
    /// 「文件不存在」时指向最可能的位置，而不是一个已知不对的旧路径。</para>
    /// </summary>
    private static string ResolvePlayerLogPath(string localLow, string fileName)
    {
        var atRoot = Path.Combine(localLow, fileName);
        if (File.Exists(atRoot)) return atRoot;
        var inLogs = Path.Combine(localLow, "Logs", fileName);
        if (File.Exists(inLogs)) return inLogs;
        // 两个都不存在：返回新版位置（更可能对）
        return inLogs;
    }

    private static string ResolveWorkshopDir(string gameRoot)
    {
        var env = System.Environment.GetEnvironmentVariable("RIMWORLD_WORKSHOP_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env;

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
