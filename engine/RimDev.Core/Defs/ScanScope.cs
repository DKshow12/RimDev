namespace RimDev.Core.Defs;

public enum ScanScope
{
    /// <summary>
    /// 只扫 ModsConfig 的 activeMods（+ 目标 mod；实测 1649 文件 / 12.91 MB）。
    ///
    /// 更正记录（2026-10-14）：曾误记为 6340 文件 / 41.83 MB —— 那是早期探针把
    /// &lt;mod&gt;\1.6\**\*.xml（含 Languages/DefInjected 等）也算进去了。
    /// 按「loadFolders 的 Defs+Patches」这个真正想要的语义，1649 由两条独立实现
    /// （RimDev.Core 的 CollectFiles 与一路 PowerShell 枚举）交叉验证一致。
    /// </summary>
    Active,
    /// <summary>扫全部 3971 个 mod（实测 32694 文件 / 299.94 MB，仅 1.6\Defs）。</summary>
    All
}
