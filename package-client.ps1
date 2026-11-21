# Packages a client folder for the launcher.
# Writes into <OutDir>\<Year>\:
#   player-<Year>-<Version>.zip    the files players download
#   manifest.json                  what the launcher reads: version, zip name, SHA-256, size
#   player-<Year>-<Version>.files.txt  SHA-256 of every file in the zip (for the public transparency list)
# Example:
#   .\package-client.ps1 -Source C:\path\to\Player -Year 2021 -Version 0.463.0.417004-p1 -OutDir C:\path\to\client-downloads
param(
    [Parameter(Mandatory)] [string] $Source,
    [Parameter(Mandatory)] [ValidatePattern('^20\d\d$')] [string] $Year,
    [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z0-9._-]+$')] [string] $Version,
    [Parameter(Mandatory)] [string] $OutDir
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$source = (Resolve-Path $Source).Path.TrimEnd('\')
$dest = Join-Path $OutDir $Year
New-Item -ItemType Directory -Force $dest | Out-Null
$name = "player-$Year-$Version"
$zipPath = Join-Path $dest "$name.zip"
if (Test-Path $zipPath) { throw "$zipPath already exists; pick a new -Version so cached copies never go stale." }

# AppSettings.xml is written by the launcher on each PC (it holds the website address).
$skip = @('AppSettings.xml')
$files = Get-ChildItem $source -Recurse -File | Where-Object { $skip -notcontains $_.Name } | Sort-Object FullName

$zip = [System.IO.Compression.ZipFile]::Open($zipPath, 'Create')
$list = New-Object System.Collections.Generic.List[string]
try {
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($source.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $rel, 'Optimal') | Out-Null
        $list.Add("$((Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLower())  $rel")
    }
} finally { $zip.Dispose() }

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
$size = (Get-Item $zipPath).Length
Set-Content -Encoding ascii (Join-Path $dest "$name.files.txt") $list
[ordered]@{ version = $Version; file = "$name.zip"; sha256 = $hash; size = $size } |
    ConvertTo-Json | Set-Content -Encoding ascii (Join-Path $dest 'manifest.json')
"Packaged $($files.Count) files into $zipPath ($([math]::Round($size / 1MB)) MB), sha256 $hash"
