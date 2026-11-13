using System.Reflection;

namespace RimDev.Core.Metadata;

/// <summary>
/// 字段表的【单一真源】。
///
/// 为什么必须抽出来：这张表有两个消费者 —— <see cref="MemberInfoIndex"/>（成员表，
/// 供 FieldNameCheck 的 RD4001/RD4005 与 api/name 命令用）与 <see cref="NameOracle.SearchMemberAcrossTypes"/>
/// （不带 --owner 的全类型成员搜索）。同一套可见性规则各写一遍必然再分叉
/// （与 DefIndexStore.DefaultCacheDir「缓存目录只有一张真源」同一个道理）。
///
/// ★ 核心事实（第 2 期 Task 7b 实测）：.NET 的 <c>GetFields(Public|NonPublic|Instance)</c>
///   **不返回基类的 private 字段**，而 RimWorld 的 XML 反序列化**看得见**它们。
///   实证三路：
///     ① 反射探针：<c>RimWorld.StyleItemDef.category</c> 是 private；<c>typeof(BeardDef).GetFields(...)</c>
///        找不到 category（HairDef 同理）—— StyleItemDef 的 24 个字段 vs BeardDef 的 25 个字段里都没有它。
///     ② 原版先例：<c>Data\Core\Defs\BeardDefs\BeardDefs.xml:8-10</c> 的 &lt;BeardDef&gt; 里真的写 &lt;category&gt;Minimal&lt;/category&gt;。
///     ③ 引擎自己的裁定：归档日志里 <c>does not correspond to any field</c> 总共只有 6 条
///        （ModMetaDataInternal.publishedFileId ×3 + ThreatFleet 已修的 3 个字段），对 category/weight 零报错。
///   不聚合 = 官方内容上 138 条 RD4001/RD4005 假阳（RD4001 ×126 category、RD4005 ×12 weight/minCombatPoints/
///   excludes/canOccurRandomly）。修法是【把表建对】，不是加白名单、不是放宽判据。
///
/// ★ 与方法的规则**正好相反**（两条规则各有各的理由，都要保留）：
///   · 字段：必须沿 BaseType 逐层 DeclaredOnly 求并集 —— 字段没有 override、只有 hiding，
///     所以同名必须取【最派生】那个（先到者胜）。
///   · 方法：必须一次 <c>GetMethods()</c> —— 它本来就含基类方法，逐层累加反而会把被覆盖的基类版本也算进来
///     （实测 Pawn.PreApplyDamage 逐层累加得 3 个、单次得 1 个）。
/// </summary>
public static class FieldReflection
{
    /// <summary>
    /// XML 语义的字段标志：实例字段 + 含 NonPublic + **不含 Static**。
    /// XML 反序列化只写实例字段，静态字段永远不是 XML 目标；含 Static 会让 ThingDef 的字段数
    /// 从 275 变成 282，与锚点不符且语义上是错的。
    /// </summary>
    public const BindingFlags XmlFieldFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>
    /// 叠加 Static 的标志，仅用于「不带 --owner 的全类型成员搜索」。
    /// 理由：该搜索的既有实现是 <c>t.GetField(name, Public|NonPublic|Instance|Static)</c>，
    /// 去掉 Static 会让 <c>name check &lt;静态字段名&gt; --kind field</c> 静默查不到 —— 那是行为回归，
    /// 不是本次要修的东西（本次要修的是「看不见基类 private 字段」这一个盲区）。
    /// </summary>
    public const BindingFlags SearchFieldFlags = XmlFieldFlags | BindingFlags.Static;

    /// <summary>自 <paramref name="type"/> 起沿 BaseType 逐层向上（含自身）。null 安全。</summary>
    public static IEnumerable<Type> SelfAndBases(Type? type)
    {
        for (var t = type; t is not null; t = t.BaseType)
            yield return t;
    }

    /// <summary>
    /// 字段表 = 本类型 + 全部基类的【实例】字段（含 private），按元素名去重、最派生优先。
    /// 这是 XML 合法字段名的唯一判据（FieldNameCheck / NameOracle 都用它）。
    /// </summary>
    public static IReadOnlyDictionary<string, FieldInfo> FieldsByName(Type type) =>
        FieldsByName(type, includeStaticFields: false);

    /// <summary>
    /// 同上，但可选择并入静态字段（<see cref="SearchFieldFlags"/> 的说明见常量注释）。
    /// </summary>
    public static IReadOnlyDictionary<string, FieldInfo> FieldsByName(Type type, bool includeStaticFields)
    {
        var flags = BindingFlags.DeclaredOnly |
                    (includeStaticFields ? SearchFieldFlags : XmlFieldFlags);
        var map = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
        foreach (var t in SelfAndBases(type))
            foreach (var f in t.GetFields(flags))
                map.TryAdd(f.Name, f);   // 先到者胜 = 最派生优先（字段只有 hiding，没有 override）
        return map;
    }

    /// <summary>
    /// 按名取【最派生】的那个字段 —— 与 <see cref="FieldsByName(Type)"/> 同一套可见性规则，
    /// 但不建字典、不枚举全部字段。给「遍历 16000+ 个类型找成员名」这类热路径用。
    ///
    /// ★ 实测依据（2026-10-14 Task 7b，`rimdev name check &lt;名&gt; --kind field --json` 取三次最佳值）：
    ///   FindField 366 / 564 / 539 ms vs FieldsByName 373 / 590 / 564 ms（对 category / minCombatPoints /
    ///   一个不存在字段）。两者语义完全相同，差别不大（这条路径的开销主要在进程启动 + 16157 次反射），
    ///   所以这里选 FindField 只是「同样的语义、更低的开销」，不是为了救性能。
    /// </summary>
    public static FieldInfo? FindField(Type type, string name, bool includeStaticFields = false)
    {
        var flags = BindingFlags.DeclaredOnly |
                    (includeStaticFields ? SearchFieldFlags : XmlFieldFlags);
        foreach (var t in SelfAndBases(type))
        {
            var f = t.GetField(name, flags);
            if (f is not null) return f;   // 最派生优先，与 FieldsByName 的 TryAdd 顺序一致
        }
        return null;
    }
}
