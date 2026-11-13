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

        var verdict = new NameOracle(cat).Check(name, kind, owner);
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
