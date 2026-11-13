namespace RimDev.Core.Defs;

/// <summary>
/// defName → 声明处列表。
///
/// ★ 更正（第 2 期 Task 6，2026-10-14 实测，三路证据）：`defName` 的唯一性是【按 DefType】的，
///   **不是全局的**。同一个 defName 可以合法地出现在不同 DefType 里 —— 原版 Core 一个包就有
///   5 个不同类型的 `Misc`（HediffDef / DesignationCategoryDef / StyleItemCategoryDef /
///   IncidentCategoryDef / PawnRenderTreeDef），`Animals` / `Space` / `Skull` / `Research` 等同理。
///   active 作用域实测：跨类型同名 **1202** 组（全部合法），同类型真重名只有 **1** 组
///   （`ThingSetMakerDef|MapGen_AncientComplexRoomLoot_Default`，Core 与 Ideology 各一份）；
///   归档的真实游戏启动日志里 `Duplicate defName` 命中 **0** —— 游戏自己的裁定就是「不算」。
///
///   <see cref="ByDefName"/> 仍然保留（全局名查询、给建议、ParentName 解析用），但它在**跨类型合并**，
///   **不能**用来判重（用它判重会一次报出 1202 条假错误）。
///
///   ★ 两个机制不要互相外推（2026-10-14 口径纠正）：**判重**走 <see cref="ByTypeAndDefName"/>（defName 唯一性按 DefType）；
///   而 **ParentName 的解析是按名字全局的** —— 命中 defName 空间或抽象名空间即算存在（见 ParentChainCheck 类注释）。
///
/// <c>Name=</c> 的抽象基类走独立的抽象命名空间，两者不能互相引用；
/// <see cref="ByAbstractName"/> 的 value 里带 DefType（需要时调用方自行过滤）；ParentName 解析【不】做这个过滤（按名字全局解析）。
/// </summary>
public sealed class DefIndex
{
    /// <summary>全局 defName（**跨类型合并**）。查询用；判重请用 <see cref="ByTypeAndDefName"/>。</summary>
    public Dictionary<string, List<RawDef>> ByDefName { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// ★ 按 (DefType, defName) 建索引 —— RimWorld 里 defName 的唯一性口径就是这个。
    /// 跨类型同名是合法的，只有同一 DefType 内同名才会合并/遮蔽。
    /// ★ 只收录【非抽象】声明：`Abstract="True"` 的节点不会被引擎建成 def，
    ///   其上的 &lt;defName&gt; 只是装饰（原版 PawnKinds_Mercenary.xml:198 就是）；详见 Build()。
    /// </summary>
    public Dictionary<(string DefType, string DefName), List<RawDef>> ByTypeAndDefName { get; }
        = new();

    /// <summary>抽象基类名（<c>Name="X"</c>）→ 声明处列表；同名可能跨类型（value 里带 DefType，需要时调用方自行过滤）。</summary>
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

                // ★ 抽象节点（Abstract="True"）不进这个索引：它们不会被引擎建成 def，
                //   其上的 <defName> 只是装饰。原版就有这个模式（Data\Core\Defs\PawnKindDefs_Humanlikes\
                //   PawnKinds_Mercenary.xml:198 的 MercenarySlasherBase 抽象模板也写了
                //   <defName>Mercenary_Slasher</defName>，而 232 行才是真 def）。
                //   把抽象声明计入判重会造出假阳（实测同类型真重名组 4 → 1）。
                //   注：ByDefName / ByAbstractName 仍收录全部声明，语义不变。
                if (!d.IsAbstract)
                {
                    // ★ 唯一性按 (DefType, defName)。
                    var key = (d.DefType, d.DefName);
                    if (!idx.ByTypeAndDefName.TryGetValue(key, out var tl))
                        idx.ByTypeAndDefName[key] = tl = new List<RawDef>();
                    tl.Add(d);
                }
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

    /// <summary>
    /// ★ 按 DefType 的 defName 存在性判定。
    ///
    /// ★ 口径纠正（2026-10-14）：这【不是】ParentName 的判定口径 —— `defName` 的唯一性是按 DefType 的，
    ///   但 `ParentName` 是【按名字全局解析】的（见 ParentChainCheck 类注释，实测：按类型未解析 26 /
    ///   全局未解析 0）。两者是两个不同机制，不要互相外推。
    ///   本重载保留是因为自测用例仍用它统计「按类型未解析」作为反证材料（CheckCases 的 [防假阳②] 证据行）；
    ///   检查规则一律用 <see cref="CanBeReferenced"/>。
    /// </summary>
    public bool IsDefNameTaken(string defType, string defName) =>
        ByTypeAndDefName.ContainsKey((defType, defName));

    public bool IsAbstractNameTaken(string name) => ByAbstractName.ContainsKey(name);

    /// <summary>defName 或抽象名都可作为 ParentName 的目标。</summary>
    public bool CanBeReferenced(string name) => IsDefNameTaken(name) || IsAbstractNameTaken(name);
}
