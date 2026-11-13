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
    /// <summary>启用集合里 Defs/Patches 的 XML 文件数。</summary>
    public const int ActiveModXmlFileCount = 6340;
    /// <summary>全库 *\1.6\Defs\*.xml 的文件数。</summary>
    public const int AllLibraryDefFileCount = 32694;
}
