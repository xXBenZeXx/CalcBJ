# Builds dist\CalcBJ.exe with the C# compiler that ships with Windows (.NET Framework 4.8). No SDK needed.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$fw = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"
$csc = Join-Path $fw "csc.exe"
if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc" }

$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force $dist | Out-Null
$icon = Join-Path $root "assets\app.ico"

if (-not (Test-Path $icon)) {
    $tool = Join-Path $root "tools\bin\MakeIcon.exe"
    New-Item -ItemType Directory -Force (Split-Path $tool) | Out-Null
    & $csc /nologo /target:exe /out:$tool /r:System.Drawing.dll (Join-Path $root "tools\MakeIcon.cs")
    if ($LASTEXITCODE -ne 0) { throw "icon tool build failed" }
    & $tool $icon
}

$sources = Get-ChildItem -Recurse (Join-Path $root "src") -Filter *.cs | ForEach-Object { $_.FullName }
$out = Join-Path $dist "CalcBJ.exe"
# A running copy keeps the EXE locked for writing, but it can still be renamed out of the way.
$old = Join-Path $dist "CalcBJ.old.exe"
if (Test-Path $old) { Remove-Item $old -ErrorAction SilentlyContinue }
if (Test-Path $out) {
    try { [IO.File]::Open($out, 'Open', 'ReadWrite', 'None').Close() }
    catch { if (-not (Test-Path $old)) { Rename-Item $out "CalcBJ.old.exe" } }
}
$manifest = Join-Path $root "assets\app.manifest"
$cscArgs = @("/nologo", "/target:winexe", "/optimize+", "/unsafe", "/platform:x64", "/utf8output", "/codepage:65001",
    "/out:$out", "/win32icon:$icon", "/win32manifest:$manifest",
    "/r:System.dll", "/r:System.Core.dll", "/r:System.Drawing.dll", "/r:System.Windows.Forms.dll",
    "/r:System.Windows.Forms.DataVisualization.dll", "/r:System.Xml.dll") + $sources
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "build failed" }
Write-Host "Built $out ($([math]::Round((Get-Item $out).Length / 1KB)) KB)"
