# 第 3 期 Task 25：把 rimdev 发布成**完整独立的 .exe**（self-contained + single-file）。
#
# 产出布局（dist/）：
#   dist/
#     net10.0/rimdev.exe     ← 1.7.x 线（Waylith / net10 托管程序集），约 74 MB
#     net8.0/rimdev.exe      ← 1.6（Unity / net472 托管程序集），约 68 MB
#     VERSION.txt            ← 本次发布的时间/来源 commit/两产物大小与 sha256
#
# ★ 为什么两份都要：net8 进程载入不了 net10 的 Assembly-CSharp，反过来也一样。
#   这不是「重复打包」，而是**两个不同的运行时**各带自己的 BCL。
#   实测（Task 25）：net8 产物跑在 1.7 安装上会拒答并说明理由（exit 4），不会给假答案。
#
# ★ 为什么 self-contained：本机装没装对应 .NET 运行时不该由用户操心。
#   实测：只拷 rimdev.exe（framework-dependent，162 KB）到空目录 ⇒ exit 0x80008096（缺运行时）。
#   self-contained 后单文件 74 MB，空目录直接可用。
#
# ★ 绝不与任何 rimdev.exe 实例并发跑（DLL 被占用会报 MSB3026，看着像警告）。
#   同 build-all.ps1：本机沙箱阻塞命名管道 ⇒ 必须 -m:1 + UseSharedCompilation=false。

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot                      # _rimdev\engine
$rimdevRoot = Split-Path -Parent $root     # _rimdev
$dist = Join-Path $rimdevRoot 'dist'

$msbuildArgs = @('-m:1', '-nodeReuse:false', '-p:UseSharedCompilation=false')
$frameworks = @('net10.0', 'net8.0')       # net10 在前的理由：本机是 1.7，先保证常用那份成功

Write-Host '=== rimdev 发布（self-contained 单文件）==='
Write-Host "输出目录: $dist"
Write-Host ''

# 产物清单（用于 VERSION.txt）
$manifest = New-Object System.Collections.Generic.List[string]
$failures = New-Object System.Collections.Generic.List[string]

foreach ($tfm in $frameworks) {
    $outDir = Join-Path $dist $tfm
    Write-Host "── $tfm ──────────────────────────────────────────"

    if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

    $publishArgs = @(
        'publish', (Join-Path $root 'RimDev.Cli\RimDev.Cli.csproj'),
        '-c', 'Release',
        '-f', $tfm,
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:PublishTrimmed=false',
        # ★ 原生库要打进单文件，否则解压出来的 exe 仍依赖旁边的 coreclr.dll
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        # ★ 不生成 .pdb —— 发布产物是给用户跑的，不是调试的
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-o', $outDir
    ) + $msbuildArgs

    $log = Join-Path $rimdevRoot ("_tmp\publish-$tfm.log")
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $log) | Out-Null

    & dotnet @publishArgs 2>&1 | Tee-Object -FilePath $log | Out-Null
    $code = $LASTEXITCODE

    # ★ 真的数错误与警告（同 build-all 的教训：不能只写死一句「0 警告 0 错误」）
    $errCount = @(Select-String -Path $log -Pattern '\berror [A-Z]+\d+' -ErrorAction SilentlyContinue).Count
    $warnCount = @(Select-String -Path $log -Pattern '\bwarning [A-Z]+\d+' -ErrorAction SilentlyContinue).Count

    $exe = Join-Path $outDir 'rimdev.exe'
    if ($code -ne 0 -or -not (Test-Path $exe)) {
        Write-Host ("  ✗ 发布失败 exit={0} error={1} warning={2}" -f $code, $errCount, $warnCount)
        $failures.Add("$tfm (exit=$code)")
        continue
    }

    # 单文件产物：目录里应该**只有** rimdev.exe（还有可能剩几个运行时文件，列出来备案）
    $others = @(Get-ChildItem $outDir -File | Where-Object { $_.Name -ne 'rimdev.exe' })
    $sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    $sha = (Get-FileHash $exe -Algorithm SHA256).Hash

    Write-Host ("  ✓ rimdev.exe  {0} MB  sha256={1}…" -f $sizeMb, $sha.Substring(0, 16))
    Write-Host ("    error={0} warning={1}  附带文件={2}" -f $errCount, $warnCount, $others.Count)

    if ($errCount -gt 0) { $failures.Add("$tfm (有 $errCount 个 error)") }

    $manifest.Add(("{0}`t{1} MB`tsha256={2}" -f $tfm, $sizeMb, $sha))
}

Write-Host ''

# ── VERSION.txt ────────────────────────────────────────────────────────────
$gitCommit = 'unknown'
$gitDirty = ''
try {
    Push-Location $rimdevRoot
    $gitCommit = (& git rev-parse --short HEAD 2>$null)
    $dirtyLines = @(& git status --porcelain 2>$null)
    if ($dirtyLines.Count -gt 0) { $gitDirty = ' (有未提交改动)' }
    Pop-Location
} catch { }

$versionLines = @(
    'rimdev — RimWorld 静态分析工具链',
    '',
    ("发布工具:   {0}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')),
    ("来源 commit: {0}{1}" -f $gitCommit, $gitDirty),
    ("宿主:       .NET {0}" -f ([System.Environment]::Version)),
    '',
    "产物（各自独立，无需预装运行时）:"
) + $manifest.ToArray()
$versionLines | Set-Content -Path (Join-Path $dist 'VERSION.txt') -Encoding UTF8

Write-Host '── 汇总 ──────────────────────────────────────────'
foreach ($m in $manifest) { Write-Host "  $m" }
if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "失败: $($failures -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host ''
Write-Host "全部成功。入口: $(Join-Path $rimdevRoot 'rimdev.cmd')（会按布局自动选 dist 里的对应产物）"
exit 0
