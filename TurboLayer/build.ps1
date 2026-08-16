param(
    [string]$ZigPath = (Join-Path $env:TEMP 'zig-0.16.0\zig.exe')
)

$ErrorActionPreference = 'Stop'
$sourceDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDirectory = Join-Path $sourceDirectory 'bin'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

if (-not (Test-Path -LiteralPath $ZigPath)) {
    throw 'Zig 0.16.0 was not found. Pass -ZigPath with the path to a trusted Zig executable.'
}

& $ZigPath c++ -std=c++17 -O2 -shared `
    (Join-Path $sourceDirectory 'turbo_layer.cpp') `
    -o (Join-Path $outputDirectory 'VR_Optimizer_Turbo_Layer.dll')
if ($LASTEXITCODE -ne 0) { throw "Turbo layer compilation failed with exit code $LASTEXITCODE." }

Remove-Item -LiteralPath (Join-Path $outputDirectory 'turbo_layer.lib') -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $outputDirectory 'VR_Optimizer_Turbo_Layer.pdb') -ErrorAction SilentlyContinue

Copy-Item -LiteralPath (Join-Path $sourceDirectory 'VR_Optimizer_Turbo_Layer.json') -Destination $outputDirectory -Force
Copy-Item -LiteralPath (Join-Path $sourceDirectory 'LICENSES.md') -Destination $outputDirectory -Force
