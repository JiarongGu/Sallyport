# Checks a release folder as a download would be checked (D33): manifest.json read; the zip it names, its size and
# digest, and every file in it - there, its size and SHA-256 the manifest's, nothing else beside them; the setup it
# names, its size and digest, and the same bytes as the program the manifest lists (D30). Throws at the first that
# does not hold; says what it checked when all do. package.ps1 runs it before anything is published; it runs as well on
# a release downloaded into a folder.
#   powershell -File apps\Sallyport.Setup\verify-release.ps1 -Release artifacts\release
param([Parameter(Mandatory)][string]$Release)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
function Digest([IO.Stream]$stream) { -join ([Security.Cryptography.SHA256]::Create().ComputeHash($stream) | ForEach-Object { $_.ToString('x2') }) }
function FileDigest([string]$path) { $s = [IO.File]::OpenRead($path); try { Digest $s } finally { $s.Dispose() } }

$manifestPath = Join-Path $Release "manifest.json"
if (-not (Test-Path $manifestPath)) { throw "no manifest.json in $Release" }
$read = Get-Content $manifestPath -Raw | ConvertFrom-Json
if (-not $read.version -or @($read.files).Count -eq 0) { throw "manifest.json names no version or no files" }
foreach ($file in $read.files) {
    # A path inside the folder it is installed in, never outside it.
    if ($file.path -match '(^|[\\/])\.\.([\\/]|$)' -or $file.path -match '^[\\/]|:') { throw "manifest.json names a path outside its folder: $($file.path)" }
}

foreach ($asset in $read.assets.setup, $read.assets.zip) {
    $path = Join-Path $Release $asset.name
    if (-not (Test-Path $path)) { throw "$($asset.name) is not in $Release" }
    if ((Get-Item $path).Length -ne $asset.size) { throw "$($asset.name) is $((Get-Item $path).Length) bytes, the manifest says $($asset.size)" }
    if ((FileDigest $path) -ne $asset.sha256) { throw "$($asset.name)'s SHA-256 is not the manifest's" }
}

$opened = [IO.Compression.ZipFile]::OpenRead((Join-Path $Release $read.assets.zip.name))
try {
    $entries = @($opened.Entries | Where-Object { $_.Name })
    if ($entries.Count -ne @($read.files).Count) { throw "the zip holds $($entries.Count) files, the manifest $(@($read.files).Count)" }
    foreach ($file in $read.files) {
        $entry = $opened.GetEntry($file.path)
        if (-not $entry) { throw "the zip has no $($file.path)" }
        if ($entry.Length -ne $file.size) { throw "$($file.path) is $($entry.Length) bytes in the zip, $($file.size) in the manifest" }
        $stream = $entry.Open()
        try { $digest = Digest $stream } finally { $stream.Dispose() }
        if ($digest -ne $file.sha256) { throw "$($file.path)'s SHA-256 in the zip is not the manifest's" }
    }
}
finally { $opened.Dispose() }

$program = @($read.files | Where-Object { $_.path -eq 'Sallyport.Client.exe' })
if ($program.Count -ne 1 -or $program[0].sha256 -ne $read.assets.setup.sha256) { throw "the setup is not the program the manifest lists" }
"verified: {0} {1}, {2} files in {3}, and {4}" -f 'Sallyport', $read.version, @($read.files).Count, $read.assets.zip.name, $read.assets.setup.name
