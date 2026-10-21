# The release's version, written in the two places that say it (docs/DECISIONS.md D28): Directory.Build.props'
# <Version>, and the CHANGELOG section that names it, dated. The release workflow runs it before it builds and commits
# what it wrote only after the release exists; run here, it shows what a release would do (git checkout undoes it).
#   powershell -File apps\Sallyport.Setup\version.ps1 [-Version 0.2.0 | -Bump none|patch|minor|major] [-Date yyyy-MM-dd]
# The library's package has a version of its own, in its project and its own changelog (nuget.yml, D36):
#   powershell -File apps\Sallyport.Setup\version.ps1 -VersionFile src\Sallyport\Sallyport.csproj -ChangelogFile src\Sallyport\CHANGELOG.md -TagPrefix nuget-v
# Prints the version. Under GitHub Actions it also writes version, tag and changed to $GITHUB_OUTPUT.
param(
    [string]$Version,
    [ValidateSet('none', 'patch', 'minor', 'major')][string]$Bump = 'none',
    [string]$Date = (Get-Date -Format 'yyyy-MM-dd'),
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot ../..)).Path,
    [string]$VersionFile = 'Directory.Build.props',
    [string]$ChangelogFile = 'CHANGELOG.md',
    [string]$TagPrefix = 'v'
)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding $false
# The changelog's headings carry an em dash; written as a char so the script reads the same under any codepage.
$dash = [char]0x2014

$props = Join-Path $Root $VersionFile
$propsText = [IO.File]::ReadAllText($props, $utf8)
if ($propsText -notmatch '<Version>(\d+)\.(\d+)\.(\d+)</Version>') { throw "$VersionFile has no <Version>X.Y.Z</Version>." }
$current = "$($Matches[1]).$($Matches[2]).$($Matches[3])"
[int]$major = $Matches[1]; [int]$minor = $Matches[2]; [int]$patch = $Matches[3]

$final = if ($Version) { $Version.Trim() } else {
    switch ($Bump) {
        'major' { "$($major + 1).0.0" }
        'minor' { "$major.$($minor + 1).0" }
        'patch' { "$major.$minor.$($patch + 1)" }
        default { $current }
    }
}
if ($final -notmatch '^\d+\.\d+\.\d+$') { throw "A version is X.Y.Z; '$final' is not." }

# Between releases the changelog heads at "## Unreleased", or at "## X.Y.Z - unreleased" (an em dash) when the next
# number is already known; a release names and dates it. A section already dated is a re-run after a failure.
$changelog = Join-Path $Root $ChangelogFile
$log = [IO.File]::ReadAllText($changelog, $utf8)
$dated = "## $final $dash $Date"
# The heading alone: its line end (LF or CRLF) and the blank line under it stay as they are.
$end = '[ \t]*(?=\r?$)'
$number = [regex]::Escape($final)
if ($log -match "(?m)^## $number $dash \d{4}-\d{2}-\d{2}$end") {
    Write-Host "$ChangelogFile already dates ${final}: a re-run, left as it is."
} elseif ($log -match "(?m)^## $number $dash unreleased$end") {
    $log = ([regex]"(?m)^## $number $dash unreleased$end").Replace($log, $dated, 1)
} elseif ($log -match "(?m)^## Unreleased$end") {
    $log = ([regex]"(?m)^## Unreleased$end").Replace($log, $dated, 1)
} else {
    throw "$ChangelogFile heads at neither '## Unreleased' nor '## $final $dash unreleased': write what ${final} brings first."
}

$changed = $false
if ($final -ne $current) {
    [IO.File]::WriteAllText($props, $propsText.Replace("<Version>$current</Version>", "<Version>$final</Version>"), $utf8)
    $changed = $true
}
if ($log -ne [IO.File]::ReadAllText($changelog, $utf8)) {
    [IO.File]::WriteAllText($changelog, $log, $utf8)
    $changed = $true
}

if ($env:GITHUB_OUTPUT) {
    Add-Content -Path $env:GITHUB_OUTPUT -Encoding utf8 -Value "version=$final", "tag=$TagPrefix$final", "changed=$($changed.ToString().ToLowerInvariant())"
}
Write-Host "version $current -> $final$(if ($changed) { ' (written)' } else { ' (nothing to write)' })"
$final
