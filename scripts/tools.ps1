# Shared helpers for the release scripts: locating build tools and the Android signing key.
# Each location can be overridden with an environment variable.

function Find-FirstExisting([string]$EnvName, [string[]]$Candidates, [string]$What) {
    $override = [Environment]::GetEnvironmentVariable($EnvName)
    if ($override) {
        if (-not (Test-Path $override)) { throw "$EnvName points to '$override', which doesn't exist." }
        return $override
    }
    $found = $Candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $found) { throw "Couldn't find $What. Set $EnvName to its location." }
    return $found
}

function Find-Dotnet10 {
    Find-FirstExisting 'TRANSCRIBER_DOTNET10' @(
        (Join-Path $env:LOCALAPPDATA 'dotnet10\dotnet.exe'),
        (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
    ) '.NET 10 SDK with the maui-android workload'
}

function Find-AndroidSdk {
    Find-FirstExisting 'TRANSCRIBER_ANDROID_SDK' @(
        $env:ANDROID_HOME,
        (Join-Path $env:LOCALAPPDATA 'Android\Sdk'),
        (Join-Path ${env:ProgramFiles(x86)} 'Android\android-sdk')
    ) 'the Android SDK'
}

function Find-Jdk {
    $bundled = Get-ChildItem (Join-Path $env:ProgramFiles 'Android\openjdk') -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | ForEach-Object FullName
    Find-FirstExisting 'TRANSCRIBER_JDK' (@($env:JAVA_HOME) + @($bundled)) 'a JDK (17 or later)'
}

function Get-SigningPaths {
    $dir = if ($env:TRANSCRIBER_SIGNING_DIR) { $env:TRANSCRIBER_SIGNING_DIR } else { Join-Path $env:APPDATA 'TranscriberSigning' }
    [pscustomobject]@{
        Directory    = $dir
        Keystore     = Join-Path $dir 'transcriber-release.keystore'
        PasswordFile = Join-Path $dir 'password.dat'
        Alias        = 'transcriber'
        PlayKeyFile  = Join-Path $dir 'play-key.dat'
    }
}

# The Google Play service account's JSON key, saved DPAPI-encrypted by ./scripts/import-play-key.ps1.
# Null if there isn't one, so releases fall back to a manual upload.
function Read-PlayKey {
    $file = (Get-SigningPaths).PlayKeyFile
    if (-not (Test-Path $file)) { return $null }
    $secure = (Get-Content $file -Raw).Trim() | ConvertTo-SecureString
    [System.Net.NetworkCredential]::new('', $secure).Password
}

function Read-SigningPassword {
    $signing = Get-SigningPaths
    if (-not (Test-Path $signing.PasswordFile)) {
        throw "No Android signing key in $($signing.Directory). Create one with ./scripts/new-signing-key.ps1."
    }
    $secure = (Get-Content $signing.PasswordFile -Raw).Trim() | ConvertTo-SecureString
    [System.Net.NetworkCredential]::new('', $secure).Password
}
