#Requires -Version 7
<#
.SYNOPSIS
    Creates the Android release signing key, or shows its password for backing up.

.DESCRIPTION
    The keystore and a DPAPI-encrypted copy of its password live outside the repository, in
    %APPDATA%\TranscriberSigning (override with TRANSCRIBER_SIGNING_DIR). Every Android release must be
    signed with this key for updates to install over earlier versions, so back up the keystore file
    and the password (run with -ShowPassword) somewhere safe, such as a password manager.

.EXAMPLE
    ./scripts/new-signing-key.ps1
.EXAMPLE
    ./scripts/new-signing-key.ps1 -ShowPassword
.EXAMPLE
    ./scripts/new-signing-key.ps1 -ImportPassword
    On a new PC: after copying transcriber-release.keystore into the signing folder, saves its password.
#>
[CmdletBinding()]
param(
    [switch]$ShowPassword,
    [switch]$ImportPassword
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'tools.ps1')

$signing = Get-SigningPaths

if ($ShowPassword) {
    if (-not (Test-Path $signing.PasswordFile)) { throw "No signing key found in $($signing.Directory)." }
    Write-Host "Keystore: $($signing.Keystore)"
    Write-Host "Alias:    $($signing.Alias)"
    Write-Host "Password: $(Read-SigningPassword)"
    return
}

if ($ImportPassword) {
    if (-not (Test-Path $signing.Keystore)) { throw "Copy transcriber-release.keystore into $($signing.Directory) first." }
    $secure = Read-Host 'Keystore password' -AsSecureString
    $plain = [System.Net.NetworkCredential]::new('', $secure).Password
    & (Join-Path (Find-Jdk) 'bin\keytool.exe') -list -keystore $signing.Keystore -storepass $plain -alias $signing.Alias *> $null
    if ($LASTEXITCODE -ne 0) { throw 'That password does not open the keystore.' }
    $secure | ConvertFrom-SecureString | Set-Content $signing.PasswordFile
    Write-Host "Saved. Releases on this PC will sign with $($signing.Keystore)."
    return
}

if (Test-Path $signing.Keystore) {
    throw "A signing key already exists at $($signing.Keystore). Replacing it would stop updates installing; delete it by hand only if you mean to."
}

$keytool = Join-Path (Find-Jdk) 'bin\keytool.exe'
New-Item -ItemType Directory -Force $signing.Directory | Out-Null

# 32 random characters from an unambiguous alphabet.
$alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789'
$password = -join (1..32 | ForEach-Object { $alphabet[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($alphabet.Length)] })

& $keytool -genkeypair -keystore $signing.Keystore -storetype PKCS12 -alias $signing.Alias `
    -keyalg RSA -keysize 4096 -validity 36500 `
    -storepass $password -keypass $password `
    -dname 'CN=Transcriber, O=Transcriber' 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "keytool failed ($LASTEXITCODE)." }

ConvertTo-SecureString $password -AsPlainText -Force | ConvertFrom-SecureString | Set-Content $signing.PasswordFile

$fingerprint = (& $keytool -list -v -keystore $signing.Keystore -storepass $password -alias $signing.Alias |
    Select-String 'SHA256:').ToString().Trim()

Write-Host "Created $($signing.Keystore)"
Write-Host "Certificate $fingerprint"
Write-Host ''
Write-Host 'Back up the keystore file and its password now (./scripts/new-signing-key.ps1 -ShowPassword).'
Write-Host 'The saved password copy only works for this Windows account.'
