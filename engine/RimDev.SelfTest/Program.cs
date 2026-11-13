using RimDev.Core.Environment;
using RimDev.SelfTest;
using RimDev.SelfTest.Cases;

// ★ 第 3 期 Task 9：先探测一次布局与**身份**（不改任何断言，只决定「绑定 1.6 语料的用例是跑还是显式 SKIP」，
//   并把「本次实际加载的 Assembly-CSharp / 运行时」打进摘要 —— 1.7.x 小版本之间做归因的唯一依据）。
T.Init();
T.Progress = args.Contains("--progress");

// ★ 第 3 期 Task 9：**产物 ↔ 布局**一致性闸门（与 CLI 同一判据）。
//   net8 产物读不了 net10 的 Assembly-CSharp（反向同理），跨用时反射得到空类型表 ——
//   此时继续跑只会把「读不了元数据」表现成一堆「类型不存在」的假失败，无法归因。
//   正确行为：报可读原因 + 退出码 4（环境不满足），并写出「本该跳过多少条 Classic16 用例」。
if (T.Layout is RimWorldLayout.Classic16 or RimWorldLayout.Waylith17
    && RimDevProducts.MismatchMessage(T.Layout) is { } mismatch)
{
    Console.WriteLine($"产物 = {RimDevProducts.RuntimeTfm} / 运行时 = {T.Runtime}");
    Console.WriteLine(mismatch);
    Console.WriteLine($"skipped-by-layout = {LayoutBoundInventory.Count}");
    Console.WriteLine("本次未运行任何用例（产物与布局不匹配：跨 TFM 无法载入对方的托管程序集）。");
    return 4;
}

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

// ★ 第 3 期 Task 5c：按需入口 `rimdev-selftest --task5c-cases`（只跑本 Task 的用例，秒级，
//   供复审时单独复跑归属键的判别性用例；与 `--task5b-cases` 同一先例）。
if (args.Contains("--task5c-cases"))
{
    Phase3Task5cCases.Run();
    return T.Report();
}
// ★ 第 3 期 Task 6：按需入口 `rimdev-selftest --task6-probe`（只打印引用字段空间的口径自算与原始观测，
//   不进常规跑；与 `--task5b-probe` / `--task4-measure` 同一先例）。
// ★ 第 3 期 Task 6：按需入口 `rimdev-selftest --task6-cases`（只跑本 Task 的用例，供复审时单独复跑）。
if (args.Contains("--task6-cases"))
{
    Phase3Task6Cases.Run();
    return T.Report();
}

if (args.Contains("--task6-probe"))
{
    return Phase3Task6Cases.RunProbe();
}

// ★ 下面每个文件用 T.RunFile 包一层：文件级初始化异常（顶层 Discover/Load 抛了）会记成一条失败用例，
//   而不是让整个自测「0 个用例、exit 1」——那种结果无法归因。
T.RunFile("env", EnvCases.Run);
T.RunFile("metadata", MetadataCases.Run);
T.RunFile("name", NameCases.Run);
T.RunFile("catalog", CatalogCases.Run);
T.RunFile("defscan", DefScanCases.Run);
T.RunFile("check", CheckCases.Run);
T.RunFile("loadfolders", LoadFolderCases.Run);
// ★ 第 3 期 Task 9：布局识别 + 「布局 → 产物」映射（版本切换的纯函数覆盖，布局无关）
T.RunFile("task9-layout", LayoutSwitchCases.Run);
// ★ 第 3 期 Task 2：check --scope 语义 / 计数自洽三式 / RD2001 cause / excludesAbstract / 旧新三条机制
T.RunFile("phase3-task2", Phase3Task2Cases.Run);
// ★ 第 3 期 Task 2b：RD4001 的两条假阳机制（Class= 优先 / 含 '.' 不退化简单名）+ ParentChainCheck 提速等价性
T.RunFile("phase3-task2b", Phase3Task2bCases.Run);
// ★ 第 3 期 Task 2c：DoFieldSearch 的另外三条分支（别名 / [Unsaved] / 抑制）与两条回归钉子
T.RunFile("phase3-task2c", Phase3Task2cCases.Run);
// ★ 第 3 期 Task 3：mod 加载顺序 + 文件夹优先级 + 遮蔽（RD2025/RD2026）+ 顺序一致性（RD2011）
T.RunFile("phase3-task3", Phase3Task3Cases.Run);
// ★ 第 3 期 Task 4：PatchOperation 家族静态求值 + RD2009/RD2014 + Step 1 时序（IL 实证）
//   另有按需入口 `rimdev-selftest --task4-measure`（Step 6 的全库口径测量，约 60 s，不进常规跑）
if (args.Contains("--task4-measure"))
{
    Phase3Task4Cases.RunMeasurement();
    return 0;
}
T.RunFile("phase3-task4", Phase3Task4Cases.Run);
// ★ 第 3 期 Task 4b：同一 packageId 多份安装不再放大重复诊断（RD2026/RD2004 的折叠）
T.RunFile("phase3-task4b", Phase3Task4bCases.Run);
// ★ 第 3 期 Task 4b：按需入口 `rimdev-selftest --task4b-measure`（全库 A/B 对照，约 60 s，不进常规跑）
//   与 Task 4 的 `--task4-measure` 同一先例：同一份 ctx 上把「修复前（ModRoot 全 null）/修复后」各跑一遍，
//   差值只可能来自折叠，不会与语料漂移混淆。
if (args.Contains("--task4b-measure"))
{
    Phase3Task4bCases.RunMeasurement();
    return 0;
}
// ★ 第 3 期 Task 5a：InheritanceLayer（ParentName 解析口径 + ResolutionBasis + 补丁字面量 + RD2008）
T.RunFile("phase3-task5a", Phase3Task5aCases.Run);
T.RunFile("phase3-task5b", Phase3Task5bCases.Run);
// ★ 第 3 期 Task 5c：FieldNameCheck 的归属键（同一物理行多 def 时各校验自己的子元素）
T.RunFile("phase3-task5c", Phase3Task5cCases.Run);

// ★ 第 3 期 Task 6：引用解析（RD3001）+ SimulatedDefSet / ReferenceFieldIndex
T.RunFile("phase3-task6", Phase3Task6Cases.Run);

return T.Report();
