using RimDev.Core.Environment;
using RimDev.Core.Metadata;

namespace RimDev.SelfTest.Cases;

public static class NameCases
{
    public static void Run()
    {
        T.Group("name");

        var env = GameEnvProbe.Discover(startDir: Facts.GameRoot);
        var oracle = new NameOracle(ManagedAssemblyCatalog.Load(env.ManagedDir));

        // 注意（实测校准）：ThingDef 自身的成员池里没有与 canBeDeconstructed / canBeDamagedByExplosions
        // 相近的名字，候选数为 0。所以这些用例只断言“不存在”与错误码，不断言候选；
        // 候选能力由“大小写候选（isMechanoid）”与“类型候选（MechWeightClass）”两条用例覆盖。
        void MustNotExist(string name, NameKind kind, string? owner)
        {
            T.Case($"{name} 不存在", () =>
            {
                var v = oracle.Check(name, kind, owner);
                T.False(v.Exists, name + " 应不存在");
                T.Eq(v.Diagnostic.Code, "RD4001", name + " 的错误码应为字段不存在");
            });
        }

        MustNotExist("canBeDeconstructed", NameKind.Field, "ThingDef");
        MustNotExist("canBeDamagedByExplosions", NameKind.Field, "ThingDef");
        MustNotExist("deconstructibleBlocks", NameKind.Field, "ThingDef");

        T.Case("alwaysDeconstructible 存在且在 BuildingProperties 上", () =>
        {
            var v = oracle.Check("alwaysDeconstructible", NameKind.Field, "BuildingProperties");
            T.True(v.Exists, "alwaysDeconstructible 应存在");
        });

        T.Case("forcedMissRadius 存在（非公开字段也算存在）", () =>
        {
            var v = oracle.Check("forcedMissRadius", NameKind.Field, "VerbProperties");
            T.True(v.Exists, "forcedMissRadius 应存在");
        });

        T.Case("大小写不符被识别为 RD4005", () =>
        {
            var v = oracle.Check("isMechanoid", NameKind.Property, "RaceProperties");
            T.False(v.Exists, "isMechanoid 不存在");
            T.Eq(v.Diagnostic.Code, "RD4005", "错误码应为大小写不符");
            T.Contains(v.Diagnostic.Suggestions![0], "IsMechanoid", "候选首项");
        });

        T.Case("类型查证不假设命名空间", () =>
        {
            T.True(oracle.Check("ThingDef", NameKind.Type).Exists, "ThingDef");
            T.True(oracle.Check("BuildingProperties", NameKind.Type).Exists, "BuildingProperties");
            var v = oracle.Check("BuildingProperties", NameKind.Type);
            T.Contains(v.Diagnostic.Message, "RimWorld.BuildingProperties", "应报全名");
        });

        T.Case("类型不存在时给候选", () =>
        {
            var v = oracle.Check("MechWeightClass", NameKind.Type);
            T.False(v.Exists, "MechWeightClass 不是类型");
            T.True(v.Diagnostic.Suggestions!.Any(s => s.Contains("MechWeightClassDef")),
                "候选应含 MechWeightClassDef，实际: " + string.Join(", ", v.Diagnostic.Suggestions!));
        });

        T.Case("枚举成员查证", () =>
        {
            var ok = oracle.Check("Normal", NameKind.EnumMember, "TickerType");
            T.True(ok.Exists, "TickerType.Normal 应存在");
            var bad = oracle.Check("Normall", NameKind.EnumMember, "TickerType");
            T.False(bad.Exists, "Normall 不应存在");
            T.Eq(bad.Diagnostic.Code, "RD4003", "枚举错误码");
        });

        T.Case("★ Def 名查证如实报告未实现，而不是猜", () =>
        {
            var v = oracle.Check("Repair", NameKind.DefName);
            T.Eq(v.Diagnostic.Severity.ToString(), "Warning", "应为 Warning 而不是 Error");
            T.Contains(v.Diagnostic.Message, "尚未实现", "应说明未实现");
            T.Contains(v.Diagnostic.Hint!, "rimsearcher", "应指路 rimsearcher");
        });

        T.Case("全类型搜成员名（不带 --owner）", () =>
        {
            var v = oracle.Check("forcedMissRadius", NameKind.Field);
            T.True(v.Exists, "应能在某类型上找到 forcedMissRadius");
        });
    }
}
