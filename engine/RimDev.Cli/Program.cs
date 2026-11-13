// ★★ 第 3 期 Task 9b：CLI 的真正入口搬到了 `CommandLine.Run(args, stdout, stderr)`
//   （可直调、可捕获输出，自测不必起子进程）。这里只做一件事：把控制台接到它上面。
//
//   为什么不是「在 Program 里再写一遍分发」：两套分发必然分叉 ——
//   旧实现就在这文件里自己扫了一遍 argv 找 --game-root（`ArgValue`），
//   而各子命令又各自 `GameEnvProbe.Discover()` 一次，于是「开关只被 env 采纳」这件事
//   在五个命令上同时成立却没有任何一处报错。
return RimDev.Cli.CommandLine.Run(args, Console.Out, Console.Error);
