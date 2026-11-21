# 编译并运行离线测试（不触碰真实游戏目录，全部在临时目录里跑）
$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
$csc  = Join-Path $root "tools\roslyn\tasks\net472\csc.exe"
$fw   = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$out  = Join-Path $PSScriptRoot "bin"

if (-not (Test-Path $csc)) { Write-Host "ERROR: Roslyn csc not found: $csc" -ForegroundColor Red; exit 2 }
New-Item -ItemType Directory -Force -Path $out | Out-Null

$refs = @(
  "/reference:$fw\System.dll",
  "/reference:$fw\System.Core.dll",
  "/reference:$fw\System.Windows.Forms.dll",
  "/reference:$fw\System.Drawing.dll",
  "/reference:$fw\mscorlib.dll"
)

$logics = @(
  (Join-Path $root "src\ModManager.cs"),
  (Join-Path $root "src\PluginMetadataReader.cs"),
  (Join-Path $root "src\ModNameLocalizer.cs")
)

$suites = @(
  @{ Name = "CoreTests"; Main = "CoreTests"; Src = "CoreTests.cs" },
  @{ Name = "NameTests"; Main = "NameTests"; Src = "NameTests.cs" }
)

$fail = 0
foreach ($s in $suites) {
  $exe  = Join-Path $out ($s.Name + ".exe")
  $src  = Join-Path $PSScriptRoot $s.Src
  $cscArgs = @("/nologo","/target:exe","/platform:x64","/langversion:7.3","/nowarn:1591,0618",
               "/out:$exe") + @("/main:$($s.Main)") + $refs + $logics + @($src)

  Write-Host ""
  Write-Host "=== $($s.Name) ===" -ForegroundColor Cyan
  & $csc @cscArgs
  if ($LASTEXITCODE -ne 0) {
    Write-Host "BUILD FAILED: $($s.Name)" -ForegroundColor Red
    $fail++
    continue
  }
  & $exe
  if ($LASTEXITCODE -ne 0) { $fail++ }
}

Write-Host ""
if ($fail -eq 0) {
  Write-Host "== ALL TEST SUITES PASS ==" -ForegroundColor Green
  exit 0
}
Write-Host "== $fail SUITE(S) FAILED ==" -ForegroundColor Red
exit 1