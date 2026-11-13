using System.Diagnostics;
using System.Threading;

namespace RimDev.Core;

/// <summary>
/// ★ Task 8 Step 3：**可控的按索引并行**（内置并行工具在本机实测只跑到 ~5 路，见下）。
///
/// 为什么不用 `System.Threading.Tasks.Parallel.For`：
/// `sim --all` 的实测是「请求 32 路、实际只跑到 ~5 路」（进程 32 核空闲、traversal 墙钟 84 s
/// 而各线程候选判定累加 426 s ⇒ 平均并发 5.06；串行同一份工作是 231 s）。
/// 也就是说 `Parallel.For` 在这个宿主里没能把工作铺开（ThreadPool 注入/工作窃取的行为不可控）。
/// 这里改用**自建线程 + 原子领取索引**：线程数由我们定，分配是动态的（谁空谁领下一个），
/// 配合调用方的 LPT 排序（重活先领），尾延迟 ≈ 总工作量 / 并行度。
///
/// 语义保证：`body` 只按索引读写调用方提供的数组 ⇒ **结果与串行逐条一致**，
/// 调用方按索引回放即可（诊断顺序、取样顺序都不变）。
/// </summary>
internal static class ParallelWork
{
    /// <summary>并行度归一：0/负 = 自动（= 处理器数）；不小于 1，不超过任务数。</summary>
    internal static int Degree(int requested, int count)
    {
        if (count <= 1) return 1;
        var dop = requested > 0 ? requested : System.Environment.ProcessorCount;
        if (dop < 1) dop = 1;
        if (dop > count) dop = count;
        return dop;
    }

    /// <summary>本次实际跑起来的线程数（诊断用；`--timings` 报出它，用来证明并行**真的**发生了）。</summary>
    internal static int LastThreadsUsed;

    internal static void Run(int count, int dop, Action<int> body)
    {
        if (count <= 0) return;
        if (dop <= 1 || count == 1)
        {
            LastThreadsUsed = 1;
            for (var i = 0; i < count; i++) body(i);
            return;
        }

        var next = -1;
        var started = 0;
        var workers = new Thread[dop];
        for (var w = 0; w < workers.Length; w++)
        {
            workers[w] = new Thread(() =>
            {
                Interlocked.Increment(ref started);
                while (true)
                {
                    var i = Interlocked.Increment(ref next);
                    if (i >= count) return;
                    body(i);
                }
            })
            {
                IsBackground = true,
                Name = "rimdev-worker",
            };
            // 让线程尽快起来：默认栈够用，别让首次调度拖尾。
            workers[w].Start();
        }

        foreach (var t in workers) t.Join();
        LastThreadsUsed = started;
    }

    /// <summary>把文件按字节数**降序**排成下发顺序（LPT 调度）——只改「谁先算」，不改任何输出。</summary>
    internal static int[] LongestFirst<T>(System.Collections.Generic.IList<T> items, Func<T, string> pathOf)
    {
        var order = new int[items.Count];
        var sizes = new long[items.Count];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
            try { sizes[i] = new FileInfo(pathOf(items[i])).Length; }
            catch { sizes[i] = 0; }
        }
        Array.Sort(order, (x, y) => sizes[y].CompareTo(sizes[x]));
        return order;
    }
}
