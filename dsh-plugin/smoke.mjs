/**
 * 插件冒烟自测 —— 不依赖 DSH 宿主，用伪造 ctx 验证 apply() 与全部工具。
 *
 *   cd _rimdev/dsh-plugin && node smoke.mjs
 *
 * 退出码：0 全通过；1 有失败。
 *
 * ★ 为什么要有它：插件是 ESM，注入到 DSH 后如果 apply() 抛错，
 *   表现是「工具不存在」而不是「插件报错」—— 很难查。这里先把 apply() 跑通，
 *   再逐个真跑 exe，把问题挡在注入之前。
 */
import { apply, name, configDefaults } from './src/index.mjs'

const registered = []
const logs = []

const fakeCtx = {
  logger: { info: (m) => logs.push(m) },
  get: (n) => (n === 'tools' ? { register: (t) => registered.push(t) } : null),
  effect: (fn) => fn(),
}

let failures = 0
const fail = (msg) => { failures++; console.log('  ❌ ' + msg) }
const ok = (msg) => console.log('  ✓ ' + msg)

console.log('=== 1. 模块元信息 ===')
console.log('  name =', name)
console.log('  rimdevRoot =', configDefaults.rimdevRoot)
if (name !== '@rimdev/dsh-rimdev') fail('name 不对')
else ok('name 正确')

console.log('\n=== 2. apply() 注册工具 ===')
apply(fakeCtx, {})
const expected = [
  'rimdev_env', 'rimdev_check', 'rimdev_order', 'rimdev_log',
  'rimdev_sim', 'rimdev_api', 'rimdev_name',
]
if (registered.length === expected.length) ok(`注册 ${registered.length} 个工具`)
else fail(`注册 ${registered.length} 个，期望 ${expected.length}`)

const byName = Object.fromEntries(registered.map((t) => [t.name, t]))
for (const n of expected) {
  if (!byName[n]) fail(`缺少工具 ${n}`)
}
for (const t of registered) {
  if (!t.description || t.description.length < 20) fail(`${t.name} 的 description 太短`)
  if (!t.parameters || t.parameters.type !== 'object') fail(`${t.name} 的 parameters 形状不对`)
  if (typeof t.execute !== 'function') fail(`${t.name} 没有 execute`)
}
if (failures === 0) ok('全部工具形状合规')

console.log('\n=== 3. 真跑 exe（每个工具至少一次）===')
// 挑快且结果确定的组合；慢的（check 全量）不放进冒烟
const cases = [
  ['rimdev_env', {}, (t) => t.includes('exit=0')],
  ['rimdev_api', { symbol: 'ThingDef' }, (t) => t.includes('exit=0')],
  ['rimdev_api', { symbol: 'NoSuchTypeZZZ' }, (t) => t.includes('exit=2')],
  ['rimdev_name', { name: 'Gun_Revolver', kind: 'def', index: true }, (t) => t.includes('exit=0')],
  ['rimdev_order', {}, (t) => t.includes('exit=0')],
  ['rimdev_check', { onlyOfficial: true, rules: ['RD2001'] }, (t) => /exit=[02]/.test(t)],
]

for (const [toolName, args, check] of cases) {
  const tool = byName[toolName]
  if (!tool) { fail(`${toolName} 未注册`); continue }
  const t0 = Date.now()
  let r
  try {
    r = await tool.execute(args)
  } catch (e) {
    fail(`${toolName} 抛异常: ${e.message}`)
    continue
  }
  const ms = Date.now() - t0
  const body = (r && r.text) || ''
  if (!body) { fail(`${toolName} 返回空`); continue }
  if (body.includes('找不到 rimdev.exe')) { fail(`${toolName}: 找不到 exe（先跑 engine\\publish.ps1）`); continue }
  if (!check(body)) {
    fail(`${toolName}(${JSON.stringify(args)}) 结果不符预期: ${body.split('\n')[0].slice(0, 120)}`)
    continue
  }
  ok(`${toolName}(${JSON.stringify(args)}) ${ms} ms`)
}

console.log('\n=== 4. 缺 exe 时必须明确报错（不静默）===')
const badCtx = { ...fakeCtx, get: fakeCtx.get }
const badRegistered = []
const badApplyCtx = {
  logger: { info: () => {} },
  get: (n) => (n === 'tools' ? { register: (t) => badRegistered.push(t) } : null),
  effect: (fn) => fn(),
}
apply(badApplyCtx, { rimdevRoot: 'C:\\definitely\\not\\here' })
const envTool = badRegistered.find((t) => t.name === 'rimdev_env')
if (!envTool) fail('错误根目录下仍应注册工具（注册与可用性分开）')
else {
  const r = await envTool.execute({})
  const body = (r && r.text) || ''
  if (body.includes('找不到 rimdev.exe')) ok('错误根目录时明确报「找不到 exe」')
  else fail('错误根目录时没有明确报错: ' + body.slice(0, 100))
}

console.log('\n' + (failures === 0 ? '全部通过' : `失败 ${failures} 项`))
process.exit(failures === 0 ? 0 : 1)
