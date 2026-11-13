using RimDev.Cli.Commands;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: rimdev <env|api|name|index|check|files|order|sim> [options]（`rimdev check --help` / `rimdev sim --help` 看 scan scope 语义）");
    return 3;
}

return args[0] switch
{
    "env" => EnvCommand.Run(args),
    "api" => ApiCommand.Run(args),
    "index" => IndexCommand.Run(args),
    "files" => FilesCommand.Run(args),
    "check" => CheckCommand.Run(args),
    // ★ 第 3 期 Task 3：mod 加载顺序 + 每个 mod 的文件夹优先级
    "order" => OrderCommand.Run(args),
    "name" => NameCommand.Run(args),
    // ★ 第 3 期 Task 6：加载结果摘要（有效 def / 遮蔽 / 悬空引用 / MayRequire / loader 边界）
    "sim" => SimCommand.Run(args),
    "--version" => PrintVersion(),
    _ => Unknown(args[0])
};

static int PrintVersion()
{
    Console.WriteLine("rimdev 0.1.0");
    return 0;
}

static int Unknown(string cmd)
{
    Console.Error.WriteLine($"未知命令: {cmd}");
    Console.Error.WriteLine("usage: rimdev <env|api|name|index|check|files|order|sim> [options]（`rimdev check --help` / `rimdev sim --help` 看 scan scope 语义）");
    return 3;
}
