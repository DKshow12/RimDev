namespace RimDev.SelfTest;

/// <summary>
/// 本机实测锚点（2026-10-14，RimWorld 1.6.4871 rev591）。
/// 这些数字来自真实命令输出，是工具正确性的外部标尺。
/// 游戏小版本升级后若断言失败，先确认是否为版本漂移而非代码缺陷。
/// </summary>
public static class Facts
{
    public const string GameRoot = @"C:\Program Files (x86)\Steam\steamapps\common\RimWorld";
    public const string WorkshopDir = @"C:\Program Files (x86)\Steam\steamapps\workshop\content\294100";

    public const int ManagedDllCount = 108;
    public const int AssemblyCSharpTypeCount = 16157;

    public const int ThingDefPublicFieldCount = 266;   // 改字段表后必须保持不变（公开字段本就会被继承）
    /// <summary>
    /// ThingDef 的 AllFieldCount —— 【含基类 private 字段】的去重字段名数。
    ///
    /// ★ 锚点复测记录（2026-10-14，第 2 期 Task 7b）：**275 → 276**。
    ///   原因不是放宽判据，而是字段表改成了沿 BaseType 逐层 DeclaredOnly 求并集：
    ///   改前单次 GetFields(Public|NonPublic|Instance) 看不到基类 private 字段，
    ///   而引擎的 XML 反序列化看得见（重灾区是 StyleItemDef.category 对 BeardDef/HairDef、
    ///   CreepJoinerBaseDef.weight/minCombatPoints 对三个 CreepJoiner* 派生类型）。
    ///   ThingDef 这条链上旧表少收的恰好 1 个 = Verse.BuildableDef.placeWorkersInstantiatedInt（private）。
    ///   PublicFieldCount=266 必须【不变】（公开字段本来就会被 GetFields 继承）——见 MetadataCases。
    /// </summary>
    public const int ThingDefAllFieldCount = 276;

    public const int WorkshopModDirCount = 3967;

    // ===== 第 2 期：目录表与 Def 语料（2026-10-14 实测）=====
    /// <summary>
    /// Mods\ + Data\ + workshop\ 的候选目录数**基线**（2026-10-14 实测 3984）。
    ///
    /// ★ 这是基线，不是精确期望值。它 = 真 mod 数 + 没有 About.xml 的目录
    ///   （`_rimdev` / `.vscode` / `.agents` / `~` 之类），而任何工具往 Mods\ 丢一个文件夹它就会变：
    ///   2026-10-14 实测因 vision-toolkit 建了 `Mods\.dsh-vision-toolkit` 而由 3984 → 3985，
    ///   那是环境变动、不是代码缺陷。因此用例用「≥ 基线 + 小容差」而不是精确等值。
    ///   真正稳定的不变量是 <see cref="ModsWithPackageId"/> 与 <see cref="RealDuplicatePackageIdCount"/>。
    /// </summary>
    public const int CandidateModDirCount = 3984;
    /// <summary>能解析出 packageId 的 mod 数（其余 13 个目录没有 About.xml）。</summary>
    public const int ModsWithPackageId = 3971;
    /// <summary>用 XML 感知提取后，真正的 packageId 重复数 = 0。</summary>
    public const int RealDuplicatePackageIdCount = 0;
    /// <summary>ModsConfig.xml 的 activeMods 条数。</summary>
    public const int ActiveModCount = 17;
    /// <summary>
    /// 启用集合按 loadFolders 的 Defs+Patches 下的 *.xml 文件数。
    ///
    /// ★ 这是一个「mod 列表金丝雀」：数字变了通常意味着用户改了启用列表，而不是代码坏了。
    ///   看到它失败时，先核对 ModsConfig.xml，再决定是否更新本常量。
    ///
    /// 更正记录（2026-10-14）：曾误记为 **6340** —— 那是我早期探针把 `&lt;mod&gt;\1.6\**\*.xml`
    /// （含 Languages/DefInjected 等）也算进去了，而且从未用代码复现过。
    /// 按「Defs+Patches」这个真正想要的语义，权威值原为 **1649**（由 RimDev.Core 自己算出，
    /// 并与独立枚举交叉验证；全库 `*\1.6\Defs\*.xml` 仍是 32694）。
    ///
    /// ★ 三期实测（2026-10-14 第 3 期 Task 1）：**1649 → 1652 → 1654**。
    ///   原因**不是**口径变化，而是**语料漂移**：ThreatFleet（另一个写入者正在改的 mod）的
    ///   `1.6\Defs\**\*.xml` 由 23 → 26 → 28 个（文件时间 20:57–21:14）。
    ///
    /// ★★ 关键不变量（主理人独立复核后钉住）：
    ///     **1649 − 23 = 1652 − 26 = 1654 − 28 = 1626**
    ///   三次快照完全吻合 ⇒ 增长**全部**来自 ThreatFleet，其余 16 个 mod 一个文件都没动。
    ///   所以金丝雀**不用容差**，而是把锚点落在**不可变子集**上：
    ///   断言 `总数 − ThreatFleet 贡献 == ActiveModXmlFileCountExcludingThreatFleet`（精确等值）。
    ///   曾一度写成「基线 ± 64」，那能漏掉「少了一个只贡献 ≤64 个文件的小 mod」——
    ///   而抓这类口径/内容漂移正是本金丝雀存在的理由（见 DefScanCases 用例注释）。
    /// </summary>
    public const int ActiveModXmlFileCount = 1654;
    /// <summary>
    /// **文件数金丝雀的真正判据（精确等值）**：启用集合扣除正在被另一个写入者改的 ThreatFleet
    /// 之后，其它 16 个 mod 按 loadFolders 的 Defs+Patches 下的 *.xml 文件数。
    /// 来源：1649 − 23 = 1652 − 26 = 1654 − 28 = **1626**（三次快照一致）。
    /// 口径漂移（把 Languages/DefInjected 算进来）会让它一次跳几千，立刻出界。
    /// </summary>
    public const int ActiveModXmlFileCountExcludingThreatFleet = 1626;
    /// <summary>
    /// 上述 1649 个文件的**总字节数量级**（2026-10-14 实测 13,537,807 B ≈ 12.91 MB）。
    ///
    /// ★ 用**区间**而不是精确等值：启用集合里包含 `ThreatFleet` —— 它正在被活跃开发
    ///   （实测 2026-10-14 19:32 有另一个写入者在改它的 XML，同一断言先得 13,537,817、
    ///   再得 13,539,209）。对**可变语料**做精确等值断言 = 假失败制造机，
    ///   与已修掉的 `CandidateModDirCount` 是同一类错误。
    ///
    /// 区间的用途是**抓口径漂移**：若 loadFolders 解析把 Languages/DefInjected 也纳进来，
    /// 字节数会直接跳到 ~41 MB，立刻出界（文件数金丝雀也会同时响）。
    /// </summary>
    public const long ActiveModXmlTotalBytesMin = 12_000_000;
    public const long ActiveModXmlTotalBytesMax = 20_000_000;
    /// <summary>全库 *\1.6\Defs\*.xml 的文件数。</summary>
    public const int AllLibraryDefFileCount = 32694;
    /// <summary>
    /// ThreatFleet 的 XML 文件数与 def 数（2026-10-14 实测，**会随该 mod 日常开发而变化**）。
    /// 仅用于报告与粗筛，**不作为精确断言**；防「静默丢 def」靠 DefScanCases 里的 DOM 逐文件对账。
    /// 更正记录：计划里曾写「17 个 XML / 233 个 Def」，那是旧快照；第 3 期 Task 1 实测已涨到
    /// **28 个 XML**（20:57–21:14 陆续落地）——它正是 <see cref="ActiveModXmlFileCount"/>
    /// 由 1649 → 1652 → 1654 的唯一原因。**这两个常量只用于报告，不作为精确断言。**
    /// </summary>
    public const int ThreatFleetXmlFileCount = 28;
    public const int ThreatFleetDefCount = 185;
}
