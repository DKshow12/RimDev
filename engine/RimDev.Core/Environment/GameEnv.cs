namespace RimDev.Core.Environment;

/// <summary>环世界安装环境的一份不可变快照。</summary>
public sealed record GameEnv(
    string GameRoot,
    string ManagedDir,
    string ModsDir,
    string DataDir,
    string WorkshopDir,
    string ConfigDir,
    string ModsConfigPath,
    string PlayerLogPath,
    string PrevPlayerLogPath,
    string VersionRaw,
    string VersionConfig,
    string TargetVersion,
    int ManagedDllCount)
{
    public bool WorkshopExists => Directory.Exists(WorkshopDir);
    public bool PrevPlayerLogExists => File.Exists(PrevPlayerLogPath);
}
