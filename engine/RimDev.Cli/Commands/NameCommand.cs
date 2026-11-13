using System.Text.Json;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;
using RimDev.Core.Metadata;

namespace RimDev.Cli.Commands;

public static class NameCommand
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>本命令认得的开关（★ Task 9c：白名单的唯一声明点，紧挨着读它们的代码）。</summary>
    public static readonly CommandFlagSet Flags = new("name", PrintHelp,
        new FlagSpec("--kind", true, "type|field|property|method|enum|def"),
        new FlagSpec("--owner", true, "Type"),
        new FlagSpec("--index"),
        new FlagSpec("--json"));

    /// <summary>`name --help`（帮助不依赖环境探测：没装游戏也能看）。</summary>
    public static void PrintHelp()
    {
        Console.WriteLine("rimdev name check <名> [options]");
        Console.WriteLine();
        Console.WriteLine("查一个名字在 RimWorld 程序集/Def 索引里是否存在，并在不存在时给候选（含大小写差异）。");
        Console.WriteLine();
        Console.WriteLine("  --kind <种类>     type | field | property | method | enum | def（缺省 = 不限种类）");
        Console.WriteLine("  --owner <Type>    只在该类型上找成员（owner-less 搜索会跨全部类型）");
        Console.WriteLine("  --index           把 --kind def 接到落盘的 Def 索引上（未建索引时如实提示，不猜）");
        Console.WriteLine("  --json            机器可读输出");
        Console.WriteLine("  --help, -h        打印本帮助（退出码 0）");
        Console.WriteLine();
        Console.WriteLine("退出码：0 存在；2 不存在（不是工具坏了）；3 参数错；4 环境不满足");
    }

    public static int Run(EnvironmentResolution cli)
    {
        var args = cli.Args;
        // rimdev name check <name> [--kind type|field|property|method|enum|def] [--owner <Type>] [--json]
        if (args.Length < 3 || args[1] != "check")
        {
            Console.Error.WriteLine("usage: rimdev name check <name> [--kind type|field|property|method|enum|def] [--owner <Type>] [--json]");
            return 3;
        }

        var name = args[2];
        var kind = ParseKind(Option(args, "--kind"));
        var owner = Option(args, "--owner");
        var json = args.Contains("--json");

        // ★★ 9b：先取环境（RD1001 + 退出码 4），再载入程序集。
        //   旧实现的 Discover() 在 `--game-root` 无效时会静默回落到自动探测，或被 catch(Exception) 吞成 RD4002。
        if (!cli.TryGetEnv(out var env)) return EnvironmentResolution.EnvExitCode;

        ManagedAssemblyCatalog cat;
        try
        {
            cat = ManagedAssemblyCatalog.Load(env.ManagedDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{Codes.AssemblyLoadFailed}: {ex.Message}");
            return 4;
        }

        // ★ --index：把 --kind def 接到落盘的 Def 索引上（不传时如实报「尚未实现」，不猜）。
        RimDev.Core.Defs.DefIndex? defIndex = null;
        if (args.Contains("--index"))
        {
            try
            {
                // ★ 9b：与上面同一个环境对象（旧实现这里又 Discover() 了一次 —— 第二处现场探测）。
                // ★ 预检修正 ④：缓存目录只有一个真源（DefIndexStore.DefaultCacheDir）。
                //   不要在这里重写 Path.Combine(Path.GetDirectoryName(env2.ModsDir), ...) ——
                //   那个公式拼出的是 <游戏根>\_rimdev\cache，既不是缓存实际位置、也在工作区之外。
                var store = new RimDev.Core.Defs.DefIndexStore(
                    RimDev.Core.Defs.DefIndexStore.DefaultCacheDir(env));
                // ★ 预检修正 ⑤：schema 不符或 meta 不可读时报 RD1004 并【当作没有索引】，不静默用旧缓存作答。
                var meta = store.Exists() ? store.LoadMeta() : null;
                if (meta is null || meta.SchemaVersion != RimDev.Core.Defs.DefIndexStore.CurrentSchemaVersion)
                {
                    Console.Error.WriteLine(meta is null
                        ? "（缓存索引不存在，先跑 `rimdev index`）"
                        : $"{Codes.CacheSchemaMismatch}: 索引缓存 schema={meta.SchemaVersion} 与当前 {RimDev.Core.Defs.DefIndexStore.CurrentSchemaVersion} 不符，先跑 `rimdev index --rebuild`");
                }
                else
                {
                    defIndex = RimDev.Core.Defs.DefIndex.Build(store.LoadDefs());
                }
            }
            // 环境不满足时如实退到 4（与上面程序集加载失败同一退出码），不要抛未捕获异常
            catch (GameEnvProbeException ex)
            {
                Console.Error.WriteLine($"{Codes.GameRootNotFound}: {ex.Message}");
                return 4;
            }
        }

        var verdict = new NameOracle(cat).Check(name, kind, owner, defIndex);
        var d = verdict.Diagnostic;

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = "1.0",
                tool = new { name = "rimdev", version = "0.1.0" },
                command = "name check",
                query = new { name, kind = kind.ToString(), owner },
                exists = verdict.Exists,
                resolvedKind = verdict.Kind.ToString(),
                diagnostics = new[]
                {
                    new
                    {
                        code = d.Code,
                        severity = d.Severity.ToString().ToLowerInvariant(),
                        scope = d.Scope.ToString().ToLowerInvariant(),
                        message = d.Message,
                        hint = d.Hint,
                        suggestions = d.Suggestions,
                        fingerprint = d.Fingerprint
                    }
                }
            }, Json));
        }
        else
        {
            Console.WriteLine((verdict.Exists ? "✓ 存在  " : "✗ 不存在 ") + d.Message);
            if (d.Hint is not null) Console.WriteLine("  提示: " + d.Hint);
            if (d.Suggestions is { Count: > 0 })
            {
                Console.WriteLine(verdict.Exists && verdict.Kind == NameKind.Unknown ? "  命中:" : "  候选:");
                foreach (var s in d.Suggestions) Console.WriteLine("    - " + s);
            }
        }

        return d.Severity == Severity.Error ? 2 : 0;
    }

    private static NameKind ParseKind(string? s) => s?.ToLowerInvariant() switch
    {
        "type" => NameKind.Type,
        "field" => NameKind.Field,
        "property" or "prop" => NameKind.Property,
        "method" => NameKind.Method,
        "enum" => NameKind.EnumMember,
        "def" => NameKind.DefName,
        _ => NameKind.Unknown
    };

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
