<#
.SYNOPSIS
  Builds self-contained DeskMaster packages (desktop app + CLI) for every platform.
.EXAMPLE
  ./build/publish.ps1                    # all platforms
  ./build/publish.ps1 -Runtimes win-x64  # just Windows x64
#>
param(
    [string[]]$Runtimes = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'),
    [string]$Configuration = 'Release',
    [string]$Output = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts')
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

dotnet test (Join-Path $root 'tests/DeskMaster.Core.Tests') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

foreach ($rid in $Runtimes) {
    $dir = Join-Path $Output "DeskMaster-$rid"
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }

    foreach ($project in 'src/DeskMaster.App', 'src/DeskMaster.Cli') {
        dotnet publish (Join-Path $root $project) -c $Configuration -r $rid --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none `
            -o $dir --nologo
        if ($LASTEXITCODE -ne 0) { throw "Publishing $project for $rid failed." }
    }

    Get-ChildItem $dir -Filter *.pdb | Remove-Item
    Copy-Item (Join-Path $root 'LICENSE'), (Join-Path $root 'README.md') $dir
    $zip = "$dir.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path (Join-Path $dir '*') -DestinationPath $zip
    Write-Host "Packaged $zip"
}
