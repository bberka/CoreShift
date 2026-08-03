# Publish CoreShift as a native AOT executable into .\publish

$ErrorActionPreference = "Stop"

# The ILCompiler locates link.exe via vswhere.exe; make sure it is reachable
# even when Visual Studio is not on the machine-wide PATH.
$vswhereDir = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer"
if (Test-Path (Join-Path $vswhereDir "vswhere.exe")) {
    $env:PATH = "$vswhereDir;$env:PATH"
}

dotnet publish .\CoreShift.slnx -c Release -r win-x64 --self-contained true -o .\publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "Published: $PWD\publish\CoreShift.exe"
Write-Host "Verify with: .\publish\CoreShift.exe --selftest"
