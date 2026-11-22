# Build CloudCLI 1.1.0 (WebView2 shell) -> dist\CloudCLI.exe + dist\CloudCLI-Setup-1.1.0.exe
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src  = Join-Path $root 'src'
$lib  = Join-Path $root 'lib'
$dist = Join-Path $root 'dist'

Write-Host '[1/3] preparing dist'
Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $dist -Force | Out-Null

Write-Host '[2/3] compiling CloudCLI.exe'
$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }
& $csc /nologo /target:winexe `
    /out:"$dist\CloudCLI.exe" `
    /win32icon:"$src\icon.ico" `
    /win32manifest:"$src\app.manifest" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    /r:"$lib\Microsoft.Web.WebView2.Core.dll" `
    /r:"$lib\Microsoft.Web.WebView2.WinForms.dll" `
    "$src\Program.cs"
if ($LASTEXITCODE -ne 0) { throw "csc failed: $LASTEXITCODE" }
Copy-Item "$lib\*.dll" $dist -Force

Write-Host '[3/3] building installer'
$makensis = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'electron-builder\Cache') -Recurse -Filter makensis.exe -ErrorAction SilentlyContinue |
            Select-Object -First 1
if (-not $makensis) { Write-Warning 'makensis.exe not found - only CloudCLI.exe built'; return }
Copy-Item "$src\installer.nsi" $dist -Force
Copy-Item "$src\icon.ico" $dist -Force
Push-Location $dist
try { & $makensis.FullName /V2 'installer.nsi' } finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw "makensis failed: $LASTEXITCODE" }
Remove-Item "$dist\installer.nsi" -Force -ErrorAction SilentlyContinue

Write-Host ''
Get-ChildItem $dist | Select-Object Name, Length | Format-Table -AutoSize | Out-String | Write-Host
