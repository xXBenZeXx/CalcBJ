# Rebuilds tools\bin\Oracle.exe from the C# engine and writes the JSON fixtures that the Rust tests compare against.
# Run it again whenever the C# engine (src\Engine) changes.
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = (Resolve-Path (Join-Path $here "..\..")).Path
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc" }

$bin = Join-Path $root "tools\bin"
New-Item -ItemType Directory -Force $bin | Out-Null
$exe = Join-Path $bin "Oracle.exe"
$sources = @(Get-ChildItem (Join-Path $root "src\Engine") -Filter *.cs | ForEach-Object { $_.FullName }) + @(Join-Path $root "tools\Oracle.cs")
$cscArgs = @("/nologo", "/target:exe", "/optimize+", "/unsafe", "/platform:x64", "/out:$exe", "/r:System.dll", "/r:System.Core.dll") + $sources
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "Oracle build failed" }

& $exe (Join-Path $here "tests\fixtures")
if ($LASTEXITCODE -ne 0) { throw "Oracle failed" }
