param([string]$Out = "$PSScriptRoot\..\build\DSPModManager.exe")
$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
$csc  = Join-Path $root "tools\roslyn\tasks\net472\csc.exe"
$src  = Join-Path $root "src"
$fw   = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"

if (-not (Test-Path $csc)) { Write-Host "ERROR: missing csc" -ForegroundColor Red; exit 2 }

$outDir = Split-Path -Parent $Out
if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
if (Test-Path $Out) { Remove-Item $Out -Force }

$sources = @(Get-ChildItem $src -Filter "*.cs" -File | Sort-Object Name)

$cscArgs = @(
  "/nologo","/target:winexe","/platform:x64","/optimize+","/langversion:7.3",
  "/nowarn:1591,0618",
  ("/out:" + $Out),
  ("/reference:" + (Join-Path $fw "System.dll")),
  ("/reference:" + (Join-Path $fw "System.Core.dll")),
  ("/reference:" + (Join-Path $fw "System.Drawing.dll")),
  ("/reference:" + (Join-Path $fw "System.Windows.Forms.dll")),
  ("/reference:" + (Join-Path $fw "mscorlib.dll"))
)
foreach ($s in $sources) { $cscArgs += $s.FullName }

Write-Host ("sources : " + $sources.Count + " -> " + (($sources | ForEach-Object Name) -join ", "))
Write-Host ("output  : " + $Out)
Write-Host ""

& $csc @cscArgs
$code = $LASTEXITCODE
Write-Host ""
if ($code -ne 0) { Write-Host ("BUILD FAILED: csc exit " + $code) -ForegroundColor Red; exit $code }
if (-not (Test-Path $Out)) { Write-Host "BUILD FAILED: no exe produced" -ForegroundColor Red; exit 3 }
$item = Get-Item $Out
Write-Host ("BUILD OK: " + $item.FullName)
Write-Host ("size    : " + $item.Length + " bytes")
Write-Host ("sha256  : " + (Get-FileHash $Out -Algorithm SHA256).Hash)
exit 0