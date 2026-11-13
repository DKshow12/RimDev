/**
 * @rimdev/dsh-rimdev — 把 rimdev.exe 的静态分析能力接进 DSH。
 *
 * · 工具：rimdev_env / rimdev_check / rimdev_order / rimdev_log / rimdev_sim / rimdev_api / rimdev_name
 * · 全部走**已发布的单文件 exe**（dist\<tfm>\rimdev.exe），不依赖 .NET 运行时是否安装。
 *
 * 设计约束（与 dsh-rimworld-doctor 同一套纪律）：
 *   · 只读：不改游戏目录、不改 ModsConfig、不启动游戏；
 *   · 不导出 inject（cordis 对象式 inject 在本环境会让插件进 no-fiber），用 ctx.get() 按需取服务；
 *   · 插件缺文件/缺 exe 时**明确报错**，不静默降级成假结果。
 *
 * ★ 为什么不用 MCP：rimdev 是一次性 CLI（跑完就退出），MCP server 需要常驻进程。
 *   用 tools 服务直接注册更简单，也不需要多一个进程。
 */

const RIMDEV_ROOT_DEFAULT = 'C:\\Program Files (x86)\\Steam\\steamapps\\common\\RimWorld\\Mods\\_rimdev'

export const name = '@rimdev/dsh-rimdev'

export const configDefaults = {
  /** _rimdev 仓库根目录（含 dist/ 与 rimdev.cmd） */
  rimdevRoot: RIMDEV_ROOT_DEFAULT,
  /** 单次命令超时（毫秒）。check --scope active 在 629 mod 下要 14 分钟，默认给 20 分钟 */
  timeoutMs: 1200000,
}

/** cordis 的 logger 是对象；统一包一层，避免注入时抛错。 */
function log(ctx, message) {
  try {
    const logger = ctx?.logger
    if (typeof logger === 'function') return logger('[rimdev] ' + message)
    if (logger && typeof logger.info === 'function') return logger.info('[rimdev] ' + message)
    if (logger && typeof logger.log === 'function') return logger.log('[rimdev] ' + message)
  } catch {
    /* ignore */
  }
  console.log('[rimdev] ' + message)
}

function text(value) {
  return { text: value }
}

export function apply(ctx, config = {}) {
  const cfg = { ...configDefaults, ...config }
  const rimdevRoot = String(cfg.rimdevRoot || RIMDEV_ROOT_DEFAULT)
  const timeoutMs = Number(cfg.timeoutMs) > 0 ? Number(cfg.timeoutMs) : 1200000

  const anyCtx = ctx
  const getSvc = (n) =>
    typeof anyCtx.get === 'function' ? anyCtx.get(n) : anyCtx[n]

  // ── 运行时依赖：child_process / fs / path ────────────────────────────────
  // 用动态 import 而不是顶层 import：注入式安装时裸导入可能解析失败（生态一致做法）。
  let nodeCp = null
  let nodeFs = null
  let nodePath = null

  const ready = (async () => {
    nodeCp = await import('node:child_process')
    nodeFs = await import('node:fs')
    nodePath = await import('node:path')
  })()

  /** 找到该用哪份 exe：按 layout 选 dist\net10.0 / dist\net8.0；都没有则报错。 */
  function resolveExe(layout) {
    const tfm = layout === 'Classic16' ? 'net8.0' : layout === 'Waylith17' ? 'net10.0' : null
    if (!tfm) return { error: `不认识的布局 ${layout}` }
    const p = nodePath.join(rimdevRoot, 'dist', tfm, 'rimdev.exe')
    if (!nodeFs.existsSync(p)) {
      return {
        error:
          `找不到 ${tfm} 产物：${p}\n` +
          `先在 ${rimdevRoot} 跑 engine\\publish.ps1 生成发布版（或 engine\\build-all.ps1 生成开发版）。`,
      }
    }
    return { exe: p, tfm }
  }

  /** 跑一次 rimdev，返回 { code, stdout, stderr }。 */
  function runRimdev(args) {
    return new Promise((resolve) => {
      const exe = nodePath.join(rimdevRoot, 'dist', 'net10.0', 'rimdev.exe')
      // 先用 net10（本机 1.7）；若不存在再试 net8，让插件在 1.6 机器上也活。
      let finalExe = exe
      if (!nodeFs.existsSync(finalExe)) {
        const alt = nodePath.join(rimdevRoot, 'dist', 'net8.0', 'rimdev.exe')
        if (nodeFs.existsSync(alt)) finalExe = alt
      }
      if (!nodeFs.existsSync(finalExe)) {
        resolve({
          code: 4,
          stdout: '',
          stderr:
            `找不到 rimdev.exe。请先在 ${rimdevRoot} 跑 engine\\publish.ps1（生成 dist\\net10.0\\rimdev.exe 与 dist\\net8.0\\rimdev.exe）。`,
        })
        return
      }

      const child = nodeCp.spawn(finalExe, args, {
        cwd: rimdevRoot,
        windowsHide: true,
        // ★ exe 自己会探测游戏根；这里只把 profile 根目录传过去让它找 profiles.json
      })

      let out = ''
      let err = ''
      let settled = false
      const finish = (r) => {
        if (settled) return
        settled = true
        clearTimeout(timer)
        resolve(r)
      }

      const timer = setTimeout(() => {
        try { child.kill() } catch { /* ignore */ }
        finish({
          code: 124,
          stdout: out,
          stderr: err + `\n[rimdev] 超时（${timeoutMs} ms）已终止。可用 --skip-rules 跳过慢规则后重试。`,
        })
      }, timeoutMs)

      child.stdout?.on('data', (d) => { out += d.toString('utf8') })
      child.stderr?.on('data', (d) => { err += d.toString('utf8') })
      child.on('error', (e) => finish({ code: 4, stdout: out, stderr: err + '\n' + e.message }))
      child.on('close', (code) => finish({ code: code ?? 0, stdout: out, stderr: err }))
    })
  }

  /** 统一包装：把 (code/stdout/stderr) 变成工具结果文本。 */
  async function runAndRender(args, { wantJson = false } = {}) {
    await ready
    const r = await runRimdev(args)
    const head = `rimdev ${args.join(' ')}  → exit=${r.code}`

    // --json 且成功：原样给出 JSON（模型可直接解析），stderr 附在后面（慢规则预告等）
    if (wantJson && r.code !== 4 && r.stdout.trim()) {
      const body = r.stdout.trim()
      const extra = r.stderr.trim() ? `\n\n[stderr]\n${r.stderr.trim()}` : ''
      return text(`${head}\n\n${body}${extra}`)
    }

    const parts = [head]
    if (r.stdout.trim()) parts.push(r.stdout.trimEnd())
    if (r.stderr.trim()) parts.push(`[stderr]\n${r.stderr.trimEnd()}`)
    return text(parts.join('\n\n'))
  }

  // ── 工具定义（本地 defineTool：只做恒等收窄，不 import @deepseek-ai/dsh-tools）──
  const defineTool = (def) => def

  const tools = [
    defineTool({
      name: 'rimdev_env',
      description:
        '环世界环境体检：布局（Classic16=1.6 / Waylith17=1.7）、游戏根、托管程序集信息、' +
        'ModsConfig 启用集、Player.log 位置。排查任何 rimdev 问题前先跑它确认体检对象没找错。',
      parameters: {
        type: 'object',
        properties: {
          json: { type: 'boolean', description: '返回完整 JSON（含全部路径与程序集元数据）' },
        },
      },
      async execute(args = {}) {
        return runAndRender(['env', ...(args.json ? ['--json'] : [])], { wantJson: !!args.json })
      },
    }),

    defineTool({
      name: 'rimdev_check',
      description:
        '静态检查（不启动游戏）。检查 XML 良构、Def 重复/继承、字段名、补丁求值、悬空引用。' +
        '★ 大集合下很慢：629 mod 全量约 14 分钟，RD2009（补丁层）独占 96%。' +
        '想只看某一类问题，用 rules 只跑指定规则（例：["RD3001"] 只看悬空引用，约 30 秒）。',
      parameters: {
        type: 'object',
        properties: {
          scope: {
            type: 'string',
            enum: ['target', 'active', 'all'],
            description: 'target=当前工作区 mod（默认）｜active=全部启用 mod｜all=全库（含未启用的工坊 mod）',
          },
          rules: {
            type: 'array',
            items: { type: 'string' },
            description: '只跑这些规则（逗号分隔或数组）。常用：RD3001=悬空引用、RD2009=补丁打空、RD4001=字段名不存在',
          },
          skipRules: {
            type: 'array',
            items: { type: 'string' },
            description: '跳过这些规则。最常用：["RD2009"] 跳过最慢的补丁层',
          },
          onlyOfficial: {
            type: 'boolean',
            description: '只查官方 6 包（Data\\Core + 5 DLC）。这是跨引擎版本最稳的基线，几秒出结果',
          },
          severity: { type: 'string', enum: ['error', 'warning', 'info'], description: '最低级别，默认 info' },
          json: { type: 'boolean', description: '返回 JSON（含每条的 cause/location/evidence）' },
        },
      },
      async execute(args = {}) {
        const argv = ['check']
        if (args.scope) argv.push('--scope', String(args.scope))
        if (args.onlyOfficial) argv.push('--only-official')
        if (Array.isArray(args.rules) && args.rules.length) argv.push('--rules', args.rules.join(','))
        if (Array.isArray(args.skipRules) && args.skipRules.length) argv.push('--skip-rules', args.skipRules.join(','))
        if (args.severity) argv.push('--severity', String(args.severity))
        if (args.json) argv.push('--json')
        return runAndRender(argv, { wantJson: !!args.json })
      },
    }),

    defineTool({
      name: 'rimdev_order',
      description:
        'mod 加载顺序（引擎语义：Core → knownExpansions → activeMods），附每个 mod 的 loadFolders 解析结果与' +
        'unresolvedPackageIds（在配置里但磁盘上找不到的 packageId）。查「某个 mod 为什么没被加载」用它。',
      parameters: {
        type: 'object',
        properties: {
          json: { type: 'boolean', description: '返回 JSON（order 数组 + config 段含 unresolvedPackageIds）' },
        },
      },
      async execute(args = {}) {
        return runAndRender(['order', ...(args.json ? ['--json'] : [])], { wantJson: !!args.json })
      },
    }),

    defineTool({
      name: 'rimdev_log',
      description:
        '解析 Player.log 做真机诊断（RD7xxx）。能认出补丁打空/Sequence 失败/字段不存在等，并尽量归因到具体 mod。' +
        '★ 与离线 check 是【同一件事的两个视角】：离线 RD2009 预测「补丁会打空」，真机 RD7031 是引擎真的报了失败。',
      parameters: {
        type: 'object',
        properties: {
          path: { type: 'string', description: '日志路径，留空用本机默认 Player.log' },
          maxLines: { type: 'number', description: '只扫最后 N 行（大日志采样用）' },
          json: { type: 'boolean', description: '返回 JSON（findings + byRule + 归因）' },
        },
      },
      async execute(args = {}) {
        const argv = ['log']
        if (args.path) argv.push('--path', String(args.path))
        if (args.maxLines) argv.push('--max-lines', String(args.maxLines))
        if (args.json) argv.push('--json')
        return runAndRender(argv, { wantJson: !!args.json })
      },
    }),

    defineTool({
      name: 'rimdev_sim',
      description:
        '加载结果摘要：有效 def / 被遮蔽的 def / 悬空引用 / MayRequire 缺失 / loader 边界。' +
        '回答「装完这套 mod，游戏实际会加载出多少个 def」。',
      parameters: {
        type: 'object',
        properties: {
          scope: { type: 'string', enum: ['target', 'active', 'all'] },
          onlyOfficial: { type: 'boolean', description: '只看官方 6 包（几秒出结果）' },
          json: { type: 'boolean' },
        },
      },
      async execute(args = {}) {
        const argv = ['sim']
        if (args.scope) argv.push('--scope', String(args.scope))
        if (args.onlyOfficial) argv.push('--only-official')
        if (args.json) argv.push('--json')
        return runAndRender(argv, { wantJson: !!args.json })
      },
    }),

    defineTool({
      name: 'rimdev_api',
      description:
        '查游戏程序集里的类型/字段/方法/枚举是否存在（含大小写差异提示）。写 C# mod 时确认 API 名字用它。' +
        '★ 注意：它读的是【本机安装】的程序集，所以答案与当前游戏版本严格对应。',
      parameters: {
        type: 'object',
        properties: {
          symbol: { type: 'string', description: '要查的类型名（可带命名空间，如 Verse.ThingDef）' },
          kind: { type: 'string', enum: ['type', 'field', 'property', 'method', 'enum'], description: '默认 type' },
          owner: { type: 'string', description: '只在该类型上找成员（查字段/方法时用）' },
          json: { type: 'boolean' },
        },
        required: ['symbol'],
      },
      async execute(args = {}) {
        const argv = ['api', 'probe', String(args.symbol)]
        if (args.kind) argv.push('--kind', String(args.kind))
        if (args.owner) argv.push('--owner', String(args.owner))
        if (args.json) argv.push('--json')
        return runAndRender(argv, { wantJson: !!args.json })
      },
    }),

    defineTool({
      name: 'rimdev_name',
      description:
        '查一个名字是否存在，不存在时给候选（含大小写差异）。查 Def 名必须加 index=true（需先有 Def 索引）。' +
        '三态：存在 / 不存在 / 未查（需要索引但没给）。',
      parameters: {
        type: 'object',
        properties: {
          name: { type: 'string', description: '要查的名字' },
          kind: { type: 'string', enum: ['type', 'field', 'property', 'method', 'enum', 'def'], description: '默认不限' },
          index: { type: 'boolean', description: '查 Def 名时接上 Def 索引（不加会答「未查」）' },
          json: { type: 'boolean' },
        },
        required: ['name'],
      },
      async execute(args = {}) {
        const argv = ['name', 'check', String(args.name)]
        if (args.kind) argv.push('--kind', String(args.kind))
        if (args.index) argv.push('--index')
        if (args.json) argv.push('--json')
        return runAndRender(argv, { wantJson: !!args.json })
      },
    }),
  ]

  // ── 注册 ────────────────────────────────────────────────────────────────
  const toolsSvc = getSvc('tools')
  if (!toolsSvc || typeof toolsSvc.register !== 'function') {
    log(ctx, 'tools 服务不可用，工具未注册')
    return
  }

  ready.then(() => {
    // 启动时探一次：exe 在不在、能不能跑。缺了就在日志里说清楚（不静默）。
    const probe = nodePath.join(rimdevRoot, 'dist', 'net10.0', 'rimdev.exe')
    const probe8 = nodePath.join(rimdevRoot, 'dist', 'net8.0', 'rimdev.exe')
    if (!nodeFs.existsSync(probe) && !nodeFs.existsSync(probe8)) {
      log(ctx, `⚠ 没找到发布产物（${probe}）。工具会注册但调用时返回明确错误；先跑 engine\\publish.ps1。`)
    }
  })

  for (const tool of tools) {
    ctx.effect(() => toolsSvc.register(tool), `rimdev: tool ${tool.name}`)
  }

  log(ctx, `已注册 ${tools.length} 个工具（根目录 ${rimdevRoot}）`)
}
