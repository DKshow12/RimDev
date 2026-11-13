using System.Xml;

namespace RimDev.Core.Defs;

/// <summary>
/// <c>InheritanceLayer</c> 的**合并语义**（第 3 期 Task 5b）：把引擎
/// <c>Verse.XmlInheritance.ResolveXmlNodeFor</c> / <c>RecursiveNodeCopyOverwriteElements</c>
/// 的行为**逐行复刻**成 <see cref="Merge"/>。
///
/// ── 为什么要复刻而不「写漂亮」────────────────────────────────────────────────────────
/// 引擎那段代码有几处**看起来像 bug 但必须照抄**的地方（项目纪律：Task 4 的
/// `PatchOperationInsert` 反序 / `SetName` 丢属性同源）：
/// <list type="number">
/// <item><b>「移除全部子节点」其实是「只移除第一个」</b>：引擎写的是
///   <c>foreach (XmlNode c in current.ChildNodes) current.RemoveChild(c);</c> ——
///   在枚举过程中修改集合，实测（本机 .NET 8 + 引擎程序集）
///   <c>&lt;t&gt;&lt;a/&gt;&lt;b/&gt;&lt;c/&gt;&lt;/t&gt;</c> 与子 <c>&lt;t&gt;XYZ&lt;/t&gt;</c> 的结果是
///   <c>&lt;t&gt;&lt;b/&gt;&lt;c/&gt;XYZ&lt;/t&gt;</c>（**只剩 a 被删掉**）。本文件照抄同一个写法，
///   于是同运行时下结果**逐字节相同**。</item>
/// <item><b>文本分支优先于元素分支并直接返回</b>：子元素只要有一个直接 Text 子节点，
///   就丢掉父的同名块的全部（按上一条的语义）子节点、只放那个文本 —— 子元素里的
///   其它元素会被**静默丢弃**（实测 <c>&lt;t&gt;XYZ&lt;w/&gt;&lt;/t&gt;</c> ⇒ <c>&lt;t&gt;XYZ&lt;/t&gt;</c>）。</item>
/// <item><b>取最后一个 Text 子节点</b>：循环里 <c>textNode</c> 被反复覆盖（不是第一个）。</item>
/// <item><b>属性在两分支里语义相反</b>：普通分支 <c>current.Attributes.RemoveAll()</c> 后只用**子**的属性；
///   <c>Inherit="False"</c> 分支**保留父的属性**再追加子的（<c>Inherit</c> 自身不写入）。</item>
/// </list>
///
/// ── 引擎原文（反编译 <c>Verse.XmlInheritance</c>，Assembly-CSharp mvid
///    `61e4173561894da49d210260257b5097`）────────────────────────────────────────────────
/// <code>
/// private static void ResolveXmlNodeFor(XmlInheritanceNode node) {
///     if (node.parent == null) { node.resolvedXmlNode = node.xmlNode; return; }
///     ...
///     XmlNode xmlNode = node.parent.resolvedXmlNode.CloneNode(deep: true);
///     RecursiveNodeCopyOverwriteElements(node.xmlNode, xmlNode);
///     node.resolvedXmlNode = xmlNode;
/// }
///
/// private static void RecursiveNodeCopyOverwriteElements(XmlNode child, XmlNode current) {
///     XmlAttribute a = child.Attributes["Inherit"];
///     if (a != null &amp;&amp; a.Value.ToLower() == "false") {
///         // (1) 清空 current 的子节点，(2) 把 child 的子节点全部 import 进来，
///         // (3) 把 child 的属性（除 Inherit）**追加**到 current（父属性保留），(4) return
///     }
///     current.Attributes.RemoveAll();                       // ← 普通分支：父属性全清
///     for (i...) current.Attributes.Append(Import(child.Attributes[i]));   // ← 只用子的属性
///     bool flag = false; XmlNode textNode = null;
///     foreach (item in child) {
///         if (item.NodeType == Text) textNode = item;       // ← 取最后一个
///         else if (item.NodeType == Element) flag = true;
///     }
///     if (textNode != null) { /* 清 current 子节点 + AppendChild(textNode) */ ; return; }
///     if (!flag) {
///         bool currentHasElement = ...;
///         if (currentHasElement) return;                    // ← 子没元素、父有 ⇒ 原样保留父的
///         /* 清 current 子节点 */ ; return;
///     }
///     foreach (item in child) {
///         if (item.NodeType != Element) continue;
///         if (IsListElement(item)) { current.AppendChild(Import(item)); continue; }   // ← &lt;li&gt; 无条件追加
///         XmlElement e = current[item.Name];                // ← **第一个**同名直接子元素
///         if (e != null) { RecursiveNodeCopyOverwriteElements(item, e); continue; }
///         current.AppendChild(Import(item));                // ← 子有、父没有 ⇒ 追加
///     }
/// }
///
/// private static bool IsListElement(XmlNode node) {
///     if (node.Name == "li") return true;
///     if (node.ParentNode != null) return allowDuplicateNodesFieldNames.Contains(node.ParentNode.Name);
///     return false;
/// }
/// </code>
///
/// ★ 每一步都有「引擎自己跑出来的观测」背书：见
///   <c>RimDev.SelfTest/Cases/Phase3Task5bCases.cs</c> 的 Step 1 用例（每条断言前都打印原始观测），
///   以及 <c>rimdev-selftest --task5b-probe</c> 的原始输出。
/// </summary>
public sealed partial class InheritanceLayer
{
    /// <summary>
    /// `[XmlInheritanceAllowDuplicateNodes]` 标注的**字段名**集合 —— 引擎 `IsListElement` 的第二条判据
    /// （`allowDuplicateNodesFieldNames.Contains(node.ParentNode.Name)`）。
    ///
    /// ★ 引擎在静态构造里用 `GenTypes.AllTypes` 全程序集扫出来；`RimDev.Core` **不引用**
    /// `Assembly-CSharp`，所以这里落成一张**字面量表**，并**由自测用例用反射全程序集重扫核对**
    /// （`Phase3Task5bCases` 的「②b」用例：数量与成员必须完全一致）。
    /// 核对面 = `DeclaredOnly` 实例字段 + `XmlInheritanceAllowDuplicateNodesAttribute`
    /// （★ 类型名**没有** `Attribute` 后缀，但反射取属性时要用带后缀的 CLR 名）。
    /// 本机实测（RimWorld 1.6.4871）为 **6** 个，全部是 `List&lt;T&gt;` 字段：
    /// `MemeDef.agreeableTraits` / `MemeDef.disagreeableTraits` / `ThoughtDef.nullifyingTraitDegrees` /
    /// `Def.descriptionHyperlinks` / `PawnKindDef.disallowedTraitsWithDegree` / `PawnKindDef.forcedTraits`。
    ///
    /// ★ 它**与 `comps` 无关**：任务书 Step 2 明确禁止拿这张表去特判 `comps` ——
    ///   `&lt;li&gt;` 的追加来自 `IsListElement` 的第一条判据（`node.Name == "li"`），**无条件**。
    /// </summary>
    public static readonly IReadOnlySet<string> AllowDuplicateNodeFieldNames =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "agreeableTraits",
            "disagreeableTraits",
            "nullifyingTraitDegrees",
            "descriptionHyperlinks",
            "disallowedTraitsWithDegree",
            "forcedTraits",
        };

    /// <summary>
    /// 把 <paramref name="child"/> 合并进**已解析的父节点** <paramref name="parentResolved"/>。
    ///
    /// 与引擎完全对应：`current = parentResolved.CloneNode(deep: true)` 后
    /// `RecursiveNodeCopyOverwriteElements(child, current)`，返回 `current`。
    /// 返回值归属 <paramref name="parentResolved"/> 所在的文档，且是**新对象**（不改动入参）。
    /// </summary>
    public static XmlNode Merge(XmlNode parentResolved, XmlNode child)
    {
        var current = parentResolved.CloneNode(deep: true);
        RecursiveMerge(child, current);
        return current;
    }

    /// <summary>
    /// 沿 `ParentName` 链把 <paramref name="node"/> 解析成合并结果 —— 引擎
    /// `Resolve()` + `GetResolvedNodeFor()` 的等价物（**未解析时返回节点自身**，与引擎一致）。
    ///
    /// 引擎的三条退化路径都返回**原始节点**（`GetResolvedNodeFor` 的末尾 `return originalNode;`），
    /// 这里一一对应：
    /// <list type="bullet">
    /// <item>没有 `ParentName` ⇒ 引擎 `resolvedXmlNode = xmlNode` ⇒ 返回原节点；</item>
    /// <item>父名在 Name 表里找不到（`Could not find parent node named ...`）⇒ 返回原节点；</item>
    /// <item>链上成环 / 上游断链（`Cyclic inheritance hierarchy detected`）⇒ 返回原节点。</item>
    /// </list>
    /// ★ 第三条必须**沿链传播**：父名找得到、但祖父名找不到时，引擎里父节点也不解析，
    ///   子节点同样拿不到结果 —— 只判「直接父名是否存在」会把这一类判成可解析。
    /// </summary>
    /// <param name="findByName">`Name` 属性 → 节点（引擎 `nodesByName` 的等价物；先到者胜）。</param>
    public static XmlNode ResolveNode(XmlNode node, Func<string, XmlNode?> findByName)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));
        if (findByName is null) throw new ArgumentNullException(nameof(findByName));

        // 键是【节点对象引用】：`XmlNode` 没有重写 Equals，默认就是引用相等，够用。
        var visiting = new HashSet<XmlNode>();
        return ResolveCore(node, findByName, visiting) ?? node;
    }

    private static XmlNode? ResolveCore(XmlNode node, Func<string, XmlNode?> findByName, HashSet<XmlNode> visiting)
    {
        var parentName = node.Attributes?["ParentName"]?.Value;
        if (string.IsNullOrEmpty(parentName)) return node;      // 无父 ⇒ 引擎原样返回该节点

        if (!visiting.Add(node)) return null;                    // 环 ⇒ 引擎判未解析 ⇒ 调用方回退到原节点
        try
        {
            var parent = findByName(parentName!);
            if (parent is null) return null;                     // 父名不存在
            var parentResolved = ResolveCore(parent, findByName, visiting);
            if (parentResolved is null) return null;             // 上游断链 ⇒ 本条也不解析
            return Merge(parentResolved, node);
        }
        finally
        {
            visiting.Remove(node);
        }
    }

    // ────────────────────────────────────────────────────────────────────────────
    // 引擎 RecursiveNodeCopyOverwriteElements 的逐行复刻
    // ────────────────────────────────────────────────────────────────────────────

    private static void RecursiveMerge(XmlNode child, XmlNode current)
    {
        var inherit = child.Attributes?["Inherit"];

        // ① `Inherit="False"`：丢掉父的**子元素**（属性保留），换上自己的子元素，
        //    再把除 `Inherit` 之外的自己的属性**追加**上去。
        if (inherit is not null && inherit.Value.ToLowerInvariant() == "false")
        {
            RemoveChildren(current, safeWhileLoop: true);
            foreach (XmlNode item in child)
                current.AppendChild(current.OwnerDocument!.ImportNode(item, deep: true));

            if (child.Attributes is not null)
            {
                foreach (XmlAttribute attribute in child.Attributes)
                {
                    if (attribute.Name == "Inherit") continue;
                    var copy = current.OwnerDocument!.CreateAttribute(attribute.Name);
                    copy.Value = attribute.Value;
                    current.Attributes!.Append(copy);
                }
            }
            return;
        }

        // ② 普通分支：**父的属性全部丢弃**，只用子元素的属性（顺序 = 子元素的声明顺序）。
        current.Attributes!.RemoveAll();
        if (child.Attributes is not null)
        {
            for (var i = 0; i < child.Attributes.Count; i++)
                current.Attributes.Append((XmlAttribute)current.OwnerDocument!.ImportNode(child.Attributes[i]!, deep: true));
        }

        // ③ 扫描子的直接子节点：有没有元素？有没有文本（取**最后一个**）？
        var hasElement = false;
        XmlNode? textNode = null;
        foreach (XmlNode item in child)
        {
            if (item.NodeType == XmlNodeType.Text) textNode = item;
            else if (item.NodeType == XmlNodeType.Element) hasElement = true;
        }

        // ④ 有一级文本 ⇒ 清掉父同名块的子节点，只放这段文本，**直接返回**
        //    （子元素里若还有嵌套元素，会被一起丢掉 —— 引擎就是这样）。
        if (textNode is not null)
        {
            RemoveChildren(current, safeWhileLoop: false);
            current.AppendChild(current.OwnerDocument!.ImportNode(textNode, deep: true));
            return;
        }

        // ⑤ 子没有元素子节点：父的同名块里**有**元素 ⇒ 原样保留；也没有 ⇒ 清空（去掉父的裸文本）。
        if (!hasElement)
        {
            var currentHasElement = false;
            foreach (XmlNode cn in current.ChildNodes)
                if (cn.NodeType == XmlNodeType.Element) { currentHasElement = true; break; }
            if (currentHasElement) return;
            RemoveChildren(current, safeWhileLoop: false);
            return;
        }

        // ⑥ 子有元素子节点：逐个 `li` 追加、同名递归、新名追加。
        foreach (XmlNode item in child)
        {
            if (item.NodeType != XmlNodeType.Element) continue;

            if (IsListElement(item))
            {
                current.AppendChild(current.OwnerDocument!.ImportNode(item, deep: true));
                continue;
            }

            var existing = FindFirstChildElement(current, item.Name);
            if (existing is not null)
            {
                RecursiveMerge(item, existing);
                continue;
            }
            current.AppendChild(current.OwnerDocument!.ImportNode(item, deep: true));
        }
    }

    /// <summary>
    /// 「清空非属性子节点」——两种写法**必须分开**，因为它们的结果**不一样**：
    /// <list type="bullet">
    /// <item><paramref name="safeWhileLoop"/>=true：引擎 `Inherit="False"` 分支的
    ///   <c>while (FirstChild) RemoveChild</c> —— **真的删光**；</item>
    /// <item><paramref name="safeWhileLoop"/>=false：引擎另外两处的
    ///   <c>foreach (XmlNode c in current.ChildNodes) RemoveChild(c)</c> ——
    ///   在枚举中改集合，实测**只删掉第一个**（3 个子节点删掉 1 个，剩 2 个）。照抄，不修漂亮。</item>
    /// </list>
    /// </summary>
    private static void RemoveChildren(XmlNode current, bool safeWhileLoop)
    {
        if (safeWhileLoop)
        {
            var n = current.FirstChild;
            while (n is not null)
            {
                var next = n.NextSibling;
                current.RemoveChild(n);
                n = next;
            }
            return;
        }

        // ★ 故意与引擎同写法（含「枚举中修改」这一条）：同运行时下结果逐字节相同。
        foreach (XmlNode childNode in current.ChildNodes)
        {
            if (childNode.NodeType != XmlNodeType.Attribute) current.RemoveChild(childNode);
        }
    }

    /// <summary>
    /// 引擎 `IsListElement`：`&lt;li&gt;` **无条件**是列表元素；此外，元素名落在
    /// <see cref="AllowDuplicateNodeFieldNames"/> 里的父块下也按列表处理（**裸元素也追加**）。
    /// </summary>
    private static bool IsListElement(XmlNode node)
    {
        if (node.Name == "li") return true;
        var parent = node.ParentNode;
        return parent is not null && AllowDuplicateNodeFieldNames.Contains(parent.Name);
    }

    /// <summary>
    /// 引擎 `current[item.Name]`：**第一个**同名直接子元素（没有则 null）。
    /// `XmlNode` 没有索引器，这里手写等价物（只按元素名，不走 XPath）。
    /// </summary>
    private static XmlNode? FindFirstChildElement(XmlNode current, string name)
    {
        foreach (XmlNode cn in current.ChildNodes)
            if (cn.NodeType == XmlNodeType.Element && string.Equals(cn.Name, name, StringComparison.Ordinal))
                return cn;
        return null;
    }
}
