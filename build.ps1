<#
.SYNOPSIS
    Builds the Bookmark Sync program and the extension for each browser into dist\.

.DESCRIPTION
    Copies src\ with the matching manifest into dist\chromium (Chrome and
    Edge) and dist\firefox, and zips each one. Stops if the version in the
    two manifests and the program source do not all match. When the .NET SDK
    is installed it also builds dist\BookmarkSync.exe.
#>

#Requires -Version 5.1

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'

# Reads the version from each place it is kept and checks they agree.
function Get-CheckedVersion {
    $versions = @{}
    foreach ($name in 'chrome', 'firefox') {
        $manifest = Get-Content -Path (Join-Path $root "manifests\$name.json") -Raw | ConvertFrom-Json
        $versions["manifests\$name.json"] = $manifest.version
    }
    $match = Select-String -Path (Join-Path $root 'app\Core\Env.cs') -Pattern 'AppVersion = "([0-9.]+)"' | Select-Object -First 1
    $versions['app\Core\Env.cs'] = $match.Matches[0].Groups[1].Value

    $distinct = @($versions.Values | Sort-Object -Unique)
    if ($distinct.Count -ne 1) {
        $lines = $versions.Keys | ForEach-Object { "  $_ : $($versions[$_])" }
        throw "Versions do not match:`n$($lines -join "`n")"
    }
    return $distinct[0]
}

# Copies the extension files and one browser's manifest into its folder,
# then zips it.
function New-BrowserBuild {
    param([string]$Manifest, [string]$Folder, [string]$Version)
    $target = Join-Path $dist $Folder
    if (Test-Path $target) { Remove-Item -Path $target -Recurse -Force }
    New-Item -Path $target -ItemType Directory | Out-Null
    Copy-Item -Path (Join-Path $root 'src\*') -Destination $target -Recurse
    Copy-Item -Path (Join-Path $root "manifests\$Manifest.json") -Destination (Join-Path $target 'manifest.json')

    $zip = Join-Path $dist "bookmark-sync-$Folder-$Version.zip"
    if (Test-Path $zip) { Remove-Item -Path $zip -Force }
    Compress-Archive -Path (Join-Path $target '*') -DestinationPath $zip
    Write-Host "Built $zip"
}

$version = Get-CheckedVersion
New-BrowserBuild -Manifest 'chrome' -Folder 'chromium' -Version $version
New-BrowserBuild -Manifest 'firefox' -Folder 'firefox' -Version $version

# The program: one exe built from app\BookmarkSync.csproj.
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    & dotnet build (Join-Path $root 'app\BookmarkSync.csproj') -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'The program did not build.' }
    Copy-Item -Path (Join-Path $root 'app\bin\Release\net48\BookmarkSync.exe') -Destination (Join-Path $dist 'BookmarkSync.exe') -Force
    Write-Host "Built $(Join-Path $dist 'BookmarkSync.exe')"
}
else {
    Write-Host 'The .NET SDK is not installed, so BookmarkSync.exe was not built.'
}
