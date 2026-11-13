# RimDev 版本切换入口（第 3 期 Task 9）—— 由 rimdev.cmd 转发调用。
#
# 语义：
#   rimdev --profile 1.6 <其余参数…>     # 用 1.6 那份安装
#   rimdev --profile 1.7 <其余参数…>     # 用 1.7.x 线那份安装
#   rimdev profile list                  # 列出所有 profile + 探测到的布局 + 将使用的产物
#   rimdev profile use <名字>            # 把 <名字> 记为默认（写回 profiles.json）
#   rimdev <其余参数…>                   # 不带 --profile 时用默认 profile
#
# ★ 产物由【布局】现场推出，不看版本号、也不看 exe 名：
#     <gameRoot>\RimWorldWin64_Data\Managed\Assembly-CSharp.dll 存在 ⇒ Classic16 ⇒ net8.0\rimdev.exe
#     否则 <gameRoot>\Assembly-CSharp.dll 存在                     ⇒ Waylith17 ⇒ net10.0\rimdev.exe
#     两者都无 ⇒ 退出码 4（环境不满足），报可读错误，不猜。
#   与 RimDev.Core 里的 RimDevProducts.ForLayout 是同一张表（自测 task9-layout 组钉住它）。
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$engine = Join-Path $root 'engine'
$profilesPath = Join-Path $root 'profiles.json'

function Exit-Env([string] $message) {
    [Console]::Error.WriteLine("RD1001: $message")
    exit 4
}
function Exit-Args([string] $message) {
    [Console]::Error.WriteLine($message)
    exit 3
}

function Read-Profiles {
    if (-not (Test-Path $profilesPath)) {
        Exit-Env "找不到 profile 文件: $profilesPath（应随仓库提供）"
    }
    return (Get-Content $profilesPath -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Get-Layout([string] $gameRoot) {
    if ([string]::IsNullOrWhiteSpace($gameRoot)) { return $null }
    $managedAc = Join-Path $gameRoot 'RimWorldWin64_Data\Managed\Assembly-CSharp.dll'
    if (Test-Path -LiteralPath $managedAc) { return 'Classic16' }
    $rootAc = Join-Path $gameRoot 'Assembly-CSharp.dll'
    if (Test-Path -LiteralPath $rootAc) { return 'Waylith17' }
    return 'Unknown'
}

function Get-Tfm([string] $layout) {
    switch ($layout) {
        'Classic16' { return 'net8.0' }
        'Waylith17' { return 'net10.0' }
        default { return $null }
    }
}

function Get-ProfileNode($profiles, [string] $name) {
    $node = $profiles.profiles.PSObject.Properties[$name]
    if ($null -eq $node) { return $null }
    return $node.Value
}

# ── 解析参数：先摘掉 --profile，再分发子命令 ────────────────────────────────────
$argv = @($args)
$cliProfile = $null
$rest = New-Object System.Collections.Generic.List[string]
for ($i = 0; $i -lt $argv.Count; $i++) {
    $a = [string]$argv[$i]
    if ($a -eq '--profile') {
        if ($i + 1 -ge $argv.Count) { Exit-Args '--profile 需要跟一个名字（如 --profile 1.6）' }
        $cliProfile = [string]$argv[$i + 1]
        $i++
        continue
    }
    if ($a -like '--profile=*') { $cliProfile = $a.Substring('--profile='.Length); continue }
    $rest.Add($a)
}

$profiles = Read-Profiles

# ── 子命令：profile list / profile use ─────────────────────────────────────────
if ($rest.Count -ge 1 -and $rest[0] -eq 'profile') {
    $sub = if ($rest.Count -ge 2) { [string]$rest[1] } else { 'list' }

    if ($sub -eq 'list') {
        $defaultName = $profiles.default
        if (-not [string]::IsNullOrWhiteSpace($env:RIMDEV_PROFILE)) { $defaultName = $env:RIMDEV_PROFILE }
        Write-Host "profiles（* = 当前默认；路径可由 RIMDEV_GAME_ROOT / RIMDEV_WORKSHOP_ROOT 覆盖）"
        foreach ($p in $profiles.profiles.PSObject.Properties) {
            $name = $p.Name
            $node = $p.Value
            $gameRoot = [string]$node.gameRoot
            if (-not [string]::IsNullOrWhiteSpace($env:RIMDEV_GAME_ROOT)) { $gameRoot = $env:RIMDEV_GAME_ROOT }
            $layout = Get-Layout $gameRoot
            $tfm = Get-Tfm $layout
            $mark = if ($name -eq $defaultName) { '*' } else { ' ' }
            $shown = if ([string]::IsNullOrWhiteSpace($gameRoot)) { '(未配置 gameRoot)' } else { $gameRoot }
            $product = if ($null -eq $tfm) { '（无可用产物）' } else { "engine\RimDev.Cli\bin\Debug\$tfm\rimdev.exe" }
            Write-Host ("{0} {1,-6} layout={2,-10} product={3}" -f $mark, $name, $(if ($null -eq $layout) { '-' } else { $layout }), $product)
            Write-Host ("           gameRoot={0}" -f $shown)
            $ws = [string]$node.workshopRoot
            if (-not [string]::IsNullOrWhiteSpace($env:RIMDEV_WORKSHOP_ROOT)) { $ws = $env:RIMDEV_WORKSHOP_ROOT }
            if (-not [string]::IsNullOrWhiteSpace($ws)) { Write-Host ("           workshopRoot={0}" -f $ws) }
        }
        exit 0
    }

    if ($sub -eq 'use') {
        if ($rest.Count -lt 3) { Exit-Args 'rimdev profile use <名字>：缺少名字（可先 rimdev profile list 看有哪些）' }
        $name = [string]$rest[2]
        $node = Get-ProfileNode $profiles $name
        if ($null -eq $node) { Exit-Args "没有名为 [$name] 的 profile（可先 rimdev profile list）" }
        $raw = Get-Content $profilesPath -Raw -Encoding UTF8
        $raw = [regex]::Replace($raw, '("default"\s*:\s*")[^"]*(")', ('${1}' + $name + '${2}'), 1)
        [IO.File]::WriteAllText($profilesPath, $raw, (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "已把默认 profile 设为 [$name]（写入 $profilesPath）"
        exit 0
    }

    Exit-Args "未知子命令: profile $sub（支持 list / use）"
}

# ── 解析生效 profile：命令行 > 环境变量 > profiles.json default ─────────────────
$profileName = $cliProfile
if ([string]::IsNullOrWhiteSpace($profileName)) { $profileName = $env:RIMDEV_PROFILE }
if ([string]::IsNullOrWhiteSpace($profileName)) { $profileName = [string]$profiles.default }
if ([string]::IsNullOrWhiteSpace($profileName)) { Exit-Env '没有指定 profile，且 profiles.json 里没有 default' }

$node = Get-ProfileNode $profiles $profileName
if ($null -eq $node) { Exit-Args "没有名为 [$profileName] 的 profile（可先 rimdev profile list）" }

$gameRoot = [string]$node.gameRoot
if (-not [string]::IsNullOrWhiteSpace($env:RIMDEV_GAME_ROOT)) { $gameRoot = $env:RIMDEV_GAME_ROOT }
$workshopRoot = [string]$node.workshopRoot
if (-not [string]::IsNullOrWhiteSpace($env:RIMDEV_WORKSHOP_ROOT)) { $workshopRoot = $env:RIMDEV_WORKSHOP_ROOT }

if ([string]::IsNullOrWhiteSpace($gameRoot)) {
    Exit-Env ("profile ``{0}`` 还没配 gameRoot，请编辑 {1} 里的 profiles.{0}.gameRoot，或设置环境变量 RIMDEV_GAME_ROOT。" -f $profileName, $profilesPath)
}
if (-not (Test-Path -LiteralPath $gameRoot)) {
    Exit-Env "profile [$profileName] 的 gameRoot 不存在: $gameRoot"
}

$layout = Get-Layout $gameRoot
$tfm = Get-Tfm $layout
if ($null -eq $tfm) {
    Exit-Env ("该安装是 Unknown 布局（既没有 <gameRoot>\RimWorldWin64_Data\Managed\Assembly-CSharp.dll，" +
              "也没有 <gameRoot>\Assembly-CSharp.dll）：$gameRoot。请确认 gameRoot 指向游戏根目录。")
}

$exe = Join-Path $engine ("RimDev.Cli\bin\Debug\{0}\rimdev.exe" -f $tfm)
if (-not (Test-Path -LiteralPath $exe)) {
    Exit-Env ("找不到 {0} 布局对应的产物: {1}。请先运行 engine\build-all.ps1 构建两套产物。" -f $layout, $exe)
}

# 让 env 能一眼看出「这次的数字属于哪次切换」。
#   之前把 --rimdev-* 放在最前面 ⇒ 每个经 shim 的调用都变成「未知命令: --rimdev-profile」。
#   这里改成「<子命令> <用户参数…> --rimdev-profile X --rimdev-invoked-via shim [--workshop-root W] --game-root G」。
$forward = @()
$forward += $rest.ToArray()
$forward += @('--rimdev-profile', $profileName, '--rimdev-invoked-via', 'shim')
if (-not [string]::IsNullOrWhiteSpace($workshopRoot)) {
    $forward += @('--workshop-root', $workshopRoot)
}
$forward += @('--game-root', $gameRoot)

& $exe @forward
exit $LASTEXITCODE
