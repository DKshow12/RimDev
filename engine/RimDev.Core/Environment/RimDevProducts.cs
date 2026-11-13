namespace RimDev.Core.Environment;

/// <summary>
/// 「布局 → 该用哪份产物」的**唯一映射**（第 3 期 Task 9）。
///
/// 单套源码 + 两份产物：
///   · <see cref="RimWorldLayout.Classic16"/>（1.6 / Unity / net472 托管程序集）→ <see cref="Net8"/>
///   · <see cref="RimWorldLayout.Waylith17"/>（1.7.x 线 / Waylith / net10 托管程序集）→ <see cref="Net10"/>
///   · <see cref="RimWorldLayout.Unknown"/> → <c>null</c>（**没有可用产物**，调用方必须报可读错误并退出 4）
///
/// ★ 这是**纯函数**：不碰磁盘、不看版本号。切换入口（`_rimdev\rimdev.cmd`）按同一规则分发，
///   自测里有一条布局无关的用例把它钉死（`task9-layout` 组），所以切换逻辑本身有覆盖，
///   不依赖本机是否真的装了 1.6。
///
/// ★ 为什么不把「用哪份产物」写进 profile：profile 存的是**安装路径**，产物由布局**现场**推出。
///   把 `product: net8` 写死进 profile，用户换了安装（或升级游戏）就会静默用错产物。
/// </summary>
public static class RimDevProducts
{
    public const string Net8 = "net8.0";
    public const string Net10 = "net10.0";

    /// <summary>
    /// ★ 本产物自己编译出来的 TFM（编译期常量，绝不在运行时推断）。
    ///   用它做「产物 ↔ 布局」一致性判定：net8 进程**载入不了** net10 的 Assembly-CSharp，
    ///   反过来 net10 进程也载入不了 net472 的，所以两侧各有各的合法布局。
    /// </summary>
#if NET10_0_OR_GREATER
    public const string RuntimeTfm = Net10;
#else
    public const string RuntimeTfm = Net8;
#endif

    /// <summary>布局 → TFM；Unknown（两处都没有 Assembly-CSharp.dll）返回 null。</summary>
    public static string? ForLayout(RimWorldLayout layout) => layout switch
    {
        RimWorldLayout.Classic16 => Net8,
        RimWorldLayout.Waylith17 => Net10,
        _ => null,
    };

    /// <summary>
    /// 本产物 + 该布局是否**自洽**（产物服务的 TFM == 本产物的 TFM）。
    /// ★ 结构性判据，与具体版本号无关：只看「哪个产物 ↔ Assembly-CSharp.dll 落在哪」这一对映射。
    /// </summary>
    public static bool IsRuntimeCompatibleWith(RimWorldLayout layout) =>
        ForLayout(layout) is { } tfm && string.Equals(tfm, RuntimeTfm, StringComparison.Ordinal);

    /// <summary>
    /// 不自洽时返回**可读**说明（退出码 4 的文案），自洽时返回 null。
    /// ★ 为什么需要它：net8 产物跑在 1.7 安装上时，反射载入得到的是**空的类型表**，
    ///   于是 `api type ThingDef` 会答「类型不存在」——那是**假错误**（类型当然存在，
    ///   只是这份产物读不了它的元数据）。宁可拒答并说明原因，也不要给错答案。
    /// </summary>
    public static string? MismatchMessage(RimWorldLayout layout)
    {
        if (IsRuntimeCompatibleWith(layout)) return null;
        var expected = ForLayout(layout);
        if (expected is null)
            return $"当前安装的布局是 {layout.ToName()}：两处都没有 {GameEnvProbe.AssemblyCSharpFileName}，无法判断该用哪份产物。" +
                   "请用 --game-root 指向一份有效安装。";
        return $"环境不匹配：当前安装是 {layout.ToName()} 布局，需要 {expected} 产物；本产物是 {RuntimeTfm}。" +
               $"请改用 {expected}\\rimdev.exe（或 `rimdev --profile <对应名> <参数…>` 让切换入口按布局分发）。";
    }

    /// <summary>产物可执行文件的相对路径（相对 engine 目录），例如 <c>RimDev.Cli\bin\Debug\net10.0\rimdev.exe</c>。</summary>
    public static string? CliRelativePath(RimWorldLayout layout)
    {
        var tfm = ForLayout(layout);
        return tfm is null ? null : Path.Combine("RimDev.Cli", "bin", "Debug", tfm, "rimdev.exe");
    }

    /// <summary>自测/诊断用的一行说明。</summary>
    public static string Describe(RimWorldLayout layout) =>
        ForLayout(layout) is { } tfm
            ? $"{layout.ToName()} → {tfm}\\rimdev.exe"
            : $"{layout.ToName()} → （无可用产物：两处都没有 Assembly-CSharp.dll）";
}
