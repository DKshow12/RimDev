namespace RimDev.Core.Defs;

public enum ScanScope
{
    /// <summary>只扫 ModsConfig 的 activeMods（+ 目标 mod；实测 6340 文件 / 41.83 MB）。</summary>
    Active,
    /// <summary>扫全部 3971 个 mod（实测 32694 文件 / 299.94 MB，仅 1.6\Defs）。</summary>
    All
}
