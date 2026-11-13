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
return T.Report();
