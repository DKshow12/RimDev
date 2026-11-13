using System.Text.Json;
using RimDev.Core.Catalog;
using RimDev.Core.Defs;
using RimDev.Core.Environment;

namespace RimDev.Cli.Commands;

/// <summary>
/// <c>rimdev order [--json] [--explain]</c>
///
/// 用途：把**引擎语义的 mod 加载顺序**摊开给人看，并同时给出每个 mod 的**文件夹优先级**
/// （<c>foldersToLoadDescendingOrder</c>，索引 0 优先级最高）—— 这两个量合起来就是
/// 「同名 def 谁覆盖谁」的全部判据，是第 3 期 Task 3 遮蔽规则的输入。
///
/// ★ 输出契约（门禁 6 钉的就是它）：**默认模式每行一个 mod，正好 <c>Entries.Count</c> 行**
/// （本机 17 行：1 个 Core + 5 个 DLC(knownExpansions) + 11 个第三方 activeMods），
/// 便于 <c>rimdev order | Measure-Object -Line</c> 这类直接计数。
/// <c>--explain</c> 才会额外打印缩进的目录明细行，**默认不加**（否则行数就不再等于 mod 数）。
///
/// 退出码：0 成功；3 参数错；4 环境不满足（缺游戏根）。
/// </summary>
public static class OrderCommand
{
    /// <summary>本命令认得的开关（★ Task 9c：白名单的唯一声明点，紧挨着读它们的代码）。</summary>
    public static readonly CommandFlagSet Flags = new("order", PrintHelp,
        new FlagSpec("--json"),
        new FlagSpec("--explain"));

    public static int Run(EnvironmentResolution cli)
    {
        var args = cli.Args;

        var json = args.Contains("--json");
        var explain = args.Contains("--explain");

        // ★★ 9b：环境**只有一个来源**（EnvironmentResolution）：不再自己 GameEnvProbe.Discover()。
        if (!cli.TryGetEnv(out var env)) return EnvironmentResolution.EnvExitCode;

        var activeIds = ModCatalog.ReadActiveMods(env.ModsConfigPath);
        var expansions = LoadOrder.ReadKnownExpansions(env.ModsConfigPath);
        var cat = ModCatalog.Build(env, env.TargetVersion, activeIds).Catalog;
        var order = LoadOrder.Build(env, cat, activeIds, expansions);

        if (json)
        {
            var payload = new
            {
                schemaVersion = "1.0",
                command = "order",
                // ★ 口径必须随数字一起报出来，否则消费方分不清「17」是 activeMods 的条数还是加载序的条目数。
                config = new
                {
                    modsConfigPath = env.ModsConfigPath,
                    // ★ 数组用 .Length（.Count 是 Enumerable.Count 扩展方法 —— 写成属性会得到 CS0828「方法组」）
                    activeModsCount = activeIds.Length,
                    knownExpansionsCount = expansions.Length,
                    corePackageId = order.CorePackageId,
                    skippedAlreadyOrdered = order.SkippedAlreadyOrdered,
                    unresolvedPackageIds = order.UnresolvedPackageIds
                },
                orderCount = order.Entries.Count,
                order = order.Entries.Select(e => new
                {
                    order = e.Order,
                    packageId = e.PackageId,
                    displayName = e.Mod.ShortName,
                    locator = e.Mod.Locator,
                    root = e.Mod.RootPath,
                    origin = e.Origin.ToString(),
                    source = e.Mod.Source.ToString(),
                    folders = FolderNames(e),
                    folderIndex = e.Folders.Select((f, i) => new
                    {
                        index = i,
                        folder = f,
                        relative = RelName(e.Mod.RootPath, f),
                        exists = Directory.Exists(f)
                    }).ToList(),
                    xmlFileCount = e.Folders.Count == 0 ? 0 : DefFileEnumerator.Enumerate(e.Folders).Count
                }).ToList()
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        // ★ 默认模式：一行一个 mod（= Entries.Count 行）
        foreach (var e in order.Entries)
        {
            Console.WriteLine(
                $"[{e.Order,2}] {e.PackageId}"
                + $"  origin={e.Origin}"
                + $"  source={e.Mod.Source}"
                + $"  folders={e.Folders.Count}"
                + $"  xml={XmlCount(e)}"
                + $"  {FoldersInline(e)}");
        }

        if (explain)
        {
            Console.WriteLine();
            Console.WriteLine($"（--explain：目录优先级明细；索引 0 = 优先级最高，前面的会遮蔽后面的）");
            foreach (var e in order.Entries)
            {
                Console.WriteLine($"[{e.Order,2}] {e.PackageId}");
                for (var i = 0; i < e.Folders.Count; i++)
                {
                    var f = e.Folders[i];
                    var mark = Directory.Exists(f) ? " " : "✗";
                    Console.WriteLine($"     [{i}] {mark} {RelName(e.Mod.RootPath, f),-24} {f}");
                }
            }
        }

        return 0;
    }

    private static int XmlCount(LoadOrderEntry e) =>
        e.Folders.Count == 0 ? 0 : DefFileEnumerator.Enumerate(e.Folders).Count;

    /// <summary>单行里展示「文件夹优先级」：<c>[0]&lt;root&gt; [1]1.6</c>（相对各自 mod 根，短且可读）。</summary>
    private static string FoldersInline(LoadOrderEntry e) =>
        e.Folders.Count == 0
            ? "[]"
            : string.Join(" ", e.Folders.Select((f, i) => $"[{i}]{RelName(e.Mod.RootPath, f)}"));

    private static List<string> FolderNames(LoadOrderEntry e) =>
        e.Folders.Select(f => RelName(e.Mod.RootPath, f)).ToList();

    /// <summary>mod 根显示成 <c>&lt;root&gt;</c>（回退分支里根目录也在列表里，必须能一眼认出来）。</summary>
    private static string RelName(string modRoot, string folder)
    {
        if (string.Equals(
                Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(modRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            return "<root>";

        var rel = Path.GetRelativePath(modRoot, folder);
        return rel.Length == 0 ? "." : rel;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("rimdev order [--json] [--explain]");
        Console.WriteLine();
        Console.WriteLine("打印引擎语义的 mod 加载顺序（Core → knownExpansions → activeMods）与每个 mod 的文件夹优先级。");
        Console.WriteLine();
        Console.WriteLine("  （默认）        每行一个 mod —— 行数 == 加载序条目数（本机 17：1 Core + 5 DLC + 11 第三方）");
        Console.WriteLine("  --explain       额外打印缩进的目录明细行（索引 0 = 优先级最高）");
        Console.WriteLine("  --json          机器可读输出（含 config / order / folderIndex / xmlFileCount）");
        Console.WriteLine("  --help, -h      打印本帮助（退出码 0）");
        Console.WriteLine();
        Console.WriteLine("退出码：0 成功；4 环境不满足（找不到游戏根）");
    }
}
