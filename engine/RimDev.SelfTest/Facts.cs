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
    /// <summary>Mods\ + Data\ + workshop\ 的候选目录总数。</summary>
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
