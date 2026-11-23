<#
.SYNOPSIS
    Builds the Bookmarks Sync Plus program and the extension for each browser.

.DESCRIPTION
    Copies extension\src\ with the matching manifest into extension\chrome
    (Chrome and Edge) and extension\firefox, and zips each one next to them.
    Stops if the version in the two manifests and the program source do not
    all match. When the .NET SDK is installed it also builds BookmarkSync.exe
    in the top folder.
#>

#Requires -Version 5.1

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$ext = Join-Path $root 'extension'

# Reads the version from each place it is kept and checks they agree.
function Get-CheckedVersion {
    $versions = @{}
    foreach ($name in 'chrome', 'firefox') {
        $manifest = Get-Content -Path (Join-Path $root "extension\manifests\$name.json") -Raw | ConvertFrom-Json
        $versions["extension\manifests\$name.json"] = $manifest.version
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
    $target = Join-Path $ext $Folder
    if (Test-Path $target) { Remove-Item -Path $target -Recurse -Force }
    New-Item -Path $target -ItemType Directory | Out-Null
    Copy-Item -Path (Join-Path $ext 'src\*') -Destination $target -Recurse
    Copy-Item -Path (Join-Path $ext "manifests\$Manifest.json") -Destination (Join-Path $target 'manifest.json')

    $zip = Join-Path $ext "bookmark-sync-plus-$Folder-$Version.zip"
    if (Test-Path $zip) { Remove-Item -Path $zip -Force }
    Compress-Archive -Path (Join-Path $target '*') -DestinationPath $zip
    Write-Host "Built $zip"
}

# The Chrome Web Store refuses a manifest that has a "key" field, so the store zip is the Chrome build without it.
# The folder build (extension\chrome) keeps the key so its id stays fixed for Load unpacked.
function New-StoreBuild {
    param([string]$Version)
    $source = Join-Path $ext 'chrome'
    $target = Join-Path $ext 'chrome-store'
    if (Test-Path $target) { Remove-Item -Path $target -Recurse -Force }
    Copy-Item -Path $source -Destination $target -Recurse
    $manifestPath = Join-Path $target 'manifest.json'
    $manifest = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
    $manifest.PSObject.Properties.Remove('key')
    [System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding($false)))
    $zip = Join-Path $ext "bookmark-sync-plus-chrome-store-$Version.zip"
    if (Test-Path $zip) { Remove-Item -Path $zip -Force }
    Compress-Archive -Path (Join-Path $target '*') -DestinationPath $zip
    Remove-Item -Path $target -Recurse -Force
    Write-Host "Built $zip (no key, for the Chrome Web Store)"
}

$version = Get-CheckedVersion
New-BrowserBuild -Manifest 'chrome' -Folder 'chrome' -Version $version
New-BrowserBuild -Manifest 'firefox' -Folder 'firefox' -Version $version
New-StoreBuild -Version $version

# The program: one exe built from app\BookmarkSync.csproj.
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    & dotnet build (Join-Path $root 'app\BookmarkSync.csproj') -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'The program did not build.' }
    Copy-Item -Path (Join-Path $root 'app\bin\Release\net48\BookmarkSync.exe') -Destination (Join-Path $root 'BookmarkSync.exe') -Force
    Write-Host "Built $(Join-Path $root 'BookmarkSync.exe')"
}
else {
    Write-Host 'The .NET SDK is not installed, so BookmarkSync.exe was not built.'
}
