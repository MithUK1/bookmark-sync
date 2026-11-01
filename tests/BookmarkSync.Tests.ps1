<#
.SYNOPSIS
    Tests for the merge, duplicate, backup and message rules in BookmarkSync.ps1.

.DESCRIPTION
    Loads the functions from the shipped script and runs them against a
    scratch folder, so nothing touches the real bookmarks or settings.
    Run with: powershell -File tests\BookmarkSync.Tests.ps1
#>

#Requires -Version 5.1

$ErrorActionPreference = 'Stop'
$env:BOOKMARKSYNC_DIR = Join-Path ([System.IO.Path]::GetTempPath()) ("BookmarkSyncTests-" + [guid]::NewGuid().ToString('N'))
. (Join-Path $PSScriptRoot '..\BookmarkSync.ps1')

$script:failures = 0
$script:passes = 0

# Records one check and prints it only when it fails.
function Assert-Equal {
    param($Expected, $Actual, [string]$Name)
    if ("$Expected" -ceq "$Actual") {
        $script:passes++
    } else {
        $script:failures++
        Write-Host "FAIL $Name`n  expected: $Expected`n  actual:   $Actual"
    }
}

# A bookmark as a browser sends it.
function New-TestBookmark {
    param([string]$Url, [string[]]$Path, [string]$Title = 'Page')
    return [pscustomobject]@{ url = $Url; title = $Title; path = $Path; index = 0 }
}

# A browser snapshot from a list of bookmarks and folders.
function New-TestSnapshot {
    param([object[]]$Bookmarks, [object[]]$Folders = @())
    return [pscustomobject]@{ bookmarks = $Bookmarks; folders = $Folders }
}

# Addresses of the master list, sorted, for easy comparison.
function Get-MasterUrls {
    param($State)
    return (($State.master.bookmarks | ForEach-Object { $_.url }) | Sort-Object) -join ' '
}

$config = (Read-Config).Config

# ---- Address rules ----
Assert-Equal '//example.com/page' (Get-NormalUrl 'https://www.Example.com/page/') 'www, case and trailing slash ignored'
Assert-Equal '//example.com/page' (Get-NormalUrl 'http://example.com:80/page') 'default port ignored'
Assert-Equal '//example.com/a?id=5' (Get-NormalUrl 'https://example.com/a?utm_source=x&id=5&fbclid=1#top') 'tracking and fragment removed'
Assert-Equal '//example.com/a' (Get-NormalUrl 'https://example.com/a?utm_medium=y') 'query dropped when only tracking'
Assert-Equal 'javascript:alert(1)' (Get-NormalUrl ' javascript:alert(1) ') 'other schemes compared as typed'
Assert-Equal '//example.com:8080/x' (Get-NormalUrl 'https://user@example.com:8080/x') 'login removed, custom port kept'

# ---- Duplicates ----
$map = ConvertTo-BookmarkMap @(
    (New-TestBookmark 'https://site.test/' @('other', 'Old')),
    (New-TestBookmark 'https://www.site.test' @('toolbar')),
    (New-TestBookmark 'https://site.test/x' @('other'))
)
$unique = Select-UniqueBookmarks $map
Assert-Equal 2 $unique.Count 'one copy of each address'
Assert-Equal 'toolbar' (@($unique.Values)[0].path -join '/') 'bookmarks bar copy wins'

# ---- First sync of two browsers merges both, removes nothing ----
$state = New-SyncState
$chrome = New-TestSnapshot @(
    (New-TestBookmark 'https://a.test' @('toolbar')),
    (New-TestBookmark 'https://b.test' @('other', 'Work'))
) @(, @('other', 'Work'))
$result = Invoke-BrowserSync -State $state -Browser 'chrome' -Snapshot $chrome -Config $config
Assert-Equal $true $result.Unchanged 'first browser already matches the new master'
Assert-Equal 'https://a.test https://b.test' (Get-MasterUrls $state) 'first browser fills the master'

$edge = New-TestSnapshot @((New-TestBookmark 'https://c.test' @('toolbar')))
$result = Invoke-BrowserSync -State $state -Browser 'edge' -Snapshot $edge -Config $config
Assert-Equal $false $result.Unchanged 'second browser needs changes'
Assert-Equal 'https://a.test https://b.test https://c.test' (Get-MasterUrls $state) 'second browser adds, nothing removed'
Assert-Equal 2 @(Get-ChildItem $Script:BackupDir -Filter '*-auto.json').Count 'automatic backup on each browser''s first sync'

# Edge applies the master and reports back.
$edgeApplied = New-TestSnapshot @($state.master.bookmarks) @($state.master.folders)
Complete-BrowserSync -State $state -Browser 'edge' -Snapshot $edgeApplied -Stats @{ created = 2; deleted = 0; errors = 0 }

# ---- A delete in one browser reaches the master ----
$edgeAfterDelete = New-TestSnapshot @($state.master.bookmarks | Where-Object { $_.url -ne 'https://a.test' }) @($state.master.folders)
$null = Invoke-BrowserSync -State $state -Browser 'edge' -Snapshot $edgeAfterDelete -Config $config
Assert-Equal 'https://b.test https://c.test' (Get-MasterUrls $state) 'delete in edge removes from master'

# Chrome still has a.test from before, but it was not added since its last
# sync, so it must not come back.
$null = Invoke-BrowserSync -State $state -Browser 'chrome' -Snapshot $chrome -Config $config
Assert-Equal 'https://b.test https://c.test' (Get-MasterUrls $state) 'old copy in chrome does not bring a delete back'

# ---- Use as source ----
$state.mirror_from = 'chrome'
$null = Invoke-BrowserSync -State $state -Browser 'chrome' -Snapshot $chrome -Config $config
Assert-Equal 'https://a.test https://b.test' (Get-MasterUrls $state) 'source browser replaces the master'
Assert-Equal 'edge' ($state.pending_overwrite -join ',') 'other browsers marked to change'

$edgeOwn = New-TestSnapshot @((New-TestBookmark 'https://z.test' @('toolbar')))
$null = Invoke-BrowserSync -State $state -Browser 'edge' -Snapshot $edgeOwn -Config $config
Assert-Equal 'https://a.test https://b.test' (Get-MasterUrls $state) 'marked browser takes the master, its own changes ignored'
Assert-Equal '' ($state.pending_overwrite -join ',') 'mark cleared after it is applied'

# ---- State file round trip ----
Save-SyncState $state
$loaded = Read-SyncState
Assert-Equal (Get-MasterUrls $state) (Get-MasterUrls $loaded) 'state survives save and load'
Assert-Equal 'other/Work' (@($loaded.master.folders)[0] -join '/') 'folder paths survive save and load'
Assert-Equal 2 @($loaded.browsers.chrome.last).Count 'last-seen keys survive save and load'

# ---- Backups and restore ----
$name = New-Backup -State $state -Reason 'manual' -IncludeSeen -Config $config
$html = Get-Content (Join-Path $Script:BackupDir "$name.html") -Raw
Assert-Equal $true ($html -match 'PERSONAL_TOOLBAR_FOLDER') 'html backup marks the bookmarks bar'
Assert-Equal $true ($html -match 'HREF="https://a.test"') 'html backup holds the bookmarks'

$state.master.bookmarks.Clear()
Restore-Backup -State $state -Name $name -Config $config
Assert-Equal 'https://a.test https://b.test' (Get-MasterUrls $state) 'restore brings the master back'
Assert-Equal 'chrome,edge' (($state.pending_overwrite | Sort-Object) -join ',') 'restore marks every browser to change'
Assert-Equal 'Backed up by you' (@(Get-BackupList | Where-Object { $_.Name -eq $name })[0].Reason) 'backup list reads the reason'

# ---- Escaping in the html backup ----
$escaped = ConvertTo-BookmarkHtml @([ordered]@{ url = 'https://x.test/?a=1&b=2'; title = 'Tom & Jerry <3'; path = [string[]]@('other'); index = 0 })
Assert-Equal $true ($escaped -match 'Tom &amp; Jerry &lt;3') 'titles are escaped'
Assert-Equal $true ($escaped -match 'a=1&amp;b=2') 'addresses are escaped'

# ---- Messages ----
$stream = New-Object System.IO.MemoryStream
# Accented and symbol characters, written as codes so the file stays plain ASCII.
$accented = 'Caf' + [char]0x00E9 + ' ' + [char]0x2713
Write-HostMessage -Stream $stream -Reply @{ ok = $true; text = $accented }
$stream.Position = 0
$back = Read-HostMessage $stream | ConvertFrom-Json
Assert-Equal $accented $back.text 'message survives length-prefixed framing'

$long = ([string][char]0x00E9 * 900) + ('x' * 1500)
$parts = Split-ReplyParts $long -PartBytes 1000
$bytes = [System.Collections.Generic.List[byte]]::new()
foreach ($part in $parts) { $bytes.AddRange([Convert]::FromBase64String($part)) }
Assert-Equal 4 $parts.Count 'large reply split into pieces'
Assert-Equal $long ([System.Text.Encoding]::UTF8.GetString($bytes.ToArray())) 'pieces join back to the same text'

# ---- Settings ----
Write-FileSafely -Path $Script:ConfigFile -Text '{ "RemoveDuplicates": false, "AutoBackupsToKeep": 2'
$broken = Read-Config
Assert-Equal $true ($null -ne $broken.Problem) 'broken settings file is reported'
Assert-Equal $true $broken.Config.RemoveDuplicates 'broken settings file falls back to defaults'

Write-FileSafely -Path $Script:ConfigFile -Text '{ "RemoveDuplicates": false, "AutoBackupsToKeep": 2 }'
$read = Read-Config
Assert-Equal $false $read.Config.RemoveDuplicates 'saved setting is read'
Assert-Equal 5 $read.Config.AutoBackupsToKeep 'backups kept never drops below 5'

Remove-Item -Path $env:BOOKMARKSYNC_DIR -Recurse -Force
Write-Host "$($script:passes) passed, $($script:failures) failed"
if ($script:failures -gt 0) { exit 1 }
