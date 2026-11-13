namespace RimDev.Core.Diagnostics;

/// <summary>
/// 错误码集中定义。完整表见 Docs\01-设计文档.md §7 与 Docs\02-功能增补-基于实战经验.md §5。
/// v1 只用到元数据与名字查证相关的码；其余在后续计划中逐步启用。
/// </summary>
public static class Codes
{
    // RD1xxx 环境与索引
    public const string GameRootNotFound = "RD1001";
    public const string ManagedDirMissing = "RD1002";
    public const string ModsConfigMissing = "RD1003";

    // RD2xxx Def 结构与加载（v1 未启用）
    // RD3xxx 引用完整性（v1 未启用）

    // RD4xxx 字段与类型（元数据引擎）
    public const string FieldNotFound = "RD4001";
    public const string FieldTypeMismatch = "RD4002";
    public const string EnumValueInvalid = "RD4003";
    public const string TypeNotFound = "RD4004";
    public const string CaseMismatch = "RD4005";
    public const string MetadataUnavailable = "RD4006";

    // RD5xxx 程序集
    public const string AssemblyLoadFailed = "RD5001";

    // RD9xxx 反模式规则包（v1 只实现可离线判定的成员相关项）
    public const string CustomXmlLoaderInfo = "RD4008";

    /// <summary>
    /// 字段标了 <c>[Unsaved]</c> 且 <c>allowLoading == false</c>，而 XML 写了它 —— 引擎会
    /// <c>Log.Error("XML error: … corresponds to a field in type … which has an Unsaved attribute. Context: …")</c>
    /// 并返回 null（第 3 期 Task 2c 落地；<c>Docs\01 §7</c> 的 RD4009）。
    ///
    /// ★★ 必须记住的可达性事实（反编译 + IL 双证据，**推翻了任务书对该分支的表述**）：
    ///   <c>Verse.XmlToObjectUtils.DoFieldSearch</c> 里 <c>DirectGetFieldByName</c> 命中时**立即 return**，
    ///   <c>[Unsaved]</c> 检查在其**之后**、只在**别名分支命中**时才可达
    ///   （IL: 0x0010 brfalse → 0x0019 ret；0x00BC 才是 `TryGetAttribute&lt;UnsavedAttribute&gt;()`）。
    ///   而原版程序集 41 个别名字段里**没有一个是 [Unsaved]**（实测 0/41）⇒ 在原版类型上这条错误
    ///   **永远发不出来**。若照「任何 [Unsaved(false)] 字段被写到就报」实现，会造出成千上万条引擎不会报的假阳。
    ///   全库实测 RD4009 = 0，原因就是这个可达性，不是漏实现。
    /// </summary>
    public const string UnsavedFieldBlocked = "RD4009";

    /// <summary>
    /// 元素名无对应字段，但该元素带 <c>IgnoreIfNoMatchingField="True"</c>（InvariantCultureIgnoreCase），
    /// 或所属类型带 <c>IgnoreSavedElementAttribute</c> 且 <c>elementToIgnore</c> 命中
    /// （OrdinalIgnoreCase）—— 引擎**故意不打**那条 <c>doesn't correspond to any field</c>。
    ///
    /// 所以这里报 **info** 并单列计数（RD4001 会算 error，两者的报告语义不同），
    /// evidence 的 <c>suppressedBy</c> 区分 <c>elementAttribute</c> / <c>typeAttribute</c>。
    /// </summary>
    public const string MissingFieldSuppressed = "RD4010";

    // ===== 第 2 期 =====
    // RD2xxx Def 结构与加载
    public const string XmlParseFailed = "RD2001";
    public const string CommentContainsDoubleDash = "RD2002";
    public const string DefNameMissing = "RD2003";
    public const string DefNameDuplicate = "RD2004";
    public const string ParentNotFound = "RD2005";
    public const string InheritanceCycle = "RD2006";
    /// <summary>
    /// <c>loadAfter</c> / <c>loadBefore</c> 与 <c>activeMods</c> 的实际顺序矛盾（第 3 期 Task 3）。
    ///
    /// ★ 码号预检（2026-10-14，任务书 §2.4 特别点名）：本常量在本文件里原本**不存在**，
    ///   而任务书说「RD2011 早已被分配为 loadAfter/loadBefore 与启用顺序矛盾（info）」。
    ///   两处证据合起来才是完整口径：`Docs\01 §7` 的 RD2xxx 码表把它列为 info，
    ///   但代码里一直没有发射点、也没有常量。本次按码表补上常量与发射点，**没有新建任何码号**。
    ///   同时任务书提到的 RD2027 确实不存在（码表与本文件都没有），不予创建。
    ///
    /// 级别恒为 <b>Info</b>：引擎会为 <c>loadAfter</c> 做排序，声明与实际顺序不一致多数情形下无害；
    /// 报成 error 会让使用者直接关掉整份报告（Task 3 Step 7 专门要求断言「绝不是 error」）。
    /// </summary>
    public const string LoadOrderViolation = "RD2011";
    public const string RootNotDefs = "RD2013";
    public const string UnknownDefType = "RD2017";


    // ===== 第 3 期 Task 1：loadFolders 规则族（引擎 Verse.ModLoadFolders.GetIssueList 的 issue 表）=====
    // ★ 码号预检（2026-10-14）：这 10 个常量在本文件里命中数原本为 0（RD2012 只在 Docs\01 §7 的码表里，代码里没发射点）。
    //   级别照 Docs\01 §7 的码表：RD2012 info / RD2018 warning / RD2019 warning / RD2020 warning /
    //   RD2021 info / RD2022 warning / RD2023 info / RD2024 error；RD2025/RD2026 属 Task 3（遮蔽），此处一并占位。
    /// <summary>loadFolders 声明了不存在的目录（Docs\01 §7：info，「通常无害」）。</summary>
    public const string LoadFolderDoesntExist = "RD2012";
    /// <summary>版本块里一个 &lt;li&gt; 都没有（引擎 ModLoadFolderListEmpty）。</summary>
    public const string LoadFolderListEmpty = "RD2018";
    /// <summary>同一个版本块内 &lt;li&gt; 重复（引擎 ModLoadFolderRepeatingFolder）。</summary>
    public const string LoadFolderRepeatingFolder = "RD2019";
    /// <summary>版本键格式非法（引擎 IsWellFormedVersionString 要求恰好两段；引擎 ModLoadFolderMalformedVersion）。</summary>
    public const string LoadFolderMalformedVersion = "RD2020";
    /// <summary>使用了已废弃的 default 版本键（引擎 ModLoadFolderDefaultDeprecated）。</summary>
    public const string LoadFolderDefaultDeprecated = "RD2021";
    /// <summary>版本键未升序（引擎 ModLoadFolderOutOfOrder）。</summary>
    public const string LoadFolderOutOfOrder = "RD2022";
    /// <summary>版本键不在该 mod 自己的 supportedVersions 里（引擎 ModLoadFolderDefinesUnsupportedGameVersion）。</summary>
    public const string LoadFolderUnsupportedVersion = "RD2023";
    /// <summary>解析出的加载文件夹列表为空 —— 该 mod 的 def 完全不会被加载。</summary>
    public const string LoadFolderResolvedEmpty = "RD2024";
    /// <summary>同名 (DefType, defName) 被更高优先级来源遮蔽（第 3 期 Task 3）。</summary>
    public const string DefShadowed = "RD2025";
    /// <summary>同一个 mod 内同一 (DefType, defName) 声明多次（第 3 期 Task 3）。</summary>
    public const string DefDuplicateWithinMod = "RD2026";
    public const string DuplicatePackageId = "RD1005";

    // ★ RD3001 的语义说明（预检修正 ②）：`01` §7 把 RD3001 定义为「引用悬空：目标 defName 不存在」，
    //   那是第 3 期引用解析的用法（需要元数据知道 defaultProjectile 是 ThingDef 引用，见 `01` T12/T15）。
    //   本期 `name check --kind def` 报的是【同一个条件】（某个 defName 不存在），差别只是没有引用点：
    //   诊断里不会有引用处的 FileAbs/Line。第 3 期实现引用解析时会复用同一个码，届时靠 FileAbs/Line 区分两用。
    public const string DefNameNotFound = "RD3001";

    // 索引缓存 schema 不匹配（`01` §7 的 RD1004）：报它并【当作没有索引】，不要静默用旧缓存作答。
    public const string CacheSchemaMismatch = "RD1004";

    // 目标 mod 无法确定（`01` §4 退出码 5 的对应码；`01` §7 的 RD1xxx 表已补此行）
    public const string TargetModNotFound = "RD1009";

    // ===== 第 3 期 Task 4：PatchOperation 家族静态求值 =====
    //
    // ★ 码号预检（2026-10-14，Task 4 §4 要求自己走一遍，不许信任务书的结论）：
    //   grep 本文件 → RD2007–RD2010、RD2014–RD2016 **确实都没有常量**（命中 0）；
    //   grep `Docs\01` §7 的 RD2xxx 码表 → **RD2009 与 RD2014 两行都已存在**，级别分别是
    //   `warning` / `info`。⇒ 本 Task **只补常量与发射点，没有新建任何码号**，级别照码表。
    //   （任务书点名的 RD2010 本 Task 用不到，不补；避免「建了常量但没有发射点」的假占位。）

    /// <summary>
    /// 补丁操作在合并后的 DOM 上求值后**什么都没改到**（`01` §7：warning，离线可判）。
    ///
    /// 引擎侧的事实（反编译 <c>Verse.PatchOperation</c>）：`Apply()` 的返回值只被 <c>neverSucceeded</c>
    /// 记录，真正的报错发生在最后一步 <c>ClearCachedPatches() → patch.Complete(modIdentifier)</c>：
    /// <code>
    /// if (neverSucceeded) Log.Error($"[{modIdentifier}] Patch operation {this} failed\nfile: {sourceFile}");
    /// </code>
    /// 也就是说「补丁打空了」在引擎里是**静默到启动末尾才报**的一条 Error，而且只有一句话、
    /// 不带 XPath 求值过程。这里把它提前、按 <c>文件:行</c> 定位。
    ///
    /// ★ 口径（防假阳的关键）：只对**会修改文档**的操作报（Add/Insert/Replace/Remove/SetName/
    ///   AttributeAdd/AttributeSet/AttributeRemove/AddModExtension）。<c>Test</c>/<c>Conditional</c>/
    ///   <c>FindMod</c>/<c>Sequence</c> 是控制流 —— 它们「返回 false」是设计意图（例如 Conditional 的
    ///   xpath 未命中就是要走 nomatch），对它们报 RD2009 会在官方内容上立刻造出假阳。
    ///   实测（见验收记录）：官方 6 包（14 个补丁文件）与启用集上 RD2009 = 0。
    /// </summary>
    public const string PatchXpathNoMatch = "RD2009";

    /// <summary>
    /// 补丁操作带自定义 <c>Class=</c>（mod 自带补丁类）—— 需要执行 mod 代码，本工具**不执行**，
    /// 跳过该操作（`01` §5.1 与 §7：info，「需真机验证」）。
    ///
    /// 引擎事实：<c>ModContentPack.LoadPatches()</c> 用
    /// <c>DirectXmlToObject.ObjectFromXml&lt;PatchOperation&gt;(childNode, doPostLoad: false)</c> 实例化，
    /// 类型由 <c>Class=</c> 属性经 <c>GenTypes.GetTypeInAnyAssembly</c> 解析 —— 因为补丁加载发生在
    /// `CreateModClasses()` 之后、mod 程序集已加载，所以**真实的 mod 补丁类会被真正执行**。
    /// 我们的静态求值无法执行它 ⇒ 报本码并跳过；这不是 mod 的错，是工具的能力边界。
    ///
    /// ★ 同时覆盖「`&lt;Operation&gt;` 没有 `Class=` 属性」这一形态：引擎此时回落到基类
    ///   <c>Verse.PatchOperation</c>，其 <c>ApplyWorker</c> 立刻
    ///   <c>Log.Error("Attempted to use PatchOperation directly; patch will always fail")</c>。
    ///   evidence 的 <c>reason</c> 区分 <c>customClass</c> / <c>missingClass</c>。
    /// </summary>
    public const string CustomPatchClass = "RD2014";
}

// ===== 第 3 期 Task 2 =====
/// <summary>
/// RD2001（XML 解析失败）的**归因**取值 —— 让报告能区分「你的文件坏了」与「工具读不了」。
///
/// ★ 为什么要单独把它写成常量而不是散在字符串里：`failed` 单独一个数字无法回答
///   「这些失败是我的 mod 有错，还是 RimDev 不认识这种编码」。第 3 期 Task 2 Step 6 要求
///   每一个失败都带 cause，并且全库 13 条**全部**是 <see cref="Encoding"/>（实测，见验收记录）。
/// </summary>
public static class ParseCause
{
    /// <summary>文件自身的字节编码与 XML 声明/默认编码不符（GBK/无 BOM 等）⇒ 是**内容**问题。</summary>
    public const string Encoding = "Encoding";

    /// <summary>良构性错误（标签不配对、非法字符出现在名字里等）⇒ 同样是**内容**问题，但与编码无关。</summary>
    public const string Structure = "Structure";

    /// <summary>
    /// 按 XmlException 的消息归因。判据只认「引擎自己说的那句话」（`Invalid character in the given encoding`）
    /// 与 .NET 的 DecoderFallback 异常名，不做启发式猜测 —— 猜错会把「结构坏了」误报成「编码问题」，
    /// 使用者会去改编码而永远修不好。
    /// </summary>
    public static string Classify(string? parseError)
    {
        if (string.IsNullOrEmpty(parseError)) return Structure;
        if (parseError.Contains("encoding", StringComparison.OrdinalIgnoreCase)) return Encoding;
        if (parseError.Contains("DecoderFallback", StringComparison.OrdinalIgnoreCase)) return Encoding;
        if (parseError.Contains("byte order mark", StringComparison.OrdinalIgnoreCase)) return Encoding;
        return Structure;
    }
}
