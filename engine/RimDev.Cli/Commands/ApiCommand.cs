using System.Text.Json;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;
using RimDev.Core.Metadata;

namespace RimDev.Cli.Commands;

public static class ApiCommand
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>本命令认得的开关（★ Task 9c：白名单的唯一声明点，紧挨着读它们的代码）。</summary>
    public static readonly CommandFlagSet Flags = new("api", PrintHelp,
        new FlagSpec("--member", true, "名"),
        new FlagSpec("--filter", true, "子串"),
        new FlagSpec("--json"));

    /// <summary>`api --help`（帮助不依赖环境探测：没装游戏也能看）。</summary>
    public static void PrintHelp()
    {
        Console.WriteLine("rimdev api <probe|type|member|ctor|list> <TypeName> [options]");
        Console.WriteLine();
        Console.WriteLine("  probe | member <TypeName> [--member <名>]   类型/成员是否存在（带字段可见性、自定义 XML loader 等细节）");
        Console.WriteLine("  type <TypeName> [--filter <子串>]           类型的成员一览（public / 含非公开）");
        Console.WriteLine("  ctor <TypeName>                             构造器签名");
        Console.WriteLine("  list [--filter <子串>]                      已加载程序集里的类型名");
        Console.WriteLine();
        Console.WriteLine("  --member <名>     只看某个成员（probe / member）");
        Console.WriteLine("  --filter <子串>   按名字收窄（type 的成员列表 / list 的类型列表）");
        Console.WriteLine("  --json            机器可读输出");
        Console.WriteLine("  --help, -h        打印本帮助（退出码 0）");
        Console.WriteLine();
        Console.WriteLine("退出码：0 成功/存在；2 查到问题（类型或成员不存在）；3 参数错；4 环境不满足");
    }

    public static int Run(EnvironmentResolution cli)
    {
        var args = cli.Args;
        // rimdev api <probe|type|member|ctor|list> <TypeName> [--member <name>] [--filter <sub>] [--json]
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: rimdev api <probe|type|member|ctor|list> <TypeName> [--member x] [--filter s] [--json]");
            return 3;
        }

        var sub = args[1];
        var typeName = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : null;
        var member = Option(args, "--member");
        var filter = Option(args, "--filter");
        var json = args.Contains("--json");

        // ★ 9c 收尾：`list` **不需要**类型名（它列的是已加载程序集里的类型名）。旧实现无条件要求
        //   typeName ⇒ `rimdev api list --filter X`（本命令 `--help` 与 `03-实施计划.md` §… 里的示例
        //   `& $cli api list --filter MechWeight` 都这么写）恒 exit 3「缺少类型名」—— 帮助文案在骗人。
        //   同时把「未知子命令」提到取环境之前：`api bogus` 应报「未知 api 子命令」，而不是「缺少类型名」。
        var needsTypeName = sub is "probe" or "type" or "member" or "ctor";
        if (!needsTypeName && sub != "list") return Unknown(sub);
        if (needsTypeName && typeName is null)
        {
            Console.Error.WriteLine($"缺少类型名（rimdev api {sub} <TypeName>）");
            return 3;
        }

        // ★★ 9b：先取环境（RD1001 + 退出码 4），再载入程序集。
        //   旧实现直接 Discover()：`--game-root` 指向不存在的路径时它静默回落到自动探测，
        //   或者抛 GameEnvProbeException 被下面的 catch(Exception) 吞成 RD4002 —— 两条都不是 RD1001 语义。
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

        if (!needsTypeName) return ListTypes(json, cat, filter);   // list：与类型名无关，先短路
        // 走到这里 needsTypeName == true，上面那条 `needsTypeName && typeName is null` 已把空值挡掉；
        // 用 `!` 是**显式**声明这个不变式（否则编译器无法穿过那个条件式判断，会报 CS8604）。
        var idx = cat.MembersOf(typeName!);
        if (idx is null)
        {
            var diag = new Diagnostic(Codes.TypeNotFound, Severity.Error, Scope.Offline,
                $"类型 {typeName} 不存在", "用 rimdev api list --filter <片段> 找相近类型",
                TypeName: typeName, Suggestions: cat.SuggestTypeNames(typeName!, 6));   // 同上：needsTypeName 已保证非空
            return Emit(json, diag, null);
        }

        return sub switch
        {
            "probe" => ProbeMember(json, idx, member),
            "type" => TypeSummary(json, idx, filter),
            "member" => ProbeMember(json, idx, member),
            "ctor" => Ctors(json, idx),
            _ => Unknown(sub)
        };
    }

    private static int ProbeMember(bool json, MemberInfoIndex idx, string? member)
    {
        if (member is null)
        {
            // 无成员名 → 只报类型存在
            var info = new Diagnostic(Codes.MetadataUnavailable, Severity.Info, Scope.Offline,
                $"类型 {idx.FullName} 存在", TypeName: idx.FullName,
                Evidence: new Dictionary<string, string>
                {
                    ["publicFields"] = idx.PublicFieldCount.ToString(),
                    ["allFields"] = idx.AllFieldCount.ToString(),
                    ["properties"] = idx.PropertyCount.ToString(),
                    ["baseType"] = idx.Type.BaseType?.Name ?? ""
                });
            return Emit(json, info, idx.FullName);
        }

        var hit = idx.FindMember(member);
        if (hit is null)
        {
            var suggestions = idx.Suggest(member, 6);
            // 若候选里存在仅大小写不同的项，按 RD4005 报（这是历史真实错误类型）
            var caseOnly = suggestions.FirstOrDefault(s => string.Equals(s, member, StringComparison.OrdinalIgnoreCase));
            var code = caseOnly is not null ? Codes.CaseMismatch : Codes.FieldNotFound;
            var diag = new Diagnostic(code, Severity.Error, Scope.Offline,
                $"{idx.FullName} 上没有成员 {member}", "用建议名替换",
                TypeName: idx.FullName, MemberName: member, Suggestions: suggestions);
            return Emit(json, diag, idx.FullName);
        }

        var detail = new Dictionary<string, string>
        {
            ["kind"] = hit.Kind.ToString(),
            ["type"] = hit.TypeName,
            ["declaredBy"] = hit.DeclaringType,
            ["isPublic"] = hit.IsPublic.ToString(),
            ["isStatic"] = hit.IsStatic.ToString()
        };
        if (hit.Detail is not null) detail["detail"] = hit.Detail;

        // 字段是否带自定义 XML 加载器（决定 XML 该写裸元素还是 <li>）
        if (hit.Kind == MemberKind.Field)
        {
            var f = idx.FindField(member);
            if (f is not null)
            {
                var elem = Nullable.GetUnderlyingType(f.FieldType) ?? f.FieldType;
                detail["elementType"] = elem.Name;
                detail["customXmlLoader"] = ManagedAssemblyCatalog.HasCustomXmlLoader(elem).ToString();
                detail["elementIsList"] = (elem.IsGenericType &&
                    elem.GetGenericTypeDefinition() == typeof(List<>)).ToString();
                if (detail["elementIsList"] == "True")
                    detail["listElementType"] = elem.GetGenericArguments()[0].Name;
            }
        }

        var ok = new Diagnostic(Codes.MetadataUnavailable, Severity.Info, Scope.Offline,
            $"{idx.FullName}.{hit.Name} 存在（{hit.Kind}）", TypeName: idx.FullName, MemberName: hit.Name,
            Evidence: detail);
        return Emit(json, ok, idx.FullName);
    }

    private static int TypeSummary(bool json, MemberInfoIndex idx, string? filter)
    {
        var members = idx.AllMembers();
        if (filter is not null)
            members = members.Where(m => m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                type = idx.FullName,
                baseType = idx.Type.BaseType?.FullName,
                publicFields = idx.PublicFieldCount,
                allFields = idx.AllFieldCount,
                properties = idx.PropertyCount,
                members = members.Select(m => new
                {
                    kind = m.Kind.ToString(),
                    name = m.Name,
                    type = m.TypeName,
                    declaredBy = m.DeclaringType,
                    isPublic = m.IsPublic,
                    detail = m.Detail
                })
            }, Json));
            return 0;
        }

        Console.WriteLine($"{idx.FullName}  (基类 {idx.Type.BaseType?.Name})");
        Console.WriteLine($"  字段 public={idx.PublicFieldCount} 含非公开={idx.AllFieldCount}   属性={idx.PropertyCount}");
        var list = members.ToList();
        Console.WriteLine($"  匹配成员 {list.Count} 个" + (filter is null ? "" : $"（filter={filter}）"));
        foreach (var m in list.Take(200))
        {
            var vis = m.IsPublic ? " " : "*";
            Console.WriteLine($"   {vis} {m.Kind,-9} {m.TypeName,-22} {m.Name}" +
                              (m.Detail is null ? "" : $"   [{m.Detail}]"));
        }
        if (list.Count > 200) Console.WriteLine($"   ...（还有 {list.Count - 200} 个，用 --filter 收窄）");
        Console.WriteLine("   * = 非公开成员（RimWorld 的 XML 同样能写它们）");
        return 0;
    }

    private static int Ctors(bool json, MemberInfoIndex idx)
    {
        var ctors = idx.Type.GetConstructors();
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                type = idx.FullName,
                count = ctors.Length,
                ctors = ctors.Select(c => new
                {
                    parameters = c.GetParameters().Select(p => new
                    {
                        type = p.ParameterType.Name,
                        name = p.Name,
                        byRef = p.ParameterType.IsByRef,
                        optional = p.IsOptional
                    })
                })
            }, Json));
            return 0;
        }

        Console.WriteLine($"{idx.FullName} 构造器 {ctors.Length} 个");
        foreach (var c in ctors)
        {
            var ps = c.GetParameters().Select(p =>
                (p.ParameterType.IsByRef ? "ref " : "") + p.ParameterType.Name.TrimEnd('&') + " " + p.Name);
            Console.WriteLine($"   ({string.Join(", ", ps)})");
        }
        return 0;
    }

    private static int ListTypes(bool json, ManagedAssemblyCatalog cat, string? filter)
    {
        var names = filter is null
            ? cat.AllTypes.Take(80).Select(t => t.Name)
            : cat.AllTypes.Select(t => t.Name)
                .Where(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Distinct().OrderBy(n => n).Take(200);
        var list = names.ToList();
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { count = list.Count, types = list }, Json));
            return 0;
        }
        Console.WriteLine(string.Join(Environment.NewLine, list));
        return 0;
    }

    private static int Emit(bool json, Diagnostic diag, string? typeName)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = "1.0",
                tool = new { name = "rimdev", version = "0.1.0" },
                command = "api",
                summary = new { error = diag.Severity == Severity.Error ? 1 : 0, info = diag.Severity == Severity.Info ? 1 : 0 },
                diagnostics = new[]
                {
                    new
                    {
                        code = diag.Code,
                        severity = diag.Severity.ToString().ToLowerInvariant(),
                        scope = diag.Scope.ToString().ToLowerInvariant(),
                        message = diag.Message,
                        hint = diag.Hint,
                        type = diag.TypeName,
                        member = diag.MemberName,
                        suggestions = diag.Suggestions,
                        evidence = diag.Evidence,
                        fingerprint = diag.Fingerprint
                    }
                }
            }, Json));
        }
        else
        {
            var prefix = diag.Severity == Severity.Error ? "✗" : "✓";
            Console.WriteLine($"{prefix} {diag.Message}");
            if (diag.Hint is not null) Console.WriteLine($"  提示: {diag.Hint}");
            if (diag.Suggestions is { Count: > 0 })
                Console.WriteLine($"  候选: {string.Join(", ", diag.Suggestions)}");
            if (diag.Evidence is not null)
                foreach (var (k, v) in diag.Evidence)
                    Console.WriteLine($"  {k,-18} {v}");
        }

        // 退出码：0 通过；2 查到了问题（与 rimsearcher 约定一致——2 表示"有问题"，不是"工具坏了"）
        return diag.Severity == Severity.Error ? 2 : 0;
    }

    private static int Unknown(string sub)
    {
        Console.Error.WriteLine($"未知 api 子命令: {sub}");
        return 3;
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
