using RimDev.SelfTest;
using RimDev.SelfTest.Cases;

EnvCases.Run();
MetadataCases.Run();
NameCases.Run();
CatalogCases.Run();
DefScanCases.Run();
CheckCases.Run();

return T.Report();
