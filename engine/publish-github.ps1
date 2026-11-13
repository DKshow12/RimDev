# 发布到 GitHub（Task 27）
#
# 前置：DSH 侧栏「Git Forge」已把 DKshow12 账号授权给本项目。
#       本脚本会先自检授权，未授权则直接退出并提示怎么授权。
#
# 做的事：
#   1. 自检：git 身份 / 工作区干净 / 无敏感文件 / dist 产物存在
#   2. 建公开仓库 DKshow12/RimDev（用 GitHub API，需要 token）
#   3. 加远端并 push main
#   4. 建 v0.1.0 Release 并上传两个 exe
#
# ★ token 从哪来：DSH 的 git-forge credential helper 不对外暴露 token。
#   本脚本用 `git credential fill` 向 helper 索取 —— 这样 token 只存在于
#   这个进程的内存里，不会写进任何文件、也不会打印。

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot   # _rimdev
$owner = 'DKshow12'
$repo  = 'RimDev'
$tag   = 'v0.1.0'

function Die([string] $msg) { Write-Host "❌ $msg" -ForegroundColor Red; exit 1 }
function Step([string] $msg) { Write-Host "── $msg" -ForegroundColor Cyan }

Write-Host '=== 发布前自检 ==='

Step '1/5 git 身份'
$name = (& git -C $root config user.name)
$email = (& git -C $root config user.email)
Write-Host "  $name <$email>"
if ($name -ne $owner) { Die "git user.name 应为 $owner，实际 $name" }

Step '2/5 工作区干净'
$dirty = @(& git -C $root status --porcelain)
if ($dirty.Count -gt 0) {
    Write-Host "  有 $($dirty.Count) 个未提交改动："
    $dirty | Select-Object -First 5 | ForEach-Object { Write-Host "    $_" }
    Die '请先提交或清理后再发布'
}
Write-Host '  ✓ 干净'

Step '3/5 敏感文件'
$tracked = @(& git -C $root ls-files)
$bad = $tracked | Where-Object { $_ -match '^(dist/|_tmp/)' }
if ($bad.Count -gt 0) { Die "不该跟踪的文件出现在仓库里：$($bad -join ', ')" }
Write-Host "  ✓ $($tracked.Count) 个被跟踪文件，dist/_tmp 均未入库"

Step '4/5 发布产物'
$exe10 = Join-Path $root 'dist\net10.0\rimdev.exe'
$exe8  = Join-Path $root 'dist\net8.0\rimdev.exe'
foreach ($p in @($exe10, $exe8)) {
    if (-not (Test-Path $p)) { Die "缺产物：$p（先跑 engine\publish.ps1）" }
    $mb = [math]::Round((Get-Item $p).Length / 1MB, 1)
    Write-Host ("  ✓ {0}  {1} MB" -f (Split-Path $p -Leaf), $mb)
}

Step '5/5 取凭据'
# 向 git credential helper 索取 token（DSH git-forge 提供）。
# 用 git credential fill：标准输入给协议/主机，标准输出拿到 username/password。
$credInput = "protocol=https`nhost=github.com`n`n"
$credOut = $credInput | & git -C $root credential fill 2>&1
$token = ($credOut | Where-Object { $_ -like 'password=*' }) -replace '^password=', ''
if ([string]::IsNullOrWhiteSpace($token)) {
    Write-Host $credOut
    Die @'
拿不到 GitHub token。请先在 DSH 侧栏「Git Forge」标签页把 DKshow12 账号授权给本项目，
然后重跑本脚本。（enforcePush=true 会拦截未授权推送。）
'@
}
Write-Host "  ✓ 已取得 token（长度 $($token.Length)，内容不显示）"

# ── 建仓库 ────────────────────────────────────────────────────────────────
Write-Host ''
Write-Host '=== 创建公开仓库 ==='

$headers = @{
    Authorization          = "Bearer $token"
    Accept                 = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent'           = 'RimDev-publisher'
}

# 先看是否已存在
try {
    $existing = Invoke-RestMethod -Uri "https://api.github.com/repos/$owner/$repo" `
        -Headers $headers -Method Get -ErrorAction Stop
    Write-Host "  仓库已存在：$($existing.html_url)（继续推送）"
} catch {
    if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw }
    $body = @{
        name        = $repo
        description = '环世界（RimWorld）Mod 开发的静态分析工具链：不启动游戏就能查出补丁打空、Def 引用错误、加载失败。65 个诊断码，392 个自测用例。'
        private     = $false
        has_issues  = $true
        has_wiki    = $false
    } | ConvertTo-Json
    $created = Invoke-RestMethod -Uri 'https://api.github.com/user/repos' `
        -Headers $headers -Method Post -Body $body -ContentType 'application/json'
    Write-Host "  ✓ 已创建：$($created.html_url)" -ForegroundColor Green
}

# ── 加远端并推送 ──────────────────────────────────────────────────────────
Write-Host ''
Write-Host '=== 推送 ==='

$remoteUrl = "https://github.com/$owner/$repo.git"
$existingRemote = (& git -C $root remote get-url origin 2>$null)
if ($existingRemote) {
    & git -C $root remote set-url origin $remoteUrl
} else {
    & git -C $root remote add origin $remoteUrl
}
Write-Host "  origin → $remoteUrl"

& git -C $root push -u origin main 2>&1 | ForEach-Object { Write-Host "  $_" }
if ($LASTEXITCODE -ne 0) { Die '推送失败' }
Write-Host '  ✓ main 已推送' -ForegroundColor Green

# 推送 tag
& git -C $root tag $tag -m "RimDev $tag" 2>$null
& git -C $root push origin $tag 2>&1 | ForEach-Object { Write-Host "  $_" }

# ── 建 Release ────────────────────────────────────────────────────────────
Write-Host ''
Write-Host '=== 创建 Release ==='

$notesPath = Join-Path $root '_tmp\release-notes.md'
$notes = if (Test-Path $notesPath) { Get-Content $notesPath -Raw -Encoding UTF8 } else { "RimDev $tag" }

$relBody = @{
    tag_name = $tag
    name     = "RimDev $tag"
    body     = $notes
    draft    = $false
    prerelease = $false
} | ConvertTo-Json

try {
    $rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$owner/$repo/releases" `
        -Headers $headers -Method Post -Body $relBody -ContentType 'application/json'
    Write-Host "  ✓ Release: $($rel.html_url)" -ForegroundColor Green
} catch {
    Write-Host "  Release 已存在或创建失败，尝试查询已有的…"
    $rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$owner/$repo/releases/tags/$tag" -Headers $headers
    Write-Host "  复用：$($rel.html_url)"
}

# 上传两个 exe
$uploadBase = $rel.upload_url -replace '\{.*\}$', ''
$assets = @(
    @{ path = $exe10; name = 'rimdev-net10.exe'; label = '1.7.x 线（Waylith / net10）' },
    @{ path = $exe8;  name = 'rimdev-net8.exe';  label = '1.6（Unity / net472）' }
)
foreach ($a in $assets) {
    $bytes = [IO.File]::ReadAllBytes($a.path)
    $url = "$uploadBase?name=$($a.name)&label=$([uri]::EscapeDataString($a.label))"
    try {
        $up = Invoke-RestMethod -Uri $url -Headers $headers -Method Post -Body $bytes `
            -ContentType 'application/octet-stream'
        Write-Host "  ✓ 上传 $($a.name)  ($([math]::Round($up.size/1MB,1)) MB)" -ForegroundColor Green
    } catch {
        Write-Host "  ✗ 上传 $($a.name) 失败：$($_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Host ''
Write-Host "完成：https://github.com/$owner/$repo" -ForegroundColor Green
Write-Host "Release：https://github.com/$owner/$repo/releases/tag/$tag"
