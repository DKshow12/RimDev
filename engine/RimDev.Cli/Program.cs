using RimDev.Core;

if (args.Length > 0 && args[0] == "--version")
{
    Console.WriteLine(Placeholder.ToolName + " 0.0.0");
    return 0;
}

Console.Error.WriteLine("usage: rimdev <env|api|name> [options]");
return 3;
