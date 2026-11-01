<#
.SYNOPSIS
    Bookmark Sync - keeps bookmarks the same in Chrome, Edge and Firefox.

.DESCRIPTION
    One script does three jobs:

      - Run it yourself and it installs or upgrades itself for the current
        user, then opens the Bookmark Sync window (browsers, backups,
        Settings).
      - Each browser's Bookmark Sync extension starts it with -NativeHost
        when bookmarks change. It answers that one request over standard
        input and output, merges the browser's bookmarks into the master
        list on this computer, and exits. Nothing keeps running in the
        background.
      - Backups of the master list are written as .html (imports into any
        browser) and .json (restorable from the window).

    Everything lives under %LOCALAPPDATA%\BookmarkSync. No admin rights,
    no extra modules, nothing sent anywhere.

.NOTES
    Requires: Windows PowerShell 5.1 or newer, and the Bookmark Sync
    extension in each browser you want kept in step.
#>

#Requires -Version 5.1

param(
    # Started by a browser to answer one request over standard input and output.
    [switch]$NativeHost
)

# ============================================================================
# Region: App identity and file locations
# ============================================================================

$Script:AppTitle   = 'Bookmark Sync'
$Script:AppVersion = '0.1.0'
$Script:VersionTag = "v$($Script:AppVersion) - MUK1"

# The name each browser uses to find this helper, and the extensions allowed
# to talk to it. The Chrome and Edge id is fixed by the public key in the
# extension's manifest, so it is the same on every computer.
$Script:HostName           = 'com.muk1.bookmarksync'
$Script:ChromiumExtensionId = 'kmajebfppgghbdedpfacpckkimnclejj'
$Script:FirefoxExtensionId  = 'bookmark-sync@muk1'

# BOOKMARKSYNC_DIR lets the tests point everything at a scratch folder.
if ($env:BOOKMARKSYNC_DIR) {
    $Script:DataDir = $env:BOOKMARKSYNC_DIR
} else {
    $Script:DataDir = Join-Path $env:LOCALAPPDATA 'BookmarkSync'
}

$Script:StateFile       = Join-Path $Script:DataDir 'state.json'
$Script:ConfigFile      = Join-Path $Script:DataDir 'config.json'
$Script:BackupDir       = Join-Path $Script:DataDir 'Backups'
$Script:PlanDir         = Join-Path $Script:DataDir 'Plans'
$Script:LogPath         = Join-Path $Script:DataDir 'BookmarkSync.log'
$Script:InstalledScript = Join-Path $Script:DataDir 'BookmarkSync.ps1'
$Script:LauncherFile    = Join-Path $Script:DataDir 'BookmarkSyncHost.cmd'
$Script:IconFile        = Join-Path $Script:DataDir 'BookmarkSync.ico'
$Script:LogMaxLines     = 1000

# Where each browser looks for native helpers registered for this user.
$Script:HostRegistryKeys = @(
    @{ Browser = 'Chrome';  Key = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$($Script:HostName)";  Manifest = 'host-chromium.json' },
    @{ Browser = 'Edge';    Key = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$($Script:HostName)"; Manifest = 'host-chromium.json' },
    @{ Browser = 'Firefox'; Key = "HKCU:\Software\Mozilla\NativeMessagingHosts\$($Script:HostName)";        Manifest = 'host-firefox.json' }
)

# Characters that never appear in a folder name or address, used to build
# lookup keys from a folder path plus an address.
$Script:PathSep = [string][char]0x1f
$Script:KeySep  = [string][char]0x1e

# A new automatic backup is taken at most this often (seconds) while
# browsers are changing things.
$Script:AutoBackupGap = 600

# Native messages from the helper to a browser are capped at 1 MB, so large
# replies are sent in pieces of this many bytes (before base64).
$Script:PartBytes = 700000

$Script:BrowserNames = @{ chrome = 'Chrome'; edge = 'Edge'; firefox = 'Firefox' }

$Script:DefaultConfig = [ordered]@{
    RemoveDuplicates  = $true
    AutoBackupsToKeep = 40
    WindowLeft        = -1
    WindowTop         = -1
}

# ============================================================================
# Region: Log
# ============================================================================

# Appends one line to the app log and trims it now and then. Logging must
# never stop a sync, so every failure here is swallowed.
function Write-AppLog {
    param([Parameter(Mandatory)][string]$Message)

    try {
        if (-not (Test-Path $Script:DataDir)) { New-Item -Path $Script:DataDir -ItemType Directory -Force | Out-Null }
        $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
        Add-Content -Path $Script:LogPath -Value "[$timestamp] $Message" -Encoding UTF8

        # Trim only when well over the cap, not on every write.
        $existing = Get-Content -Path $Script:LogPath -ErrorAction SilentlyContinue
        if ($existing.Count -gt ($Script:LogMaxLines + 200)) {
            $existing | Select-Object -Last $Script:LogMaxLines | Set-Content -Path $Script:LogPath -Encoding UTF8
        }
    } catch {
        return
    }
}

# Returns the last lines of the log for the window.
function Get-RecentAppLog {
    param([int]$Lines = 200)
    if (-not (Test-Path $Script:LogPath)) { return @() }
    Get-Content -Path $Script:LogPath -Tail $Lines -ErrorAction SilentlyContinue
}

# ============================================================================
# Region: Files and settings
# ============================================================================

# Writes text to a temporary file, checks it reads back the same, then
# swaps it in. A crash part way through never leaves a half-written file.
function Write-FileSafely {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text
    )
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path $dir)) { New-Item -Path $dir -ItemType Directory -Force | Out-Null }

    $temp = "$Path.tmp"
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($temp, $Text, $utf8)
    if ([System.IO.File]::ReadAllText($temp, $utf8) -ne $Text) {
        throw "Could not write $Path, the copy did not read back correctly."
    }
    Move-Item -Path $temp -Destination $Path -Force
}

# Turns null, a single item or a collection into a list, so callers never
# have to worry about how JSON handed it over.
function ConvertTo-List {
    param($Items)
    $list = [System.Collections.Generic.List[object]]::new()
    if ($null -eq $Items) { return ,$list }
    if ($Items -is [string]) { $list.Add($Items); return ,$list }
    foreach ($item in $Items) { if ($null -ne $item) { $list.Add($item) } }
    return ,$list
}

# Reads config.json over the defaults. A missing or broken file never stops
# start-up: the defaults are used and the problem is reported back.
function Read-Config {
    $config = [ordered]@{}
    foreach ($name in $Script:DefaultConfig.Keys) { $config[$name] = $Script:DefaultConfig[$name] }
    $problem = $null

    if (Test-Path $Script:ConfigFile) {
        try {
            $saved = Get-Content -Path $Script:ConfigFile -Raw -Encoding UTF8 | ConvertFrom-Json
            foreach ($name in $Script:DefaultConfig.Keys) {
                $property = $saved.PSObject.Properties[$name]
                if ($property -and $null -ne $property.Value) {
                    $default = $Script:DefaultConfig[$name]
                    if ($default -is [bool]) {
                        $config[$name] = [bool]$property.Value
                    } else {
                        $config[$name] = [int]$property.Value
                    }
                }
            }
        } catch {
            $problem = "The settings file could not be read, so the defaults are in use. $($_.Exception.Message)"
        }
    }

    if ($config.AutoBackupsToKeep -lt 5) { $config.AutoBackupsToKeep = 5 }
    if ($config.AutoBackupsToKeep -gt 500) { $config.AutoBackupsToKeep = 500 }
    return @{ Config = $config; Problem = $problem }
}

# Saves the settings in the same order as the defaults.
function Save-Config {
    param([Parameter(Mandatory)]$Config)
    Write-FileSafely -Path $Script:ConfigFile -Text ($Config | ConvertTo-Json)
}

# A blank master list and no browsers yet.
function New-SyncState {
    return @{
        master           = @{ bookmarks = (ConvertTo-List $null); folders = (ConvertTo-List $null) }
        browsers         = @{}
        pending_overwrite = (ConvertTo-List $null)
        mirror_from      = $null
        last_auto_backup = 0
    }
}

# Loads state.json into plain hashtables and lists. The file is only ever
# written by this script, so a file that will not read is a real fault and
# is passed up rather than silently replaced.
function Read-SyncState {
    $state = New-SyncState
    if (-not (Test-Path $Script:StateFile)) { return $state }

    $raw = Get-Content -Path $Script:StateFile -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($raw.master) {
        $state.master.bookmarks = ConvertTo-List $raw.master.bookmarks
        $state.master.folders = ConvertTo-List $raw.master.folders
    }
    if ($raw.browsers) {
        foreach ($property in $raw.browsers.PSObject.Properties) {
            $entry = $property.Value
            $state.browsers[$property.Name] = @{
                last         = $(if ($null -eq $entry.last) { $null } else { ConvertTo-List $entry.last })
                last_folders = $(if ($null -eq $entry.last_folders) { $null } else { ConvertTo-List $entry.last_folders })
                seen         = [double]$entry.seen
                snapshot     = $entry.snapshot
            }
        }
    }
    $state.pending_overwrite = ConvertTo-List $raw.pending_overwrite
    $state.mirror_from = $raw.mirror_from
    if ($raw.last_auto_backup) { $state.last_auto_backup = [double]$raw.last_auto_backup }
    return $state
}

# Saves the state as compact JSON.
function Save-SyncState {
    param([Parameter(Mandatory)]$State)
    Write-FileSafely -Path $Script:StateFile -Text (ConvertTo-Json -InputObject $State -Depth 12 -Compress)
}

# Runs a block while holding a lock shared by every copy of this script, so
# two browsers syncing at the same moment, or the window and a browser,
# never write the state over each other.
function Invoke-WithStateLock {
    param([Parameter(Mandatory)][scriptblock]$Action)
    $mutex = New-Object System.Threading.Mutex($false, 'BookmarkSyncState')
    $held = $false
    try {
        try {
            $held = $mutex.WaitOne(30000)
        } catch [System.Threading.AbandonedMutexException] {
            # The last holder exited without letting go. The lock is ours now.
            $held = $true
        }
        if (-not $held) { throw 'Bookmark Sync is busy with another browser, try again in a moment.' }
        & $Action
    } finally {
        if ($held) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

# Copies data from the old Bookmark Hub location the first time, so nothing
# set up with the earlier version is lost. Copies only, never moves.
function Copy-LegacyData {
    if (-not $env:USERPROFILE) { return }
    $oldDir = Join-Path $env:USERPROFILE 'BookmarkHub'
    $oldState = Join-Path $oldDir 'state.json'
    if ((Test-Path $oldState) -and -not (Test-Path $Script:StateFile)) {
        if (-not (Test-Path $Script:DataDir)) { New-Item -Path $Script:DataDir -ItemType Directory -Force | Out-Null }
        Copy-Item -Path $oldState -Destination $Script:StateFile
        $oldBackups = Join-Path $oldDir 'backups'
        if (Test-Path $oldBackups) {
            if (-not (Test-Path $Script:BackupDir)) { New-Item -Path $Script:BackupDir -ItemType Directory -Force | Out-Null }
            Copy-Item -Path (Join-Path $oldBackups '*') -Destination $Script:BackupDir -Force
        }
        Write-AppLog "Copied bookmarks and backups from the earlier Bookmark Hub folder"
    }
}

# ============================================================================
# Region: Bookmark rules
#
# These match the extension's own copy in background.js. Both sides must
# turn an address into the same key, so change them together.
# ============================================================================

# Reduces an address to the part that decides whether two bookmarks are the
# same page: ignores http/https, letter case in the host, "www.", default
# ports, trailing slashes, tracking parameters and the #fragment. Anything
# that is not a web address is compared as typed.
function Get-NormalUrl {
    param([AllowEmptyString()][string]$Url)

    $text = $Url.Trim()
    $match = [regex]::Match($text, '^(?i)(https?)://([^/?#]*)([^?#]*)(\?[^#]*)?')
    if (-not $match.Success) { return $text }

    $hostPart = $match.Groups[2].Value.ToLowerInvariant()
    $hostPart = $hostPart.Substring($hostPart.LastIndexOf('@') + 1)
    if ($hostPart.StartsWith('www.')) { $hostPart = $hostPart.Substring(4) }
    $hostPart = $hostPart -replace ':(80|443)$', ''

    $path = $match.Groups[3].Value.TrimEnd('/')

    $query = ''
    if ($match.Groups[4].Success) {
        $kept = [System.Collections.Generic.List[string]]::new()
        foreach ($pair in $match.Groups[4].Value.Substring(1).Split('&')) {
            if ($pair -eq '') { continue }
            $name = $pair.Split('=')[0].ToLowerInvariant()
            if ($name -notmatch '^(utm_|fbclid$|gclid$|mc_eid$)') { $kept.Add($pair) }
        }
        if ($kept.Count -gt 0) { $query = '?' + ($kept -join '&') }
    }
    return "//$hostPart$path$query"
}

# One bookmark's identity: the folder it is in plus its normalised address.
function Get-BookmarkKey {
    param([Parameter(Mandatory)]$Bookmark)
    return ((ConvertTo-List $Bookmark.path) -join $Script:PathSep) + $Script:KeySep + (Get-NormalUrl $Bookmark.url)
}

# Builds an ordered key-to-bookmark map. The first copy of a key wins, so
# two identical bookmarks in the same folder count once.
function ConvertTo-BookmarkMap {
    param($Bookmarks)
    $map = [ordered]@{}
    foreach ($bookmark in (ConvertTo-List $Bookmarks)) {
        $key = Get-BookmarkKey $bookmark
        if (-not $map.Contains($key)) {
            $map[$key] = [ordered]@{
                url   = [string]$bookmark.url
                title = [string]$bookmark.title
                path  = [string[]](ConvertTo-List $bookmark.path).ToArray()
                index = [int]$bookmark.index
            }
        }
    }
    return ,$map
}

# Builds a joined-path-to-folder map from a list of folder paths.
function ConvertTo-FolderMap {
    param($Folders)
    $map = @{}
    foreach ($folder in (ConvertTo-List $Folders)) {
        $path = [string[]](ConvertTo-List $folder).ToArray()
        if ($path.Count -gt 0) { $map[$path -join $Script:PathSep] = $path }
    }
    return ,$map
}

# Keeps only bookmarks and folders under the two areas that sync (bookmarks
# bar and other bookmarks), in a known shape, whatever the browser sent.
function ConvertTo-CleanSnapshot {
    param($Snapshot)
    $bookmarks = [System.Collections.Generic.List[object]]::new()
    foreach ($bookmark in (ConvertTo-List $Snapshot.bookmarks)) {
        $path = ConvertTo-List $bookmark.path
        if ($bookmark.url -is [string] -and $path.Count -gt 0 -and ($path[0] -eq 'toolbar' -or $path[0] -eq 'other')) {
            $bookmarks.Add([ordered]@{
                url   = $bookmark.url
                title = [string]$bookmark.title
                path  = [string[]]$path.ToArray()
                index = [int]$bookmark.index
            })
        }
    }
    $folders = [System.Collections.Generic.List[object]]::new()
    foreach ($folder in (ConvertTo-List $Snapshot.folders)) {
        $path = ConvertTo-List $folder
        if ($path.Count -gt 0 -and ($path[0] -eq 'toolbar' -or $path[0] -eq 'other')) {
            $folders.Add([string[]]$path.ToArray())
        }
    }
    return @{ bookmarks = $bookmarks; folders = $folders }
}

# Where a bookmark sits, best first: loose on the bookmarks bar, in a bar
# folder, loose in other bookmarks, in an other-bookmarks folder.
function Get-LocationRank {
    param([Parameter(Mandatory)]$Bookmark)
    $depth = $Bookmark.path.Count
    if ($Bookmark.path[0] -eq 'toolbar') {
        if ($depth -eq 1) { return 0 }
        return 1
    }
    if ($depth -eq 1) { return 2 }
    return 3
}

# Keeps one copy of each address, in the best location by Get-LocationRank.
# Ties keep the first one seen.
function Select-UniqueBookmarks {
    param([Parameter(Mandatory)]$Map)
    $best = @{}
    foreach ($key in $Map.Keys) {
        $urlKey = Get-NormalUrl $Map[$key].url
        if (-not $best.ContainsKey($urlKey) -or (Get-LocationRank $Map[$key]) -lt (Get-LocationRank $Map[$best[$urlKey]])) {
            $best[$urlKey] = $key
        }
    }
    $keep = @{}
    foreach ($key in $best.Values) { $keep[$key] = $true }

    $result = [ordered]@{}
    foreach ($key in $Map.Keys) {
        if ($keep.ContainsKey($key)) { $result[$key] = $Map[$key] }
    }
    return ,$result
}

# Makes sure every folder that holds a bookmark is in the folder list, so a
# folder is never dropped while something is still in it.
function Add-ParentFolders {
    param(
        [Parameter(Mandatory)]$Map,
        [Parameter(Mandatory)]$Folders
    )
    foreach ($bookmark in $Map.Values) {
        for ($depth = 2; $depth -le $bookmark.path.Count; $depth++) {
            $path = [string[]]$bookmark.path[0..($depth - 1)]
            $Folders[$path -join $Script:PathSep] = $path
        }
    }
}

# True when two key collections hold exactly the same keys.
function Test-SameKeys {
    param($First, $Second)
    $a = @($First)
    $b = @($Second)
    if ($a.Count -ne $b.Count) { return $false }
    $lookup = @{}
    foreach ($key in $a) { $lookup[$key] = $true }
    foreach ($key in $b) { if (-not $lookup.ContainsKey($key)) { return $false } }
    return $true
}

# Returns a browser's record, creating it the first time it syncs.
function Get-BrowserEntry {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)][string]$Browser
    )
    if (-not $State.browsers.ContainsKey($Browser)) {
        $State.browsers[$Browser] = @{ last = $null; last_folders = $null; seen = 0; snapshot = $null }
    }
    return $State.browsers[$Browser]
}

# Seconds since 1970, the time format kept in the state file.
function Get-UnixTime {
    return [double][DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
}

# Lists a map's values sorted by folder path, for a tidy state file.
function Get-SortedFolders {
    param([Parameter(Mandatory)]$Folders)
    $list = [System.Collections.Generic.List[object]]::new()
    foreach ($key in ($Folders.Keys | Sort-Object)) { $list.Add($Folders[$key]) }
    return ,$list
}

# Merges one browser's current bookmarks into the master list.
#
# First sync: everything the browser has is added, nothing is removed.
# After that: what was added or removed in the browser since its last sync
# is added to or removed from the master. A browser marked to be overwritten
# gives up its own changes and takes the master as it is. A browser chosen
# as the source replaces the master outright.
#
# Returns Unchanged when the browser already matches the master, in which
# case its sync is recorded here and nothing needs sending back.
function Invoke-BrowserSync {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)][string]$Browser,
        [Parameter(Mandatory)]$Snapshot,
        [Parameter(Mandatory)]$Config
    )

    $snap = ConvertTo-CleanSnapshot $Snapshot
    $entry = Get-BrowserEntry $State $Browser
    $current = ConvertTo-BookmarkMap $snap.bookmarks
    $currentFolders = ConvertTo-FolderMap $snap.folders
    $entry.snapshot = @{ bookmarks = @($current.Values); folders = @($currentFolders.Values) }
    $entry.seen = Get-UnixTime

    $master = ConvertTo-BookmarkMap $State.master.bookmarks
    $masterFolders = ConvertTo-FolderMap $State.master.folders
    $oldKeys = @($master.Keys)
    $firstSync = $null -eq $entry.last
    $name = $Script:BrowserNames[$Browser]

    if ($State.pending_overwrite.Contains($Browser)) {
        [void]$State.pending_overwrite.Remove($Browser)
        $note = 'changed to match the master list'
    } elseif ($State.mirror_from -eq $Browser) {
        New-Backup -State $State -Reason 'before-source' -IncludeSeen -Config $Config | Out-Null
        $master = [ordered]@{}
        foreach ($key in $current.Keys) { $master[$key] = $current[$key] }
        $masterFolders = @{}
        foreach ($key in $currentFolders.Keys) { $masterFolders[$key] = $currentFolders[$key] }
        $State.mirror_from = $null
        $State.pending_overwrite = ConvertTo-List ($State.browsers.Keys | Where-Object { $_ -ne $Browser })
        $note = 'used as the source, the other browsers will be changed to match'
    } else {
        $last = @{}
        foreach ($key in (ConvertTo-List $entry.last)) { $last[$key] = $true }
        $lastFolders = ConvertTo-FolderMap $entry.last_folders

        $added = 0
        $removed = 0
        foreach ($key in $current.Keys) {
            if (-not $last.ContainsKey($key)) {
                if (-not $master.Contains($key)) { $master[$key] = $current[$key] }
                $added++
            }
        }
        foreach ($key in $last.Keys) {
            if (-not $current.Contains($key)) {
                if ($master.Contains($key)) { $master.Remove($key) }
                $removed++
            }
        }
        foreach ($key in $currentFolders.Keys) {
            if (-not $lastFolders.ContainsKey($key)) { $masterFolders[$key] = $currentFolders[$key] }
        }
        foreach ($key in $lastFolders.Keys) {
            if (-not $currentFolders.ContainsKey($key)) { $masterFolders.Remove($key) }
        }
        $note = "$added added and $removed removed in this browser"
    }

    if ($Config.RemoveDuplicates) { $master = Select-UniqueBookmarks $master }
    Add-ParentFolders -Map $master -Folders $masterFolders

    $changed = -not (Test-SameKeys $master.Keys $oldKeys) -or -not (Test-SameKeys $master.Keys $current.Keys)
    $stale = (Get-UnixTime) - $State.last_auto_backup -gt $Script:AutoBackupGap
    if ($changed -and ($firstSync -or $stale)) {
        New-Backup -State $State -Reason 'auto' -IncludeSeen -Config $Config | Out-Null
        $State.last_auto_backup = Get-UnixTime
    }

    $State.master = @{ bookmarks = (ConvertTo-List $master.Values); folders = (Get-SortedFolders $masterFolders) }
    Write-AppLog "${name}: sync, $note, master list now $($master.Count)"

    $unchanged = (Test-SameKeys $master.Keys $current.Keys) -and (Test-SameKeys $masterFolders.Keys $currentFolders.Keys)
    if ($unchanged) {
        $entry.last = ConvertTo-List $current.Keys
        $entry.last_folders = ConvertTo-List $currentFolders.Values
    }
    return @{ Unchanged = $unchanged }
}

# Records what a browser looks like after it has applied the master list,
# so its next sync can tell what the user changed since.
function Complete-BrowserSync {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)][string]$Browser,
        [Parameter(Mandatory)]$Snapshot,
        $Stats
    )
    $snap = ConvertTo-CleanSnapshot $Snapshot
    $entry = Get-BrowserEntry $State $Browser
    $current = ConvertTo-BookmarkMap $snap.bookmarks
    $entry.last = ConvertTo-List $current.Keys
    $entry.last_folders = $snap.folders
    $entry.snapshot = @{ bookmarks = @($current.Values); folders = $snap.folders }
    $entry.seen = Get-UnixTime

    if ($Stats -and ($Stats.created -or $Stats.deleted -or $Stats.errors)) {
        Write-AppLog "$($Script:BrowserNames[$Browser]): applied, $([int]$Stats.created) added, $([int]$Stats.deleted) removed, $([int]$Stats.errors) could not be changed"
    }
}

# ============================================================================
# Region: Backups
# ============================================================================

# Writes the bookmarks as a standard bookmarks .html file, the format every
# browser can import.
function ConvertTo-BookmarkHtml {
    param($Bookmarks)

    $tree = @{ items = ([System.Collections.Generic.List[object]]::new()); subs = [ordered]@{} }
    foreach ($bookmark in (ConvertTo-List $Bookmarks)) {
        $node = $tree
        foreach ($name in $bookmark.path) {
            if (-not $node.subs.Contains($name)) {
                $node.subs[$name] = @{ items = ([System.Collections.Generic.List[object]]::new()); subs = [ordered]@{} }
            }
            $node = $node.subs[$name]
        }
        $node.items.Add($bookmark)
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('<!DOCTYPE NETSCAPE-Bookmark-file-1>')
    $lines.Add('<META HTTP-EQUIV="Content-Type" CONTENT="text/html; charset=UTF-8">')
    $lines.Add('<TITLE>Bookmarks</TITLE>')
    $lines.Add('<H1>Bookmarks</H1>')
    $lines.Add('<DL><p>')
    Add-BookmarkHtmlNode -Node $tree -Depth 1 -Lines $lines
    $lines.Add('</DL><p>')
    return ($lines -join "`n")
}

# Writes one folder level of the bookmarks .html, then its sub-folders.
function Add-BookmarkHtmlNode {
    param($Node, [int]$Depth, $Lines)
    $pad = '    ' * $Depth
    foreach ($bookmark in $Node.items) {
        $title = $bookmark.title
        if (-not $title) { $title = $bookmark.url }
        $Lines.Add("$pad<DT><A HREF=`"$([System.Net.WebUtility]::HtmlEncode($bookmark.url))`">$([System.Net.WebUtility]::HtmlEncode($title))</A>")
    }
    foreach ($name in $Node.subs.Keys) {
        $attribute = ''
        $title = $name
        if ($Depth -eq 1) {
            if ($name -eq 'toolbar') {
                $attribute = ' PERSONAL_TOOLBAR_FOLDER="true"'
                $title = 'Bookmarks bar'
            } elseif ($name -eq 'other') {
                $title = 'Other bookmarks'
            }
        }
        $Lines.Add("$pad<DT><H3$attribute>$([System.Net.WebUtility]::HtmlEncode($title))</H3>")
        $Lines.Add("$pad<DL><p>")
        Add-BookmarkHtmlNode -Node $Node.subs[$name] -Depth ($Depth + 1) -Lines $Lines
        $Lines.Add("$pad</DL><p>")
    }
}

# Saves the master list (and, with -IncludeSeen, what each browser last
# had) as a .json and .html pair. Old automatic backups beyond the number
# kept in Settings are removed; manual ones are always kept.
function New-Backup {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)][string]$Reason,
        [switch]$IncludeSeen,
        $Config
    )
    if (-not (Test-Path $Script:BackupDir)) { New-Item -Path $Script:BackupDir -ItemType Directory -Force | Out-Null }

    # Milliseconds keep two backups in the same second apart.
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmssfff'
    $name = "bookmarks-$stamp-$Reason"
    $snapshots = @{}
    foreach ($browser in $State.browsers.Keys) { $snapshots[$browser] = $State.browsers[$browser].snapshot }

    $payload = [ordered]@{ version = 1; created = $stamp; reason = $Reason; master = $State.master; browsers = $snapshots }
    Write-FileSafely -Path (Join-Path $Script:BackupDir "$name.json") -Text (ConvertTo-Json -InputObject $payload -Depth 12 -Compress)

    # The .html copy also holds anything a browser had that the master does
    # not, so a backup never loses a bookmark that was about to be removed.
    $export = ConvertTo-BookmarkMap $State.master.bookmarks
    if ($IncludeSeen) {
        foreach ($snapshot in $snapshots.Values) {
            if ($null -eq $snapshot) { continue }
            $seen = ConvertTo-BookmarkMap $snapshot.bookmarks
            foreach ($key in $seen.Keys) { if (-not $export.Contains($key)) { $export[$key] = $seen[$key] } }
        }
    }
    Write-FileSafely -Path (Join-Path $Script:BackupDir "$name.html") -Text (ConvertTo-BookmarkHtml $export.Values)

    $keep = 40
    if ($Config) { $keep = [int]$Config.AutoBackupsToKeep }
    $auto = @(Get-ChildItem -Path $Script:BackupDir -Filter '*-auto.json' | Sort-Object Name)
    if ($auto.Count -gt $keep) {
        foreach ($old in $auto[0..($auto.Count - $keep - 1)]) {
            Remove-Item -Path $old.FullName -ErrorAction SilentlyContinue
            Remove-Item -Path ([System.IO.Path]::ChangeExtension($old.FullName, '.html')) -ErrorAction SilentlyContinue
        }
    }
    Write-AppLog "Backup saved: $name"
    return $name
}

# Lists backups newest first, with a readable date and reason.
function Get-BackupList {
    if (-not (Test-Path $Script:BackupDir)) { return @() }
    $result = [System.Collections.Generic.List[object]]::new()
    foreach ($file in (Get-ChildItem -Path $Script:BackupDir -Filter 'bookmarks-*.json' | Sort-Object Name -Descending)) {
        $match = [regex]::Match($file.BaseName, '^bookmarks-(\d{8})-(\d{6})\d{0,3}-(.+)$')
        if (-not $match.Success) { continue }
        $when = [datetime]::ParseExact($match.Groups[1].Value + $match.Groups[2].Value, 'yyyyMMddHHmmss', $null)
        $reason = switch ($match.Groups[3].Value) {
            'auto'           { 'Automatic' }
            'manual'         { 'Backed up by you' }
            'before-restore' { 'Before a restore' }
            'before-source'  { 'Before a browser was used as the source' }
            default          { $match.Groups[3].Value }
        }
        $result.Add([pscustomobject]@{ Name = $file.BaseName; When = $when; Reason = $reason })
    }
    return $result
}

# Makes a backup the master list again. The current list is backed up
# first, and every browser is set to change to match on its next sync.
function Restore-Backup {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)][string]$Name,
        $Config
    )
    $path = Join-Path $Script:BackupDir ([System.IO.Path]::GetFileName($Name) + '.json')
    $payload = Get-Content -Path $path -Raw -Encoding UTF8 | ConvertFrom-Json
    New-Backup -State $State -Reason 'before-restore' -IncludeSeen -Config $Config | Out-Null
    $State.master = @{ bookmarks = (ConvertTo-List $payload.master.bookmarks); folders = (ConvertTo-List $payload.master.folders) }
    $State.mirror_from = $null
    $State.pending_overwrite = ConvertTo-List $State.browsers.Keys
    Write-AppLog "Restored backup $Name, master list now $($State.master.bookmarks.Count)"
}

# ============================================================================
# Region: Native messaging (talking to the browsers)
#
# Each message is a 4-byte little-endian length followed by that many bytes
# of UTF-8 JSON, on standard input and output.
# ============================================================================

# Reads exactly Count bytes, or returns null if the stream ends first.
function Read-StreamBytes {
    param([System.IO.Stream]$Stream, [int]$Count)
    $buffer = New-Object byte[] $Count
    $offset = 0
    while ($offset -lt $Count) {
        $read = $Stream.Read($buffer, $offset, $Count - $offset)
        if ($read -le 0) { return $null }
        $offset += $read
    }
    return ,$buffer
}

# Reads one message and returns its JSON text, or null if there is none.
function Read-HostMessage {
    param([Parameter(Mandatory)][System.IO.Stream]$Stream)
    $header = Read-StreamBytes $Stream 4
    if ($null -eq $header) { return $null }
    $length = [BitConverter]::ToInt32($header, 0)
    if ($length -le 0) { return $null }
    $body = Read-StreamBytes $Stream $length
    if ($null -eq $body) { return $null }
    return [System.Text.Encoding]::UTF8.GetString($body)
}

# Writes one reply as a length-prefixed UTF-8 JSON message.
function Write-HostMessage {
    param(
        [Parameter(Mandatory)][System.IO.Stream]$Stream,
        [Parameter(Mandatory)]$Reply
    )
    $json = ConvertTo-Json -InputObject $Reply -Depth 6 -Compress
    $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($json)
    $Stream.Write([BitConverter]::GetBytes([int]$bytes.Length), 0, 4)
    $Stream.Write($bytes, 0, $bytes.Length)
    $Stream.Flush()
}

# Splits text into base64 pieces small enough for one reply each. The
# extension joins the bytes back together before reading them.
function Split-ReplyParts {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text, [int]$PartBytes = $Script:PartBytes)
    $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($Text)
    $parts = [System.Collections.Generic.List[string]]::new()
    for ($offset = 0; $offset -lt $bytes.Length; $offset += $PartBytes) {
        $parts.Add([Convert]::ToBase64String($bytes, $offset, [Math]::Min($PartBytes, $bytes.Length - $offset)))
    }
    if ($parts.Count -eq 0) { $parts.Add('') }
    return ,$parts
}

# The file holding the master list waiting to be sent to one browser.
function Get-PlanFile {
    param([Parameter(Mandatory)][string]$Browser)
    return Join-Path $Script:PlanDir "$Browser.json"
}

# Answers one request from a browser. Returns the reply to send back.
#
#   status  - is the helper installed, and which version
#   sync    - merge this browser's bookmarks, reply with the master list
#             (first piece) or unchanged
#   part    - send a further piece of the master list
#   ack     - the browser has applied the master list, record its bookmarks
#   open    - open the Bookmark Sync window
function Invoke-HostRequest {
    param([Parameter(Mandatory)]$Request)

    $type = [string]$Request.type
    if ($type -eq 'status') {
        return @{ ok = $true; version = $Script:AppVersion }
    }
    if ($type -eq 'open') {
        Start-DetachedWindow
        return @{ ok = $true }
    }

    $browser = [string]$Request.browser
    if (-not $Script:BrowserNames.ContainsKey($browser)) {
        throw "Unknown browser '$browser'."
    }

    if ($type -eq 'part') {
        $parts = Split-ReplyParts ([System.IO.File]::ReadAllText((Get-PlanFile $browser)))
        $index = [int]$Request.part
        if ($index -lt 0 -or $index -ge $parts.Count) { throw 'That part of the bookmark list does not exist, sync again.' }
        return @{ ok = $true; part = $index; parts = $parts.Count; data = $parts[$index] }
    }

    $config = (Read-Config).Config
    $script:reply = $null
    $null = Invoke-WithStateLock {
        $state = Read-SyncState
        if ($type -eq 'sync') {
            $result = Invoke-BrowserSync -State $state -Browser $browser -Snapshot $Request -Config $config
            Save-SyncState $state
            if ($result.Unchanged) {
                $script:reply = @{ ok = $true; unchanged = $true }
            } else {
                $text = ConvertTo-Json -InputObject $state.master -Depth 6 -Compress
                Write-FileSafely -Path (Get-PlanFile $browser) -Text $text
                $parts = Split-ReplyParts $text
                $script:reply = @{ ok = $true; unchanged = $false; part = 0; parts = $parts.Count; data = $parts[0] }
            }
        } elseif ($type -eq 'ack') {
            Complete-BrowserSync -State $state -Browser $browser -Snapshot $Request -Stats $Request.stats
            Save-SyncState $state
            Remove-Item -Path (Get-PlanFile $browser) -ErrorAction SilentlyContinue
            $script:reply = @{ ok = $true }
        } else {
            throw "Unknown request '$type'."
        }
    }
    return $script:reply
}

# Runs as the browser's helper: one request in, one reply out, then exit.
# Errors go back to the extension as a message, never as console output,
# which would break the message stream.
function Invoke-NativeHost {
    # Progress bars would be written into the message stream.
    $ProgressPreference = 'SilentlyContinue'
    $inStream = [Console]::OpenStandardInput()
    $outStream = [Console]::OpenStandardOutput()
    try {
        $text = Read-HostMessage $inStream
        if ($null -eq $text) { return }
        $reply = Invoke-HostRequest ($text | ConvertFrom-Json)
    } catch {
        Write-AppLog "Request failed: $($_.Exception.Message)"
        $reply = @{ ok = $false; error = $_.Exception.Message }
    }
    Write-HostMessage -Stream $outStream -Reply $reply
}

# Opens the Bookmark Sync window as its own process. Started through WMI so
# the browser does not close it when the helper exits.
function Start-DetachedWindow {
    $command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$($Script:InstalledScript)`""
    Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $command } | Out-Null
}

# ============================================================================
# Region: Install, upgrade and removal
# ============================================================================

# Reads the version stamp from the installed copy, or null if none.
function Get-InstalledVersion {
    if (-not (Test-Path $Script:InstalledScript)) { return $null }
    $match = Select-String -Path $Script:InstalledScript -Pattern "^\`$Script:AppVersion = '([0-9.]+)'" | Select-Object -First 1
    if (-not $match) { return $null }
    return [version]$match.Matches[0].Groups[1].Value
}

# Writes the launcher the browsers start, the two helper descriptions they
# read, and the registry entries that point each browser at them. Safe to
# run again at any time, it only puts things back as they should be.
function Register-NativeHost {
    $launcher = "@echo off`r`npowershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"%~dp0BookmarkSync.ps1`" -NativeHost`r`n"
    Write-FileSafely -Path $Script:LauncherFile -Text $launcher

    $chromium = [ordered]@{
        name            = $Script:HostName
        description     = $Script:AppTitle
        path            = $Script:LauncherFile
        type            = 'stdio'
        allowed_origins = @("chrome-extension://$($Script:ChromiumExtensionId)/")
    }
    $firefox = [ordered]@{
        name               = $Script:HostName
        description        = $Script:AppTitle
        path               = $Script:LauncherFile
        type               = 'stdio'
        allowed_extensions = @($Script:FirefoxExtensionId)
    }
    Write-FileSafely -Path (Join-Path $Script:DataDir 'host-chromium.json') -Text ($chromium | ConvertTo-Json)
    Write-FileSafely -Path (Join-Path $Script:DataDir 'host-firefox.json') -Text ($firefox | ConvertTo-Json)

    foreach ($entry in $Script:HostRegistryKeys) {
        New-Item -Path $entry.Key -Value (Join-Path $Script:DataDir $entry.Manifest) -Force | Out-Null
    }
}

# Copies this script into the app folder, registers it with the browsers
# and adds a Start menu shortcut. Settings, bookmarks and backups are kept.
function Install-App {
    if (-not (Test-Path $Script:DataDir)) { New-Item -Path $Script:DataDir -ItemType Directory -Force | Out-Null }
    if ($PSCommandPath -ne $Script:InstalledScript) {
        Copy-Item -Path $PSCommandPath -Destination $Script:InstalledScript -Force
    }
    Register-NativeHost

    $stream = [System.IO.File]::Create($Script:IconFile)
    try { $Script:AppIcon.Save($stream) } finally { $stream.Dispose() }

    $shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) "$($Script:AppTitle).lnk"
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = 'powershell.exe'
    $shortcut.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$($Script:InstalledScript)`""
    $shortcut.IconLocation = $Script:IconFile
    $shortcut.Description = $Script:AppTitle
    $shortcut.Save()

    Write-AppLog "Installed $($Script:VersionTag)"
}

# Undoes Install-App and sends the app folder (bookmarks state, backups,
# settings, log) to the Recycle Bin, so it can still be got back.
function Uninstall-App {
    foreach ($entry in $Script:HostRegistryKeys) {
        if (Test-Path $entry.Key) { Remove-Item -Path $entry.Key -Force }
    }
    $shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) "$($Script:AppTitle).lnk"
    if (Test-Path $shortcutPath) { Remove-Item -Path $shortcutPath -Force }

    Add-Type -AssemblyName Microsoft.VisualBasic
    if (Test-Path $Script:DataDir) {
        [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory(
            $Script:DataDir,
            [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,
            [Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin)
    }
}

# ============================================================================
# Region: Window look (matches Monitor Switcher)
# ============================================================================

# Loads WinForms and defines the shared palette, fonts and icon. Kept in a
# function so the browser helper never pays for loading the UI.
function Initialize-Ui {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    [System.Windows.Forms.Application]::EnableVisualStyles()

    $Script:UiFont      = New-Object System.Drawing.Font('Segoe UI', 9.5)
    $Script:UiFontBold  = New-Object System.Drawing.Font('Segoe UI', 9.5, [System.Drawing.FontStyle]::Bold)
    $Script:UiFontSmall = New-Object System.Drawing.Font('Segoe UI', 8.5)
    $Script:UiFontMono  = New-Object System.Drawing.Font('Consolas', 9)

    $Script:ColorWindowBg   = [System.Drawing.Color]::FromArgb(231, 234, 239)
    $Script:ColorCardBg     = [System.Drawing.Color]::White
    $Script:ColorBorder     = [System.Drawing.Color]::FromArgb(188, 194, 202)
    $Script:ColorText       = [System.Drawing.Color]::FromArgb(31, 35, 40)
    $Script:ColorMutedText  = [System.Drawing.Color]::FromArgb(107, 114, 128)
    $Script:ColorAccent     = [System.Drawing.Color]::FromArgb(43, 108, 223)
    $Script:ColorAccentDark = [System.Drawing.Color]::FromArgb(27, 79, 173)
    $Script:ColorCurrentBg  = [System.Drawing.Color]::FromArgb(224, 242, 233)
    $Script:ColorCurrentFg  = [System.Drawing.Color]::FromArgb(15, 110, 75)
    $Script:ColorWarningFg  = [System.Drawing.Color]::FromArgb(180, 83, 9)
    $Script:ColorErrorFg    = [System.Drawing.Color]::FromArgb(180, 0, 0)

    $Script:AppIcon = New-AppIcon
}

# Draws the app icon: a bookmark ribbon with two arrows chasing each other,
# so there is no separate .ico file to ship.
function New-AppIcon {
    $size = 32
    $bitmap = New-Object System.Drawing.Bitmap($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([System.Drawing.Color]::Transparent)

        # Rounded square background, same family as the other tools.
        $square = New-Object System.Drawing.Drawing2D.GraphicsPath
        $square.AddArc(1, 1, 10, 10, 180, 90)
        $square.AddArc(21, 1, 10, 10, 270, 90)
        $square.AddArc(21, 21, 10, 10, 0, 90)
        $square.AddArc(1, 21, 10, 10, 90, 90)
        $square.CloseFigure()
        $graphics.FillPath((New-Object System.Drawing.SolidBrush($Script:ColorAccent)), $square)

        # Bookmark ribbon.
        $ribbon = [System.Drawing.Point[]]@(
            (New-Object System.Drawing.Point(10, 6)),
            (New-Object System.Drawing.Point(22, 6)),
            (New-Object System.Drawing.Point(22, 26)),
            (New-Object System.Drawing.Point(16, 21)),
            (New-Object System.Drawing.Point(10, 26))
        )
        $graphics.FillPolygon([System.Drawing.Brushes]::White, $ribbon)

        # Two small arrows on the ribbon, the keeping-in-step idea.
        $pen = New-Object System.Drawing.Pen($Script:ColorAccentDark, 1.6)
        $graphics.DrawArc($pen, 12, 9, 8, 8, 200, 140)
        $graphics.DrawLine($pen, 19, 10, 20, 13)
        $graphics.DrawArc($pen, 12, 9, 8, 8, 20, 140)
        $graphics.DrawLine($pen, 13, 16, 12, 13)

        return [System.Drawing.Icon]::FromHandle($bitmap.GetHicon())
    } finally {
        $graphics.Dispose()
    }
}

# Background, font, icon, the slim accent bar across the top and the
# version label bottom left, applied to every window.
function Set-FormChrome {
    param([Parameter(Mandatory)][System.Windows.Forms.Form]$Form)
    $Form.BackColor = $Script:ColorWindowBg
    $Form.Font = $Script:UiFont
    $Form.Icon = $Script:AppIcon
    $Form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
    $Form.MaximizeBox = $false
    $Form.MinimizeBox = $false
    $Form.KeyPreview = $true

    $accentBar = New-Object System.Windows.Forms.Panel
    $accentBar.Height = 4
    $accentBar.Dock = [System.Windows.Forms.DockStyle]::Top
    $accentBar.BackColor = $Script:ColorAccent
    $Form.Controls.Add($accentBar)

    $version = New-Object System.Windows.Forms.Label
    $version.Text = $Script:VersionTag
    $version.Font = $Script:UiFontSmall
    $version.ForeColor = $Script:ColorMutedText
    $version.AutoSize = $true
    $version.Location = New-Object System.Drawing.Point(16, ($Form.ClientSize.Height - 22))
    $version.Anchor = [System.Windows.Forms.AnchorStyles]'Bottom, Left'
    $Form.Controls.Add($version)
}

# Flat buttons. One -Primary action per window, -Warning only for actions
# that remove or replace something.
function New-StyledButton {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][int]$X,
        [Parameter(Mandatory)][int]$Y,
        [int]$Width = 110,
        [switch]$Primary,
        [switch]$Warning
    )
    $button = New-Object System.Windows.Forms.Button
    $button.Text = $Text
    $button.Location = New-Object System.Drawing.Point($X, $Y)
    $button.Size = New-Object System.Drawing.Size($Width, 30)
    $button.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
    $button.Font = $Script:UiFont
    $button.Cursor = [System.Windows.Forms.Cursors]::Hand
    $button.FlatAppearance.BorderSize = 1

    if ($Primary) {
        $button.BackColor = $Script:ColorAccent
        $button.ForeColor = [System.Drawing.Color]::White
        $button.FlatAppearance.BorderColor = $Script:ColorAccent
        $button.FlatAppearance.MouseOverBackColor = $Script:ColorAccentDark
        $button.FlatAppearance.MouseDownBackColor = $Script:ColorAccentDark
    } elseif ($Warning) {
        $button.BackColor = $Script:ColorCardBg
        $button.ForeColor = $Script:ColorWarningFg
        $button.FlatAppearance.BorderColor = $Script:ColorWarningFg
        $button.FlatAppearance.MouseOverBackColor = [System.Drawing.Color]::FromArgb(255, 247, 237)
    } else {
        $button.BackColor = $Script:ColorCardBg
        $button.ForeColor = $Script:ColorText
        $button.FlatAppearance.BorderColor = $Script:ColorBorder
        $button.FlatAppearance.MouseOverBackColor = $Script:ColorWindowBg
    }
    return $button
}

# A bold section heading.
function New-Heading {
    param([string]$Text, [int]$X, [int]$Y)
    $label = New-Object System.Windows.Forms.Label
    $label.Text = $Text
    $label.Font = $Script:UiFontBold
    $label.ForeColor = $Script:ColorText
    $label.AutoSize = $true
    $label.Location = New-Object System.Drawing.Point($X, $Y)
    return $label
}

# A short grey line under a setting saying what it does.
function New-SmallPrint {
    param([string]$Text, [int]$X, [int]$Y, [int]$Width)
    $label = New-Object System.Windows.Forms.Label
    $label.Text = $Text
    $label.Font = $Script:UiFontSmall
    $label.ForeColor = $Script:ColorMutedText
    $label.Location = New-Object System.Drawing.Point($X, $Y)
    $label.Size = New-Object System.Drawing.Size($Width, 32)
    return $label
}

# A white card holding a list, with columns and full-row selection.
function New-CardList {
    param([int]$X, [int]$Y, [int]$Width, [int]$Height, [object[]]$Columns)
    $list = New-Object System.Windows.Forms.ListView
    $list.View = [System.Windows.Forms.View]::Details
    $list.FullRowSelect = $true
    $list.MultiSelect = $false
    $list.HideSelection = $false
    $list.HeaderStyle = [System.Windows.Forms.ColumnHeaderStyle]::Nonclickable
    $list.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
    $list.BackColor = $Script:ColorCardBg
    $list.ForeColor = $Script:ColorText
    $list.Location = New-Object System.Drawing.Point($X, $Y)
    $list.Size = New-Object System.Drawing.Size($Width, $Height)
    foreach ($column in $Columns) { [void]$list.Columns.Add($column[0], $column[1]) }
    return $list
}

# Asks a yes or no question. Used before anything that replaces bookmarks.
function Confirm-Action {
    param([string]$Message, [string]$Title)
    $answer = [System.Windows.Forms.MessageBox]::Show($Message, $Title,
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Question,
        [System.Windows.Forms.MessageBoxDefaultButton]::Button2)
    return $answer -eq [System.Windows.Forms.DialogResult]::Yes
}

# Shows an error popup. Only for things that actually failed.
function Show-Error {
    param([string]$Message)
    [void][System.Windows.Forms.MessageBox]::Show($Message, $Script:AppTitle,
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Warning)
}

# Turns a stored sync time into "just now", "5 min ago" or a date.
function Format-SyncTime {
    param([double]$Seconds)
    if ($Seconds -le 0) { return 'Never' }
    $when = [DateTimeOffset]::FromUnixTimeSeconds([long]$Seconds).LocalDateTime
    $minutes = [int]((Get-Date) - $when).TotalMinutes
    if ($minutes -lt 1) { return 'Just now' }
    if ($minutes -lt 60) { return "$minutes min ago" }
    if ($when.Date -eq (Get-Date).Date) { return $when.ToString('HH:mm') }
    return $when.ToString('d MMM HH:mm')
}

# ============================================================================
# Region: Help
# ============================================================================

$Script:HelpText = @'
USING IT
Install the Bookmark Sync extension in each browser you want kept in step
(Chrome, Edge, Firefox). Each browser syncs a few seconds after you add,
move or delete a bookmark, when it starts, and every 5 minutes. Nothing
runs in the background in between, the browser starts the helper when it
needs it.

The first time a browser syncs, everything it has is added to the master
list and nothing is removed. After that, adds and deletes in any browser
reach the others.

The extension's toolbar button shows when it last synced. Sync now does it
straight away. A red ! means it could not reach this app: run
BookmarkSync.ps1 again to repair the install.

BROWSERS
The list shows each browser that has synced, how many bookmarks it had and
when. Use as source makes the selected browser the master: on its next
sync its bookmarks replace the master list and the other browsers are
changed to match. A backup is taken first.

BACKUPS (UNDO)
A backup is taken automatically before changes, at most every 10 minutes,
and before any restore. Back up now takes one straight away. Each backup is
a .json file (restore it here) and a .html file (import it into any
browser). Restore makes the selected backup the master list again, after
backing up the current one, and every browser changes to match on its next
sync. Restoring the newest automatic backup is the way to undo a sync.

DUPLICATES
With Remove duplicate bookmarks on, the same address is kept once. A copy
on the bookmarks bar wins, then one in a bookmarks bar folder, then other
bookmarks. Addresses that differ only by www, a trailing slash or tracking
codes count as the same.

LIMITS
Bookmarks are added at the end of their folder, so exact order is not
synced. The Firefox bookmarks menu and other bookmarks count as one area.
Browser pages such as chrome:// and about: are never synced or changed.

SETTINGS
Settings > Open settings file opens config.json. Restore defaults puts the
settings back. Remove all app data takes Bookmark Sync off this computer
and sends its folder, backups included, to the Recycle Bin.
'@

# Shows the help text in its own window.
function Show-HelpWindow {
    param($Owner)
    $form = New-Object System.Windows.Forms.Form
    $form.Text = "$($Script:AppTitle) - Help"
    $form.ClientSize = New-Object System.Drawing.Size(560, 520)
    $form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterParent
    Set-FormChrome $form

    $text = New-Object System.Windows.Forms.TextBox
    $text.Multiline = $true
    $text.ReadOnly = $true
    $text.ScrollBars = [System.Windows.Forms.ScrollBars]::Vertical
    $text.BackColor = $Script:ColorCardBg
    $text.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
    $text.Location = New-Object System.Drawing.Point(16, 20)
    $text.Size = New-Object System.Drawing.Size(528, 440)
    $text.Text = $Script:HelpText -replace "`r?`n", "`r`n"
    $form.Controls.Add($text)

    $close = New-StyledButton -Text 'Close' -X 434 -Y 474
    $close.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $form.Controls.Add($close)
    $form.CancelButton = $close

    $form.Add_Shown({ $text.SelectionLength = 0 })
    [void]$form.ShowDialog($Owner)
}

# ============================================================================
# Region: Settings window
# ============================================================================

# Settings: duplicates, automatic backups, help and files, two-level reset.
# Returns 'removed' when the user removed the app, so the main window closes.
function Show-SettingsWindow {
    param($Owner)
    $script:settingsResult = $null
    $script:settingsConfig = (Read-Config).Config

    $form = New-Object System.Windows.Forms.Form
    $form.Text = "$($Script:AppTitle) - Settings"
    $form.ClientSize = New-Object System.Drawing.Size(440, 470)
    $form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterParent
    Set-FormChrome $form

    $form.Controls.Add((New-Heading 'Duplicates' 16 20))
    $script:dedupeBox = New-Object System.Windows.Forms.CheckBox
    $script:dedupeBox.Text = 'Remove duplicate bookmarks'
    $script:dedupeBox.AutoSize = $true
    $script:dedupeBox.Location = New-Object System.Drawing.Point(16, 44)
    $script:dedupeBox.Checked = $script:settingsConfig.RemoveDuplicates
    $form.Controls.Add($script:dedupeBox)
    $form.Controls.Add((New-SmallPrint 'Keeps one copy of each address, preferring the bookmarks bar.' 34 68 390))

    $form.Controls.Add((New-Heading 'Automatic backups' 16 104))
    $keepLabel = New-Object System.Windows.Forms.Label
    $keepLabel.Text = 'Keep the last'
    $keepLabel.AutoSize = $true
    $keepLabel.Location = New-Object System.Drawing.Point(16, 132)
    $form.Controls.Add($keepLabel)
    $script:keepBox = New-Object System.Windows.Forms.NumericUpDown
    $script:keepBox.Minimum = 5
    $script:keepBox.Maximum = 500
    $script:keepBox.Value = $script:settingsConfig.AutoBackupsToKeep
    $script:keepBox.Location = New-Object System.Drawing.Point(104, 129)
    $script:keepBox.Width = 64
    $form.Controls.Add($script:keepBox)
    $keepTail = New-Object System.Windows.Forms.Label
    $keepTail.Text = 'automatic backups'
    $keepTail.AutoSize = $true
    $keepTail.Location = New-Object System.Drawing.Point(174, 132)
    $form.Controls.Add($keepTail)
    $form.Controls.Add((New-SmallPrint 'Older automatic backups are removed. Ones you make are always kept.' 16 158 400))

    $form.Controls.Add((New-Heading 'Help and files' 16 194))
    $help = New-StyledButton -Text 'Help' -X 16 -Y 220 -Width 90
    $help.Add_Click({ Show-HelpWindow $form })
    $form.Controls.Add($help)
    $openSettings = New-StyledButton -Text 'Open settings file' -X 114 -Y 220 -Width 140
    $openSettings.Add_Click({
        if (-not (Test-Path $Script:ConfigFile)) { Save-Config $script:settingsConfig }
        Start-Process notepad.exe -ArgumentList "`"$($Script:ConfigFile)`""
    })
    $form.Controls.Add($openSettings)
    $openLog = New-StyledButton -Text 'Open log' -X 262 -Y 220 -Width 90
    $openLog.Add_Click({
        if (Test-Path $Script:LogPath) { Start-Process notepad.exe -ArgumentList "`"$($Script:LogPath)`"" }
    })
    $form.Controls.Add($openLog)

    $form.Controls.Add((New-Heading 'Reset' 16 270))
    $defaults = New-StyledButton -Text 'Restore defaults' -X 16 -Y 296 -Width 140
    $defaults.Add_Click({
        if (Confirm-Action 'Put every setting back to its default? Bookmarks and backups are not touched.' 'Restore defaults') {
            $script:dedupeBox.Checked = $Script:DefaultConfig.RemoveDuplicates
            $script:keepBox.Value = $Script:DefaultConfig.AutoBackupsToKeep
            $script:settingsConfig.WindowLeft = -1
            $script:settingsConfig.WindowTop = -1
        }
    })
    $form.Controls.Add($defaults)
    $remove = New-StyledButton -Text 'Remove all app data' -X 164 -Y 296 -Width 160 -Warning
    $remove.Add_Click({
        $message = "This takes Bookmark Sync off this computer:`r`n`r`n" +
            "- the browsers stop being able to sync (remove the extension yourself)`r`n" +
            "- the Start menu shortcut is removed`r`n" +
            "- the app folder, including all backups, goes to the Recycle Bin`r`n`r`n" +
            "Your bookmarks in each browser stay as they are. Carry on?"
        if (Confirm-Action $message 'Remove all app data') {
            try {
                Uninstall-App
                $script:settingsResult = 'removed'
                $form.Close()
            } catch {
                Show-Error "Could not remove everything. $($_.Exception.Message)"
            }
        }
    })
    $form.Controls.Add($remove)
    $form.Controls.Add((New-SmallPrint 'Restore defaults resets settings only. Remove all app data uninstalls.' 16 334 400))

    $save = New-StyledButton -Text 'Save' -X 16 -Y 400 -Width 90 -Primary
    $save.Add_Click({
        $script:settingsConfig.RemoveDuplicates = $script:dedupeBox.Checked
        $script:settingsConfig.AutoBackupsToKeep = [int]$script:keepBox.Value
        try {
            Save-Config $script:settingsConfig
            Write-AppLog 'Settings saved'
            $form.Close()
        } catch {
            Show-Error "Settings were not saved. $($_.Exception.Message)"
        }
    })
    $form.Controls.Add($save)
    $form.AcceptButton = $save

    $close = New-StyledButton -Text 'Close' -X 314 -Y 400
    $close.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $form.Controls.Add($close)
    $form.CancelButton = $close

    [void]$form.ShowDialog($Owner)
    return $script:settingsResult
}

# ============================================================================
# Region: Main window
# ============================================================================

# Fills the browser list from the state file.
function Update-BrowserList {
    $script:browserList.Items.Clear()
    try {
        $state = Read-SyncState
    } catch {
        Set-Status "The bookmark state could not be read. $($_.Exception.Message)" $Script:ColorErrorFg
        return
    }
    foreach ($browser in ($state.browsers.Keys | Sort-Object)) {
        $entry = $state.browsers[$browser]
        $count = 0
        if ($entry.snapshot -and $entry.snapshot.bookmarks) { $count = @($entry.snapshot.bookmarks).Count }
        $next = ''
        if ($state.mirror_from -eq $browser) { $next = 'Becomes the source' }
        elseif ($state.pending_overwrite.Contains($browser)) { $next = 'Changes to match' }

        $label = $Script:BrowserNames[$browser]
        if (-not $label) { $label = $browser }
        $item = New-Object System.Windows.Forms.ListViewItem($label)
        $item.Tag = $browser
        [void]$item.SubItems.Add([string]$count)
        [void]$item.SubItems.Add((Format-SyncTime $entry.seen))
        [void]$item.SubItems.Add($next)
        [void]$script:browserList.Items.Add($item)
    }
    $script:masterLabel.Text = "Master list: $(@($state.master.bookmarks).Count) bookmarks"
}

# Fills the backup list from the backup folder.
function Update-BackupList {
    $script:backupList.Items.Clear()
    foreach ($backup in (Get-BackupList)) {
        $item = New-Object System.Windows.Forms.ListViewItem($backup.When.ToString('d MMM yyyy HH:mm'))
        $item.Tag = $backup.Name
        [void]$item.SubItems.Add($backup.Reason)
        [void]$script:backupList.Items.Add($item)
    }
}

# Shows a one-line status under the lists. Green for Complete.
function Set-Status {
    param([string]$Text, $Color)
    $script:statusLabel.Text = $Text
    $script:statusLabel.ForeColor = $Color
}

# The main window: browsers, backups, Back up now, Settings, Close.
function Show-MainWindow {
    $script:mainConfig = (Read-Config).Config

    $form = New-Object System.Windows.Forms.Form
    $form.Text = $Script:AppTitle
    $form.ClientSize = New-Object System.Drawing.Size(560, 580)
    $form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
    Set-FormChrome $form
    $script:mainForm = $form

    # Put the window back where it was, if that spot is still on a screen.
    if ($script:mainConfig.WindowLeft -ge 0) {
        $spot = New-Object System.Drawing.Point($script:mainConfig.WindowLeft, $script:mainConfig.WindowTop)
        foreach ($screen in [System.Windows.Forms.Screen]::AllScreens) {
            if ($screen.WorkingArea.Contains($spot)) {
                $form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
                $form.Location = $spot
            }
        }
    }

    $form.Controls.Add((New-Heading 'Browsers' 16 20))
    $script:masterLabel = New-Object System.Windows.Forms.Label
    $script:masterLabel.AutoSize = $true
    $script:masterLabel.ForeColor = $Script:ColorMutedText
    $script:masterLabel.Location = New-Object System.Drawing.Point(300, 21)
    $form.Controls.Add($script:masterLabel)

    $script:browserList = New-CardList -X 16 -Y 44 -Width 528 -Height 110 -Columns @(
        @('Browser', 110), @('Bookmarks', 90), @('Last sync', 120), @('Next sync', 186))
    $form.Controls.Add($script:browserList)
    $form.Controls.Add((New-SmallPrint 'Each browser syncs a few seconds after a bookmark change, when it starts, and every 5 minutes.' 16 158 400))

    $useSource = New-StyledButton -Text 'Use as source' -X 424 -Y 160 -Width 120
    $useSource.Add_Click({
        if ($script:browserList.SelectedItems.Count -eq 0) {
            Set-Status 'Pick a browser in the list first.' $Script:ColorWarningFg
            return
        }
        $browser = $script:browserList.SelectedItems[0].Tag
        $label = $Script:BrowserNames[$browser]
        $message = "On $label's next sync, its bookmarks replace the master list and the other browsers are changed to match.`r`n`r`nA backup is taken first. Carry on?"
        if (-not (Confirm-Action $message 'Use as source')) { return }
        try {
            $null = Invoke-WithStateLock {
                $state = Read-SyncState
                $state.mirror_from = $browser
                $state.pending_overwrite = ConvertTo-List ($state.pending_overwrite | Where-Object { $_ -ne $browser })
                Save-SyncState $state
            }
            Write-AppLog "$label chosen as the source for its next sync"
            Set-Status "Complete - $label becomes the source on its next sync." $Script:ColorCurrentFg
            Update-BrowserList
        } catch {
            Show-Error "Could not set the source. $($_.Exception.Message)"
        }
    })
    $form.Controls.Add($useSource)

    $form.Controls.Add((New-Heading 'Backups' 16 206))
    $script:backupList = New-CardList -X 16 -Y 230 -Width 528 -Height 200 -Columns @(
        @('Date', 160), @('Reason', 346))
    $form.Controls.Add($script:backupList)

    $restore = New-StyledButton -Text 'Restore' -X 16 -Y 438 -Width 90 -Warning
    $restore.Add_Click({
        if ($script:backupList.SelectedItems.Count -eq 0) {
            Set-Status 'Pick a backup in the list first.' $Script:ColorWarningFg
            return
        }
        $item = $script:backupList.SelectedItems[0]
        $message = "Make the backup from $($item.Text) the master list?`r`n`r`nThe current list is backed up first. Every browser changes to match on its next sync."
        if (-not (Confirm-Action $message 'Restore backup')) { return }
        try {
            $null = Invoke-WithStateLock {
                $state = Read-SyncState
                Restore-Backup -State $state -Name $item.Tag -Config $script:mainConfig
                Save-SyncState $state
            }
            Set-Status 'Complete - restored, each browser updates on its next sync.' $Script:ColorCurrentFg
            Update-BrowserList
            Update-BackupList
        } catch {
            Show-Error "The backup was not restored, nothing was changed. $($_.Exception.Message)"
        }
    })
    $form.Controls.Add($restore)

    $openFolder = New-StyledButton -Text 'Open folder' -X 114 -Y 438 -Width 110
    $openFolder.Add_Click({
        if (-not (Test-Path $Script:BackupDir)) { New-Item -Path $Script:BackupDir -ItemType Directory -Force | Out-Null }
        Start-Process explorer.exe -ArgumentList "`"$($Script:BackupDir)`""
    })
    $form.Controls.Add($openFolder)

    $script:statusLabel = New-Object System.Windows.Forms.Label
    $script:statusLabel.Location = New-Object System.Drawing.Point(16, 480)
    $script:statusLabel.Size = New-Object System.Drawing.Size(528, 20)
    $form.Controls.Add($script:statusLabel)

    $backupNow = New-StyledButton -Text 'Back up now' -X 16 -Y 512 -Width 120 -Primary
    $backupNow.Add_Click({
        try {
            $null = Invoke-WithStateLock {
                $state = Read-SyncState
                $null = New-Backup -State $state -Reason 'manual' -IncludeSeen -Config $script:mainConfig
            }
            Set-Status 'Complete - backup saved.' $Script:ColorCurrentFg
            Update-BackupList
        } catch {
            Show-Error "The backup was not saved. $($_.Exception.Message)"
        }
    })
    $form.Controls.Add($backupNow)

    $settings = New-StyledButton -Text 'Settings' -X 144 -Y 512 -Width 100
    $settings.Add_Click({
        if ((Show-SettingsWindow $form) -eq 'removed') {
            $script:removed = $true
            $form.Close()
            return
        }
        $script:mainConfig = (Read-Config).Config
    })
    $form.Controls.Add($settings)

    $close = New-StyledButton -Text 'Close' -X 434 -Y 512
    $close.Add_Click({ $form.Close() })
    $form.Controls.Add($close)
    $form.CancelButton = $close

    # Keep the browser list current while the window is open.
    $timer = New-Object System.Windows.Forms.Timer
    $timer.Interval = 10000
    $timer.Add_Tick({ Update-BrowserList })

    $form.Add_Shown({
        # Opened from a browser, the window can start behind it.
        $form.TopMost = $true
        $form.Activate()
        $form.TopMost = $false
        $timer.Start()
    })
    $form.Add_FormClosing({
        $timer.Stop()
        if ($script:removed) { return }
        try {
            $config = (Read-Config).Config
            $config.WindowLeft = $form.Location.X
            $config.WindowTop = $form.Location.Y
            Save-Config $config
        } catch {
            Write-AppLog "Window position not saved: $($_.Exception.Message)"
        }
    })

    Update-BrowserList
    Update-BackupList
    $configCheck = Read-Config
    if ($configCheck.Problem) {
        Set-Status 'The settings file has a problem, defaults are in use. Settings > Open settings file.' $Script:ColorWarningFg
        Write-AppLog $configCheck.Problem
    }
    [void]$form.ShowDialog()
}

# ============================================================================
# Region: Start
# ============================================================================

# Installs or upgrades when run from a new copy, then opens the window.
# An older copy hands over to the newer installed one instead.
function Start-App {
    Initialize-Ui
    try {
        Copy-LegacyData
        if ($PSCommandPath -ne $Script:InstalledScript) {
            $installed = Get-InstalledVersion
            $current = [version]$Script:AppVersion
            if ($null -eq $installed -or $current -gt $installed) {
                Install-App
            } elseif ($current -lt $installed) {
                Start-Process powershell.exe -ArgumentList "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$($Script:InstalledScript)`""
                return
            } else {
                Register-NativeHost
            }
        }
    } catch {
        Show-Error "Bookmark Sync could not be installed. $($_.Exception.Message)"
        return
    }
    Show-MainWindow
}

# Dot-sourcing (the tests) loads the functions without starting anything.
if ($MyInvocation.InvocationName -ne '.') {
    if ($NativeHost) {
        Invoke-NativeHost
    } else {
        Start-App
    }
}
