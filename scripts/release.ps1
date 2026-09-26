#Requires -Version 7
<#
.SYNOPSIS
    Cuts a release of the Windows or Android app.

.DESCRIPTION
    1. Checks the repository is clean, on main, up to date, and the tag is free.
    2. Runs the tests.
    3. Bumps the app's version in Versions.props and moves its Unreleased entries in CHANGELOG.md into
       a new release section, then commits "Release Transcriber for <App> <version>".
    4. Builds the artifact (Windows: self-contained exe; Android: APK signed with the release key) and
       checks the version (and, for Android, the signing certificate) inside it.
    5. Tags <app>-v<version>, pushes the commit and tag together, and creates a GitHub release with the
       artifact attached and the changelog entries as notes.

    If the build fails, the release commit is undone and nothing is pushed.

.PARAMETER App
    windows or android.

.PARAMETER Bump
    Which part of the version to increase: patch (default), minor or major.

.PARAMETER Version
    An explicit new version (X.Y.Z) instead of -Bump.

.PARAMETER DryRun
    Build the artifact with the next version number but commit, tag and publish nothing.

.PARAMETER AllowEmptyNotes
    Release even though CHANGELOG.md has no Unreleased entries for the app.

.EXAMPLE
    ./scripts/release.ps1 android -Bump minor
.EXAMPLE
    ./scripts/release.ps1 windows -DryRun
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('windows', 'android')]
    [string]$App,

    [ValidateSet('patch', 'minor', 'major')]
    [string]$Bump = 'patch',

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [switch]$DryRun,

    [switch]$AllowEmptyNotes
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'tools.ps1')

$root = Split-Path $PSScriptRoot -Parent
$name = @{ windows = 'Windows'; android = 'Android' }[$App]

function Step([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

# Arguments go in an array so PowerShell never mistakes "-v" or "-p:..." for its own parameters.
# Output goes to the console, not the pipeline, so functions that call this return only what they mean to.
function Invoke-Checked([string]$File, [string[]]$Arguments) {
    & $File @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "'$File $($Arguments -join ' ')' failed with exit code $LASTEXITCODE." }
}

# ----- Versions ----------------------------------------------------------------------------

function Get-NextVersion {
    $props = Get-Content (Join-Path $root 'Versions.props') -Raw
    $m = [regex]::Match($props, "<$($name)Version>(\d+)\.(\d+)\.(\d+)</$($name)Version>")
    if (-not $m.Success) { throw "Versions.props has no <$($name)Version>." }
    $current = [version]::new([int]$m.Groups[1].Value, [int]$m.Groups[2].Value, [int]$m.Groups[3].Value)

    $next = if ($Version) { [version]$Version } else {
        switch ($Bump) {
            'major' { [version]::new($current.Major + 1, 0, 0) }
            'minor' { [version]::new($current.Major, $current.Minor + 1, 0) }
            default { [version]::new($current.Major, $current.Minor, $current.Build + 1) }
        }
    }
    if ($next -le $current) { throw "The new version ($($next.ToString(3))) must be higher than $($current.ToString(3))." }
    if ($App -eq 'android' -and ($next.Minor -gt 99 -or $next.Build -gt 99)) {
        throw 'Android versionCode is MAJOR*10000 + MINOR*100 + PATCH, so minor and patch must stay below 100.'
    }
    return @{ Current = $current.ToString(3); Next = $next.ToString(3) }
}

function Set-Version([string]$New) {
    $path = Join-Path $root 'Versions.props'
    $props = Get-Content $path -Raw
    $props = [regex]::Replace($props, "<$($name)Version>[^<]*</$($name)Version>", "<$($name)Version>$New</$($name)Version>")
    Set-Content $path $props -NoNewline
}

# ----- Changelog ---------------------------------------------------------------------------

# Finds "## Unreleased" → "### <App>" and returns the section boundaries and its entries.
function Get-ChangelogSection {
    $lines = @(Get-Content (Join-Path $root 'CHANGELOG.md'))
    $unreleased = [array]::IndexOf($lines, '## Unreleased')
    if ($unreleased -lt 0) { throw 'CHANGELOG.md has no "## Unreleased" section.' }

    $end = $lines.Count
    for ($i = $unreleased + 1; $i -lt $lines.Count; $i++) { if ($lines[$i].StartsWith('## ')) { $end = $i; break } }

    $heading = -1
    for ($i = $unreleased + 1; $i -lt $end; $i++) { if ($lines[$i] -eq "### $name") { $heading = $i; break } }
    if ($heading -lt 0) { throw "CHANGELOG.md's Unreleased section has no ""### $name"" heading." }

    $headingEnd = $end
    for ($i = $heading + 1; $i -lt $end; $i++) { if ($lines[$i].StartsWith('#')) { $headingEnd = $i; break } }

    $entries = @(if ($headingEnd - $heading -gt 1) { $lines[($heading + 1)..($headingEnd - 1)] })
    # Trim blank lines around the entries.
    while ($entries.Count -gt 0 -and -not $entries[0].Trim()) { $entries = @($entries | Select-Object -Skip 1) }
    while ($entries.Count -gt 0 -and -not $entries[-1].Trim()) { $entries = @($entries | Select-Object -SkipLast 1) }

    [pscustomobject]@{ Lines = $lines; Heading = $heading; HeadingEnd = $headingEnd; End = $end; Entries = $entries }
}

# Empties "### <App>" under Unreleased and adds "## <App> <version> - <date>" with its entries below Unreleased.
function Update-Changelog($section, [string]$New, [string[]]$Entries) {
    $l = $section.Lines
    $out = [System.Collections.Generic.List[string]]::new()
    $out.AddRange([string[]]$l[0..$section.Heading])
    $out.Add('')
    if ($section.HeadingEnd -lt $section.End) { $out.AddRange([string[]]$l[$section.HeadingEnd..($section.End - 1)]) }
    while ($out.Count -gt 0 -and -not $out[$out.Count - 1].Trim()) { $out.RemoveAt($out.Count - 1) }
    $out.Add('')
    $out.Add("## $name $New - $(Get-Date -Format 'yyyy-MM-dd')")
    $out.Add('')
    $out.AddRange($Entries)
    $out.Add('')
    if ($section.End -lt $l.Count) { $out.AddRange([string[]]$l[$section.End..($l.Count - 1)]) }
    Set-Content (Join-Path $root 'CHANGELOG.md') $out
}

# ----- Builds ------------------------------------------------------------------------------

function Build-Windows([string]$New) {
    $out = Join-Path $root "artifacts\windows-$New"
    Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
    Invoke-Checked 'dotnet' @(
        'publish', (Join-Path $root 'src\Transcriber.App'), '-c', 'Release', '-r', 'win-x64', '--self-contained',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true',
        '-p:DebugType=none', "-p:WindowsVersion=$New", '-o', $out, '--nologo', '-v', 'q')

    $artifact = Join-Path $root "artifacts\Transcriber-windows-$New.exe"
    Copy-Item (Join-Path $out 'Transcriber.exe') $artifact -Force

    $embedded = (Get-Item $artifact).VersionInfo.ProductVersion
    if (-not "$embedded".StartsWith($New)) { throw "The built exe reports version '$embedded', expected $New." }
    Write-Host "    $artifact ($embedded)"
    return $artifact
}

function Build-Android([string]$New) {
    $dotnet10 = Find-Dotnet10
    $sdk = Find-AndroidSdk
    $jdk = Find-Jdk
    $signing = Get-SigningPaths
    $env:TRANSCRIBER_SIGNING_PASSWORD = Read-SigningPassword

    $out = Join-Path $root "artifacts\android-$New"
    Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
    Invoke-Checked $dotnet10 @(
        'publish', (Join-Path $root 'src\Transcriber.Mobile'), '-c', 'Release', '-f', 'net10.0-android',
        '-p:PhoneOnly=true', "-p:AndroidVersion=$New", "-p:AndroidSdkDirectory=$sdk", "-p:JavaSdkDirectory=$jdk",
        '-p:AndroidKeyStore=true', "-p:AndroidSigningKeyStore=$($signing.Keystore)", "-p:AndroidSigningKeyAlias=$($signing.Alias)",
        '-p:AndroidSigningStorePass=env:TRANSCRIBER_SIGNING_PASSWORD', '-p:AndroidSigningKeyPass=env:TRANSCRIBER_SIGNING_PASSWORD',
        '-o', $out, '--nologo', '-v', 'q')

    $apk = Get-ChildItem $out -Filter '*-Signed.apk' | Select-Object -First 1
    if (-not $apk) { throw "No signed APK was produced in $out." }
    $artifact = Join-Path $root "artifacts\Transcriber-android-$New.apk"
    Copy-Item $apk.FullName $artifact -Force

    # Check the version and that it's signed with the release key, not a debug key.
    $buildTools = Get-ChildItem (Join-Path $sdk 'build-tools') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    $package = & (Join-Path $buildTools.FullName 'aapt2.exe') dump badging $artifact | Select-String '^package:'
    $code = [int]$New.Split('.')[0] * 10000 + [int]$New.Split('.')[1] * 100 + [int]$New.Split('.')[2]
    if ("$package" -notmatch "versionCode='$code'" -or "$package" -notmatch "versionName='$([regex]::Escape($New))'") {
        throw "The APK reports '$package', expected version $New (code $code)."
    }

    $env:JAVA_HOME = $jdk
    $certs = & (Join-Path $buildTools.FullName 'apksigner.bat') verify --print-certs $artifact
    if ($LASTEXITCODE -ne 0) { throw 'apksigner could not verify the APK.' }
    $apkSha = ([regex]::Match("$certs", 'SHA-256 digest: ([0-9a-f]+)')).Groups[1].Value
    $keySha = ((& (Join-Path $jdk 'bin\keytool.exe') -list -v -keystore $signing.Keystore -storepass $env:TRANSCRIBER_SIGNING_PASSWORD `
        -alias $signing.Alias | Select-String 'SHA256:').ToString() -replace '.*SHA256:\s*', '' -replace ':', '').ToLowerInvariant()
    if (-not $apkSha -or $apkSha -ne $keySha) { throw "The APK isn't signed with the release key (APK $apkSha, key $keySha)." }

    Write-Host "    $artifact (versionCode $code, release key)"
    return $artifact
}

# ----- Release -----------------------------------------------------------------------------

Push-Location $root
$committed = $false
try {
    $versions = Get-NextVersion
    $new = $versions.Next
    $tag = "$App-v$new"
    $title = "Transcriber for $name $new"

    $section = Get-ChangelogSection
    $entries = $section.Entries
    if ($entries.Count -eq 0) {
        if (-not $AllowEmptyNotes) { throw "CHANGELOG.md has no Unreleased entries under ""### $name"". Add them, or pass -AllowEmptyNotes." }
        $entries = @('- Maintenance release.')
    }
    $notes = $entries -join "`n"

    if (-not $DryRun) {
        Step 'Checking the repository'
        if (git status --porcelain) { throw 'There are uncommitted changes. Commit or stash them first.' }
        if ((git rev-parse --abbrev-ref HEAD) -ne 'main') { throw 'Releases are cut from main.' }
        Invoke-Checked 'git' @('fetch', '--quiet', 'origin')
        if ([int](git rev-list --count HEAD..origin/main) -gt 0) { throw 'main is behind origin/main. Pull first.' }
        if (git tag --list $tag) { throw "Tag $tag already exists." }
        if (git ls-remote --tags origin "refs/tags/$tag") { throw "Tag $tag already exists on GitHub." }
        gh auth status *> $null
        if ($LASTEXITCODE -ne 0) { throw "The GitHub CLI isn't signed in. Run 'gh auth login'." }
    }
    if ($App -eq 'android') { $null = Read-SigningPassword } # fail early if the key is missing

    Step "$title (currently $($versions.Current))$(if ($DryRun) { ' — dry run' })"
    Write-Host $notes

    Step 'Running tests'
    Invoke-Checked 'dotnet' @('test', (Join-Path $root 'tests\Transcriber.Tests'), '--nologo', '-v', 'q')

    if (-not $DryRun) {
        Step 'Updating Versions.props and CHANGELOG.md'
        Set-Version $new
        Update-Changelog $section $new $entries
        Invoke-Checked 'git' @('add', 'Versions.props', 'CHANGELOG.md')
        Invoke-Checked 'git' @('commit', '--quiet', '-m', "Release $title")
        $committed = $true
    }

    Step 'Building'
    $artifact = if ($App -eq 'windows') { Build-Windows $new } else { Build-Android $new }

    if ($DryRun) {
        Write-Host "Dry run complete: $artifact. Nothing was committed, tagged or published." -ForegroundColor Green
        return
    }

    Step "Tagging $tag and pushing"
    Invoke-Checked 'git' @('tag', '-a', $tag, '-m', "$title`n`n$notes")
    Invoke-Checked 'git' @('push', '--atomic', '--quiet', 'origin', 'main', $tag)
    $committed = $false # pushed: never undo from here on

    Step 'Creating the GitHub release'
    $notesFile = Join-Path $root "artifacts\$tag-notes.md"
    Set-Content $notesFile $notes
    gh release create $tag $artifact --title $title --notes-file $notesFile --verify-tag
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "The tag is pushed but the GitHub release wasn't created. Retry with:`n  gh release create $tag `"$artifact`" --title `"$title`" --notes-file `"$notesFile`" --verify-tag"
        exit 1
    }
    Write-Host "Released $title" -ForegroundColor Green
}
catch {
    if ($committed) {
        Write-Warning 'Undoing the release commit.'
        git tag --delete $tag 2>$null | Out-Null
        git reset --hard --quiet HEAD~1
    }
    throw
}
finally {
    Remove-Item Env:TRANSCRIBER_SIGNING_PASSWORD -ErrorAction SilentlyContinue
    Pop-Location
}
