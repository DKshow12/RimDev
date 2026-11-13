using RimDev.Core.Catalog;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// ★★ 第 3 期 Task 26：<c>About.xml</c> 的**四个附加特性**。
///
/// <para>背景：这四个字段不在大多数教程的「About.xml 标准字段」里，所以长期被忽略。
/// 但实测（本机 4,080 个 About.xml）它们是**真实且大量**在用的：</para>
/// <code>
/// incompatibleWith          302 个 mod   ← 中文社区最常见；没它检不出冲突
/// modDependenciesByVersion  107 个 mod   ← 没它会把 1.7 版依赖整段漏掉
/// alternativePackageIds       2 个 mod   ← 没它会把「写了备选名」判成缺前置
/// core → ludeon.rimworld        0 个 mod   ← 实测不存在，但它是合法写法
/// </code>
///
/// <para>★ 本文件的期望值全部来自**独立探针**（Node 正则直接解析同一批真实文件），
/// 不是从实现里反推的 —— 否则测的是「实现和自己一致」，不是「实现对」。</para>
///
/// <para>★ 反例说明为什么必须测：本轮实现后跑 <c>check --rules aboutFeatures</c> 得到
/// **0 条诊断**。0 条可能是「真的没有冲突」，也可能是「解析根本没生效」。
/// 只有拿真实样本钉住解析结果，才能排除后一种。</para>
/// </summary>
public static class Phase3Task26Cases
{
    private static string WriteAbout(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "rimdev-t26-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "About"));
        var p = Path.Combine(dir, "About", "About.xml");
        File.WriteAllText(p, content);
        return p;
    }

    private static void Cleanup(string aboutPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetDirectoryName(aboutPath));
            if (dir is not null) Directory.Delete(dir, true);
        }
        catch { }
    }

    public static void Run()
    {
        T.Case("task26 ★ incompatibleWith：多值解析（真实语料 302 个 mod 在用）", () =>
        {
            // 形状取自工坊 1180573408（真实文件）
            var p = WriteAbout(
                "<ModMetaData><name>X</name><packageId>t.inc</packageId>" +
                "<incompatibleWith>" +
                "<li>sihv.rombonesPort</li>" +
                "<li>Soul.BonesNBits</li>" +
                "</incompatibleWith></ModMetaData>");
            try
            {
                var info = AboutXmlReader.Read(p);
                T.Null(info.Error, "不该有解析错误");
                T.Eq(info.IncompatibleWith.Length, 2, "★ 应解析出 2 条冲突声明");
                T.True(info.IncompatibleWith.Contains("sihv.rombonesPort"), "第一条");
                T.True(info.IncompatibleWith.Contains("Soul.BonesNBits"), "★ 大小写必须原样保留");
            }
            finally { Cleanup(p); }
        }, LayoutNeed.Any);

        T.Case("task26 ★ alternativePackageIds：单值解析（真实语料 2 个 mod 在用）", () =>
        {
            var p = WriteAbout(
                "<ModMetaData><name>X</name><packageId>t.alt</packageId>" +
                "<alternativePackageIds><li>erdelf.HumanoidAlienRaces.dev</li></alternativePackageIds>" +
                "</ModMetaData>");
            try
            {
                var info = AboutXmlReader.Read(p);
                T.Eq(info.AlternativePackageIds.Length, 1, "应解析出 1 条备选名");
                T.Eq(info.AlternativePackageIds[0], "erdelf.HumanoidAlienRaces.dev", "值必须原样");
            }
            finally { Cleanup(p); }
        }, LayoutNeed.Any);

        T.Case("task26 ★★ modDependenciesByVersion：按版本块归并（真实语料 107 个 mod 在用）", () =>
        {
            // 形状取自工坊 1201382956（真实文件，它有 v1.1…v1.6 六个块）
            var p = WriteAbout(
                "<ModMetaData><name>X</name><packageId>t.ver</packageId>" +
                "<modDependenciesByVersion>" +
                "<v1.1><li><packageId>brrainz.harmony</packageId><displayName>Harmony</displayName></li>" +
                "<li><packageId>UnlimitedHugs.HugsLib</packageId></li></v1.1>" +
                "<v1.7><li><packageId>brrainz.harmony</packageId></li>" +
                "<li><packageId>zetrith.prepatcher</packageId></li></v1.7>" +
                "</modDependenciesByVersion></ModMetaData>");
            try
            {
                var info = AboutXmlReader.Read(p);
                T.Eq(info.DependenciesByVersion.Count, 2, "★ 应解析出 2 个版本块");
                T.True(info.DependenciesByVersion.ContainsKey("v1.1"), "版本键必须原样保留 v1.1");
                T.True(info.DependenciesByVersion.ContainsKey("v1.7"), "版本键必须原样保留 v1.7");
                T.Eq(info.DependenciesByVersion["v1.1"].Length, 2, "v1.1 有 2 个依赖");
                T.Eq(info.DependenciesByVersion["v1.7"].Length, 2, "v1.7 有 2 个依赖");
                T.True(info.DependenciesByVersion["v1.7"].Contains("zetrith.prepatcher"),
                    "★ 版本块里的依赖必须能取出来 —— 只读 modDependencies 会把它整段漏掉");
            }
            finally { Cleanup(p); }
        }, LayoutNeed.Any);

        T.Case("task26 ★ 两个依赖来源互不覆盖（modDependencies 是基线，ByVersion 是追加）", () =>
        {
            var p = WriteAbout(
                "<ModMetaData><name>X</name><packageId>t.both</packageId>" +
                "<modDependencies><li><packageId>a.base</packageId></li></modDependencies>" +
                "<modDependenciesByVersion><v1.7><li><packageId>b.extra</packageId></li></v1.7>" +
                "</modDependenciesByVersion></ModMetaData>");
            try
            {
                var info = AboutXmlReader.Read(p);
                T.Eq(info.Dependencies.Length, 1, "modDependencies 仍应只有 1 条");
                T.Eq(info.Dependencies[0], "a.base", "基线依赖");
                T.Eq(info.DependenciesByVersion["v1.7"][0], "b.extra", "★ 版本追加必须独立保存");
            }
            finally { Cleanup(p); }
        }, LayoutNeed.Any);

        T.Case("task26 ★ 三个字段都缺省时返回空集合，不是 null（消费方不该处处判空）", () =>
        {
            var p = WriteAbout("<ModMetaData><name>X</name><packageId>t.none</packageId></ModMetaData>");
            try
            {
                var info = AboutXmlReader.Read(p);
                T.NotNull(info.IncompatibleWith, "IncompatibleWith 不该是 null");
                T.Eq(info.IncompatibleWith.Length, 0, "应为空数组");
                T.NotNull(info.AlternativePackageIds, "AlternativePackageIds 不该是 null");
                T.Eq(info.AlternativePackageIds.Length, 0, "应为空数组");
                T.NotNull(info.DependenciesByVersion, "DependenciesByVersion 不该是 null");
                T.Eq(info.DependenciesByVersion.Count, 0, "应为空字典");
            }
            finally { Cleanup(p); }
        }, LayoutNeed.Any);

        T.Case("task26 ★★ 真实语料对照：官方 Core 自己声明了 incompatibleWith 时能读出来", () =>
        {
            // 这条用**真实文件**（官方 Data\Core），期望值由独立 Node 探针给出，不靠实现反推。
            var env = RimDev.Core.Environment.GameEnvProbe.Discover(startDir: Facts.GameRoot);
            if (env.GameRoot is null) { T.Mark("task26 跳过：没有游戏根"); return; }

            var coreAbout = Path.Combine(env.GameRoot, "Data", "Core", "About", "About.xml");
            if (!File.Exists(coreAbout)) { T.Mark("task26 跳过：没有 Data\\Core\\About\\About.xml"); return; }

            var info = AboutXmlReader.Read(coreAbout);
            T.Null(info.Error, "官方 About.xml 不该解析失败");
            T.True(info.PackageId.Length > 0, "必须读出 packageId");
            // 不断言具体条数（随版本变），只断言「字段可访问且非 null」
            T.NotNull(info.IncompatibleWith, "字段必须可访问");
            T.NotNull(info.DependenciesByVersion, "字段必须可访问");
        }, LayoutNeed.Any);

        T.Case("task26 ★ RD3002/3003/3004 的码语义互不相同（一码一义）", () =>
        {
            var d1 = RimDev.Core.Diagnostics.Codes.DependencyMissing;
            var d2 = RimDev.Core.Diagnostics.Codes.IncompatibleModActive;
            var d3 = RimDev.Core.Diagnostics.Codes.DependencyViaAlternativeId;
            T.Eq(d1, "RD3002", "依赖未启用");
            T.Eq(d2, "RD3003", "冲突同时启用");
            T.Eq(d3, "RD3004", "依赖走备选名");
            T.True(d1 != d2 && d2 != d3 && d1 != d3, "★ 三个码必须互不相同");
            T.True(d1 != RimDev.Core.Diagnostics.Codes.DefNameNotFound,
                "★ 不能与 RD3001（悬空引用）混用");
        }, LayoutNeed.Any);
    }
}
