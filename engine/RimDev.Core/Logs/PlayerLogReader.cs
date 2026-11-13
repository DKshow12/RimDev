using System.Text;

namespace RimDev.Core.Logs;

/// <summary>日志里的一个「块」= 一行带时间戳的正文 + 它后面的续行（栈帧等）。</summary>
public sealed record LogBlock(
    int LineNumber,
    string? Timestamp,
    string? Level,
    string Text,
    IReadOnlyList<string> StackLines);

/// <summary>
/// ★★ 第 3 期 Task 15：读 <c>Player.log</c>。
///
/// ── 两条必须遵守的事实（实测，Docs/10 §6.2）────────────────────────────────
/// <list type="number">
/// <item><b>文件被游戏占用</b>：游戏运行时 <c>Player.log</c> 处于打开状态，
///   独占读会抛 <c>IOException</c>。必须用 <c>FileShare.ReadWrite | FileShare.Delete</c>。</item>
/// <item><b>按行首时间戳切块，不能按空行</b>：消息体内部可能有空行；
///   而每条日志以 <c>yyyy-MM-dd HH:mm:ss.fff LEVEL [Source] msg</c> 开头，续行是 4 空格 + <c>at </c>。</item>
/// </list>
/// </summary>
public static class PlayerLogReader
{
    /// <summary>行首时间戳形态：<c>2026-11-09 22:38:24.965</c>。</summary>
    private static readonly System.Text.RegularExpressions.Regex HeadPattern = new(
        @"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\s+(?<rest>.*)$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>级别 token（在时间戳之后）。</summary>
    private static readonly System.Text.RegularExpressions.Regex LevelPattern = new(
        @"^(?<level>ERROR|WARNING|EXCEPTION|INFO|DEBUG)\b\s*(?<msg>.*)$",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// 续行判据。
    /// <para>★ Task 15b 修正：**不能只看「4 空格 + at」**。本机 1.7 实测，
    /// 真实的多行日志块长这样（`Player.log:8757`）：</para>
    /// <code>
    /// 2026-11-11 23:03:55.051 ERROR [Verse] [FileSource] [Mod: …]
    /// [Likely: XML Extensions] [… - Start of stack trace]        ← 没有缩进！
    /// Verse.PatchOperationAdd(xpath="…"): Failed to find a node…  ← 这才是真正内容
    /// [End of stack trace]
    /// Source file: C:\…
    /// </code>
    /// <para>若只认缩进续行，这整块会退化成「一句空壳子」，真正的
    /// <c>Failed to find a node with the given xpath</c> 根本不参与规则匹配 ⇒ 每条都落进
    /// 「未归类」。实测：本机 2.4 MB 日志里有 **62 条** 这个形态。</para>
    /// <para>开放判据：**不以时间戳开头的行就是续行**。这比猜缩进更可靠 ——
    /// 日志块的边界就是时间戳，不是缩进。</para>
    /// </summary>
    private static bool IsContinuation(string line) =>
        line.Length > 0 && !HeadPattern.IsMatch(line);

    /// <summary>
    /// 读日志并按块切分。<paramref name="maxLines"/> ≤ 0 表示不限。
    /// <para>流式逐行读，不把整个文件载入内存（真实日志可达数百 MB）。</para>
    /// </summary>
    public static IEnumerable<LogBlock> ReadBlocks(string path, int maxLines = 0)
    {
        // 共享读：游戏运行时文件被占用。
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        string? curTs = null, curLevel = null;
        var curText = new StringBuilder();
        var curStack = new List<string>();
        var curLineNo = 0;
        var lineNo = 0;
        var emitted = 0;

        while (sr.ReadLine() is { } line)
        {
            lineNo++;
            if (maxLines > 0 && lineNo > maxLines) break;

            var head = HeadPattern.Match(line);
            if (head.Success)
            {
                // 先吐出上一块
                if (curTs is not null || curText.Length > 0)
                {
                    yield return new LogBlock(curLineNo, curTs, curLevel, curText.ToString(), curStack.ToArray());
                    if (++emitted > 0 && maxLines > 0 && emitted >= maxLines) yield break;
                    curStack.Clear();
                }
                curLineNo = lineNo;
                curTs = head.Groups["ts"].Value;
                curText.Clear();
                curStack.Clear();

                var rest = head.Groups["rest"].Value;
                var lv = LevelPattern.Match(rest);
                if (lv.Success)
                {
                    curLevel = lv.Groups["level"].Value.ToUpperInvariant();
                    curText.Append(lv.Groups["msg"].Value);
                }
                else
                {
                    curLevel = null;
                    curText.Append(rest);
                }
                continue;
            }

            if (IsContinuation(line))
            {
                curStack.Add(line.TrimEnd());
            }
            else if (curText.Length > 0)
            {
                // 同一块内的普通续行（消息体换行）
                curText.Append('\n').Append(line);
            }
        }

        if (curTs is not null || curText.Length > 0)
            yield return new LogBlock(curLineNo, curTs, curLevel, curText.ToString(), curStack.ToArray());
    }
}
