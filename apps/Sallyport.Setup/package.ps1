# Builds Sallyport's setup (docs/design/product.md §6): the client and the service published self-contained for
# win-x64 into one folder — no .NET to install first — zipped, and carried inside a single-file Sallyport.Setup.exe.
#   powershell -File apps\Sallyport.Setup\package.ps1 [-Out artifacts]
param([string]$Out = "artifacts")
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..\..").Path
$artifacts = Join-Path $root $Out
$payload = Join-Path $artifacts "payload"
$zip = Join-Path $artifacts "payload.zip"
if (Test-Path $payload) { Remove-Item -Recurse -Force $payload }
if (Test-Path $zip) { Remove-Item -Force $zip }
New-Item -ItemType Directory -Force $artifacts | Out-Null

# Each program one file (the owner: the install folder's root tidied to its programs), the runtime inside it; only
# WebView2's native loader stays beside the client, rather than being unpacked into %TEMP% on the system drive when it
# starts. The setup too, without a payload: the uninstaller in the install folder.
$single = '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none',
    '-p:PublishDocumentationFile=false', '-p:PublishReferencesDocumentationFiles=false'
foreach ($app in 'Sallyport.Client', 'Sallyport.Service', 'Sallyport.Setup') {
    dotnet publish "$root\apps\$app" -c Release -r win-x64 --self-contained -nologo -v q -o $payload @single
    if ($LASTEXITCODE -ne 0) { throw "publishing $app failed" }
}
# The publish also leaves WebView2's loader under runtimes\, a second copy of the one beside the client, which WebView2
# finds first.
if (Test-Path (Join-Path $payload 'WebView2Loader.dll')) { Remove-Item -Recurse -Force (Join-Path $payload 'runtimes') -ErrorAction SilentlyContinue }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($payload, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

dotnet publish "$root\apps\Sallyport.Setup" -c Release -r win-x64 --self-contained -nologo -v q -o $artifacts `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true "-p:Payload=$zip"
if ($LASTEXITCODE -ne 0) { throw "publishing the setup failed" }
$setup = Get-Item (Join-Path $artifacts "Sallyport.Setup.exe")
"payload: {0:N0} files, {1:N1} MB unpacked, {2:N1} MB zipped; setup: {3:N1} MB" -f `
    (Get-ChildItem $payload -Recurse -File).Count, ((Get-ChildItem $payload -Recurse -File | Measure-Object Length -Sum).Sum / 1MB),
    ((Get-Item $zip).Length / 1MB), ($setup.Length / 1MB)
