namespace RimDev.Core.Defs;

/// <summary>
/// defName → 声明处列表（RimWorld 的 defName 是**全局唯一**的，所以同名即冲突）。
/// <c>Name=</c> 的抽象基类走独立的抽象命名空间，两者不能互相引用。
/// </summary>
public sealed class DefIndex
{
    public Dictionary<string, List<RawDef>> ByDefName { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<RawDef>> ByAbstractName { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> CountByType { get; } = new(StringComparer.Ordinal);
    public int TotalDefs { get; private set; }
    public int AbstractDefs { get; private set; }

    public static DefIndex Build(IEnumerable<RawDef> defs)
    {
        var idx = new DefIndex();
        foreach (var d in defs)
        {
            idx.TotalDefs++;
            idx.CountByType[d.DefType] = idx.CountByType.GetValueOrDefault(d.DefType) + 1;

            if (!string.IsNullOrEmpty(d.DefName))
            {
                if (!idx.ByDefName.TryGetValue(d.DefName, out var l))
                    idx.ByDefName[d.DefName] = l = new List<RawDef>();
                l.Add(d);
            }
            if (!string.IsNullOrEmpty(d.NameAttr))
            {
                idx.AbstractDefs++;
                if (!idx.ByAbstractName.TryGetValue(d.NameAttr, out var l))
                    idx.ByAbstractName[d.NameAttr] = l = new List<RawDef>();
                l.Add(d);
            }
        }
        return idx;
    }

    public bool IsDefNameTaken(string name) => ByDefName.ContainsKey(name);
    public bool IsAbstractNameTaken(string name) => ByAbstractName.ContainsKey(name);

    /// <summary>defName 或抽象名都可作为 ParentName 的目标。</summary>
    public bool CanBeReferenced(string name) => IsDefNameTaken(name) || IsAbstractNameTaken(name);
}
