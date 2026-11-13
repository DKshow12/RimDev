# @rimdev/dsh-rimdev

把 [RimDev](../../) 工具链接入 DSH —— 让模型可以直接调用环世界静态分析。

## 它做什么

把 `rimdev.exe` 的 7 个命令暴露成模型可调用的工具：

| 工具 | 对应 CLI | 用途 |
|---|---|---|
| `rimdev_env` | `env` | 环境体检：布局（1.6/1.7）、游戏根、程序集、Player.log 位置 |
| `rimdev_check` | `check` | 静态检查（XML 良构 / Def 继承 / 字段名 / 补丁求值 / 悬空引用） |
| `rimdev_order` | `order` | mod 加载顺序 + `unresolvedPackageIds` |
| `rimdev_log` | `log` | Player.log 真机诊断（RD7xxx） |
| `rimdev_sim` | `sim` | 加载结果摘要（有效 def / 遮蔽 / MayRequire） |
| `rimdev_api` | `api probe` | 查游戏程序集里的类型/字段/方法是否存在 |
| `rimdev_name` | `name check` | 查名字是否存在（含大小写差异候选） |

## 为什么不用 MCP

`rimdev` 是一次性 CLI（跑完就退出），MCP server 需要常驻进程。
用 DSH 的 `tools` 服务直接注册更简单，也不用多养一个进程。

## 依赖

**只依赖已发布的 exe**，不需要装 .NET 运行时：

```
_rimdev\dist\net10.0\rimdev.exe   (1.7 线，约 71 MB，self-contained)
_rimdev\dist\net8.0\rimdev.exe    (1.6，约 65 MB，self-contained)
```

生成方式（在 `_rimdev` 根目录）：

```powershell
pwsh -File engine\publish.ps1
```

## 配置

```yaml
- insert:
    - id: rimdev-tools
      name: "@rimdev/dsh-rimdev"
      config:
        rimdevRoot: 'C:\...\Mods\_rimdev'   # 含 dist/ 的仓库根
        timeoutMs: 1200000                   # 单次命令超时；全量 check 要 14 分钟
```

## 设计约束（与 `dsh-rimworld-doctor` 同一套纪律）

- **只读**：不改游戏目录、不改 ModsConfig、不启动游戏。
- **不导出 `inject`**：cordis 的对象式 inject 在本环境会让插件进 no-fiber，用 `ctx.get()` 按需取服务。
- **缺 exe 时明确报错**，不静默降级成假结果。

## 与 dsh-rimworld-doctor 的关系

两者**共存不冲突**，能力是包含关系：

| | rimdoctor | **rimdev** |
|---|---|---|
| 诊断码 | 6 个检查项 | **59 个 RDxxxx 码** |
| 补丁层求值 | ❌ | ✅ RD2009（XPath 打空预测） |
| 继承树合并 | 部分 | ✅ 完整（ElementTypeResolver） |
| 程序集元数据 | ❌ | ✅ api / name |
| 规则子集 | ❌ | ✅ `--rules` / `--skip-rules` |
| 侧栏面板 | ✅ | ❌（本插件只有工具，无 UI） |

短平快的体检用 rimdoctor（有面板、快）；需要深挖（补丁为什么打空、某个 API 存不存在、只看某一类问题）用 rimdev。

## 慢的地方

`check --scope active` 在 629 mod 下要 **14 分钟**，其中 `RD2009`（补丁层）独占 96%。
工具描述里已写明，并给了两个规避手段：

- `rules: ["RD3001"]` —— 只看悬空引用，约 30 秒
- `skipRules: ["RD2009"]` —— 跳过最慢的那条

## 冒烟自测

```bash
cd _rimdev/dsh-plugin
node smoke.mjs
```

会伪造 ctx 调用 `apply()`，确认 7 个工具都注册成功，并真跑 exe 验证每个工具能返回结果。
