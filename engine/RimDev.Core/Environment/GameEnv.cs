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
    int ManagedDllCount,
    // ★ 第 3 期 Task 9：布局是**结构化判据**（Assembly-CSharp.dll 落在哪），不是版本号比较。
    RimWorldLayout Layout,
    // Assembly-CSharp.dll 的实际路径（ManagedDir 决定，拼一下即可；留成字段避免各处重复拼）
    string AssemblyCSharpPath)
{
    public bool WorkshopExists => Directory.Exists(WorkshopDir);
    public bool PrevPlayerLogExists => File.Exists(PrevPlayerLogPath);
    public bool IsClassic16Layout => Layout == RimWorldLayout.Classic16;
    public bool IsWaylith17Layout => Layout == RimWorldLayout.Waylith17;

    /// <summary>身份快照（env / 自测摘要共用；只读元数据，不载入程序集）。</summary>
    public InstallationIdentity Identity()
    {
        var asm = ManagedAssemblyIdentity.Read(AssemblyCSharpPath);
        return new InstallationIdentity(
            Layout: Layout.ToName(),
            GameRoot: GameRoot,
            ManagedDir: ManagedDir,
            AssembliesInManaged: ManagedDllCount,
            WorkshopRoot: WorkshopDir,
            Assembly: asm,
            Runtime: ManagedAssemblyIdentity.RuntimeDescription);
    }
}
