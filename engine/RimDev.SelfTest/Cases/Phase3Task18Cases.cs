using RimDev.Core.Defs;

namespace RimDev.SelfTest.Cases;

/// <summary>
/// ★★ 第 3 期 Task 18：**<c>&lt;success&gt;</c> 的四种语义**（引擎源码逐行核对）。
///
/// 引擎原文（`Verse.PatchOperation.Apply`，反编译源 `Verse/PatchOperation.cs:26-48`）：
/// <code>
/// bool flag = ApplyWorker(xml);
/// if (success == Success.Always)      flag = true;      // ← 无条件覆盖
/// else if (success == Success.Never)  flag = false;
/// else if (success == Success.Invert) flag = !flag;
/// if (flag) neverSucceeded = false;                     // 供 Complete() 报「从未成功」
/// return flag;
/// </code>
///
/// ★ 关键：<c>Always</c> / <c>Never</c> 是**无条件覆盖**，不看 <c>ApplyWorker</c> 的结果。
///   本工具在 Task 12 引入三态后，一度让 <c>Unknown</c>（嵌套里有自定义 Class）覆盖了
///   <c>Always</c> 的判定 —— 那是**语义错误**：引擎里 <c>Always</c> 就是 true，没有"也许"。
///
/// ★ 另一条来自实战的坑（知识库原文）：
///   「<c>Never</c> 不是『什么都不做』，而是『执行了但标记失败』，后续 Sequence 会因此中断」
///   —— 所以 <c>Never</c> 必须真的让父 Sequence 停，而不是被当成"跳过"。
///
/// 本文件钉四件事：
/// <list type="number">
/// <item><c>Always</c> 无条件成功（哪怕 body 是失败/未知）</item>
/// <item><c>Never</c> 无条件失败（哪怕 body 成功），且**真的中断父 Sequence**</item>
/// <item><c>Invert</c> 取反</item>
/// <item><c>Normal</c>/缺省 = 按实际结果</item>
/// </list>
/// </summary>
public static class Phase3Task18Cases
{
    private const string DefsXml =
        "<Defs><ThingDef><defName>ZZ_S18</defName><statBases/></ThingDef></Defs>";

    private static PatchRunResult RunPatch(string patchXml)
    {
        var dir = Path.Combine(Path.GetTempPath(), "rimdev-t18-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var defFile = Path.Combine(dir, "Defs.xml");
            File.WriteAllText(defFile, DefsXml);
            var patchFile = Path.Combine(dir, "P.xml");
            File.WriteAllText(patchFile, "<Patch>" + patchXml + "</Patch>");
            return PatchLayer.Apply(
                new[] { (defFile, "m") },
                new[] { (patchFile, "m") },
                new HashSet<string>(StringComparer.Ordinal));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>会失败的 Add（xpath 打空）。</summary>
    private const string FailingAdd =
        "<li Class=\"PatchOperationAdd\">" +
        "<xpath>/Defs/ThingDef[defName=\"ZZ_Missing\"]/statBases</xpath>" +
        "<value><X>1</X></value></li>";

    /// <summary>会成功的 Add。</summary>
    private const string OkAdd =
        "<li Class=\"PatchOperationAdd\">" +
        "<xpath>/Defs/ThingDef[defName=\"ZZ_S18\"]/statBases</xpath>" +
        "<value><OKMARK>1</OKMARK></value></li>";

    public static void Run()
    {
        T.Case("task18 ★★ success=Always 必须无条件成功（哪怕 body 失败）", () =>
        {
            // Always 包住一个会失败的 Add：外层结果必须是成功
            var r = RunPatch(
                "<Operation Class=\"PatchOperationSequence\">" +
                "<operations>" +
                "<li Class=\"PatchOperationAdd\">" +
                "<success>Always</success>" +
                "<xpath>/Defs/ThingDef[defName=\"ZZ_Missing\"]/statBases</xpath>" +
                "<value><X>1</X></value></li>" +
                OkAdd +
                "</operations></Operation>");

            // 判据：Always 让第一条"算成功" ⇒ Sequence 继续 ⇒ 第二条执行
            T.NotNull(r.Document.SelectSingleNode("//ThingDef[defName=\"ZZ_S18\"]/statBases/OKMARK"),
                "Always 覆盖了失败 ⇒ 后续兄弟操作必须继续执行");
        }, LayoutNeed.Any);

        T.Case("task18 ★★ success=Never 必须真的中断父 Sequence（不是「跳过」）", () =>
        {
            // Never 包住一个会成功的 Add：结果变成失败 ⇒ Sequence 停
            var r = RunPatch(
                "<Operation Class=\"PatchOperationSequence\">" +
                "<operations>" +
                "<li Class=\"PatchOperationAdd\">" +
                "<success>Never</success>" +
                "<xpath>/Defs/ThingDef[defName=\"ZZ_S18\"]/statBases</xpath>" +
                "<value><NEVERMARK>1</NEVERMARK></value></li>" +
                OkAdd +
                "</operations></Operation>");

            // 知识库的坑：Never ≠ 什么都不做。它执行了（NEVERMARK 应被加上），但标记失败 ⇒ 停。
            T.NotNull(r.Document.SelectSingleNode("//ThingDef[defName=\"ZZ_S18\"]/statBases/NEVERMARK"),
                "★ Never 仍然**执行**了操作（不是「跳过」）—— 知识库原文的坑");
            T.Null(r.Document.SelectSingleNode("//ThingDef[defName=\"ZZ_S18\"]/statBases/OKMARK"),
                "★ 但它标记失败 ⇒ 父 Sequence 必须中断，第二条不该执行");
        }, LayoutNeed.Any);

        T.Case("task18 success=Invert 取反", () =>
        {
            // Invert 包住失败的 Add ⇒ 变成功 ⇒ 后续继续
            var r = RunPatch(
                "<Operation Class=\"PatchOperationSequence\">" +
                "<operations>" +
                "<li Class=\"PatchOperationAdd\">" +
                "<success>Invert</success>" +
                "<xpath>/Defs/ThingDef[defName=\"ZZ_Missing\"]/statBases</xpath>" +
                "<value><X>1</X></value></li>" +
                OkAdd +
                "</operations></Operation>");

            T.NotNull(r.Document.SelectSingleNode("//ThingDef[defName=\"ZZ_S18\"]/statBases/OKMARK"),
                "Invert 把失败变成功 ⇒ 后续继续");
        }, LayoutNeed.Any);

        T.Case("task18 Normal（缺省）按实际结果", () =>
        {
            var r = RunPatch(
                "<Operation Class=\"PatchOperationSequence\">" +
                "<operations>" + FailingAdd + OkAdd + "</operations>" +
                "</Operation>");

            T.Null(r.Document.SelectSingleNode("//ThingDef[defName=\"ZZ_S18\"]/statBases/OKMARK"),
                "缺省 Normal：失败就是失败 ⇒ 中断");
        }, LayoutNeed.Any);

        T.Case("task18 ★★ success=Always 也必须能覆盖 Unknown（三态不许吞掉 Always）", () =>
        {
            // 自定义 Class = 本工具判 Unknown。但外层写了 Always ⇒ 引擎里就是 true。
            // 若实现让 Unknown 覆盖 Always，父 Sequence 会误以为失败而中断 —— 语义错误。
            var r = RunPatch(
                "<Operation Class=\"PatchOperationSequence\">" +
                "<operations>" +
                "<li Class=\"MyMod.CustomPatch\">" +
                "<success>Always</success>" +
                "<xpath>/Defs</xpath></li>" +
                OkAdd +
                "</operations></Operation>");

            T.NotNull(r.Document.SelectSingleNode("//ThingDef[defName=\"ZZ_S18\"]/statBases/OKMARK"),
                "★ Always 是无条件 true（引擎原文 `flag = true`）⇒ 哪怕 body 是未知，后续也必须继续");
        }, LayoutNeed.Any);

        T.Case("task18 ★★ 嵌套：外层 Always 必须压住内层 Unknown", () =>
        {
            // Sequence(Always) 包着 Sequence(自定义类)。内层产生 Unknown，
            // 但外层写了 Always ⇒ 引擎里必然 true ⇒ 后续兄弟必须执行。
            // 若实现让 Unknown 穿出 Always，这里会中断 —— 语义错误。
            var r = RunPatch(
                "<Operation Class=\"PatchOperationSequence\">" +
                "<operations>" +
                "<li Class=\"PatchOperationSequence\">" +
                "<success>Always</success>" +
                "<operations>" +
                "<li Class=\"MyMod.CustomPatch\"><xpath>/Defs</xpath></li>" +
                "</operations></li>" +
                OkAdd +
                "</operations></Operation>");

            T.NotNull(r.Document.SelectSingleNode("//ThingDef[defName=\"ZZ_S18\"]/statBases/OKMARK"),
                "外层 Always 无条件 true ⇒ 内层 Unknown 不许泄漏成中断");
        }, LayoutNeed.Any);

        T.Case("task18 ★★ Insert 的 Prepend 多子元素结果是【倒序】（引擎 for-倒序 的必然结果）", () =>
        {
            // 引擎 Verse/PatchOperationInsert.cs:32-38 逐字：
            //   for (int num = node.ChildNodes.Count - 1; num >= 0; num--)
            //       parentNode.InsertBefore(ImportNode(node.ChildNodes[num]), xmlNode);
            //
            // 执行序列（都插在 xmlNode 的【紧前面】）：
            //   起点      [statBases]
            //   插 B3     [B3][statBases]
            //   插 B2     [B3][B2][statBases]
            //   插 B1     [B3][B2][B1][statBases]
            // ⇒ 最终 B3,B2,B1 —— **倒序**，不是正序。
            //
            // ★ 这条极易被「顺手改成正序」而破坏与真机的一致性。
            var r = RunPatch(
                "<Operation Class=\"PatchOperationInsert\">" +
                "<xpath>/Defs/ThingDef[defName=\"ZZ_S18\"]/statBases</xpath>" +
                "<value><B1/><B2/><B3/></value>" +
                "</Operation>");

            var parent = r.Document.SelectSingleNode("//ThingDef[defName=\"ZZ_S18\"]");
            T.NotNull(parent, "ThingDef 应存在");
            var names = parent!.ChildNodes.Cast<System.Xml.XmlNode>()
                .Where(n => n.NodeType == System.Xml.XmlNodeType.Element)
                .Select(n => n.Name).ToList();
            var idx = names.IndexOf("statBases");
            T.True(idx > 0, "statBases 前面应有插入的兄弟节点");
            var inserted = names.Take(idx).Where(n => n.StartsWith("B")).ToList();
            T.Eq(string.Join(",", inserted), "B3,B2,B1",
                "★ Prepend 多子元素结果是倒序（引擎 for-倒序 + InsertBefore 同一参照点的必然结果）");
        }, LayoutNeed.Any);

        T.Case("task18 ★★ Insert 的 Append 多子元素同样倒序（但挂在【目标之后】）", () =>
        {
            // 引擎 :26-30：foreach (childNode) parent.InsertAfter(Import(childNode), xmlNode);
            // 执行序列（都插在 xmlNode 的【紧后面】）：
            //   起点      [statBases]
            //   插 A1     [statBases][A1]
            //   插 A2     [statBases][A2][A1]
            //   插 A3     [statBases][A3][A2][A1]
            // ⇒ 最终 A3,A2,A1。
            //
            // 注意：Insert 是插到**兄弟位置**，不是塞进 statBases 里面 ——
            // 所以判据必须查 ThingDef 的子节点，不能查 statBases 的子节点。
            var r = RunPatch(
                "<Operation Class=\"PatchOperationInsert\">" +
                "<order>Append</order>" +
                "<xpath>/Defs/ThingDef[defName=\"ZZ_S18\"]/statBases</xpath>" +
                "<value><A1/><A2/><A3/></value>" +
                "</Operation>");

            var parent = r.Document.SelectSingleNode("//ThingDef[defName=\"ZZ_S18\"]");
            T.NotNull(parent, "ThingDef 应存在");
            var names = parent!.ChildNodes.Cast<System.Xml.XmlNode>()
                .Where(n => n.NodeType == System.Xml.XmlNodeType.Element)
                .Select(n => n.Name).ToList();
            var idx = names.IndexOf("statBases");
            T.True(idx >= 0 && idx + 1 < names.Count, "statBases 后面应有插入的兄弟节点");
            var after = names.Skip(idx + 1).Where(n => n.StartsWith("A")).ToList();
            T.Eq(string.Join(",", after), "A3,A2,A1",
                "★ Append 多子元素也是倒序（InsertAfter 同一参照点）；且它挂在 statBases 【之后】");
        }, LayoutNeed.Any);
    }
}
