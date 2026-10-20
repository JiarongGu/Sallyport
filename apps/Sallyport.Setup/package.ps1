# Builds Sallyport's release (docs/DECISIONS.md D28): the client and the service published self-contained for win-x64
# (no .NET to install first), zipped and carried inside one single-file setup, named for its version, with its SHA-256
# beside it and the notes the GitHub release shows.
#   powershell -File apps\Sallyport.Setup\package.ps1 [-Out artifacts] [-Version 0.1.0]
# artifacts\release\  Sallyport-<version>-win-x64-setup.exe and its .sha256: what is published
# artifacts\release-notes.md                          what the release says
# artifacts\build\    the payload and the setup's own publish, kept for a look and replaced by the next run
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
$payload = Join-Path $build "payload"
$zip = Join-Path $build "payload.zip"

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

# Each program one file (the owner: the install folder's root tidied to its programs), the runtime inside it; only
# WebView2's native loader stays beside the client, rather than being unpacked into %TEMP% on the system drive when it
# starts. The setup too, without a payload: the uninstaller in the install folder.
$common = '-c', 'Release', '-r', 'win-x64', '--self-contained', '-nologo', '-v', 'q', "-p:Version=$Version"
$single = '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none',
    '-p:PublishDocumentationFile=false', '-p:PublishReferencesDocumentationFiles=false'
foreach ($app in 'Sallyport.Client', 'Sallyport.Service', 'Sallyport.Setup') {
    Run "publishing $app" { dotnet publish "$root\apps\$app" @common -o $payload @single }
}
# The publish also leaves WebView2's loader under runtimes\, a second copy of the one beside the client, which WebView2
# finds first.
if (Test-Path (Join-Path $payload 'WebView2Loader.dll')) { Remove-Item -Recurse -Force (Join-Path $payload 'runtimes') -ErrorAction SilentlyContinue }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($payload, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$setupBuild = Join-Path $build "setup"
Run 'publishing the setup' {
    dotnet publish "$root\apps\Sallyport.Setup" @common -o $setupBuild `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true "-p:Payload=$zip"
}

$name = "Sallyport-$Version-win-x64-setup.exe"
$setup = Join-Path $release $name
Copy-Item (Join-Path $setupBuild "Sallyport.Setup.exe") $setup
$sha256 = (Get-FileHash -Algorithm SHA256 $setup).Hash.ToLowerInvariant()
# sha256sum's own form, so `sha256sum -c` checks it where the file is downloaded.
[IO.File]::WriteAllText("$setup.sha256", "$sha256  $name`n", (New-Object System.Text.UTF8Encoding $false))

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

## What's in $Version

$body
"@
[IO.File]::WriteAllText($notes, $text, (New-Object System.Text.UTF8Encoding $false))

$payloadFiles = Get-ChildItem $payload -Recurse -File
"payload: {0:N0} files, {1:N1} MB unpacked, {2:N1} MB zipped" -f $payloadFiles.Count, (($payloadFiles | Measure-Object Length -Sum).Sum / 1MB), ((Get-Item $zip).Length / 1MB)
"release: {0} ({1:N1} MB), SHA-256 {2}" -f $name, ((Get-Item $setup).Length / 1MB), $sha256
"notes:   $notes"
