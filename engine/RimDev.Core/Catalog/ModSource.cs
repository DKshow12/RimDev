namespace RimDev.Core.Catalog;

public enum ModSource
{
    /// <summary>游戏自带内容（&lt;游戏根&gt;\Data\Core 等）。</summary>
    Official,
    /// <summary>&lt;游戏根&gt;\Mods\ 下的本地 mod。</summary>
    Local,
    /// <summary>Steam 工坊（workshop\content\294100\&lt;publishedFileId&gt;）。</summary>
    Workshop
}

public static class ModSourceExtensions
{
    /// <summary>同 packageId 冲突时的优先级：官方 &gt; 本地 &gt; 工坊。</summary>
    public static int Priority(this ModSource s) => s switch
    {
        ModSource.Official => 0,
        ModSource.Local => 1,
        _ => 2
    };
}
