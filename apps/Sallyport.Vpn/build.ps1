param(
    # Where the layout and the package are written; never inside the repository.
    [Parameter(Mandatory)] [string]$Out,
    # The folder holding makeappx.exe and signtool.exe (Microsoft.Windows.SDK.BuildTools' bin\<version>\x64).
    [Parameter(Mandatory)] [string]$Tools,
    # The test certificate's PFX and its password, kept outside the repository with the certificate.
    [Parameter(Mandatory)] [string]$Pfx,
    [Parameter(Mandatory)] [string]$PfxPassword
)
# Builds Sallyport's per-app VPN package (D12): publishes the plug-in and the bridge into a layout, packs it with makeappx
# and signs it with the test certificate.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$layout = Join-Path $Out 'layout'
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }

# The host and the component share one folder: the host's runtime loads the component, and both use the same C#/WinRT.
dotnet publish "$here\PluginHost\Sallyport.Vpn.PluginHost.csproj" -c Release -o "$layout\Plugin" | Out-Host
if ($LASTEXITCODE) { throw 'publishing the plug-in host failed' }
dotnet publish "$here\Plugin\Sallyport.Vpn.Plugin.csproj" -c Release -o "$layout\Plugin" | Out-Host
if ($LASTEXITCODE) { throw 'publishing the plug-in component failed' }
# Publish leaves the component's WinMD behind; the app model reads a class's metadata from it when it activates one.
# Found rather than named: the x64 platform puts the build under bin\x64\, and the folder shape is the SDK's to change.
$winmd = Get-ChildItem "$here\Plugin\bin" -Recurse -Filter 'Sallyport.Vpn.Plugin.winmd' |
    Where-Object { $_.FullName -match '\\Release\\' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $winmd) { throw 'the plug-in component built no WinMD' }
Copy-Item $winmd.FullName "$layout\Plugin"
dotnet publish "$here\Bridge\Sallyport.Vpn.Bridge.csproj" -c Release -o "$layout\Bridge" | Out-Host
if ($LASTEXITCODE) { throw 'publishing the bridge failed' }
Copy-Item "$here\package\AppxManifest.xml" $layout
Copy-Item "$here\package\Assets" "$layout\Assets" -Recurse

$msix = Join-Path $Out 'Sallyport.Vpn.msix'
# makeappx names every file it packs; that is only worth reading when it fails.
$packed = & "$Tools\makeappx.exe" pack /d $layout /p $msix /o
if ($LASTEXITCODE) { $packed | Out-Host; throw 'makeappx failed' }
& "$Tools\signtool.exe" sign /fd SHA256 /f $Pfx /p $PfxPassword $msix | Out-Host
if ($LASTEXITCODE) { throw 'signtool failed' }
$msix
