using RimDev.SelfTest;
using RimDev.SelfTest.Cases;

EnvCases.Run();
MetadataCases.Run();
NameCases.Run();

return T.Report();
