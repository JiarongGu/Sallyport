# Builds Sallyport's release (docs/DECISIONS.md D28, D30): Sallyport's one program - the window, the service and setup
# - published self-contained for win-x64 (no .NET to install first) as one file, named as the setup for its version,
# with its SHA-256 beside it and the notes the GitHub release shows. Run under that name it installs itself.
#   powershell -File apps\Sallyport.Setup\package.ps1 [-Out artifacts] [-Version 0.1.0]
# artifacts\release\  Sallyport-<version>-win-x64-setup.exe and its .sha256, the installed folder as
#                     Sallyport-<version>-win-x64.zip, and manifest.json (each file's size and SHA-256): what is published
# artifacts\release-notes.md                          what the release says
# artifacts\build\    the program's publish, kept for a look and replaced by the next run
param([string]$Out = "artifacts", [string]$Version)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..\..").Path
$artifacts = Join-Path $root $Out
$build = Join-Path $artifacts "build"
$release = Join-Path $artifacts "release"
$notes = Join-Path $artifacts "release-notes.md"
# A tool's progress and warnings come on stderr (npm's above all); whether it failed is its exit code, never its stderr,
# which Windows PowerShell would otherwise turn into an error that stops the build.
function Run([string]$what, [scriptblock]$command) {
    $ErrorActionPreference = 'Continue'
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
}
foreach ($folder in $build, $release) { if (Test-Path $folder) { Remove-Item -Recurse -Force $folder } }
New-Item -ItemType Directory -Force $build, $release | Out-Null
$program = Join-Path $build "program"

# Directory.Build.props' version unless one is given (the workflow gives the one it has just written); every program
# carries the same.
if (-not $Version) { $Version = (Run 'reading the version' { dotnet msbuild "$root\apps\Sallyport.Setup" -getProperty:Version -nologo }).Trim() }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "A version is X.Y.Z; '$Version' is not." }
# What this version brings, before anything is built: a release with nothing to say is stopped here.
$dash = [char]0x2014
$changelog = [IO.File]::ReadAllText((Join-Path $root "CHANGELOG.md"), (New-Object System.Text.UTF8Encoding $false))
$heading = '(?m)^## ' + [regex]::Escape($Version) + "(?: $dash [^\r\n]*)?\r?\n"
$found = [regex]::Match($changelog, $heading + '(?<body>.*?)(?=^## |\z)', 'Singleline, Multiline')
if (-not $found.Success -or -not $found.Groups['body'].Value.Trim()) { throw "CHANGELOG.md has no section for $Version." }

# The page from this tree, always: the client embeds whatever wwwroot holds, and one left by an earlier build would
# ship as it is.
Push-Location "$root\apps\Sallyport.Client\web"
try {
    Run 'npm ci' { npm ci --no-audit --no-fund }
    Run 'building the page' { npm run build }
}
finally { Pop-Location }

# The small uninstaller (D32), compiled ahead of time, so it carries no .NET runtime. Its linker is found through
# vswhere, which Visual Studio's installer keeps out of PATH.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (-not (Get-Command vswhere.exe -ErrorAction SilentlyContinue) -and (Test-Path "$vswhere\vswhere.exe")) { $env:PATH = "$vswhere;$env:PATH" }
$uninstaller = Join-Path $build "uninstall"
Run 'compiling the uninstaller' {
    dotnet publish "$root\apps\Sallyport.Uninstall" -c Release -r win-x64 -nologo -v q "-p:Version=$Version" -o $uninstaller -p:DebugType=none
}

# One program, one file, one .NET runtime inside it (D30): the window, the service and setup. WebView2's native loader
# and the uninstaller are carried inside it too, and written beside it when it installs itself: the loader rather than
# unpacked into %TEMP% on the system drive at every start.
Run 'publishing the program' {
    dotnet publish "$root\apps\Sallyport.Client" -c Release -r win-x64 --self-contained -nologo -v q "-p:Version=$Version" -o $program `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none `
        -p:PublishDocumentationFile=false -p:PublishReferencesDocumentationFiles=false "-p:UninstallerExe=$uninstaller\uninstall.exe"
}

$name = "Sallyport-$Version-win-x64-setup.exe"
$setup = Join-Path $release $name
Copy-Item (Join-Path $program "Sallyport.Client.exe") $setup
$sha256 = (Get-FileHash -Algorithm SHA256 $setup).Hash.ToLowerInvariant()
# sha256sum's own form, so `sha256sum -c` checks it where the file is downloaded.
[IO.File]::WriteAllText("$setup.sha256", "$sha256  $name`n", (New-Object System.Text.UTF8Encoding $false))

# The release as a folder too (D33; the owner: "a zip/folder bundle too with mainfest file for file hash check"): the
# installed folder's own files under their installed names, zipped, and a manifest of each one's size and SHA-256 and of
# the release's two assets, which an update checks every file against. The sibling repositories' shape
# (D3dxSkinManager's build-manifest, Shenora's UpdateManifest): {version, generatedAt, files[{path, size, sha256}]}.
$folder = Join-Path $build "folder"
New-Item -ItemType Directory -Force $folder | Out-Null
Copy-Item (Join-Path $program "Sallyport.Client.exe"), (Join-Path $program "WebView2Loader.dll"), (Join-Path $uninstaller "uninstall.exe") $folder
$zipName = "Sallyport-$Version-win-x64.zip"
$zip = Join-Path $release $zipName
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($folder, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
function Digest([string]$path) { (Get-FileHash -Algorithm SHA256 $path).Hash.ToLowerInvariant() }
$files = @(Get-ChildItem $folder -File | Sort-Object Name | ForEach-Object {
    [ordered]@{ path = $_.Name; size = $_.Length; sha256 = (Digest $_.FullName) } })
$manifest = [ordered]@{
    version = $Version
    generatedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    files = $files
    assets = [ordered]@{
        setup = [ordered]@{ name = $name; size = (Get-Item $setup).Length; sha256 = $sha256 }
        zip = [ordered]@{ name = $zipName; size = (Get-Item $zip).Length; sha256 = (Digest $zip) }
    }
}
$manifestPath = Join-Path $release "manifest.json"
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding $false))

# Checked as a download would be, before anything is published (verify-release.ps1).
& "$PSScriptRoot\verify-release.ps1" -Release $release
$read = Get-Content $manifestPath -Raw | ConvertFrom-Json

$body = $found.Groups['body'].Value.Trim()
$text = @"
# Sallyport $Version

Sallyport sends the games and apps you choose through your OpenVPN server; everything else keeps your own connection.

## Install

1. Download **$name** and run it. It asks once for administrator rights, lets you choose where Sallyport goes
   (``C:\Program Files\Sallyport`` unless you choose another folder), and installs NT Kernel's free packet driver
   unless you untick it: sending a whole app through your VPN needs it.
2. Open Sallyport, import your ``.ovpn`` file, add your game or app, turn on **Use VPN** for it, and connect.

Needs 64-bit Windows 10 version 1809 or later and Microsoft's WebView2 Runtime (part of Windows 11; setup says so if
it is missing). Installing over an earlier version keeps your profiles, apps and settings; Windows' **Installed apps**
removes it.

This build is not code-signed yet, so Windows SmartScreen may say it protected your PC. Check that the file's SHA-256
is the one below, then choose **More info**, then **Run anyway**.

``````
$sha256  $name
``````

Unattended: ``$name /quiet [--folder "D:\Program Files\Sallyport"] [--no-driver] [--language en|zh-Hans]`` answers
by its exit code (0 done, 1 failed, 2 bad arguments).

Also here: **$zipName**, the installed folder's files as they are installed, and **manifest.json**, each file's size
and SHA-256 and the two downloads', which Sallyport's updates check every file against.

## What's in $Version

$body
"@
[IO.File]::WriteAllText($notes, $text, (New-Object System.Text.UTF8Encoding $false))

"release: {0} ({1:N1} MB), SHA-256 {2}" -f $name, ((Get-Item $setup).Length / 1MB), $sha256
"folder:  {0} ({1:N1} MB, {2} files), checked against manifest.json" -f $zipName, ((Get-Item $zip).Length / 1MB), @($read.files).Count
"notes:   $notes"
