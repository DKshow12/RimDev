using RimDev.SelfTest;
using RimDev.SelfTest.Cases;

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
return T.Report();
