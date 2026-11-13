using System.Text.Json;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;
using RimDev.Core.Metadata;

namespace RimDev.Cli.Commands;

public static class NameCommand
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static int Run(string[] args)
    {
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

        ManagedAssemblyCatalog cat;
        try
        {
            cat = ManagedAssemblyCatalog.Load(GameEnvProbe.Discover().ManagedDir);
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
                var env2 = GameEnvProbe.Discover();
                // ★ 预检修正 ④：缓存目录只有一个真源（DefIndexStore.DefaultCacheDir）。
                //   不要在这里重写 Path.Combine(Path.GetDirectoryName(env2.ModsDir), ...) ——
                //   那个公式拼出的是 <游戏根>\_rimdev\cache，既不是缓存实际位置、也在工作区之外。
                var store = new RimDev.Core.Defs.DefIndexStore(
                    RimDev.Core.Defs.DefIndexStore.DefaultCacheDir(env2));
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
