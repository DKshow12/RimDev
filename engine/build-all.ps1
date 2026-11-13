# 依次构建三个项目并运行自测。退出码非 0 表示失败。
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$projects = @('RimDev.Core\RimDev.Core.csproj', 'RimDev.Cli\RimDev.Cli.csproj', 'RimDev.SelfTest\RimDev.SelfTest.csproj')

foreach ($p in $projects) {
    Write-Host "=== build $p ==="
    dotnet build (Join-Path $root $p) -v:q --nologo
    if ($LASTEXITCODE -ne 0) { Write-Error "build failed: $p"; exit 1 }
}

Write-Host "=== selftest ==="
& (Join-Path $root 'RimDev.SelfTest\bin\Debug\net8.0\rimdev-selftest.exe')
exit $LASTEXITCODE
