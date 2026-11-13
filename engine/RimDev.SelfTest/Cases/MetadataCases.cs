using RimDev.Core.Environment;
using RimDev.Core.Metadata;

namespace RimDev.SelfTest.Cases;

public static class MetadataCases
{
    public static void Run()
    {
        T.Group("metadata");

        var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
        var cat = ManagedAssemblyCatalog.Load(env.ManagedDir);

        T.Case("装载 Assembly-CSharp 并取得 16157 个类型", () =>
        {
            T.Eq(cat.TypeCount, Facts.AssemblyCSharpTypeCount, "类型总数");
            T.Null(cat.LoaderWarning, "不应有 ReflectionTypeLoadException 告警");
            T.True(cat.ResolveFailures.Count == 0, "解析失败数应为 0，实际 " + cat.ResolveFailures.Count);
        });

        T.Case("按简单名查找不假设命名空间", () =>
        {
            T.Eq(cat.Find("ThingDef")?.FullName, "Verse.ThingDef", "ThingDef 全名");
            T.Eq(cat.Find("BuildingProperties")?.FullName, "RimWorld.BuildingProperties", "BuildingProperties 全名（不在 Verse）");
            T.Eq(cat.Find("CompProperties_Refuelable")?.FullName, "RimWorld.CompProperties_Refuelable", "CompProperties_Refuelable 全名");
        });

        T.Case("ThingDef 字段计数与可见性分层", () =>
        {
            var idx = cat.MembersOf("ThingDef")!;
            T.Eq(idx.PublicFieldCount, Facts.ThingDefPublicFieldCount, "public 字段数");
            T.Eq(idx.AllFieldCount, Facts.ThingDefAllFieldCount, "含非公开字段数");
            T.Eq(idx.Type.BaseType?.Name, "BuildableDef", "基类");
        });

        T.Case("★ 非公开字段必须被枚举：VerbProperties.forcedMissRadius", () =>
        {
            var idx = cat.MembersOf("VerbProperties")!;
            var f = idx.FindField("forcedMissRadius");
            T.NotNull(f, "forcedMissRadius 应存在（即使它是 private）");
            T.False(f!.IsPublic, "forcedMissRadius 应为非公开字段——这正是必须枚举 NonPublic 的证据");
            T.Eq(f.FieldType.Name, "Single", "forcedMissRadius 类型");
        });

        T.Case("不存在的字段判定为不存在（历史三大误报）", () =>
        {
            var idx = cat.MembersOf("ThingDef")!;
            T.Null(idx.FindField("canBeDeconstructed"), "canBeDeconstructed");
            T.Null(idx.FindField("canBeDamagedByExplosions"), "canBeDamagedByExplosions");
            T.Null(idx.FindField("deconstructibleBlocks"), "deconstructibleBlocks");
        });

        T.Case("错误名的正确替代物存在", () =>
        {
            T.NotNull(cat.MembersOf("ThingDef")!.FindField("resourcesFractionWhenDeconstructed"),
                "ThingDef.resourcesFractionWhenDeconstructed");
            T.NotNull(cat.MembersOf("BuildingProperties")!.FindField("alwaysDeconstructible"),
                "BuildingProperties.alwaysDeconstructible");
        });

        T.Case("canBeDeconstructed 判为不存在（且实测无相近候选）", () =>
        {
            var idx = cat.MembersOf("ThingDef")!;
            T.Null(idx.FindField("canBeDeconstructed"), "canBeDeconstructed 应不存在");

            // 实测：候选池是 ThingDef 自身成员，而没有任何成员名与 canBeDeconstructed 相近，
            // 因此候选数为 0 是【正确行为】。不能写成“必须有候选”——那是会失败的断言。
            // 正确的替代物在别的类型上（BuildingProperties.alwaysDeconstructible），
            // 由下一条用例单独断言。若游戏新版本加了相近字段，此断言会失败——那是提醒你更新锚点。
            var suggestions = idx.Suggest("canBeDeconstructed", 8);
            T.Eq(suggestions.Count, 0, "实测无相近候选；实际: " + string.Join(", ", suggestions));
        });

        T.Case("成对规则的字段只在 PawnKindDef 上同时存在", () =>
        {
            var pk = cat.MembersOf("PawnKindDef")!;
            T.NotNull(pk.FindField("weaponMoney"), "PawnKindDef.weaponMoney");
            T.NotNull(pk.FindField("weaponTags"), "PawnKindDef.weaponTags");

            // ★ 实测校准（权威判定，见自审第 8 条）：
            //   ThingDef   weaponMoney=无        weaponTags=存在 List<string>
            //   PawnKindDef weaponMoney=FloatRange weaponTags=存在 List<string>
            //   —— weaponTags 在 ThingDef 上【也】存在（武器自己带 tag），但 weaponMoney 只在 PawnKindDef 上，
            //      所以“weaponMoney 与 weaponTags 必须成对出现”这条规则（RD9003）的宿主是 PawnKindDef。
            //   先前此处误写成“ThingDef.weaponTags 应不存在”，是会失败的断言。
            var td = cat.MembersOf("ThingDef")!;
            T.Null(td.FindField("weaponMoney"), "ThingDef.weaponMoney 应不存在");
            T.NotNull(td.FindField("weaponTags"), "ThingDef.weaponTags 应存在（List<string>）");
        });

        T.Case("★ 大小写敏感：IsMechanoid 存在、isMechanoid 不存在", () =>
        {
            var rp = cat.MembersOf("RaceProperties")!;
            T.NotNull(rp.FindProperty("IsMechanoid"), "IsMechanoid（属性）");
            T.Null(rp.FindProperty("isMechanoid"), "isMechanoid 不应存在");
            T.Null(rp.FindField("isMechanoid"), "isMechanoid 字段不应存在");
            var hint = rp.Suggest("isMechanoid", 3);
            T.True(hint.Contains("IsMechanoid"), "应把 IsMechanoid 作为大小写候选，实际: " + string.Join(", ", hint));
        });

        T.Case("DamageInfo.Amount 只读且构造器为 2 个", () =>
        {
            var idx = cat.MembersOf("DamageInfo")!;
            var p = idx.FindProperty("Amount");
            T.NotNull(p, "Amount");
            T.False(p!.CanWrite, "Amount 应为只读");
            var ctors = idx.Type.GetConstructors();
            T.Eq(ctors.Length, 2, "构造器数");
            T.True(ctors.Any(c => c.GetParameters().Length == 14), "应有一个 14 参数构造器");
            T.True(ctors.Any(c => c.GetParameters().Length == 1 &&
                                  c.GetParameters()[0].ParameterType.Name == "DamageInfo"),
                "应有 DamageInfo(DamageInfo) 克隆构造器");
        });

        T.Case("Pawn.PreApplyDamage 签名与 virtual", () =>
        {
            var idx = cat.MembersOf("Pawn")!;
            var ms = idx.FindMethods("PreApplyDamage");
            T.Eq(ms.Count, 1, "PreApplyDamage 重载数");
            var m = ms[0];
            var ps = m.GetParameters();
            T.Eq(ps.Length, 2, "参数个数");
            T.True(ps.All(p => p.ParameterType.IsByRef), "两个参数都应为 ref");
            T.True(m.IsVirtual, "应为 virtual");
            T.Contains(MemberInfoIndex.DescribeSignature(m), "ref", "签名应标出 ref");
        });

        T.Case("自定义 XML 加载器是实例方法（shape 顾问的判据）", () =>
        {
            var t = cat.Find("ThingDefCountClass");
            T.NotNull(t, "ThingDefCountClass");
            T.True(ManagedAssemblyCatalog.HasCustomXmlLoader(t!), "ThingDefCountClass 应有实例加载器 → 裸元素写法");
            T.False(ManagedAssemblyCatalog.HasCustomXmlLoader(cat.Find("ThingDef")!),
                "ThingDef 自身不应被当作元素级自定义加载器类型");
        });

        T.Case("类型候选建议可用", () =>
        {
            var s = cat.SuggestTypeNames("BuildingPropertie", 5);
            T.True(s.Contains("BuildingProperties"), "应建议 BuildingProperties，实际: " + string.Join(", ", s));
        });
    }
}
