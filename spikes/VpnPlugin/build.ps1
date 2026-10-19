param(
    # Where the layout and the package are written; never inside the repository.
    [Parameter(Mandatory)] [string]$Out,
    # The folder holding makeappx.exe and signtool.exe (Microsoft.Windows.SDK.BuildTools' bin\<version>\x64).
    [Parameter(Mandatory)] [string]$Tools,
    # The test certificate's PFX and its password, kept outside the repository with the certificate.
    [Parameter(Mandatory)] [string]$Pfx,
    [Parameter(Mandatory)] [string]$PfxPassword
)
# Builds the VPN spike's package (D12): publishes the plug-in and the control console into a layout, packs it with
# makeappx and signs it with the test certificate.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$layout = Join-Path $Out 'layout'
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }

# The host and the component share one folder: the host's runtime loads the component, and both use the same C#/WinRT.
dotnet publish "$here\PluginHost\Sallyport.VpnSpike.PluginHost.csproj" -c Release -o "$layout\Plugin" | Out-Host
if ($LASTEXITCODE) { throw 'publishing the plug-in host failed' }
dotnet publish "$here\Plugin\Sallyport.VpnSpike.Plugin.csproj" -c Release -o "$layout\Plugin" | Out-Host
if ($LASTEXITCODE) { throw 'publishing the plug-in component failed' }
# Publish leaves the component's WinMD behind; the app model reads a class's metadata from it when it activates one.
Copy-Item "$here\Plugin\bin\Release\net10.0-windows10.0.22621.0\win-x64\Sallyport.VpnSpike.Plugin.winmd" "$layout\Plugin"
dotnet publish "$here\Control\Sallyport.VpnSpike.Control.csproj" -c Release -o "$layout\Control" | Out-Host
if ($LASTEXITCODE) { throw 'publishing the control console failed' }
Copy-Item "$here\package\AppxManifest.xml" $layout
Copy-Item "$here\package\Assets" "$layout\Assets" -Recurse

$msix = Join-Path $Out 'Sallyport.VpnSpike.msix'
& "$Tools\makeappx.exe" pack /d $layout /p $msix /o | Out-Host
if ($LASTEXITCODE) { throw 'makeappx failed' }
& "$Tools\signtool.exe" sign /fd SHA256 /f $Pfx /p $PfxPassword $msix | Out-Host
if ($LASTEXITCODE) { throw 'signtool failed' }
$msix
