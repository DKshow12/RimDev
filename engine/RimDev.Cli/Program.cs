using RimDev.Cli.Commands;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: rimdev <env|api|name|index|check> [options]");
    return 3;
}

return args[0] switch
{
    "env" => EnvCommand.Run(args),
    "api" => ApiCommand.Run(args),
    "index" => IndexCommand.Run(args),
    "check" => CheckCommand.Run(args),
    "name" => NameCommand.Run(args),
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
    Console.Error.WriteLine("usage: rimdev <env|api|name|index|check> [options]");
    return 3;
}
