# RimDev

> **一个用于环世界（RimWorld）Mod 开发的静态分析工具链。**
> 不启动游戏，就能查出你的 mod 会不会加载失败、补丁为什么打空、引用了不存在的 Def。

> ### ⚠️ 关于这个项目的编写方式
>
> **本项目代码由 AI 编程生成。**
> 项目中的全部 C# 实现（27,391 行 / 104 个文件）、测试用例（373 个）、
> 文档与知识库，都是在 AI 辅助下逐轮迭代出来的。
>
> 人类负责的部分是：**定方向、定判据、做验收**。
> AI 负责的部分是：**写代码、跑验证、记笔记**。
>
> 这不是一句免责声明，而是这个项目的方法论本身 —— 见
> [「为什么说它是 AI 写的，而且这很重要」](#为什么说它是-ai-写的而且这很重要)。

---

## 它解决什么问题

环世界 mod 开发有个很痛的断层：**写错了不会立刻报错，要开游戏加载完才知道**。

而 1.7 之后更糟 —— 一个 mod 列表 600+ 个 mod，加载一次好几分钟。
改一行 XML，等五分钟，发现补丁打空了，日志里只有一句：

```
[Some Mod] Patch operation Verse.PatchOperationAdd(Defs/ThingDef[defName="X"]/statBases) failed
```

你甚至不知道该去哪个文件的哪一行。

**RimDev 让你在加载游戏之前就知道答案。**

```bash
rimdev check --scope active --rules RD2009
```

---

## 核心能力

### 1. 补丁层求值 —— 这是它最独特的地方

绝大多数环世界静态检查工具只做「XML 良构 + Def 重名 + 引用了不存在的 def」。
但它们都**不求值补丁**。

RimDev 会真的把 `Patches/` 里的 XPath 在一份完整的 XmlDocument 上跑一遍，
告诉你哪条补丁会打空、为什么打空：

```
RD2009 补丁打空：PatchOperationAdd 在 /Defs/ThingDef[defName="RK_PlateHelmA"]/statBases 命中 0 个节点
       文件: 3301484845/Patches/NewRatkinPlus/ThingDefs_Races/Race_Rakinlike.xml:14
```

这条诊断对应真机日志里的：

```
[Verse] [FileSource] [Mod: Ratkin CE Patch Collection (Lemontea.RatkinCEPatchCollection)]
Verse.PatchOperationAdd(xpath="Defs/ThingDef[defName="RK_PlateHelmA"]/statBases"): Failed to find a node
```

**离线预测与真机实证是同一件事的两个视角。** 实测同一台机器、同一套 629 个 mod：
离线报 52 条，真机报 59 条，主因完全吻合（差额来自引擎在两层各报一次）。

### 2. 真机日志诊断

```bash
rimdev log
```

把 `Player.log` 归类成可行动的诊断，并尽量归因到具体 mod。
实测一份 2.3 MB / 24,712 行的日志：**104 ms** 出结果。

能认出补丁失败的 6 种真机形态（这些是跑真实日志一条条扒出来的，不是猜的）：

| 形态 | 出现阶段 |
|---|---|
| `Failed to find a node with the given xpath` | ApplyPatches |
| `Patch operation <Op>(<xpath>) failed` | ClearCachedPatches |
| `Patch operation <Op> failed`（**无括号**） | 同上，操作类无 xpath 属性 |
| `PatchOperationSequence(count=N, lastFailedOperation=…)` | 引擎 ToString() 内部状态 |
| `PatchOperationSequence: Error in the operation at position=N` | 嵌套 Sequence |
| `Tried to set a value … to a field which does not exist` | 字段名错 |

> 未归类从最初 **117 条降到 4 条**，剩下 4 条全是环境噪音 / mod 自身警告 ——
> 那些**不该**被归类为缺陷。

### 3. 完整的 Def 继承树

实现了引擎 `XmlInheritance` 的合并语义（逐行对照反编译源）：

- `ParentName` 链解析（多级）
- `<li>` 列表语义 vs 同名标量字段合并
- `allowDuplicateNodesFieldNames` 白名单
- `Inherit="False"` 开关
- 完全限定元素名合并（`AlienRace.ThingDef_AlienRace` → `Verse.ThingDef`）

> 实测：修复前有 **41,493 / 311,476 个 def（13.32%）** 没有被注册到正确的 DefType 下。

### 4. 程序集元数据查询

```bash
rimdev api probe ThingDef                      # 类型存在吗
rimdev api probe statBases --owner ThingDef    # 字段存在吗
rimdev name check Gun_Revolver --kind def --index
```

读的是**本机安装**的程序集，所以答案与当前游戏版本严格对应。

---

## 快速开始

### 方式一：直接用发布版（推荐）

从 [Releases](../../releases) 下载对应版本的单文件 exe：

| 文件 | 适用 | 大小 |
|---|---|---|
| `rimdev-net10.exe` | **1.7.x 线**（Waylith / net10 运行时） | ~71 MB |
| `rimdev-net8.exe` | **1.6**（Unity / net472 托管程序集） | ~65 MB |

**无需预装 .NET 运行时**，直接跑：

```bash
rimdev-net10.exe env                    # 先体检，确认找对了游戏
rimdev-net10.exe check --scope active   # 全量检查
rimdev-net10.exe log                    # 看日志
```

> 两份产物不是重复打包 —— `net8` 进程载入不了 `net10` 的 `Assembly-CSharp`，反之亦然。
> 用错会明确拒答（exit 4）并告诉你该用哪份，**不会给假答案**。

### 方式二：从源码构建

```powershell
git clone <this-repo>
cd _rimdev
pwsh -File engine\publish.ps1     # 生成 dist\net10.0 与 dist\net8.0
./rimdev.cmd env                  # 转发器会按布局自动选对应产物
```

---

## 命令一览

| 命令 | 用途 | 典型耗时 |
|---|---|---|
| `env` | 环境体检：布局 / 游戏根 / 程序集 / 日志位置 | 0.6 s |
| `check` | 静态检查（XML / Def / 字段 / 补丁 / 引用） | **2.5 s**（官方 6 包）｜14 min（629 mod 全量） |
| `order` | mod 加载顺序 + 未解析的 packageId | 2.7 s |
| `log` | Player.log 真机诊断 | 0.1 s / 2.3 MB |
| `sim` | 加载结果摘要（有效 def / 遮蔽 / MayRequire） | 6.5 s（官方） |
| `api` | 程序集类型/字段/方法查询 | 0.2 s |
| `name` | 名字存在性 + 大小写候选 | 0.4 s |
| `index` | 建 Def 索引（供 `name`/`sim` 用） | 5 s |
| `files` | 文件清单与归属 | 5 s |

### 慢的那个：`check` 全量

629 个 mod 下 `check --scope active` 要 **14 分钟**，其中 `RD2009`（补丁层）独占 96%。

这是成本，不是 bug —— 24,838 条补丁操作 × 100,236 节点 DOM 的 XPath 求值。
（缓存不安全：补丁会改文档，同一 XPath 前后求值可能命中不同节点。）

**三个规避手段：**

```bash
rimdev check --scope active --rules RD3001        # 只看悬空引用，约 30 秒
rimdev check --scope active --skip-rules RD2009   # 跳过最慢的补丁层
rimdev check --scope active --only-official       # 只看官方 6 包，2.5 秒
```

输出里会明确写出跑了哪几条、哪几条没跑 —— **「没跑」和「跑了没问题」必须能区分**：

```
规则子集        跑 1 / 9 条：RD3001
                （未跑：loadFolders, RD2001, RD2002, RD2004, RD2005, RD2025, RD4001, RD2009）
```

---

## 诊断码

62 个码，按族划分：

| 族 | 数量 | 领域 |
|---|---|---|
| `RD1xxx` | 6 | 环境（游戏根 / 托管目录 / ModsConfig） |
| `RD2xxx` | 26 | XML 与 Def（良构 / 继承 / loadFolders / **补丁求值**） |
| `RD3xxx` | 1 | 引用解析（悬空引用，含未启用 mod 的降级） |
| `RD4xxx` | 10 | 程序集元数据（类型 / 字段 / 枚举 / 命名） |
| `RD5xxx` | 1 | 遮蔽与加载顺序 |
| `RD6xxx` | 3 | 文本与纹理 |
| `RD7xxx` | 15 | **真机日志**（补丁失败 / 字段错 / 未归类） |

退出码约定：

| 码 | 含义 |
|---|---|
| 0 | 无 error |
| 2 | 有 error（不是工具坏了） |
| 3 | 参数错 |
| 4 | 环境不满足（缺游戏根 / 布局不匹配） |
| 5 | 目标 mod 找不到 |

---

## 接入 DSH（DeepSeek Harness）

仓库内含 `dsh-plugin/`，把 7 个命令暴露成模型可调用的工具：

```yaml
- insert:
    - id: rimdev-tools
      name: "@rimdev/dsh-rimdev"
      config:
        rimdevRoot: 'C:\...\Mods\_rimdev'
        timeoutMs: 1200000
```

工具：`rimdev_env` / `rimdev_check` / `rimdev_order` / `rimdev_log` / `rimdev_sim` / `rimdev_api` / `rimdev_name`

冒烟自测：`cd dsh-plugin && node smoke.mjs`

---

## 站在谁的肩膀上

这个工具不是凭空写出来的。它建立在三类现成资源上，每一类都明确标了**用在哪、有没有落地**。

### 一、游戏引擎反编译源（最核心）

**首要依据是 RimWorld 自己的程序集反编译源**，不是网上的教程。

用 `ilspycmd` 反编译本机安装的 `Assembly-CSharp.dll`（1.7.5005 rev1082，9,255 个 `.cs`，
非混淆），逐行对照实现语义。举几个直接来自源码的行为：

| 实现 | 源码锚点 |
|---|---|
| `<success>` 四态无条件覆盖 | `Verse/PatchOperation.cs:23-48` |
| `Insert` 的 `Append` 多子元素**反序** | `Verse/PatchOperationInsert.cs:26-38` |
| 只有顶层 Operation 调 `Complete()` | `Verse/LoadedModManager.cs:479-496` |
| `XmlInheritance` 的列表语义与白名单 | `Verse/XmlInheritance.cs:379-428` |

代码注释里保留这些锚点，知识库里有 `check_anchors.py` 可以验证行号还在不在。
**引擎行为以源码为准，不以文档为准** —— 这条纪律来自实测：官方文档和社区教程在
1.7 上有多处过时。

### 二、RimCrow（[Inky-Feather/RimCrow](https://github.com/Inky-Feather/RimCrow)）

桌面模组管理器，177 个 Python 文件 / 101k 行。它做**模组管理**，不分析 Def，与 RimDev 互补。
读了它的源码，**借鉴了三样东西**：

**① 日志诊断规则库**（`backend/managers/mgr_game_logs.py` 的 `LogAnalyzer`，约 803 行）

它的 11 条诊断正则**全部吸收**，并在此基础上扩到 19 条：

| 已吸收 | 说明 |
|---|---|
| `missing_def` | `is not defined in any loaded mods` |
| `xml_unknown_field` | `doesn't correspond to any field in type` |
| `cross_reference_missing` | `Could not resolve cross-reference` |
| `could_not_load_reference` | `Could not load reference to … named …` |
| `missing_texture` | `Failed to find any texture` |
| `null_reference_exception` | `NullReferenceException` |
| `type_load_exception` | `ReflectionTypeLoadException / TypeLoadException / …` |
| `translation_data_error` | `Translation data … has errors` |
| `package_id_format` | `PackageId … is not in valid format` |
| `patch_obsolete_method` | `patch on obsolete method / patched jump instruction` |
| `config_error` | `Config error in` |

**我们自己加的 8 条**（RimCrow 没有，全靠跑真实日志扒出来）：

`patch_xpath_no_match` / `patch_operation_failed` / `patch_sequence_error` /
`patch_sequence_detail` / `field_not_exist` / `patch_document_rejected` /
`patch_operation_rejected` / `patch_base_class_used`

> 这几条把「未归类」从 **117 条压到 4 条**。它们是补丁失败的六种不同说法 ——
> 引擎在不同阶段、对不同操作类型，文案完全不一样。这部分 RimCrow 没有覆盖。

**② 两条纪律（比代码更值钱）**

- **噪音黑名单**：这些框架的报错不该算到具体 mod 头上 ——
  `brrainz.harmony`、`unlimitedhugs.hugslib`、`imranfish.xmlextensions`、
  `zetrith.prepatcher`、`ludeon.rimworld` 等 9 个。
  没有它，「归因到 mod」会变成误伤。
- **版本号假阳性过滤**：从日志里提 packageId 时，`1.4` / `1.5.2` 会被误当成包名，
  用形如 `^\d+\.\d+(\.\d+)?$` 的正则排掉。

**③ 明确没有借鉴的**

- **拿 `steamDB.json` 当权威**：它的 49 MB / 57,679 条覆盖本地工坊 93.9%，很惊人。
  但它是**快照**，会过期；而且我们的判据必须来自本机当前安装。
- **Def 分析**：它没有这项能力，无从借鉴。

**④ 发现了但还没做的（诚实记账）**

对比 `mgr_game_logs.py:189-250` 的 `_parse_dependencies`，RimCrow 处理了四个
`About.xml` 特性，**RimDev 目前都还没实现**：

| 特性 | RimCrow | RimDev |
|---|---|---|
| `modDependenciesByVersion` | ✓ 按版本归并 | ❌ |
| `alternativePackageIds` | ✓ | ❌ |
| `incompatibleWith` | ✓ | ❌ |
| `core` → `ludeon.rimworld` 别名 | ✓ | ❌ |

后果：写 `core` 的依赖会判不到，写备选名的依赖会误判「缺前置」。
已有实现可参照，只是这一轮没排上。

### 三、DSH 生态（插件写法）

`dsh-plugin/` 的写法参照了同生态的 `dsh-rimworld-doctor`：

- **取服务的方式**：不导出 `inject`，用 `ctx.get('tools')` 按需取 ——
  cordis 的对象式 inject 在这个环境会让插件进 no-fiber。
- **本地 `defineTool` 恒等收窄**：不 import `@deepseek-ai/dsh-tools`，
  因为注入式安装走 junction 时裸导入会在运行期解析失败。

这两条都是**踩过的坑**，不是风格偏好。

### 四、明确不是借鉴对象的

- **[RimSort](https://rimsort.github.io/RimSort/)**：加载顺序排序器，有拓扑排序和 631 条社区规则库。
  是排序领域的权威，但与静态分析不重叠。
  （顺便：那 631 条里只有 2 条能在 steamDB 对上 ⇒ 两套口径不一致，只能当补充证据。）

---

## 为什么说它是 AI 写的，而且这很重要

这个项目本身就是一个**关于 AI 能不能做工程**的实验。

### 做到了什么

- 27,391 行 C# 实现，104 个文件，零第三方依赖（手写 csproj，不引 NuGet）
- 373 个自测用例，覆盖 34 个测试文件
- 62 个诊断码，每个都有明确的语义边界
- 1,200+ 行知识库，记录每个结论的**反编译源行号锚点**

### 关键是这些数字背后的纪律

**每一条结论都必须有证据，而且证据要能复现。**

举几个真实的例子：

**① 反直觉行为照抄，不「修正」**

引擎 `PatchOperationInsert` 的 `Append` 分支，多子元素时结果是**反序**的：

```csharp
foreach (XmlNode childNode in node.ChildNodes)
    parentNode.InsertAfter(ImportNode(childNode), xmlNode);   // 都插在同一参照点后面
```

这明显像 bug，但它是引擎的真实行为。我们**照抄**，并写了一条用例钉死它 ——
还做了负对照：把循环改成正序，用例立刻 FAIL。

**② 断言必须由真实数据钉住，不能凭直觉**

第一版测试曾断言「补丁层预估值 = 真机值」。
实测：离线 52 条，真机 59 条。**差 7 条。**

不是 bug —— 引擎在两个阶段各报一次，层级折叠规则不同。
如果当初写死「必须相等」，这条用例会成为永久性的假红。

**③ 探针错误必须记下来，因为它会重复犯**

这个项目里犯过至少三次同类错误：

- 用 `cmd | head -5` 查退出码 → 读到的是 `head` 的码，误判「门禁失效」
- 用 `ls -d "path"/*/` 查目录 → 被 `!` 开头的目录打断 glob，得出「目录为空」的错结论
- 用错误的 JSON key（`modPackageId` 而不是 `mod`）做统计 → 得出矛盾结果

每一条都写进了知识库的「排查方法论」，因为它们**会再犯**。

**④ 「没跑」和「跑了没问题」必须能区分**

这是项目里最常出现的纪律。任何被跳过、被降级、被过滤的东西，
输出里都必须明确写出来。否则用户会把「没检查」读成「没问题」——
那是**假 0**，比报错更危险。

### 没做到的（诚实边界）

- **不执行 mod 代码。** 自定义 `PatchOperation` 子类、Harmony 补丁的实际效果，
  本工具一律标 `Unknown`，不猜。
- **1.6 的回归测试没跑完。** 50 个用例需要真实的 1.6 安装，本机没有。
  覆盖率是 245/295，不是全部。
- **全量 `check` 很慢。** 14 分钟是固有成本，只做了「提前告知」和「可跳过」，
  没有真正优化掉。
- **`About.xml` 四个特性还没实现**（见上一节 ④）。
- **没有 GUI。** 纯 CLI。

---

## 文档

| 文件 | 内容 |
|---|---|
| `Docs/10-工具链知识库.md` | **主文档**，14 章：生态对照、四条真缺陷、排查方法论、版本治理、可复用探针、架构速查 |
| `Docs/01-设计文档.md` | 设计决策与取舍 |
| `dsh-plugin/README.md` | DSH 插件说明 |

知识库里的每条结论都带**反编译源行号**，例如：

```
引擎原文 Verse/PatchOperation.cs:23-48：
  bool flag = ApplyWorker(xml);
  if (success == Success.Always) flag = true;      ← 无条件覆盖
  else if (success == Success.Never) flag = false;
```

可以直接用 `check_anchors.py` 验证这些行号在不在。

---

## 环境要求

- Windows（本工具用 `win-x64` 发布；源码理论可跨平台，但未验证）
- 已安装环世界（1.6 或 1.7.x 线）
- 若从源码构建：.NET SDK 8 + 10（双 TFM 需要）

---

## License

BSD-3-Clause

---

## 最后

如果你觉得这个工具有用，欢迎用它查你自己 mod 的补丁问题 —— 尤其是 `--rules RD2009`。

如果你觉得它哪里说得不对，**直接说**。这个项目的全部判据都来自实测，
没有实测支持的结论不该存在。
