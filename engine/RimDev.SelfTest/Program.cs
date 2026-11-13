using RimDev.SelfTest;
using RimDev.SelfTest.Cases;

// ★ 第 3 期 Task 5b：按需入口 `rimdev-selftest --task5b-probe`（只跑引擎对照台探针并打印原始观测，
//   秒级、不进常规跑；与 `--task4-measure` / `--task4b-measure` 同一先例）。
if (args.Contains("--task5b-probe"))
{
    return Phase3Task5bCases.RunProbe();
}

// ★ 第 3 期 Task 5b：按需入口 `rimdev-selftest --task5b-cases`（只跑本 Task 的用例，秒级，
//   供复审时单独复跑主门禁；与 `--task4-measure` 同一先例）。
if (args.Contains("--task5b-cases"))
{
    Phase3Task5bCases.Run();
    return T.Report();
}

EnvCases.Run();
MetadataCases.Run();
NameCases.Run();
CatalogCases.Run();
DefScanCases.Run();
CheckCases.Run();
LoadFolderCases.Run();
// ★ 第 3 期 Task 2：check --scope 语义 / 计数自洽三式 / RD2001 cause / excludesAbstract / 旧新三条机制
Phase3Task2Cases.Run();
// ★ 第 3 期 Task 2b：RD4001 的两条假阳机制（Class= 优先 / 含 '.' 不退化简单名）+ ParentChainCheck 提速等价性
Phase3Task2bCases.Run();
// ★ 第 3 期 Task 2c：DoFieldSearch 的另外三条分支（别名 / [Unsaved] / 抑制）与两条回归钉子
Phase3Task2cCases.Run();
// ★ 第 3 期 Task 3：mod 加载顺序 + 文件夹优先级 + 遮蔽（RD2025/RD2026）+ 顺序一致性（RD2011）
Phase3Task3Cases.Run();
// ★ 第 3 期 Task 4：PatchOperation 家族静态求值 + RD2009/RD2014 + Step 1 时序（IL 实证）
//   另有按需入口 `rimdev-selftest --task4-measure`（Step 6 的全库口径测量，约 60 s，不进常规跑）
if (args.Contains("--task4-measure"))
{
    Phase3Task4Cases.RunMeasurement();
    return 0;
}
Phase3Task4Cases.Run();
// ★ 第 3 期 Task 4b：同一 packageId 多份安装不再放大重复诊断（RD2026/RD2004 的折叠）
Phase3Task4bCases.Run();
// ★ 第 3 期 Task 4b：按需入口 `rimdev-selftest --task4b-measure`（全库 A/B 对照，约 60 s，不进常规跑）
//   与 Task 4 的 `--task4-measure` 同一先例：同一份 ctx 上把「修复前（ModRoot 全 null）/ 修复后」各跑一遍，
//   差值只可能来自折叠，不会与语料漂移混淆。
if (args.Contains("--task4b-measure"))
{
    Phase3Task4bCases.RunMeasurement();
    return 0;
}
// ★ 第 3 期 Task 5a：InheritanceLayer（ParentName 解析口径 + ResolutionBasis + 补丁字面量 + RD2008）
Phase3Task5aCases.Run();
Phase3Task5bCases.Run();

return T.Report();
