using System.Text;
using System.Text.Json;
// DefaultCacheDir(GameEnv env) 需要 GameEnv（RimDev.Core.Environment）
using RimDev.Core.Environment;

namespace RimDev.Core.Defs;

/// <summary>文件指纹，用于增量跳过未变文件（实测启用集合 1649 文件，全量重扫实测 185 ms）。</summary>
public sealed record FileFingerprint(long Size, long MtimeUtcTicks);

/// <summary>
/// 索引的磁盘形态：一个 JSONL（每行一个 def 头）+ 一个 JSON 指纹表。
///
/// 为什么不用 SQLite：本机 nuget.org 不可达，SQLite 只能靠本地缓存（8.0.10 确实可用），
/// 但引入 PackageReference 会触发 NU1900（漏洞库拉取失败）警告，破坏「构建 0 警告」纪律。
/// 实测规模（启用集合 12.91 MB / 1649 个 XML、全库 299.94 MB）下，JSONL + 内存 Dictionary 足够。
/// 见实施计划 §0.3。
/// </summary>
public sealed class DefIndexStore
{
    /// <summary>
    /// 磁盘索引的 schema 版本。`rimdev index` 写入它，`rimdev check` / `name --index` 校验它。
    ///
    /// ★ 为什么要有这个常量：消费方若只判 <see cref="Exists"/> 就直接 <see cref="LoadDefs"/>，
    ///   一旦索引格式升级（字段增删/短键改名），旧缓存会被【静默当成新格式】读出来 —— 又是一类
    ///   「不报错、只是答案错」。`01` §7 早已定义 RD1004 = 索引缓存损坏或 schema 版本不匹配 →
    ///   报它并当作没有索引（去跑 --rebuild），而不是继续用它作答。
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// 缓存目录的【单一真源】。
    ///
    /// ★ 必须从 env.ModsDir（= 游戏根\Mods）再往下取 _rimdev：_rimdev 就在 Mods\ 里，
    ///   而 Path.GetDirectoryName(env.ModsDir) 得到的是游戏根，会拼出 &lt;游戏根&gt;\_rimdev\cache ——
    ///   那既不是缓存实际位置，也在 DSH 工作区 (...\RimWorld\Mods) 之外，写入会被沙箱直接拒绝。
    ///   索引命令与消费命令各写一遍这个公式必然再次分叉，所以只留这一处。
    /// </summary>
    public static string DefaultCacheDir(GameEnv env) => Path.Combine(env.ModsDir, "_rimdev", "cache");

    private readonly string _dir;

    public DefIndexStore(string dir) => _dir = dir;

    public string DefsPath => Path.Combine(_dir, "defs.jsonl");
    public string FingerprintsPath => Path.Combine(_dir, "fingerprints.json");
    public string MetaPath => Path.Combine(_dir, "index-meta.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public Dictionary<string, FileFingerprint> LoadFingerprints()
    {
        if (!File.Exists(FingerprintsPath)) return new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var text = File.ReadAllText(FingerprintsPath);
            return JsonSerializer.Deserialize<Dictionary<string, FileFingerprint>>(text)
                   ?? new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static FileFingerprint Fingerprint(string fileAbs)
    {
        var fi = new FileInfo(fileAbs);
        return new FileFingerprint(fi.Length, fi.LastWriteTimeUtc.Ticks);
    }

    /// <summary>把扫描结果写成 JSONL（覆盖写），同时写指纹表与元信息。</summary>
    public void Save(ScanResult scan, IReadOnlyDictionary<string, FileFingerprint> fingerprints, string scopeNote)
    {
        Directory.CreateDirectory(_dir);

        using (var w = new StreamWriter(DefsPath, false, new UTF8Encoding(false)))
        {
            foreach (var d in scan.Defs)
            {
                w.WriteLine(JsonSerializer.Serialize(new
                {
                    t = d.DefType,
                    n = d.DefName,
                    a = d.NameAttr,
                    p = d.ParentName,
                    m = d.MayRequire,
                    x = d.IsAbstract,
                    mod = d.ModPackageId,
                    f = d.FileAbs,
                    r = d.FileRelToMod,
                    l = d.Line
                }, Json));
            }
        }

        var fpCount = fingerprints.Count;
        // ★ 指纹表只包含「落盘那一刻仍然存在」的文件（FingerprintAll 会跳过不存在的路径）。
        //   主动写入中的语料（本项目就有：另一个写入者在改 Mods\ThreatFleet）可能在
        //   「枚举」与「取指纹」之间消失 —— 这会让 files != fingerprints，成为一个**真的**不一致。
        //   不把它悄悄吞掉，而是单独记一个计数，让自洽断言失败时能立刻说清是哪一类。
        var vanished = scan.Files.Count(f => !fingerprints.ContainsKey(f.FileAbs));

        File.WriteAllText(FingerprintsPath, JsonSerializer.Serialize(fingerprints, Json));
        File.WriteAllText(MetaPath, JsonSerializer.Serialize(new
        {
            // ★ 单一真源：写盘与消费方校验必须用同一个常量（见 CurrentSchemaVersion 注释）
            schemaVersion = CurrentSchemaVersion,
            scope = scopeNote,
            // ==== 自洽三元组的五个数（名字描述【实际动作】）====
            filesEnumerated = scan.FilesScanned,                       // 磁盘上枚举到的文件
            fingerprintsRecorded = fpCount,                             // 落盘进指纹表的条数
            filesVanishedBeforeFingerprint = vanished,                  // 枚举后被删、指纹表里没有的
            filesParsedOk = scan.Files.Count(f => f.ParseError is null), // 真读了且成功
            filesSkippedUnchanged = 0,                                  // 本次是全量重扫，没有跳过任何文件
            filesParseFailed = scan.FilesFailed,                        // 真读了但失败
            // ==== 结果与元信息 ====
            defsIndexed = scan.Defs.Count,
            elapsedMs = scan.ElapsedMs,
            savedAtUtc = DateTime.UtcNow.ToString("o")
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>读回索引。用于后续 check / name --kind def 免重扫。</summary>
    public List<RawDef> LoadDefs()
    {
        var list = new List<RawDef>();
        if (!File.Exists(DefsPath)) return list;

        foreach (var line in File.ReadLines(DefsPath))
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            list.Add(new RawDef(
                DefType: r.GetProperty("t").GetString() ?? "",
                DefName: r.GetProperty("n").GetString(),
                NameAttr: r.GetProperty("a").GetString(),
                ParentName: r.GetProperty("p").GetString(),
                MayRequire: r.GetProperty("m").GetString(),
                IsAbstract: r.GetProperty("x").GetBoolean(),
                ModPackageId: r.GetProperty("mod").GetString() ?? "",
                FileAbs: r.GetProperty("f").GetString() ?? "",
                FileRelToMod: r.GetProperty("r").GetString() ?? "",
                Line: r.GetProperty("l").GetInt32()));
        }
        return list;
    }

    /// <summary>挑出需要重新解析的文件（指纹变了或不在缓存里）。</summary>
    public static List<(string FileAbs, string ModPackageId, string ModRoot)> SelectChanged(
        IReadOnlyList<(string FileAbs, string ModPackageId, string ModRoot)> all,
        IReadOnlyDictionary<string, FileFingerprint> cache)
    {
        var changed = new List<(string, string, string)>();
        foreach (var f in all)
        {
            if (!File.Exists(f.FileAbs)) continue;
            if (cache.TryGetValue(f.FileAbs, out var fp) && fp == Fingerprint(f.FileAbs)) continue;
            changed.Add(f);
        }
        return changed;
    }

    /// <summary>
    /// 同时算出「需要重解析的文件」与「缓存里有、本次输入里已消失的文件」。
    ///
    /// ★ 为什么必须同时要这两项：只看 SelectChanged 时，「只删不改」会让 Changed 为空，
    ///   而调用方（rimdev index）会据此走「直接 store.LoadDefs()」的快路径，
    ///   把**已删除文件的 def 当成最新结果**返回 —— 这是静默错答，比慢更糟。
    ///   实测复现（2026-10-14）：缓存 2 个文件、删掉 1 个后磁盘剩 1 个，
    ///   SelectChanged 返回 0，只有 Diff.Removed 才能指出这次不一致。
    /// </summary>
    public static (List<(string FileAbs, string ModPackageId, string ModRoot)> Changed, List<string> Removed) Diff(
        IReadOnlyList<(string FileAbs, string ModPackageId, string ModRoot)> all,
        IReadOnlyDictionary<string, FileFingerprint> cache)
    {
        var changed = SelectChanged(all, cache);

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in all) present.Add(f.FileAbs);

        var removed = new List<string>();
        foreach (var k in cache.Keys)
            if (!present.Contains(k)) removed.Add(k);
        removed.Sort(StringComparer.OrdinalIgnoreCase);

        return (changed, removed);
    }

    public static Dictionary<string, FileFingerprint> FingerprintAll(
        IReadOnlyList<(string FileAbs, string ModPackageId, string ModRoot)> all)
    {
        var d = new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in all)
            if (File.Exists(f.FileAbs)) d[f.FileAbs] = Fingerprint(f.FileAbs);
        return d;
    }

    public bool Exists() => File.Exists(DefsPath) && File.Exists(MetaPath);

    /// <summary>
    /// index-meta.json 的内容 —— **自洽三元组**的载体（第 3 期 Task 2 Step 3，修 F3）。
    ///
    /// ★ 字段命名纪律（本项目已因此栽过三次）：名字必须描述【实际动作】，不是【判定输入】。
    ///   · <see cref="FilesParsedOk"/> —— 本次**真的重新读了并成功**的文件数
    ///     （不是「指纹判定需要重解析」的个数；后者叫 <see cref="FilesSkippedUnchanged"/> 的反面，不要混用）
    ///   · <see cref="FilesSkippedUnchanged"/> —— 本次**因为指纹一致而没读**的文件数
    ///   · <see cref="FilesParseFailed"/> —— 本次**真的读了但失败**的文件数（⊆ 读过的集合）
    ///   旧字段名 `files`/`failed` 无法回答「这次到底干了什么」，已随 schemaVersion 2 废弃。
    /// </summary>
    public sealed record Meta(
        int SchemaVersion,
        string? Scope,
        int FilesEnumerated,
        int FingerprintsRecorded,
        int FilesVanishedBeforeFingerprint,
        int FilesParsedOk,
        int FilesSkippedUnchanged,
        int FilesParseFailed,
        int DefsIndexed,
        long ElapsedMs,
        string? SavedAtUtc)
    {
        /// <summary>
        /// 三式自洽（Task 2 Step 3 要求的那一条）：
        /// <c>filesEnumerated == fingerprintsRecorded == filesParsedOk + filesSkippedUnchanged + filesParseFailed</c>。
        /// 不成立意味着「指纹表 / 计数器 / 磁盘集合」三者互相矛盾，缓存不可信。
        /// </summary>
        public bool SelfConsistent =>
            FilesEnumerated == FingerprintsRecorded
            && FilesEnumerated == FilesParsedOk + FilesSkippedUnchanged + FilesParseFailed;

        /// <summary>把三式的实际数字写出来（报告与 RD1004 的 Evidence 都用它，避免只给一个 bool）。</summary>
        public string SelfConsistencyText =>
            $"files={FilesEnumerated} fingerprints={FingerprintsRecorded} "
            + $"parsedOk+skipped+failed={FilesParsedOk}+{FilesSkippedUnchanged}+{FilesParseFailed}"
            + $"(vanishedBeforeFingerprint={FilesVanishedBeforeFingerprint})";
    }

    /// <summary>
    /// 缓存可用性判定结果。<see cref="Usable"/> = false 时调用方必须报 <c>RD1004</c> 并**拒绝使用缓存**
    /// （去跑全量重扫），而不是拿着自相矛盾的索引作答。
    /// </summary>
    public sealed record CacheCheck(bool Usable, string Reason, Meta? Meta, int FingerprintCount);

    /// <summary>
    /// 读回 index-meta.json。**必须用它**：走缓存快路径时若把解析失败数当成 0，
    /// 上一次真扫描的失败就会被报成「解析失败 0」—— 一个假的「全清」信号。
    /// 文件缺失或损坏时返回 null（调用方按「无缓存」处理）。
    /// </summary>
    public Meta? LoadMeta()
    {
        if (!File.Exists(MetaPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(MetaPath));
            var r = doc.RootElement;
            return new Meta(
                SchemaVersion: GetInt(r, "schemaVersion"),
                Scope: r.TryGetProperty("scope", out var sc) ? sc.GetString() : null,
                FilesEnumerated: GetInt(r, "filesEnumerated"),
                FingerprintsRecorded: GetInt(r, "fingerprintsRecorded"),
                FilesVanishedBeforeFingerprint: GetInt(r, "filesVanishedBeforeFingerprint"),
                FilesParsedOk: GetInt(r, "filesParsedOk"),
                FilesSkippedUnchanged: GetInt(r, "filesSkippedUnchanged"),
                FilesParseFailed: GetInt(r, "filesParseFailed"),
                DefsIndexed: GetInt(r, "defsIndexed"),
                ElapsedMs: GetLong(r, "elapsedMs"),
                SavedAtUtc: r.TryGetProperty("savedAtUtc", out var sv2) ? sv2.GetString() : null);
        }
        catch
        {
            return null;
        }
    }

    private static int GetInt(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static long GetLong(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    /// <summary>
    /// **启动断言**（Task 2 Step 3）：在决定「能不能用缓存」之前先把三式核一遍。
    ///
    /// 检查顺序按「越致命越靠前」排列，并且每一条都给得出**具体数字**（理由字符串直接进 RD1004 的 Evidence）：
    /// <list type="number">
    /// <item>defs.jsonl / index-meta.json 是否存在；</item>
    /// <item>schemaVersion 是否匹配（格式升级后旧缓存必须被拒绝，不能静默当新格式读）；</item>
    /// <item>三式自洽（<see cref="Meta.SelfConsistent"/>）；</item>
    /// <item>指纹表的**实际条数**是否等于 meta 里记的条数（防指纹文件被截断）；</item>
    /// <item>缓存的文件集是否等于本次磁盘枚举到的文件数。</item>
    /// </list>
    /// </summary>
    public CacheCheck ValidateCache(int filesEnumeratedNow)
    {
        if (!File.Exists(DefsPath)) return new CacheCheck(false, "defs.jsonl 不存在", null, 0);

        var meta = LoadMeta();
        if (meta is null) return new CacheCheck(false, "index-meta.json 缺失或损坏", null, 0);

        if (meta.SchemaVersion != CurrentSchemaVersion)
            return new CacheCheck(false,
                $"索引 schemaVersion 不匹配（缓存 {meta.SchemaVersion} / 期望 {CurrentSchemaVersion}）", meta, 0);

        var fpCount = LoadFingerprints().Count;

        if (meta.FilesEnumerated != meta.FingerprintsRecorded)
            return new CacheCheck(false,
                $"files != fingerprints（{meta.FilesEnumerated} != {meta.FingerprintsRecorded}；"
                + $"枚举后消失 {meta.FilesVanishedBeforeFingerprint} 个）", meta, fpCount);

        var sum = meta.FilesParsedOk + meta.FilesSkippedUnchanged + meta.FilesParseFailed;
        if (meta.FilesEnumerated != sum)
            return new CacheCheck(false,
                $"files != parsedOk + skippedUnchanged + parseFailed（{meta.FilesEnumerated} != {sum}）", meta, fpCount);

        if (fpCount != meta.FingerprintsRecorded)
            return new CacheCheck(false,
                $"指纹表实际条数 != meta 记录条数（{fpCount} != {meta.FingerprintsRecorded}）", meta, fpCount);

        if (meta.FilesEnumerated != filesEnumeratedNow)
            return new CacheCheck(false,
                $"缓存文件集与磁盘枚举不一致（{meta.FilesEnumerated} != {filesEnumeratedNow}）", meta, fpCount);

        return new CacheCheck(true, "自洽（" + meta.SelfConsistencyText + "）", meta, fpCount);
    }
}
