using RimDev.Core.Diagnostics;

namespace RimDev.Core.Metadata;

public enum NameKind { Type, Field, Property, Method, EnumMember, DefName, Unknown }

public sealed record NameVerdict(Diagnostic Diagnostic, NameKind Kind, bool Exists);

/// <summary>
/// 名字查证器（设计文档 02 §3-F1 的 v1 子集）。
/// 动机：本项目因凭语义猜"原版名字"造成 8 次加载期错误
/// （Repair→Construction、CarryCapacity→CarryingCapacity、MechanoidCentipede→MechanicalCentipede、
///  Shot_MachineGun/Building_Collapse 不存在、canBeDeconstructed 等三字段不存在）。
/// 本类把这些查证从"开游戏才知道"提前到命令行。
/// </summary>
public sealed class NameOracle
{
    private readonly ManagedAssemblyCatalog _cat;

    public NameOracle(ManagedAssemblyCatalog cat) => _cat = cat;

    public NameVerdict Check(string name, NameKind kind = NameKind.Unknown, string? ownerType = null)
    {
        return kind switch
        {
            NameKind.Type => CheckType(name),
            NameKind.Field or NameKind.Property or NameKind.Method => CheckMember(name, ownerType, kind),
            NameKind.EnumMember => CheckEnumMember(name, ownerType),
            NameKind.DefName => NotImplementedDefName(name),
            _ => CheckAny(name)
        };
    }

    private NameVerdict CheckType(string name)
    {
        var t = _cat.Find(name);
        if (t is not null)
            return new NameVerdict(new Diagnostic(Codes.MetadataUnavailable, Severity.Info, Scope.Offline,
                $"类型 {name} 存在 → {t.FullName}", TypeName: t.FullName), NameKind.Type, true);

        return new NameVerdict(new Diagnostic(Codes.TypeNotFound, Severity.Error, Scope.Offline,
            $"类型 {name} 不存在", "用候选中的全名替换；注意命名空间不可假设（BuildingProperties 在 RimWorld 而非 Verse）",
            TypeName: name, Suggestions: _cat.SuggestTypeNames(name, 6)), NameKind.Type, false);
    }

    private NameVerdict CheckMember(string name, string? ownerType, NameKind kind)
    {
        if (ownerType is null)
            return SearchMemberAcrossTypes(name, kind);

        var idx = _cat.MembersOf(ownerType);
        if (idx is null)
            return CheckType(ownerType);

        var hit = idx.FindMember(name);
        if (hit is not null)
            return new NameVerdict(new Diagnostic(Codes.MetadataUnavailable, Severity.Info, Scope.Offline,
                $"{idx.FullName}.{name} 存在（{hit.Kind} {hit.TypeName}）", TypeName: idx.FullName, MemberName: name),
                ToNameKind(hit.Kind), true);

        var suggestions = idx.Suggest(name, 6);
        var caseOnly = suggestions.FirstOrDefault(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));
        return new NameVerdict(new Diagnostic(
            caseOnly is not null ? Codes.CaseMismatch : Codes.FieldNotFound,
            Severity.Error, Scope.Offline,
            $"{idx.FullName} 上没有成员 {name}",
            caseOnly is not null ? $"大小写不符，正确写法是 {caseOnly}" : "用候选名替换",
            TypeName: idx.FullName, MemberName: name, Suggestions: suggestions),
            kind, false);
    }

    /// <summary>
    /// MemberKind（Task 4 的成员表枚举，含 Constructor）与 NameKind（名字查证枚举）是两个不同的枚举，
    /// 不能隐式互换——命中成员时必须显式换算。两者在 Field/Property/Method/Type 上同名同义，
    /// 换算不改变任何取值；Constructor 在 NameKind 中没有对应项，归入 Unknown（表示"存在但类别未细分"）。
    /// （计划原代码直接把 hit.Kind 传给 NameVerdict，编译不过：CS1503。）
    /// </summary>
    private static NameKind ToNameKind(MemberKind kind) => kind switch
    {
        MemberKind.Field => NameKind.Field,
        MemberKind.Property => NameKind.Property,
        MemberKind.Method => NameKind.Method,
        MemberKind.Type => NameKind.Type,
        _ => NameKind.Unknown
    };

    private NameVerdict CheckEnumMember(string name, string? ownerType)
    {
        if (ownerType is null)
        {
            foreach (var t in _cat.AllTypes.Where(t => t.IsEnum))
            {
                if (Enum.GetNames(t).Contains(name))
                    return new NameVerdict(new Diagnostic(Codes.MetadataUnavailable, Severity.Info, Scope.Offline,
                        $"枚举成员 {name} 存在 → {t.FullName}.{name}", TypeName: t.FullName, MemberName: name),
                        NameKind.EnumMember, true);
            }
            return new NameVerdict(new Diagnostic(Codes.EnumValueInvalid, Severity.Error, Scope.Offline,
                $"找不到枚举成员 {name}", "指定 --owner <EnumType> 可收窄搜索"),
                NameKind.EnumMember, false);
        }

        var et = _cat.Find(ownerType);
        if (et is null || !et.IsEnum)
            return new NameVerdict(new Diagnostic(Codes.TypeNotFound, Severity.Error, Scope.Offline,
                $"{ownerType} 不是已加载的枚举类型", TypeName: ownerType), NameKind.EnumMember, false);

        var names = Enum.GetNames(et);
        if (names.Contains(name))
            return new NameVerdict(new Diagnostic(Codes.MetadataUnavailable, Severity.Info, Scope.Offline,
                $"{et.FullName}.{name} 存在", TypeName: et.FullName, MemberName: name), NameKind.EnumMember, true);

        return new NameVerdict(new Diagnostic(Codes.EnumValueInvalid, Severity.Error, Scope.Offline,
            $"{et.FullName} 上没有枚举成员 {name}", "用合法值之一替换", TypeName: et.FullName, MemberName: name,
            Suggestions: names.Where(n => MemberInfoIndex.Similarity(name, n) > 0).Take(6).ToList()),
            NameKind.EnumMember, false);
    }

    /// <summary>未指定宿主类型时跨全部类型搜索成员名（16157 个类型，耗时约百毫秒级）。</summary>
    private NameVerdict SearchMemberAcrossTypes(string name, NameKind kind)
    {
        var hits = new List<string>();
        foreach (var t in _cat.AllTypes)
        {
            try
            {
                const System.Reflection.BindingFlags f =
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;

                var fi = t.GetField(name, f);
                if (fi is not null) { hits.Add($"{t.FullName} (Field {fi.FieldType.Name})"); if (hits.Count >= 12) break; continue; }

                var pi = t.GetProperty(name, f);
                if (pi is not null) { hits.Add($"{t.FullName} (Property {pi.PropertyType.Name})"); if (hits.Count >= 12) break; }
            }
            catch
            {
                // 个别类型元数据不可读属正常
            }
        }

        if (hits.Count > 0)
            return new NameVerdict(new Diagnostic(Codes.MetadataUnavailable, Severity.Info, Scope.Offline,
                $"成员名 {name} 在 {hits.Count} 个类型上存在", "用 --owner <Type> 收窄", MemberName: name,
                Suggestions: hits), kind, true);

        return new NameVerdict(new Diagnostic(Codes.FieldNotFound, Severity.Error, Scope.Offline,
            $"全程序集找不到名为 {name} 的成员", "确认拼写；或用 rimdev api list --filter 找类型",
            MemberName: name), kind, false);
    }

    private NameVerdict CheckAny(string name)
    {
        var t = _cat.Find(name);
        if (t is not null)
            return new NameVerdict(new Diagnostic(Codes.MetadataUnavailable, Severity.Info, Scope.Offline,
                $"{name} 是一个类型 → {t.FullName}", TypeName: t.FullName), NameKind.Type, true);

        var memberVerdict = SearchMemberAcrossTypes(name, NameKind.Unknown);
        if (memberVerdict.Exists) return memberVerdict;

        var suggestions = _cat.SuggestTypeNames(name, 6);
        return new NameVerdict(new Diagnostic(Codes.TypeNotFound, Severity.Error, Scope.Offline,
            $"{name} 既不是类型也不是成员",
            "v1 只查类型/字段/属性/方法/枚举；Def 名的查证需要 Def 索引（v2）",
            TypeName: name, MemberName: name, Suggestions: suggestions), NameKind.Unknown, false);
    }

    /// <summary>Def 名查证需要 v2 的 Def 索引，v1 明确不做，且必须如实说明而不是猜。</summary>
    private static NameVerdict NotImplementedDefName(string name) =>
        new(new Diagnostic(Codes.MetadataUnavailable, Severity.Warning, Scope.Offline,
            $"Def 名 {name} 的存在性检查尚未实现（需要 Def 索引，属 v2 范围）",
            "v1 已支持 --kind type|field|property|method|enum；Def 名请先用 rimsearcher list --type <DefType>",
            TypeName: name, MemberName: name), NameKind.DefName, false);
}
