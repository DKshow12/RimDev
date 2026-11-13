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

    public const int ThingDefPublicFieldCount = 266;
    public const int ThingDefAllFieldCount = 275;

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
    /// 按「Defs+Patches」这个真正想要的语义，权威值是 **1649**（由 RimDev.Core 自己算出，
    /// 并与独立枚举交叉验证；全库 `*\1.6\Defs\*.xml` 仍是 32694）。
    /// </summary>
    public const int ActiveModXmlFileCount = 1649;
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
    /// 更正记录：计划里曾写「17 个 XML / 233 个 Def」，那是旧快照；现为 23 个 XML / 185 个 def。
    /// </summary>
    public const int ThreatFleetXmlFileCount = 23;
    public const int ThreatFleetDefCount = 185;
}
