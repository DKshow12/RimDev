using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace RimDev.Core.Environment;

/// <summary>
/// 被测对象的**身份**：Assembly-CSharp.dll 的名字/版本/MVID/尺寸，以及本进程跑在哪个运行时上。
///
/// ★ 为什么必须有这个：1.7.x 会继续改元数据（类型数、字段、def 内容都可能变）。
///   没有身份打印时，锚点漂移会表现成「某个用例莫名红了」而**无法归因**；
///   有了这一行，「1.7.x 上跑的和另一台 1.7.y 上跑的不是同一个对象」当场可见。
///
/// ★ 这里**只读元数据**（PEReader），不把程序集载入进程 ——
///   因此 net8 产物也能读 net10 的 Assembly-CSharp（载入会抛 ReflectionTypeLoadException，读元数据不会）。
/// </summary>
public sealed record ManagedAssemblyIdentity(
    string FilePath,
    string FileName,
    string? AssemblyName,
    string? AssemblyVersion,
    string? Mvid,
    string? MetadataVersion,
    long FileBytes,
    int? TypeDefinitionCount,
    string? Error)
{
    public static ManagedAssemblyIdentity Missing(string path) =>
        new(path, Path.GetFileName(path), null, null, null, null, 0, null, "文件不存在");

    public static ManagedAssemblyIdentity Read(string path)
    {
        var fileName = Path.GetFileName(path);
        if (!File.Exists(path))
            return Missing(path);

        long bytes;
        try { bytes = new FileInfo(path).Length; }
        catch { bytes = 0; }

        string? asmName = null, asmVersion = null, mvid = null, metadataVersion = null;
        int? typeDefs = null;
        string? error = null;

        // 名称/版本走 AssemblyName.GetAssemblyName（只读元数据，不载入）。
        try
        {
            var an = System.Reflection.AssemblyName.GetAssemblyName(path);
            asmName = an.Name;
            asmVersion = an.Version?.ToString();
        }
        catch (Exception ex)
        {
            error = $"AssemblyName: {ex.GetType().Name}: {ex.Message}";
        }

        try
        {
            using var fs = File.OpenRead(path);
            using var pe = new PEReader(fs);
            var md = pe.GetMetadataReader();
            mvid = md.GetGuid(md.GetModuleDefinition().Mvid).ToString("D");
            metadataVersion = md.MetadataVersion;
            typeDefs = md.TypeDefinitions.Count;
        }
        catch (Exception ex)
        {
            error = (error is null ? "" : error + " | ") + $"PEReader: {ex.GetType().Name}: {ex.Message}";
        }

        return new ManagedAssemblyIdentity(path, fileName, asmName, asmVersion, mvid,
            metadataVersion, bytes, typeDefs, error);
    }

    /// <summary>例如 ".NET 10.0.13" / ".NET Framework 4.8.9310.0"。</summary>
    public static string RuntimeDescription => RuntimeInformation.FrameworkDescription;

    /// <summary>一行式身份（自测摘要 / env 人读输出共用；内容与 json 字段一一对应）。</summary>
    public string ToOneLine() =>
        $"{FileName} name={AssemblyName ?? "?"} version={AssemblyVersion ?? "?"} " +
        $"mvid={Mvid ?? "?"} metadata={MetadataVersion ?? "?"} " +
        $"bytes={FileBytes} typeDefs={(TypeDefinitionCount?.ToString() ?? "?")} " +
        $"runtime={RuntimeDescription}" +
        (Error is null ? "" : $" error=[{Error}]");
}

/// <summary>一份安装的完整身份快照：布局 + 目录 + 程序集身份 + 运行时。</summary>
public sealed record InstallationIdentity(
    string Layout,
    string GameRoot,
    string ManagedDir,
    int AssembliesInManaged,
    string WorkshopRoot,
    ManagedAssemblyIdentity Assembly,
    string Runtime)
{
    public string ToOneLine() =>
        $"layout={Layout} managedDir={ManagedDir} managedDlls={AssembliesInManaged} " +
        $"runtime={Runtime} | {Assembly.ToOneLine()}";
}
