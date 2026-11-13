namespace RimDev.Core.Environment;

/// <summary>
/// RimWorld 安装的**磁盘布局**。这不是「游戏版本号」——本条目的全部意义就是：
/// 判定布局只能看 <b>Assembly-CSharp.dll 落在哪</b>，不能看 exe 文件名。
///
/// ★ 实测依据（2026-10-24，两台 1.7 副本）：
///   - 桌面副本 `C:\Users\21596\Desktop\rim\rim\RimWorld` 同时存在 `RimWorldWin64.exe` 与 `RimWorld.exe`；
///   - Steam 副本 `...\steamapps\common\RimWorld` **只有** `RimWorld.exe`（没有 RimWorldWin64.exe）。
///   ⇒ 用 exe 名判布局会在两种副本上给出相反结论；用 Assembly-CSharp 的位置则一致（两份都是「在根目录」）。
///
/// ★ 同族纪律：不要在这里（或任何别处）写 `if (version >= 1.7)`。1.7.x 的小版本会继续改元数据，
///   版本号分支会在下次升级时**静默失效**；结构化判据不会。
/// </summary>
public enum RimWorldLayout
{
    /// <summary>未能识别（既没有 1.6 的 Managed\Assembly-CSharp.dll，也没有根目录的）。</summary>
    Unknown = 0,

    /// <summary>
    /// 1.6 及更早（Unity / net472 托管程序集）：
    /// <c>&lt;root&gt;\RimWorldWin64_Data\Managed\Assembly-CSharp.dll</c> 存在 ⇒ ManagedDir = 该 Managed 目录。
    /// </summary>
    Classic16 = 1,

    /// <summary>
    /// 1.7.x 线（Waylith / net10）：<c>&lt;root&gt;\Assembly-CSharp.dll</c> 存在 ⇒ ManagedDir = 游戏根。
    /// </summary>
    Waylith17 = 2,
}

public static class RimWorldLayoutNames
{
    public const string Classic16 = "Classic16";
    public const string Waylith17 = "Waylith17";
    public const string Unknown = "Unknown";

    public static string ToName(this RimWorldLayout layout) => layout switch
    {
        RimWorldLayout.Classic16 => Classic16,
        RimWorldLayout.Waylith17 => Waylith17,
        _ => Unknown,
    };

    /// <summary>人读描述（env 输出用）。不写具体版本号，只写结构化布局。</summary>
    public static string ToDescription(this RimWorldLayout layout) => layout switch
    {
        RimWorldLayout.Classic16 => "Classic16（<root>\\RimWorldWin64_Data\\Managed\\Assembly-CSharp.dll）",
        RimWorldLayout.Waylith17 => "Waylith17（<root>\\Assembly-CSharp.dll）",
        _ => "Unknown（两处都没有 Assembly-CSharp.dll）",
    };
}
