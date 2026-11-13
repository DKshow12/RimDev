// 用 System.Reflection.Metadata 只读元数据，查类型的基类链。
// 目的：证明「不加载程序集也能拿到继承链」这条路可行（零崩溃风险）。
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length < 2)
{
    Console.WriteLine("用法: mdtype <dll> <类型全名> [更多类型...]");
    return 1;
}

var dll = args[0];
if (!File.Exists(dll)) { Console.WriteLine($"找不到 {dll}"); return 1; }

using var fs = File.OpenRead(dll);
using var pe = new PEReader(fs);
if (!pe.HasMetadata) { Console.WriteLine("不是托管程序集"); return 1; }
var md = pe.GetMetadataReader();

Console.WriteLine($"程序集: {md.GetString(md.GetAssemblyDefinition().Name)}");
Console.WriteLine($"类型总数: {md.TypeDefinitions.Count}");

foreach (var wanted in args.Skip(1))
{
    TypeDefinitionHandle found = default;
    string? foundName = null;
    foreach (var h in md.TypeDefinitions)
    {
        var td = md.GetTypeDefinition(h);
        var ns = md.GetString(td.Namespace);
        var nm = md.GetString(td.Name);
        var full = string.IsNullOrEmpty(ns) ? nm : ns + "." + nm;
        if (full == wanted) { found = h; foundName = full; break; }
    }
    if (foundName is null) { Console.WriteLine($"  {wanted}: 未找到"); continue; }

    // 沿基类链向上走
    var chain = new System.Collections.Generic.List<string>();
    var cur = found;
    for (int depth = 0; depth < 12; depth++)
    {
        var td = md.GetTypeDefinition(cur);
        chain.Add(md.GetString(td.Name));
        var baseRef = td.BaseType;
        if (baseRef.IsNil) break;
        switch (baseRef.Kind)
        {
            case HandleKind.TypeDefinition:
                cur = (TypeDefinitionHandle)baseRef;
                continue;
            case HandleKind.TypeReference:
                var tr = md.GetTypeReference((TypeReferenceHandle)baseRef);
                var trNs = md.GetString(tr.Namespace);
                var trNm = md.GetString(tr.Name);
                chain.Add(string.IsNullOrEmpty(trNs) ? trNm + " (外部)" : trNs + "." + trNm + " (外部)");
                break;
            default:
                chain.Add($"<{baseRef.Kind}>");
                break;
        }
        break;
    }
    Console.WriteLine($"  {foundName}");
    Console.WriteLine($"    链: {string.Join(" → ", chain)}");
}
return 0;
