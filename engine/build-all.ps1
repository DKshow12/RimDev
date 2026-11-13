# 第 3 期 Task 9：**双目标**构建 + 双自测 + shim 冒烟 + 汇总。
#
#   net8.0  → 给 1.6（Unity / net472 托管程序集）
#   net10.0 → 给 1.7.x 线（Waylith / net10 托管程序集）
#
# 步骤：① 依次 build 三个工程（一次构建两个 TFM，0 警告 0 错误才继续）；② 每个 TFM 各跑一次自测；
#       ③ shim 冒烟（`rimdev profile list` 与「未配置 profile」的退出码）；④ 汇总。
#
# ★ 无 1.6 安装时**仍然退出码 0**，但报告里必须出现 `SKIP N (需 Classic16 布局)` 行
#   —— 「跳过」必须是显式的，不许把 266 改成 266−N、也不许放宽断言。
# ★ 绝不与任何 rimdev.exe 实例并发跑（DLL 被占用会报 6 条 MSB3026，看着像 6 个警告）。
# ★ 本机沙箱**阻塞命名管道** ⇒ 多节点 MSBuild（默认 -m）在双目标工程上会**静默失败**
#   （"生成失败 / 0 个警告 / 0 个错误"，没有任何 error 行）。必须 `-m:1`。
#   同族：`-p:UseSharedCompilation=false`（csc 的 VBCSCompiler 走命名管道，同样会被拖 20 s 超时）。
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$rimdevRoot = Split-Path -Parent $root

$msbuildArgs = @('-m:1', '-nodeReuse:false', '-p:UseSharedCompilation=false')

$frameworks = @('net8.0', 'net10.0')
$projects = @(
    'RimDev.Core\RimDev.Core.csproj',
    'RimDev.Cli\RimDev.Cli.csproj',
    'RimDev.SelfTest\RimDev.SelfTest.csproj'
)

# ★★ 构建门禁必须**真的数警告**：修前这里只有 `if ($LASTEXITCODE -ne 0)`，而汇总里的
#   「构建 0 警告 0 错误」是一句**写死的字符串** —— MSBuild 打出 `warning CS####` 时脚本照样报「0 警告」
#   （2026-10-24 主理人复验 Task 9c 当场命中：raw 日志 4 行 warning，汇总行写 0）。
#   这正是本项目反复追的那一族：**统计字段描述的是宣称，而不是实际动作**。
#   现在：① 收集每个工程的构建输出并数 `warning <代码><数字>`；② 有一条就 exit 1；
#   ③ 汇总行打印**实测值** `$totalWarnings` 而不是字面量。
#   附带好处：并发跑导致的 MSB3026（DLL 被占用）现在会当场判红，不再「6 条警告 + 汇总写 0 警告」地混过去。
$totalWarnings = 0
foreach ($p in $projects) {
    Write-Host "=== build $p [net8.0;net10.0] ==="
    # 不要接给会提前终止管道的 cmdlet（那会改写 $LASTEXITCODE）；先收集再判。
    $buildLog = & dotnet build (Join-Path $root $p) -v:q --nologo @msbuildArgs 2>&1
    $buildCode = $LASTEXITCODE
    foreach ($l in $buildLog) { Write-Host $l }
    # ★ MSBuild 会把同一条警告打印两遍（正文一次 + 末尾汇总列表一次），且 Cli 会作为 SelfTest 的依赖
    #   再被构建一次 ⇒ 不去重会把「2 条警告」报成「8 条」。先按行去重，口径写进报错文案。
    $warns = @($buildLog | Where-Object { "$_" -match 'warning\s+[A-Z]+\d+' } | Select-Object -Unique)
    $totalWarnings += $warns.Count
    if ($buildCode -ne 0) { Write-Error "build failed: $p"; exit 1 }
    if ($warns.Count -gt 0) {
        Write-Error ("build produced {0} warning line(s)（每个 TFM 各计一次；阈值 0）: {1}（0 警告是门禁判据，不是祝福语）" -f $warns.Count, $p)
        exit 1
    }
}

$results = @()

foreach ($tfm in $frameworks) {
    Write-Host ""
    Write-Host "############################ selftest $tfm ############################"

    $exe = Join-Path $root "RimDev.SelfTest\bin\Debug\$tfm\rimdev-selftest.exe"
    if (-not (Test-Path $exe)) { Write-Error "selftest exe not found: $exe"; exit 1 }

    $lines = & $exe 2>&1
    $code = $LASTEXITCODE
    # ★ 不把输出接给 Select-Object 之类的提前终止管道（那会改写 $LASTEXITCODE）。
    foreach ($l in $lines) { Write-Host $l }

    $skipped = 0
    $total = 0
    $passed = 0
    $failed = 0
    $skippedNames = @()
    $inSkipList = $false
    foreach ($l in $lines) {
        $s = [string]$l
        if ($s -match '^skipped-by-layout\s*=\s*(\d+)') { $skipped = [int]$Matches[1]; continue }
        if ($s -match '^合计\s*(\d+)\s*个用例：(\d+)\s*通过\s*/\s*(\d+)\s*失败') {
            $total = [int]$Matches[1]; $passed = [int]$Matches[2]; $failed = [int]$Matches[3]; continue
        }
        if ($s -match '^被跳过的用例') { $inSkipList = $true; continue }
        if ($inSkipList) {
            if ($s -match '^\s+-\s+(.+)$') { $skippedNames += $Matches[1].Trim(); continue }
            if ($s -match '^\s*（') { continue }
            if ($s.Trim() -eq '') { continue }
            $inSkipList = $false
        }
    }

    # ★ 第 3 期 Task 9：产物 ↔ 布局不匹配是**预期**结局，不是失败。
    #   net8 产物只能服务 Classic16（1.6），本机是 Waylith17（1.7.x）⇒ 自测拒跑并退 4（程序里带可读原因）。
    #   这里必须把它记成「不适用」，而不是当成 FAIL —— 否则「无 1.6 安装时 build-all 退出码 0」永远不成立。
    if ($code -eq 4 -and (($lines -join "`n") -match '环境不匹配')) {
        $results += [pscustomobject]@{
            Tfm           = $tfm
            Total         = 0
            Passed        = 0
            Failed        = 0
            Skipped       = $skipped
            Names         = @()
            NotApplicable = $true
        }
        Write-Host ("[$tfm] 自测 不适用：本产物服务另一个布局（见上方「环境不匹配」行）；skipped-by-layout = $skipped")
        continue
    }

    if ($code -ne 0) {
        Write-Error "selftest failed [$tfm] (exit=$code)"
        exit $code
    }

    $results += [pscustomobject]@{
        Tfm           = $tfm
        Total         = $total
        Passed        = $passed
        Failed        = $failed
        Skipped       = $skipped
        Names         = $skippedNames
        NotApplicable = $false
    }
}

# ── shim 冒烟（版本切换入口）───────────────────────────────────────────────────
Write-Host ""
Write-Host "############################ shim 冒烟 ############################"
$shim = Join-Path $rimdevRoot 'rimdev.cmd'
$shimOk = $true

if (-not (Test-Path $shim)) {
    Write-Host "shim 冒烟: FAIL（找不到 $shim）"
    $shimOk = $false
}
else {
    $list = & $shim profile list 2>&1
    $listCode = $LASTEXITCODE
    foreach ($l in $list) { Write-Host $l }
    $has17 = ($list -join "`n") -match '1\.7'
    if ($listCode -eq 0 -and $has17) {
        Write-Host "shim 冒烟: PASS（profile list 退出码 0，输出含 1.7）"
    }
    else {
        Write-Host "shim 冒烟: FAIL（profile list 退出码 $listCode，含 1.7 = $has17）"
        $shimOk = $false
    }

    # ★ 必须冒烟一条**真正转发**的调用：只测 `profile list` 会漏掉「分发的参数顺序写错」这类缺陷
    #   （实测踩过：shim 曾把 --rimdev-profile 放在最前面 ⇒ 每个经 shim 的调用都变成「未知命令」）。
    #   这里断言 env 的两行身份标记真的出现了。
    $env17 = & $shim --profile 1.7 env 2>&1
    $code17 = $LASTEXITCODE
    $env17Text = $env17 -join "`n"
    $hasProfile17 = $env17Text -match 'profile\s+1\.7'
    $hasShimMark = $env17Text -match 'invokedVia=shim'
    if ($code17 -eq 0 -and $hasProfile17 -and $hasShimMark) {
        Write-Host "shim 冒烟: PASS（--profile 1.7 env 退出码 0，profile=1.7 且 invokedVia=shim）"
    }
    else {
        Write-Host "shim 冒烟: FAIL（--profile 1.7 env 退出码 $code17，profile=1.7 = $hasProfile17，invokedVia=shim = $hasShimMark）"
        $shimOk = $false
    }

    # 本机没有 1.6 安装 ⇒ profile `1.6` 的 gameRoot 为空 ⇒ 必须退出码 4 且给出提示。
    $env16 = & $shim --profile 1.6 env 2>&1
    $code16 = $LASTEXITCODE
    foreach ($l in $env16) { Write-Host $l }
    $hint16 = ($env16 -join "`n") -match '还没配 gameRoot'
    if ($code16 -eq 4 -and $hint16) {
        Write-Host "shim 冒烟: PASS（--profile 1.6 env 退出码 4 且提示 gameRoot 未配置）"
    }
    else {
        Write-Host "shim 冒烟: FAIL（--profile 1.6 env 退出码 $code16，提示命中 = $hint16）"
        $shimOk = $false
    }
}

Write-Host ""
Write-Host "================ build-all 汇总 ================"
foreach ($r in $results) {
    $cli = Join-Path $root "RimDev.Cli\bin\Debug\$($r.Tfm)\rimdev.exe"
    $exists = Test-Path $cli
    $art = $(if ($exists) { 'rimdev.exe ✓' } else { 'rimdev.exe ✗' })
    if ($r.NotApplicable) {
        Write-Host ("[{0}] 构建 {1} 警告 0 错误 | 自测 不适用（本产物服务另一个布局，需对应布局的安装）| 产物 {2}" -f $r.Tfm, $totalWarnings, $art)
        Write-Host ("[{0}] SKIP {1} (需 Classic16 布局) — 未跑用例（产物/布局不匹配）" -f $r.Tfm, $r.Skipped)
        continue
    }
    Write-Host ("[{0}] 构建 {1} 警告 0 错误 | 自测 {2}/{3} 通过（失败 {4}）| 产物 {5}" -f `
        $r.Tfm, $totalWarnings, $r.Passed, $r.Total, $r.Failed, $art)
    if ($r.Skipped -gt 0) {
        Write-Host ("[{0}] SKIP {1} (需 Classic16 布局)" -f $r.Tfm, $r.Skipped)
        foreach ($n in $r.Names) { Write-Host ("[{0}]   - {1}" -f $r.Tfm, $n) }
    }
    else {
        Write-Host ("[{0}] SKIP 0 (需 Classic16 布局)" -f $r.Tfm)
    }
}
Write-Host ("shim: " + $(if ($shimOk) { 'PASS（`rimdev --profile 1.6|1.7 …` 自动选产物）' } else { 'FAIL' }))
Write-Host "（1.6 用 net8.0\rimdev.exe；1.7.x 用 net10.0\rimdev.exe；两者同一套源码，由 shim 现场探测布局后自动选）"

$bad = @($results | Where-Object { $_.Failed -ne 0 })
if ($bad.Count -gt 0 -or -not $shimOk) { exit 1 }
exit 0
