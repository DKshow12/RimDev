# mdtype — 只读 .NET 元数据的类型继承链查询

**用途**：在不加载程序集的前提下，查一个类型的**基类链**。

**为什么存在**：判定「XML 元素名含点（`AlienRace.ThingDef_AlienRace`）时它到底是哪个 Def 的子类」，
需要读继承链。但：

- `Assembly.LoadFrom` 加载 mod DLL **有崩溃风险** —— mod 多是 net472（1.6 时代编译），
  RimDev 跑在 net10，`Docs/08` 记过 net8 载 1.7 `Assembly-CSharp` 会 `ReflectionTypeLoadException`
- `ManagedAssemblyCatalog` 只加载**官方** Managed 目录，看不到 mod 的 `Assemblies/*.dll`

**本工具的做法**：`System.Reflection.Metadata` + `PEReader` 读元数据表。
**不执行任何代码、不做类型加载** ⇒ 零崩溃风险，可安全用于第三方 DLL。

---

## 用法

```powershell
# 首次需构建
$env:TEMP = "<_rimdev>\_tmp\temp"
$env:TMP  = $env:TEMP
$env:DOTNET_CLI_HOME = "<_rimdev>\_tmp\dotnet-home"
dotnet build -v:q --nologo

# 查一个或多个类型
mdtype.exe <dll路径> <类型全名> [更多类型...]
```

**输出示例**（格式）：

```
程序集: <AssemblyName>
类型总数: 911
  AlienRace.ThingDef_AlienRace
    链: ThingDef_AlienRace → Verse.ThingDef (外部)
```

- 链里末尾标 `(外部)` 的表示基类在**另一个程序集**，需要再去那个程序集里查
- `未找到` 表示该 DLL 没定义这个类型（可能只是引用了它，不是提供者）

---

## 判读规则（重要）

| 链的样子 | 含义 | 对 RimDev 的处置 |
|---|---|---|
| `Xxx → Verse.Def (外部)` | 直接派生自 `Def`，**不是**某个具体子类 | **不要**归并到某个具体 Def 类型，只能归并到 `Def` |
| `Xxx → Verse.ThingDef (外部)` | 是 `ThingDef` 的子类 | 归并到 `ThingDef` |
| `Xxx → ... → Verse.Def` 多层 | 沿链找**第一个** `Verse.*Def` | 归并到那一层 |
| `未找到` | 这个 DLL 不提供该类型 | 去别的 DLL 找；找不到则只能走显式映射或使用侧反推 |

---

## 已实测的事实（2026-11-10）

- 探针本身 **44 行**，`net10.0`，**零外部依赖**，编译 0 警告 0 错误
- `Yokai_AllCode.dll`（729,088 B）：`ThingDef_AlienRace` 在偏移 541704，`AlienRace` 在 541713，
  两者相差 9 = `AlienRace.` 的长度 ⇒ 元数据里的类型全名串是 `AlienRace.ThingDef_AlienRace`
- **注意**：`Yokai_AllCode.dll` **引用**该类型但并不**定义**它（`mdtype` 查它返回「未找到」）
  ⇒ 找类型要看**提供者** DLL，不是引用者

## 已知限制

- 只读元数据，**拿不到**运行期才有的东西（`DefGenerator` 产物、C# 动态生成类型）
- 泛型实例化类型名带 `` `1 `` 后缀，匹配时要注意
- 嵌套类型用 `+` 分隔（`Outer+Inner`）

---

## 相关

- 知识库：`Docs/10-工具链知识库.md` §2.2（全限定子类名未归并）
- 已知陷阱：`ManagedAssemblyCatalog.Find()` 的简单名退化**不能**用于 XML 元素名解析
  （会让 `VEF.Abilities.AbilityDef` 兜底命中 `RimWorld.AbilityDef`，实测造成 17,623 条假阳）
