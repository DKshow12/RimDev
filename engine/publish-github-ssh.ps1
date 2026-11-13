# 发布到 GitHub（SSH 通道，Task 27b）
#
# ★ 为什么有这个版本：本机 SSH key 已授权到 DKshow12（实测 ssh -T 回 "Hi DKshow12"），
#   而 DSH 的 git-forge 侧栏授权走 HTTPS token —— 两条路都通，SSH 少一步。
#
# 前置：仓库已在 GitHub 网页上建好（空仓库，不要初始化 README）。
#       建库链接见 _tmp/create-repo-url.txt。
#
# 做的事：
#   1. 自检（身份 / 干净 / 无敏感文件 / 产物存在 / SSH 可用）
#   2. 加 SSH 远端
#   3. push main
#   4. 打 tag 并 push
#   5. 用 gh 或 API 建 Release（无 token 时跳过，只提示手动步骤）
#
# 用法：pwsh -File engine\publish-github-ssh.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$owner = 'DKshow12'
$repo  = 'RimDev'
$tag   = 'v0.1.0'

function Die([string] $msg) { Write-Host "✗ $msg" -ForegroundColor Red; exit 1 }
function Step([string] $msg) { Write-Host "── $msg" -ForegroundColor Cyan }
function Ok([string] $msg)   { Write-Host "  ✓ $msg" -ForegroundColor Green }

Write-Host '=== 发布前自检 ==='

Step '1/5 git 身份'
$name = (& git -C $root config user.name)
$email = (& git -C $root config user.email)
Write-Host "  $name <$email>"
if ($name -ne $owner) { Die "git user.name 应为 $owner，实际 $name" }
Ok '身份正确'

Step '2/5 工作区干净'
$dirty = @(& git -C $root status --porcelain)
if ($dirty.Count -gt 0) {
    $dirty | Select-Object -First 5 | ForEach-Object { Write-Host "    $_" }
    Die "有 $($dirty.Count) 个未提交改动，请先提交"
}
Ok '干净'

Step '3/5 敏感文件'
$tracked = @(& git -C $root ls-files)
$bad = $tracked | Where-Object { $_ -match '^(dist/|_tmp/)' }
if ($bad.Count -gt 0) { Die "不该跟踪：$($bad -join ', ')" }
Ok "$($tracked.Count) 个被跟踪文件，dist/_tmp 均未入库"

Step '4/5 发布产物'
$exe10 = Join-Path $root 'dist\net10.0\rimdev.exe'
$exe8  = Join-Path $root 'dist\net8.0\rimdev.exe'
foreach ($p in @($exe10, $exe8)) {
    if (-not (Test-Path $p)) { Die "缺产物：$p（先跑 engine\publish.ps1）" }
    Ok ("{0}  {1} MB" -f (Split-Path $p -Leaf), [math]::Round((Get-Item $p).Length / 1MB, 1))
}

Step '5/5 SSH 连通性'
$ssh = (& ssh -T -o StrictHostKeyChecking=accept-new -o ConnectTimeout=15 git@github.com 2>&1) -join ' '
if ($ssh -notmatch 'Hi ') { Write-Host "  $ssh"; Die 'SSH 未授权到 GitHub（ssh -T git@github.com 测试失败）' }
Ok "已认证：$($ssh.Split("`n")[0])"

# ── 远端 ──────────────────────────────────────────────────────────────────
Write-Host ''
Write-Host '=== 配置远端 ==='
$remoteUrl = "git@github.com:$owner/$repo.git"
$existing = (& git -C $root remote get-url origin 2>$null)
if ($existing) {
    & git -C $root remote set-url origin $remoteUrl
    Write-Host "  已更新 origin"
} else {
    & git -C $root remote add origin $remoteUrl
    Write-Host "  已添加 origin"
}
Write-Host "  $remoteUrl"

# 探测仓库是否存在（SSH ls-remote）
Write-Host ''
Write-Host '=== 探测远端仓库 ==='
$probe = (& git -C $root ls-remote --heads origin 2>&1) -join "`n"
if ($probe -match 'Could not read from remote repository|does not exist|Repository not found') {
    Write-Host $probe -ForegroundColor Yellow
    Die @"
远端仓库还不存在。请先在浏览器建一个空仓库（不要初始化 README / .gitignore）：

  https://github.com/new?name=$repo&visibility=public

建好后重跑本脚本。
"@
}
Write-Host "  远端可达"

# ── 推送 ──────────────────────────────────────────────────────────────────
Write-Host ''
Write-Host '=== 推送 main ==='
& git -C $root push -u origin main 2>&1 | ForEach-Object { Write-Host "  $_" }
if ($LASTEXITCODE -ne 0) { Die '推送失败' }
Ok 'main 已推送'

Write-Host ''
Write-Host '=== 打 tag 并推送 ==='
$existingTag = (& git -C $root tag -l $tag)
if (-not $existingTag) {
    & git -C $root tag $tag -m "RimDev $tag"
    Write-Host "  已创建 tag $tag"
} else {
    Write-Host "  tag $tag 已存在"
}
& git -C $root push origin $tag 2>&1 | ForEach-Object { Write-Host "  $_" }
Ok "tag $tag 已推送"

# ── Release ───────────────────────────────────────────────────────────────
Write-Host ''
Write-Host '=== Release ==='
$hasGh = (Get-Command gh -ErrorAction SilentlyContinue) -ne $null
$ghAuthed = $false
if ($hasGh) {
    $st = (& gh auth status 2>&1) -join ' '
    $ghAuthed = $st -notmatch 'not logged into any'
}

if ($hasGh -and $ghAuthed) {
    Write-Host "  用 gh 创建 Release 并上传产物…"
    $notes = Join-Path $root '_tmp\release-notes.md'
    $notesArg = if (Test-Path $notes) { @('--notes-file', $notes) } else { @('--notes', "RimDev $tag") }
    & gh release create $tag $exe10 $exe8 --repo "$owner/$repo" --title "RimDev $tag" @notesArg 2>&1 |
        ForEach-Object { Write-Host "  $_" }
    if ($LASTEXITCODE -eq 0) { Ok 'Release 已创建' }
    else { Write-Host '  Release 创建失败（可手动创建）' -ForegroundColor Yellow }
} else {
    Write-Host '  gh 未登录，跳过自动 Release。手动步骤：' -ForegroundColor Yellow
    Write-Host "    1. 打开 https://github.com/$owner/$repo/releases/new?tag=$tag"
    Write-Host "    2. 标题填：RimDev $tag"
    Write-Host "    3. 说明粘 _tmp\release-notes.md 的内容"
    Write-Host "    4. 上传 dist\net10.0\rimdev.exe → 改名 rimdev-net10.exe"
    Write-Host "    5. 上传 dist\net8.0\rimdev.exe  → 改名 rimdev-net8.exe"
}

Write-Host ''
Write-Host "完成：https://github.com/$owner/$repo" -ForegroundColor Green
Write-Host "Release：https://github.com/$owner/$repo/releases/tag/$tag"
