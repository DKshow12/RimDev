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

    /// <summary>★ Task 23：Def 名查证需要 Def 索引，而本次没给。</summary>
    /// <remarks>与 <see cref="MetadataUnavailable"/>（RD4006）分开：那个指「元数据引擎生不了」，
    /// 本码指「元数据好好的，只是这条查询需要索引」。混用会让用户去修环境而永远修不好。</remarks>
    public const string DefNameIndexRequired = "RD4007";

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

    /// <summary>
    /// ★ Task 24：**About.xml 解析失败**。
    ///
    /// <para>与 <see cref="XmlParseFailed"/> RD2001 平行：那个指 Def XML，本码指 mod 元数据。</para>
    ///
    /// <para>★为什么必须报：About.xml 不良构时，该 mod 在目录表里**根本不存在**
    /// （packageId 拿不到 ⇒ 无法入列），而 `order` 它只会出现在
    /// <c>unresolvedPackageIds</c> 里 —— 那个字段名看不出「你的 About.xml 坏了」。</para>
    ///
    /// <para>实测（2026-11-13）：刚写的 mod 因 description 里一个裸 <c>&lt;</c>
    /// 被静默丢弃，工具对此没有任何提示。</para>
    /// </summary>
    public const string AboutXmlParseFailed = "RD2007";
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

    // ★★ Task 26：About.xml 三个附加特性的消费侧诊断（RD3xxx 族 = 引用/依赖层）。
    //   选 RD3xxx 而不是继续挤 RD2xxx（2020–2027 已满），因为这三条都是
    //   「声明的东西对不上」而不是「XML 写坏了」。

    /// <summary>
    /// ★ Task 26：<c>modDependencies</c> / <c>modDependenciesByVersion</c> 里声明的
    /// **前置 mod 没有启用**。
    /// <para>实测该特性有 107 个 mod 在用；不读它会把 1.7 版的依赖整段漏掉，
    /// 于是「缺前置」变成假阴（真缺了也不报）。</para>
    /// </summary>
    public const string DependencyMissing = "RD3002";

    /// <summary>
    /// ★ Task 26：<c>incompatibleWith</c> 声明的**冲突 mod 同时启用**了。
    /// <para>实测 302 个 mod 用了这个字段（中文社区最常见）。</para>
    /// </summary>
    public const string IncompatibleModActive = "RD3003";

    /// <summary>
    /// ★ Task 26：依赖里写的名字**是该 mod 的 <c>alternativePackageIds</c> 之一**。
    /// <para>能对上就不该报缺前置 —— 否则是假阳。实测只有 2 个 mod 用备选名，
    /// 但错了就会让人去装一个「已经装了」的前置。</para>
    /// </summary>
    public const string DependencyViaAlternativeId = "RD3004";

    /// <summary>
    /// ❹ 第 3 期 Task 5a：节点带 `MayRequire` / `MayRequireAnyOf`，但指定的 mod **未安装** ⇒ 引擎
    ///   `XmlInheritance.TryRegister` 会**跳过该节点**（它不会被建成 def、它的 `Name=` 也不会进父节点表）。
    ///   本规则逐节点报 **info**（不是 modulator 的错，也不是工具的能力边界，而是「这个节点在本机不存在」）。
    ///
    /// ★★ 码表冲突（已由主理人裁决）：`01` §7 里 **`RD2008` 与 `RD2016` 是同一个事件的两个码**。
    ///   因此只新建并使用本码；`RD2016` 保留码号占位但在 `01` §7 那一行注明
    ///   「与 RD2008 同一事件，已合并，不再发射」——**两个码都不得再用于别的语义**。
    ///
    /// 零级语义（引擎侧，反编译**逐行**；`Docs\01` §5.1.5 那段旧说法已被证伪，以本条为准）：
    ///   `XmlInheritance.TryRegister` 里 `MayRequire` 走
    ///   `ModLister.AllModsActiveNoSuffix(value.Split(','))` —— 列表里**每一项都**要
    ///   `GetActiveModWithIdentifier(x.Trim(), ignorePostfix: true) != null`（**与**）；
    ///   `MayRequireAnyOf` 走 `ModLister.AnyFromListActive(...)`（**或**）。两个属性之间也是「与」。
    ///   判定集合 = **已启用（Active）** 的 mod —— `TryAddMod` 会把未启用的 mod 也放进
    ///   两张 packageId 表，但查询时逐条要求 `item.Active`。**不是「已安装」。**
    ///
    /// ★ 方向必须写清（任务书 Step 4 要求的实测确认）：跳过节点会让静态分析**多报** RD2005 ——
    ///   因为一个「本可解析的父类」（其实带着未满足的 MayRequire）会被当成不存在。这**不是**假阳：
    ///   引擎里那个父类确实不存在，我们与引擎对齐。
    /// </summary>
    public const string MayRequireModMissing = "RD2008";

    // ── RD7xxx 日志诊断（★ 第 3 期 Task 15）─────────────────────────
    //
    // ★ 码段裁决（Docs/10 §6.1，主理人已拍板）：
    //   · RD6xxx **已被贴图与素材**占用（RD6001–RD6004、RD6010，见 01 §7 第 931–938 行）
    //   · 日志诊断在文档里**本来就有专门段** RD7xxx（01 §7 第 940–946 行）
    //   · 新增码从 **RD7020** 起（RD7014–RD7030 已 grep 确认在文档与代码中全未出现）
    //
    // 一个码两种含义 = 项目历史上 RD2008/RD2016 重复的翻版，不能再犯。

    /// <summary>官方文档已定义但本工具**尚未发射**的日志码（RD7001–RD7005）—— 留作占位与对齐。</summary>
    /// <remarks>实现走 RD7020+：那一段的语义是「一条规则一个码」，与文档的毛粗分类不完全对应。
    /// 两者并存不冲突：RD7001–RD7005 依旧可用，RD7020+ 是更细的分档。</remarks>
    public const string LogCrossRefUnresolved = "RD7001";

    /// <summary>★ Task 15：日志里「引用了不存在的 Def」（最高价值的一类）。</summary>
    /// <remarks>引擎原文：<c>&lt;owner&gt; &lt;slot&gt; is not defined in any loaded mods.</c>
    /// 与我们的 RD3001（离线预测悬空）形成「离线说会怎样 vs 真机实际怎样」的对账。</remarks>
    public const string LogMissingDef = "RD7020";

    /// <summary>★ Task 15：XML 字段不属于目标类型（引擎 <c>XML error: … doesn't correspond to any field</c>）。</summary>
    public const string LogXmlUnknownField = "RD7021";

    /// <summary>★ Task 15：Def 配置错误（引擎 <c>Config error in …</c>）。</summary>
    public const string LogDefConfigError = "RD7022";

    /// <summary>★ Task 15：跨引用解析失败（引擎 <c>Could not resolve cross-reference</c>）。</summary>
    public const string LogCrossRefMissing = "RD7023";

    /// <summary>★ Task 15：资源（贴图）找不到。</summary>
    public const string LogMissingTexture = "RD7024";

    /// <summary>★ Task 15：程序集 / 类型加载失败（常见于游戏升级后旧 DLL 未适配）。</summary>
    public const string LogAssemblyConflict = "RD7025";

    /// <summary>★ Task 15：空引用异常（单看信息量有限，需结合堆栈）。</summary>
    public const string LogNullReference = "RD7026";

    /// <summary>★ Task 15：翻译数据有错（不致崩，但影响文本显示）。</summary>
    public const string LogTranslationError = "RD7027";

    /// <summary>★ Task 15：packageId 格式不规范（影响依赖识别与加载顺序）。</summary>
    public const string LogPackageIdFormat = "RD7028";

    /// <summary>★ Task 15：补丁作用在过时方法上（Harmony 相关，通常意味着 mod 需要更新）。</summary>
    public const string LogPatchObsolete = "RD7029";

    /// <summary>★ Task 15b：补丁的 xpath 在真机里匹配 0 个节点。</summary>
    /// <remarks>与离线 <see cref="PatchXpathNoMatch"/>（RD2009）**完全对应**：
    /// 一个是我们离线预测「这条补丁会打空」，另一个是引擎真的报了。
    /// 两者对账是本工具从「离线推测」进到「可验证」的关键一步。</remarks>
    public const string LogPatchXpathNoMatch = "RD7031";

    /// <summary>★ Task 15：未归类的错误/异常行（保留原文）。</summary>
    /// <remarks>**不参与「比基线新增」判定** —— 否则游戏版本一变，
    /// 通用报错全被算成新增缺陷，门禁变成噪音源。</remarks>
    public const string LogUncategorized = "RD7030";
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
