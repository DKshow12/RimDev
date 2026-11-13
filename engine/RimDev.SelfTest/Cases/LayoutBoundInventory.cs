namespace RimDev.SelfTest.Cases;

/// <summary>
/// **绑定 1.6 语料/1.6 托管程序集**的用例清单（第 3 期 Task 9）。
///
/// 分类判据（写死在这里，避免以后有人「顺手」把新用例塞进来）：
/// 该用例的断言落在**只在本机 1.6 安装上成立**的具体值或对象上，则它属于此表 ——
///   · 1.6 托管程序集锚点：`Managed dll = 108`、`GetTypes() = 16157`、字段/引用空间计数、
///     以及需要 1.6 Unity 程序集链（`UnityEngine.CoreModule.dll`）的「引擎对照台」；
///   · 1.6 语料锚点：`activeMods = 17`、官方 6 包（Core + 5 DLC 都在 activeMods 里）、
///     `defs`/诊断计数（RD2004=1、RD2026=14、官方 error=1、RD3001=0…）、ThreatFleet 在索引里；
///   · 1.6 路径锚点：`Player.log` 在 `LocalLow\` 根（1.7 搬到了 `LocalLow\Logs\`）。
///
/// 当前布局**不是** Classic16 时，这些用例**显式 SKIP 并逐条记名**（计入 `skipped-by-layout`）；
/// **不放宽、不删除、不改总数 266**。装上 1.6（或 `--game-root` 指向 1.6）后重跑，它们必须原样全部回绿。
///
/// ★ 两道防静默的钉子：
///   ① 表里的每一条都必须**真的命中一个已注册用例**；没命中就在摘要里记一条 FAIL
///      （改了用例名却忘了改这张表 ⇒ 当场变红，而不是悄悄不跳过）。
///   ② 被跳过的用例在摘要里逐条打印名字（build-all.ps1 也会把它们逐条列出来）。
/// </summary>
public static class LayoutBoundInventory
{
    private static readonly (string Group, string Name, string Why)[] Bound =
    {
        // ── env：1.6 的 Managed 内容 / 版本串 / 日志路径 ────────────────────────────
        ("env", "Managed 目录 dll 数 = 108",
            "1.6 的 Managed 有 108 个 dll；1.7.x 线的 ManagedDir 是游戏根，dll 数完全不同"),
        ("env", "版本口径取 ModsConfig 而非 Version.txt",
            "断言 Version.txt 含 \"1.6.\"，而 1.7.x 上是 \"1.7.\""),
        ("env", "配置与日志路径存在",
            "断言 Player.log 在 LocalLow 根；1.7 把它搬到了 LocalLow\\Logs\\"),

        // ── metadata：1.6 程序集的类型总数 ───────────────────────────────────────
        ("metadata", "装载 Assembly-CSharp 并取得 16157 个类型",
            "GetTypes() 计数是 1.6 程序集的锚点（1.7.x 是另一个数）"),

        // ── catalog / defscan：1.6 时代的启用列表（activeMods=17）──────────────────
        ("catalog", "★ 目录表：真 mod 数在基线附近、0 个重复、候选目录数在基线附近",
            "断言 activeMods = 17（1.6 时代的启用列表）"),
        ("catalog", "启用 mod 全部定位到目录，且互相不重叠",
            "同样以 activeMods = 17 为前提"),
        ("defscan", "★ 启用集合的文件数与实测锚点一致（mod 列表金丝雀）",
            "断言「非 ThreatFleet 的启用 XML = 1626」（1.6 时代启用集 + 正在开发的 ThreatFleet）"),

        // ── check：1.6 语料上的诊断计数与 ThreatFleet 索引 ─────────────────────────
        ("check", "★ 防假阳①：active 全集上 RD2004 只有 1 条（同 DefType 真重名），绝不报 1202 条",
            "断言 active 全集上 RD2004 恰好 1 组（1.6 语料的具体值）"),
        ("check", "★ 防假阳②：官方内容（Core + 5 DLC）RD2005 与 RD2006 都必须为 0",
            "以「官方 6 包」为前提：1.6 的 ModsConfig 把 Core+5 DLC 都列进 activeMods，1.7 只列 Core"),
        ("check", "★ Task 2 Step 8：官方 6 个包上跑【全部】默认规则 → error 必须 0（假阳比漏报更贵）",
            "同上：需要官方 6 个包参与扫描"),
        ("check", "★ Def 名命中索引（TF_ThreatHive）：报存在且是 Info",
            "需要 ThreatFleet 在启用集里（当前 ModsConfig 未启用它）"),
        ("check", "★ Def 名未命中（TF_ThreatHiveX）：Error + 候选必须含真名",
            "候选里必须有 TF_ThreatHive ⇒ 同样依赖 ThreatFleet 启用"),

        // ── loadfolders：引擎版本口径（三段串）─────────────────────────────────────
        ("loadfolders", "★ Q1 引擎版本口径：反射取 RimWorld.VersionControl（三段串！）",
            "断言 CurrentVersionString = \"1.6.4871\"（引擎版本，非布局）"),

        // ── phase3-task2b / task3 / task4 / task4b / task5a / task5c / task6 ──────
        ("phase3-task2b", "★ 官方 6 包（Core + 5 DLC）：error 0（真实语料，走 CheckRunner.Default 全部规则）",
            "官方 6 包 + 「恰好 1 条 error」的 1.6 语料锚点"),
        ("phase3-task2b", "★ ParentChainCheck 提速：真实语料（active 全集名字池）上逐条等价",
            "以 1.6 启用集的 Name 池规模（>10000）为前提"),
        ("phase3-task3", "★ Step 1 加载序：Core=0 → knownExpansions(5 DLC) → activeMods（真环境）",
            "断言加载序条目数 == activeMods 条数 == 17（1.6 的 ModsConfig 形状）"),
        ("phase3-task3", "★ Step 1 knownExpansions 只取该段（不能扫全文的 <li>，那会混进 activeMods 的 17 条）",
            "同样断言 activeMods = 17"),
        ("phase3-task3", "★ active 全集（17 个 mod）：RD2026=0 / RD2025=1 / 没有任何 error",
            "断言 active 全集 RD2025 = 1（1.6 语料的具体值）"),
        ("phase3-task3", "★★ 官方 6 包上跑【全部】默认规则 → error 仍必须 0（回归门：新规则不许破坏它）",
            "官方 6 包 + 官方 error 恰好 1 条的 1.6 语料锚点"),
        ("phase3-task4", "★★ 零假阳门：官方 6 包的 14 个补丁文件上 RD2009 / RD2014 必须为 0",
            "以「官方 14 个补丁文件」为前提（1.6 官方内容形状）"),
        ("phase3-task4", "★★ Step 1 阶段计数（启用集实测）：assets/unified/patchOps/registered 全部由代码算出",
            "前提是「启用集里有补丁文件」（1.6 启用集才成立）"),
        ("phase3-task4b", "★★ (g) 真语料：全库 CE 的 RD2026 == 0，其余 6 个 mod 合计 14 条（逐 mod 表）",
            "断言「其余 6 个 mod 合计 14 条」——1.6 全库语料的具体值"),
        ("phase3-task5a", "★★ (j) 真语料：官方 6 包与启用集上 Unresolved == 0、环 0、RD2005 == 0",
            "断言启用集 Name 空间命中 1907 条（1.6 语料规模）+ 官方 6 包"),
        ("phase3-task5c", "★★ 结构对账（active 真语料）：逐文件 probe 行号序列 == DefScanner 行号序列，不一致必须为 0",
            "前提是「真实语料里确实存在同一行多个 def 的文件」（1.6 启用集里的 ThreatFleet 才有）"),
        ("phase3-task6", "★ parity 门禁不误报：active 真语料上实跑 ReferenceCheck 不得产出任何归属键不一致",
            "引用点数量级前置断言以 1.6 启用集规模为前提"),
        ("phase3-task6", "★ 引用字段空间逐项复算（255 / 65 / 1188 / 1040 / 148 / 122 / 1748 / 927 / 739 / 82）",
            "全部是 1.6 Assembly-CSharp 的引用字段空间计数"),
        ("phase3-task6", "★ 容器形态分布 + 防「List 被静默跳过」的正面钉子",
            "形态计数（scalar 1252 等）是 1.6 程序集的具体值"),
        ("phase3-task6", "★ 全程序集槽数 8685（≠任务书 8733）：差值 48 全部是 Enumerator 闭包字段",
            "全程序集引用槽计数是 1.6 程序集的具体值"),
        ("phase3-task6", "★ 继承 defName ≡ InheritanceLayer.Merge：真语料逐声明对账（active 语料，抽样 ≥200）",
            "有效 def 名数量级以 1.6 启用集为前提"),
        ("phase3-task6", "★★ Step 4 官方门禁：CreepJoinerBaseDef 类引用 0 条（修前 116 条）+ 每条 RD3001 都能归因",
            "断言官方内容上 RD3001 = 0 —— 1.7 官方内容不同（含一批新的悬空引用），属 Task 10 复基线范围"),
        ("phase3-task6", "★★ 6b/Step 5 门禁：官方 6 包 error 恰好 1 条，且是那条【已定性的 Anomaly 真缺陷】",
            "断言官方 6 包 error 恰好 1 条（1.6 语料的已知真缺陷 Ambient_MetalHell）"),
    };

    private static readonly HashSet<string> Matched = new(StringComparer.Ordinal);

    private static string Key(string group, string name) => group + "\u0000" + name;

    public static bool TryGet(string group, string name, out string why)
    {
        foreach (var b in Bound)
        {
            if (b.Group == group && b.Name == name)
            {
                why = b.Why;
                return true;
            }
        }
        why = "";
        return false;
    }

    public static void MarkMatched(string group, string name) => Matched.Add(Key(group, name));

    /// <summary>表里存在但**没有命中任何已注册用例**的条目（改名/删用例忘了同步 ⇒ 必须变红）。</summary>
    public static IReadOnlyList<string> Unmatched()
    {
        var res = new List<string>();
        foreach (var b in Bound)
            if (!Matched.Contains(Key(b.Group, b.Name)))
                res.Add($"{b.Group}/{b.Name}");
        return res;
    }

    public static int Count => Bound.Length;
}
