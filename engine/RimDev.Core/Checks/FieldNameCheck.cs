using System.Xml;
using RimDev.Core.Defs;
using RimDev.Core.Diagnostics;
using RimDev.Core.Metadata;

namespace RimDev.Core.Checks;

/// <summary>
/// ★ 本期最有价值的一条：把 XML 元素名对着目标 Def 类的**真实字段表**校验。
///
/// 动机（项目历史）：静态校验（XML 良构 + defName 去重）**永远查不出「字段名不存在」**。
/// 本项目 10 类加载期错误里 6 类是名字不存在，而当时静态校验全绿。
/// 例：canBeDamagedByExplosions / canBeDeconstructed / deconstructibleBlocks 三个字段
/// 在程序集里**根本不存在**，却写成 XML 后被游戏报"doesn't correspond to any field"。
///
/// 实现要点：
///  - 复用 <see cref="MemberInfoIndex"/>：字段枚举 = Public|NonPublic|Instance（不含 Static），
///    大小写敏感。★ 这一条不能改：VerbProperties.forcedMissRadius 是 private 字段但合法。
///  - 只校验**能确定目标 C# 类型**的 def（DefType 名能解析成 Managed 里的类型）；
///    解析不到的报 RD2017（info）而不是当作错误。
///  - 只校验**顶层字段与一级子元素**：`&lt;comps&gt;&lt;li Class="..."&gt;` 这类多态节点的内层字段
///    需要按 Class 分派，属第 3 期。
///  - ★ **每个文件只读一次**（Task 6 修正 ⑤）：按 <c>ctx.Defs.GroupBy(d =&gt; d.FileAbs)</c> 分组，
///    文件打开一次、按 <c>Line</c> 把元素归属到对应 def。若对每个 def 各读一遍自己的文件，
///    active 作用域 14480 个 def、<c>--scope all</c> 342520 个 def 会变成几十万次读盘。
/// </summary>
public sealed class FieldNameCheck : ICheck
{
    public string Id => Codes.FieldNotFound;

    public IEnumerable<Diagnostic> Run(CheckContext ctx)
    {
        ManagedAssemblyCatalog? catalog = null;
        try { catalog = ManagedAssemblyCatalog.Load(ReadManagedDir()); }
        catch { /* 元数据不可用时静默跳过（v1 已有独立命令覆盖该场景） */ }
        // ★ 形态确定：不在 catch 里 yield break ——
        //   迭代器里 try/catch 与 yield 的组合限制不靠记忆，改成「先赋值，再判 null」。
        if (catalog is null) yield break;

        // ★ 报告过滤器在【每个文件的分组内】做：只报目标 mod 的 def，文件仍然只读一次。
        foreach (var g in ctx.Defs.GroupBy(d => d.FileAbs, StringComparer.OrdinalIgnoreCase))
        {
            var defsInFile = new List<RawDef>();
            foreach (var d in g)
                if (ctx.TargetModPackageId is null || d.ModPackageId == ctx.TargetModPackageId)
                    defsInFile.Add(d);
            if (defsInFile.Count == 0) continue;

            var elementsByDefLine = ReadElementsByDef(g.Key);

            foreach (var d in defsInFile)
            {
                var type = catalog.Find(d.DefType);
                if (type is null)
                {
                    yield return new Diagnostic(Codes.UnknownDefType, Severity.Info, Scope.Offline,
                        $"Def 类型 `{d.DefType}` 在 Managed 里找不到对应 C# 类型，字段名校验被跳过",
                        "自定义 Def 类型需要 mod 自己的程序集；本期只校验原版类型",
                        FileAbs: d.FileAbs, Line: d.Line,
                        ModPackageId: d.ModPackageId, DefName: d.DefName, DefType: d.DefType);
                    continue;
                }

                if (!elementsByDefLine.TryGetValue(d.Line, out var elements)) continue;
                var members = catalog.MembersOf(type);

                foreach (var (elementName, line) in elements)
                {
                    if (elementName is "defName" or "ParentName" or "Name" or "Abstract" or "MayRequire") continue;
                    if (members.FindField(elementName) is not null) continue;
                    if (members.FindProperty(elementName) is not null) continue;

                    var suggestions = members.Suggest(elementName, 5);
                    var caseOnly = suggestions.FirstOrDefault(s => string.Equals(s, elementName, StringComparison.OrdinalIgnoreCase));
                    yield return new Diagnostic(
                        caseOnly is not null ? Codes.CaseMismatch : Codes.FieldNotFound,
                        Severity.Error, Scope.Offline,
                        $"{d.DefType} 上没有字段 `{elementName}`",
                        caseOnly is not null ? $"大小写不符，正确写法是 {caseOnly}" : "用候选名替换",
                        FileAbs: d.FileAbs, Line: line,
                        ModPackageId: d.ModPackageId, DefName: d.DefName, DefType: d.DefType,
                        Suggestions: suggestions,
                        Evidence: new Dictionary<string, string>
                        {
                            ["defTypeFullName"] = type.FullName ?? type.Name,
                            ["publicFields"] = members.PublicFieldCount.ToString(),
                            ["allFields"] = members.AllFieldCount.ToString()
                        });
                }
            }
        }
    }

    /// <summary>
    /// ★ 一次流式读入某文件里**所有** def 的一级子元素，按 def 起始行分组。
    /// 这是「每个文件只读一次」的落点：文件打开一次，元素按行号归属到对应 def。
    /// 归属依据是「depth==1 的元素起始行 == RawDef.Line」，与 DefScanner 的行号口径完全一致。
    /// </summary>
    public static Dictionary<int, List<(string Name, int Line)>> ReadElementsByDef(string fileAbs)
    {
        var byDefLine = new Dictionary<int, List<(string Name, int Line)>>();
        foreach (var (defLine, name, line) in ScanTopLevelElements(fileAbs))
        {
            if (!byDefLine.TryGetValue(defLine, out var l))
                byDefLine[defLine] = l = new List<(string, int)>();
            l.Add((name, line));
        }
        return byDefLine;
    }

    /// <summary>读某个 def（从 startLine 起）的一级子元素名与行号。只读该文件一次。</summary>
    public static List<(string Name, int Line)> ReadTopLevelElements(string fileAbs, int startLine) =>
        ScanTopLevelElements(fileAbs)
            .Where(x => x.DefLine == startLine)
            .Select(x => (x.Name, x.Line))
            .ToList();

    /// <summary>底层单遍扫描：返回 (def 起始行, 一级子元素名, 元素行号)。</summary>
    private static List<(int DefLine, string Name, int Line)> ScanTopLevelElements(string fileAbs)
    {
        var list = new List<(int, string, int)>();
        try
        {
            using var stream = File.OpenRead(fileAbs);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                IgnoreComments = true, IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Prohibit
            });
            var li = (IXmlLineInfo)reader;
            var defLine = -1;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                var line = li.HasLineInfo() ? li.LineNumber : 0;
                if (reader.Depth == 1)
                {
                    defLine = line;                       // 一个 def 的开始
                    continue;
                }
                if (reader.Depth == 2 && defLine >= 0)
                    list.Add((defLine, reader.Name, line));
            }
        }
        catch { /* 解析失败由 RD2001 报告 */ }
        return list;
    }

    private static string ReadManagedDir()
    {
        var env = RimDev.Core.Environment.GameEnvProbe.Discover();
        return env.ManagedDir;
    }
}
