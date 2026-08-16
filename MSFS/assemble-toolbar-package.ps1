param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$sourceRoot = Join-Path $PSScriptRoot 'FlightDeckDashboard'
$compiledRoot = Join-Path $sourceRoot 'Build\Packages\flightdecktools-vr-dashboard'
$assetRoot = Join-Path $sourceRoot 'PackageAssets'
$packageRoot = Join-Path $PSScriptRoot 'Package\flightdecktools-vr-dashboard'

if (-not (Test-Path (Join-Path $compiledRoot 'ingamepanels\flightdecktools-vr-dashboard.spb'))) {
    throw 'Compile the FlightDeckDashboard project with the official MSFS Package Tool before assembling it.'
}
if (-not (Test-Path (Join-Path $compiledRoot 'manifest.json'))) {
    throw 'The compiled MSFS package manifest was not found.'
}

if (Test-Path $packageRoot) {
    $resolvedPackage = [IO.Path]::GetFullPath($packageRoot)
    $resolvedMsfs = [IO.Path]::GetFullPath((Join-Path $ProjectRoot 'MSFS\Package'))
    if (-not $resolvedPackage.StartsWith($resolvedMsfs, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to replace package outside $resolvedMsfs"
    }
    Remove-Item -LiteralPath $resolvedPackage -Recurse -Force
}

New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $compiledRoot 'manifest.json') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $compiledRoot 'ingamepanels') -Destination $packageRoot -Recurse
Copy-Item -LiteralPath (Join-Path $assetRoot 'html_ui') -Destination $packageRoot -Recurse

$files = Get-ChildItem -LiteralPath $packageRoot -Recurse -File |
    Where-Object Name -NotIn @('layout.json', 'manifest.json') |
    Sort-Object FullName

$content = foreach ($file in $files) {
    [ordered]@{
        path = $file.FullName.Substring($packageRoot.Length + 1).Replace('\', '/').ToLowerInvariant()
        size = $file.Length
        date = $file.LastWriteTimeUtc.ToFileTimeUtc()
    }
}

$layout = [ordered]@{ content = @($content) }
$utf8NoBom = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $packageRoot 'layout.json'), ($layout | ConvertTo-Json -Depth 4), $utf8NoBom)

$manifestPath = Join-Path $packageRoot 'manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$manifest.content_type = 'MISC'
$manifest.title = 'VR Optimizer'
$manifest.package_version = '2.0.0'
$manifest.total_package_size = [string](($files | Measure-Object Length -Sum).Sum)
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8), $utf8NoBom)

Write-Output "Assembled $packageRoot with $($files.Count) content files."
