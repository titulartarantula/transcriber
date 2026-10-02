#Requires -Version 7
<#
.SYNOPSIS
    Saves the Google Play service account key so releases can upload to Play.

.DESCRIPTION
    Encrypts the JSON key downloaded from Google Cloud with DPAPI and stores it next to the signing key
    in %APPDATA%\TranscriberSigning (override with TRANSCRIBER_SIGNING_DIR). The saved copy only works
    for this Windows account. Delete the downloaded JSON afterwards; to use another PC, download a new
    key for the same service account and import it there.

.EXAMPLE
    ./scripts/import-play-key.ps1 ~/Downloads/transcriber-release-1a2b3c.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$Path
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'tools.ps1')

$json = Get-Content $Path -Raw
$key = $json | ConvertFrom-Json
if ($key.PSObject.Properties['type']?.Value -ne 'service_account' -or -not $key.PSObject.Properties['client_email']) {
    throw "$Path isn't a service account key. In Google Cloud: IAM & Admin → Service accounts → Keys → Add key → JSON."
}

$signing = Get-SigningPaths
New-Item -ItemType Directory -Force $signing.Directory | Out-Null
ConvertTo-SecureString $json -AsPlainText -Force | ConvertFrom-SecureString | Set-Content $signing.PlayKeyFile

Write-Host "Saved the key for $($key.client_email)."
Write-Host "Invite that address in Play Console → Users and permissions if you haven't, then delete $Path."
