using RimDev.SelfTest;

T.Group("harness");
T.Case("断言台本身可用", () => { T.Eq(1, 1, "恒等"); T.True(true, "恒真"); });

return T.Report();
