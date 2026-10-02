#Requires -Version 7
<#
.SYNOPSIS
    Uploads an App Bundle to Google Play and rolls it out on a track.

.DESCRIPTION
    release.ps1 runs this after publishing an Android release. Run it by hand to upload a bundle that
    release.ps1 didn't, or to retry. Needs the service account key saved by ./scripts/import-play-key.ps1.

.PARAMETER Bundle
    The .aab to upload, e.g. artifacts\Transcriber-android-0.3.1.aab.

.PARAMETER Notes
    The release's changelog entries; they become the "What's new" text testers see.

.PARAMETER Track
    internal (default), alpha, beta or production.

.EXAMPLE
    ./scripts/play-upload.ps1 artifacts\Transcriber-android-0.3.1.aab
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$Bundle,

    [string[]]$Notes = @(),

    [ValidateSet('internal', 'alpha', 'beta', 'production')]
    [string]$Track = 'internal',

    [string]$Language = 'en-US'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'tools.ps1')

$Bundle = (Resolve-Path $Bundle).Path
$key = Read-PlayKey
if (-not $key) { throw 'No Google Play key saved. Import one with ./scripts/import-play-key.ps1.' }

# Changelog markdown → plain bullets. Play allows 500 characters of release notes.
$text = (($Notes -join "`n") -replace '(?m)^\s*-\s+', '• ' -replace '(?m)\n\s{2,}(?=\S)', ' ' -replace '\*\*', '').Trim()
if ($text.Length -gt 500) { $text = $text.Substring(0, 499) + '…' }
$notesFile = [System.IO.Path]::GetTempFileName()
Set-Content $notesFile $text -NoNewline

$env:TRANSCRIBER_PLAY_KEY = $key
try {
    # No --nologo: dotnet run passes options it doesn't know through to the program.
    & dotnet run --project (Join-Path $PSScriptRoot 'PlayUpload') -c Release -v q -- `
        'dev.transcriber.app' $Bundle $Track $Language $notesFile
    if ($LASTEXITCODE -ne 0) { throw "The Play upload failed (exit code $LASTEXITCODE)." }
}
finally {
    Remove-Item Env:TRANSCRIBER_PLAY_KEY -ErrorAction SilentlyContinue
    Remove-Item $notesFile -ErrorAction SilentlyContinue
}
