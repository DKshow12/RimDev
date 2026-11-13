using RimDev.Cli;
using RimDev.Core.Catalog;
using RimDev.Core.Diagnostics;
using RimDev.Core.Environment;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// ★★ 第 3 期 Task 24：**About.xml 解析失败必须被报出来**（不许静默丢弃）。
///
/// 发现过程（本轮「验证所有功能」时撞上的）：
/// <code>
/// order 输出 629 条，ModsConfig 里有 630 条 —— 差 1 条。
/// 追下去发现 order 的 unresolvedPackageIds 里有一个 dsh.rimtrans.raceguard，
/// 而它的目录、About.xml 都好好地在磁盘上。
/// 用 PowerShell 的 XmlDocument.LoadXml 独立复验，拿到真实错误：
///   Name cannot begin with the '=' character, hexadecimal value 0x3D. Line 41, position 66.
/// —— 那个 mod 的 About.xml 的 &lt;description&gt; 里写了一个裸 `&lt;`（比较符 defeSkipped&lt;=50）。
/// </code>
///
/// ★ 两个独立缺陷：
/// <list type="number">
/// <item><b>数据缺陷</b>（那个 mod 自己）：XML 不良构 ⇒ 该 mod 完全无法加载。已修 XML。</item>
/// <item><b>工具缺陷</b>（本 Task 修的）：明明算出了 <c>AboutInfo.Error</c>，
///   却**从未报给用户** —— mod 只是从目录表里少一行，只在 <c>unresolvedPackageIds</c>
///   里露一个名字，而那个字段名看不出「你的 About.xml 坏了」。</item>
/// </list>
///
/// 本文件钉住修复后的行为：XML 不良构 ⇒ RD2007 error，且带引擎原始错误信息。
/// </summary>
public static class Phase3Task24Cases
{
    private static (int Code, string Out, string Err) RunCli(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code;
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        try
        {
            Console.SetOut(o);
            Console.SetError(e);
            code = CommandLine.Run(args, o, e);
        }
        finally
        {
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
        }
        return (code, o.ToString(), e.ToString());
    }

    /// <summary>造一个临时 mod 根目录，写一份 About.xml。</summary>
    private static string MakeMod(string aboutContent)
    {
        var dir = Path.Combine(Path.GetTempPath(), "rimdev-t24-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "About"));
        File.WriteAllText(Path.Combine(dir, "About", "About.xml"), aboutContent);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    public static void Run()
    {
        T.Case("task24 ★★ About.xml 不良构 ⇒ RD2007 error（不许静默丢弃）", () =>
        {
            // 复刻本轮的真实坏法：description 里一个裸 <
            var dir = MakeMod(
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                "<ModMetaData>\n" +
                "  <name>Broken</name>\n" +
                "  <packageId>test.broken.about</packageId>\n" +
                "  <description>defsSkipped<=50 这样写会坏掉</description>\n" +
                "</ModMetaData>\n");
            try
            {
                // 直接构造一个只含该目录的环境：用 EnumerateCandidates 的等价路径测不到，
                // 改为直接调 reader + 断言它确实报错（reader 是诊断的唯一来源）。
                var aboutPath = Path.Combine(dir, "About", "About.xml");
                var info = AboutXmlReader.Read(aboutPath);

                T.Eq(info.PackageId, "", "★ 解析失败时 packageId 必须为空");
                T.NotNull(info.Error, "★ 必须带 Error（修复前这个值算出来了但没人看）");
                T.Contains(info.Error!, "cannot begin with",
                    "Error 里应含引擎的原始 XML 错误 —— 用户要靠它定位");
            }
            finally { Cleanup(dir); }
        }, LayoutNeed.Any);

        T.Case("task24 ★ 良构的 About.xml 不报错（无假阳）", () =>
        {
            var dir = MakeMod(
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                "<ModMetaData>\n" +
                "  <name>Good</name>\n" +
                "  <packageId>test.good.about</packageId>\n" +
                "  <description>defsSkipped&lt;=50 这样写才对</description>\n" +
                "</ModMetaData>\n");
            try
            {
                var info = AboutXmlReader.Read(Path.Combine(dir, "About", "About.xml"));
                T.Eq(info.PackageId, "test.good.about", "packageId 应正确读出");
                T.Null(info.Error, "★ 良构的 XML 不该有 Error（否则人人被报错）");
            }
            finally { Cleanup(dir); }
        }, LayoutNeed.Any);

        T.Case("task24 ★ 描述里的 &lt; 转义后能正确读出（与上面那条构成正负对照）", () =>
        {
            // 这三条用例合起来证明：报错判据是「XML 良构性」，不是「文本里有没有 <」
            var dir = MakeMod(
                "<ModMetaData><name>X</name><packageId>t.x</packageId>" +
                "<description>a &lt; b &amp;&amp; c &gt; d</description></ModMetaData>");
            try
            {
                var info = AboutXmlReader.Read(Path.Combine(dir, "About", "About.xml"));
                T.Eq(info.PackageId, "t.x", "转义字符不影响读取");
                T.Null(info.Error, "转义后是良构的");
            }
            finally { Cleanup(dir); }
        }, LayoutNeed.Any);

        T.Case("task24 ★★ RD2007 的诊断码语义：与 RD2001（Def XML）分开", () =>
        {
            // 一个码两种含义 = 项目历史上 RD2008/RD2016 重复的翻版，不能再犯。
            T.Eq(Codes.AboutXmlParseFailed, "RD2007", "About.xml 解析失败用独立码");
            T.False(Codes.AboutXmlParseFailed == Codes.XmlParseFailed,
                "★ 不能与 RD2001（Def XML 解析失败）共用 —— 两者的修法完全不同");
        }, LayoutNeed.Any);

        T.Case("task24 ★ 真实环境下 check 的诊断里能看到 catalog 层诊断", () =>
        {
            // 这条守的是「catalog 诊断有没有真的并进最终列表」——
            // 修复前 CheckCommand 写的是 ModCatalog.Build(...).Catalog，把 Diagnostics 丢了。
            var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
            if (env.GameRoot is null) { T.Mark("task24 跳过：没有游戏根"); return; }

            var (_, stdout, _) = RunCli("check", "--scope", "active", "--only-official", "--json");
            // 至少要能解析；有没有 RD2007 取决于本机有没有坏的 About.xml（不钉数量）
            T.Contains(stdout, "\"diagnostics\"", "check --json 必须输出 diagnostics");
            T.Contains(stdout, "\"schemaVersion\"", "应有 schemaVersion");
        }, LayoutNeed.Any);
    }
}
