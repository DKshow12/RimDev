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
// ★ 第 3 期 Task 9b：按需入口 `rimdev-selftest --task9b-cases`（只跑本 Task 的用例，十几秒 ——
//   其中 check/sim 各一次真扫描；与 `--task5b-cases` / `--task6-cases` 同一先例）。
if (args.Contains("--task9b-cases"))
{
    Phase3Task9bCases.Run();
    return T.Report();
}
// ★ 第 3 期 Task 9c：按需入口 `rimdev-selftest --task9c-cases`（只跑本 Task 的用例，约十秒 ——
//   其中 index/files/check×2/sim 各一次真扫描）。
if (args.Contains("--task26c-cases"))
{
    T.RunFile("phase3-task26c", Phase3Task26cCases.Run);
    return T.Report();
}
if (args.Contains("--task26b-cases"))
{
    T.RunFile("phase3-task26b", Phase3Task26bCases.Run);
    return T.Report();
}
if (args.Contains("--task26-cases"))
{
    T.RunFile("phase3-task26", Phase3Task26Cases.Run);
    return T.Report();
}
if (args.Contains("--task24-cases"))
{
    T.RunFile("phase3-task24", Phase3Task24Cases.Run);
    return T.Report();
}
if (args.Contains("--task22-cases"))
{
    T.RunFile("phase3-task22", Phase3Task22Cases.Run);
    return T.Report();
}
if (args.Contains("--task21-cases"))
{
    T.RunFile("phase3-task21", Phase3Task21Cases.Run);
    return T.Report();
}
if (args.Contains("--task20-cases"))
{
    T.RunFile("phase3-task20", Phase3Task20Cases.Run);
    return T.Report();
}
if (args.Contains("--task19-cases"))
{
    T.RunFile("phase3-task19", Phase3Task19Cases.Run);
    return T.Report();
}
if (args.Contains("--task18-cases"))
{
    T.RunFile("phase3-task18", Phase3Task18Cases.Run);
    return T.Report();
}
if (args.Contains("--task17-cases"))
{
    T.RunFile("phase3-task17", Phase3Task17Cases.Run);
    return T.Report();
}
if (args.Contains("--task16-cases"))
{
    T.RunFile("phase3-task16", Phase3Task16Cases.Run);
    return T.Report();
}
if (args.Contains("--task15-cases"))
{
    T.RunFile("phase3-task15", Phase3Task15Cases.Run);
    return T.Report();
}
if (args.Contains("--task14-cases"))
{
    T.RunFile("phase3-task14", Phase3Task14Cases.Run);
    return T.Report();
}
if (args.Contains("--task13-cases"))
{
    T.RunFile("phase3-task13", Phase3Task13Cases.Run);
    return T.Report();
}
if (args.Contains("--task12-cases"))
{
    T.RunFile("phase3-task12", Phase3Task12Cases.Run);
    return T.Report();
}
if (args.Contains("--task11-cases"))
{
    // ★ 独立入口：跳过其他组（它们在 641 mod 语料下要跑很久），只跑 task11 的判别性用例。
    T.RunFile("phase3-task11", Phase3Task11Cases.Run);
    return T.Report();
}
if (args.Contains("--task9c-cases"))
{
    Phase3Task9cCases.Run();
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

// ★ 第 3 期 Task 9b：**全局环境开关的贯通性**（`--game-root` / `--workshop-root` 不只被 env 采纳）。
//   ★★ 判别性：对每条扫描/查询命令断言「不存在的根 ⇒ RD1001 + 退出码 4 且不产出结果」——
//   修前这五条全部照常输出扫描结果、一个错都不报，所以这些用例在旧实现上必然红。
T.RunFile("phase3-task9b", Phase3Task9bCases.Run);

// ★ 第 3 期 Task 9c：**未知开关（尤其是拼错的 --game-root）放在命令之后被静默吞掉**。
//   ★★ 判别性：8 条命令 + 拼错的开关 ⇒ 退出码 3 且**不产出结果**；另一半断言同一批命令带真实开关仍然正常。
//   修前这 8 条全部照常输出扫描结果、一个错都不报，所以前一半用例在旧实现上必然红。
T.RunFile("phase3-task9c", Phase3Task9cCases.Run);
// ★ 第 3 期 Task 11：引用方 mod 未启用 ⇒ RD3001 降级为 info（全库 3971 条里 3256 条是这类假阳）
T.RunFile("phase3-task11", Phase3Task11Cases.Run);
// ★ 第 3 期 Task 12：补丁层三态（跳过 ≠ 失败），防 Sequence 因自定义 Class 而提前中止
T.RunFile("phase3-task12", Phase3Task12Cases.Run);
// ★ 第 3 期 Task 13：全限定元素名归并（AlienRace.ThingDef_AlienRace → ThingDef）
T.RunFile("phase3-task13", Phase3Task13Cases.Run);
// ★ 第 3 期 Task 14：真实 mod 程序集上的归并端到端验证
T.RunFile("phase3-task14", Phase3Task14Cases.Run);
// ★ 第 3 期 Task 15：日志诊断（RD7xxx，从［引擎自己说的话］回推）
T.RunFile("phase3-task15", Phase3Task15Cases.Run);
// ★ 第 3 期 Task 16：补丁失败的五种真机形态（日志规则覆盖度）
T.RunFile("phase3-task16", Phase3Task16Cases.Run);
// ★ 第 3 期 Task 17：慢规则的用户告知（防「以为挂了」）
T.RunFile("phase3-task17", Phase3Task17Cases.Run);
// ★ 第 3 期 Task 18：<success> 四种语义（Always 无条件 / Never 真中断 / Invert / Normal）
T.RunFile("phase3-task18", Phase3Task18Cases.Run);
// ★ 第 3 期 Task 19：按规则子集运行（--rules / --skip-rules）
T.RunFile("phase3-task19", Phase3Task19Cases.Run);
// ★ 第 3 期 Task 20：补丁失败的最后四种形态（未归类收尾 4 条）
T.RunFile("phase3-task20", Phase3Task20Cases.Run);
// ★ 第 3 期 Task 21：命令间口径一致性（sim 补 --only-official + 假 0 防护）
T.RunFile("phase3-task21", Phase3Task21Cases.Run);
// ★ 第 3 期 Task 22：四扫描命令 --only-official 口径统一 + 同块数字一致
T.RunFile("phase3-task22", Phase3Task22Cases.Run);
// ★ 第 3 期 Task 24：About.xml 解析失败必须报出（不许静默丢弃）
T.RunFile("phase3-task24", Phase3Task24Cases.Run);
// ★ 第 3 期 Task 26：About.xml 四个附加特性（302/107/2 个 mod 在用的那批）
T.RunFile("phase3-task26", Phase3Task26Cases.Run);
// ★ Task 26b：AboutFeatureCheck 行为验证（防「诊断 0 条」的假阴）
T.RunFile("phase3-task26b", Phase3Task26bCases.Run);
// ★ Task 26c：端到端验证（真实语料的 2 条冲突必须报出来）
T.RunFile("phase3-task26c", Phase3Task26cCases.Run);

return T.Report();
