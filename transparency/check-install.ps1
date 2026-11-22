# Checks an installed Prometheus player or Studio against the published fingerprint list.
#
# Usage (PowerShell):
#   .\check-install.ps1                      # checks the newest installed 2021 player
#   .\check-install.ps1 -Kind 2021-studio    # checks the newest installed 2021 Studio
#   .\check-install.ps1 -Folder <path> -List <files-sha256.txt>
#
# Every file listed in files-sha256.txt must exist and have exactly that SHA-256 fingerprint.
# Files the launcher or the game create on your PC (AppSettings.xml, PrometheusPlayer.exe, logs, settings)
# are not part of the download, so they are listed separately instead of counted as problems.
param(
    [ValidateSet('2021', '2021-studio')] [string] $Kind = '2021',
    [string] $Folder,
    [string] $List
)
$ErrorActionPreference = 'Stop'

$listName = if ($Kind -eq '2021') { '2021-player' } else { '2021-studio' }
if (-not $List) { $List = Join-Path $PSScriptRoot "$listName\files-sha256.txt" }
if (-not $Folder) {
    $versions = Join-Path $env:LOCALAPPDATA "Prometheus\Versions\$Kind"
    if (-not (Test-Path $versions)) { throw "No installed Prometheus $Kind found in $versions. Pass -Folder." }
    $Folder = (Get-ChildItem $versions -Directory | Where-Object { Test-Path (Join-Path $_.FullName '.complete') } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
    if (-not $Folder) { throw "No complete install found in $versions." }
}
Write-Host "Checking $Folder"
Write-Host "against  $List`n"

$expected = @{}
foreach ($line in Get-Content $List) {
    if ($line -match '^([0-9a-f]{64})\s+(.+)$') { $expected[$Matches[2].Replace('/', '\')] = $Matches[1] }
}

$ok = 0; $changed = @(); $missing = @()
foreach ($rel in $expected.Keys) {
    $path = Join-Path $Folder $rel
    if (-not (Test-Path -LiteralPath $path)) { $missing += $rel; continue }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLower()
    if ($hash -eq $expected[$rel]) { $ok++ } else { $changed += $rel }
}

$root = (Resolve-Path $Folder).Path.TrimEnd('\') + '\'
$extra = Get-ChildItem $Folder -Recurse -File | ForEach-Object { $_.FullName.Substring($root.Length) } |
    Where-Object { -not $expected.ContainsKey($_) }

Write-Host "$ok of $($expected.Count) files match exactly." -ForegroundColor Green
if ($changed) { Write-Host "`nDifferent from the published file ($($changed.Count)):" -ForegroundColor Red; $changed | ForEach-Object { "  $_" } }
if ($missing) { Write-Host "`nMissing ($($missing.Count)):" -ForegroundColor Yellow; $missing | ForEach-Object { "  $_" } }
if ($extra) { Write-Host "`nNot part of the download (created on your PC by the launcher or the game):"; $extra | ForEach-Object { "  $_" } }

# The launcher starts the player under its own name; it must be an exact copy of RobloxPlayerBeta.exe.
$copy = Join-Path $Folder 'PrometheusPlayer.exe'
if ((Test-Path $copy) -and $expected.ContainsKey('RobloxPlayerBeta.exe')) {
    $same = (Get-FileHash $copy -Algorithm SHA256).Hash.ToLower() -eq $expected['RobloxPlayerBeta.exe']
    Write-Host ("`nPrometheusPlayer.exe is " + $(if ($same) { 'an exact copy of RobloxPlayerBeta.exe.' } else { 'NOT an exact copy of RobloxPlayerBeta.exe!' })) -ForegroundColor $(if ($same) { 'Green' } else { 'Red' })
}
if ($changed -or $missing) { exit 1 }
