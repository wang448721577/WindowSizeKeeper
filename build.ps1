# Builds dist\WindowSizeKeeper.exe with the .NET Framework 4.x C# compiler.
# Usage: powershell -ExecutionPolicy Bypass -File build.ps1
param(
    [string]$OutDir = "dist"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (-not (Test-Path -LiteralPath $csc)) {
    throw "csc.exe not found. .NET Framework 4.x is required (it ships with Windows 10/11)."
}

$out = Join-Path $root $OutDir
New-Item -ItemType Directory -Force -Path $out | Out-Null
$exe = Join-Path $out "WindowSizeKeeper.exe"

$cscArgs = @(
    "/nologo"
    "/codepage:65001"
    "/target:winexe"
    "/optimize+"
    "/out:$exe"
    "/win32icon:$(Join-Path $root 'assets\app.ico')"
    "/win32manifest:$(Join-Path $root 'src\app.manifest')"
    "/r:System.dll"
    "/r:System.Windows.Forms.dll"
    "/r:System.Drawing.dll"
    (Join-Path $root "src\Program.cs")
    (Join-Path $root "src\AssemblyInfo.cs")
)

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "build failed with exit code $LASTEXITCODE" }

$info = Get-Item -LiteralPath $exe
Write-Host ("built: {0}  ({1:N0} bytes)" -f $info.FullName, $info.Length)
Write-Host ("version: " + $info.VersionInfo.FileVersion)
