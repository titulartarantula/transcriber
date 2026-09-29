# Transcriber

A small Windows 11 app that records one or more audio sources at once, transcribes them with your own
Whisper server, separates and names the speakers, and saves a Markdown note to a folder or straight into
your Obsidian vault.

## What it does

- **Records several sources together.** Pick any mix of microphones and outputs. An output is captured
  through WASAPI loopback, so "Speakers" records everything playing on it: Teams, Zoom, a browser tab.
  Each source goes to its own track on a shared timeline.
- **Transcribes with your server.** It works with any OpenAI-compatible `/v1/audio/transcriptions`
  endpoint (faster-whisper-server, Speaches, LocalAI, OpenAI). The API key is optional and is stored
  encrypted with Windows DPAPI.
- **Separates speakers.**
  - Each source is labelled with its own name, for example *Me* for your mic and *Remote* for the call.
  - Sources with **Split voices** on are diarized locally (pyannote segmentation plus speaker embeddings,
    via sherpa-onnx, on the CPU), which gives *Remote 1*, *Remote 2*, and so on.
  - Names are suggested from what people say: self-introductions ("Hi, I'm Priya") and greetings that
    name whoever replies ("Thanks, Sanjay.").
  - Before the note is saved, a dialog shows each speaker with a sample line so you can confirm or type
    names.
  - If you listen on speakers rather than headphones, your mic re-records the call. Those mic lines are
    detected and dropped.
- **Writes a note** with YAML frontmatter (date, start and end time, duration, speakers, sources, model, tags) and
  a transcript stamped with the time of day (24-hour clock):

  ```markdown
  **[14:03:12] Sanjay:** Yeah, sure. Generative AI refers to …
  ```

  You can save it as a Markdown file in any folder, or push it through the Obsidian Local REST API. If
  Obsidian isn't running, the note is kept in `%LOCALAPPDATA%\Transcriber\unsent` so nothing is lost.
- **Transcribe a file…** runs an existing recording through the same pipeline.

## Setup

1. Run `publish\Transcriber.exe`. It is self-contained, so .NET doesn't need to be installed.
2. **Settings → Speech to text:** enter your server URL, for example `http://192.168.1.100:8000`, and
   click **Test and load models**.
3. **Settings → Output:**
   - *Markdown file in a folder.* Pointing it at a folder inside your vault works without any plugin.
   - *Obsidian vault.* Install the **Local REST API** community plugin, copy its API key from the
     plugin settings, and click **Test connection**. The default URL is `https://127.0.0.1:27124`.

### Obsidian through an MCP server (works from anywhere)

If your vault is exposed through a remote Obsidian MCP server (OAuth-protected, Streamable HTTP), choose
**Obsidian through an MCP server**, enter its URL (for example `https://obsidian-mcp.example.com/mcp`) and
click **Sign in**. Your browser opens once to approve. The app registers itself as an OAuth client, and
keeps an encrypted refresh token in `%APPDATA%\Transcriber\mcp-auth.dat`. Notes are written with the
server's `obsidian_create_note` tool. The target folder is listed first so an existing note is never
overwritten. The server must offer `obsidian_create_note` and `obsidian_list_directory`.

### Obsidian on another PC (LAN or WireGuard)

Point the REST API URL at the Obsidian PC's address, for example `https://192.168.1.100:27124`. The
plugin's certificate is self-signed and only names `127.0.0.1`, so Windows can't verify it. The first
**Test connection** shows its SHA-256 fingerprint and asks whether to trust it. After that, the app
accepts that exact certificate and nothing else. If the certificate later changes, the app refuses to
connect and asks again. **Forget** in Settings clears the trusted fingerprint.

To compare the fingerprint, run this on the Obsidian PC:

```powershell
$t=[Net.Sockets.TcpClient]::new('127.0.0.1',27124); $s=[Net.Security.SslStream]::new($t.GetStream(),$false,{$true})
$s.AuthenticateAsClient('127.0.0.1'); [Security.Cryptography.X509Certificates.X509Certificate2]::new($s.RemoteCertificate).GetCertHashString('SHA256')
```

On the Obsidian PC, the plugin must listen on all interfaces rather than only 127.0.0.1 (its binding host
setting). Windows Firewall must also allow inbound TCP 27124 from your WireGuard subnet.

### Microphones over Remote Desktop

Inside an RDP session Windows shows only the audio devices the RDP client redirects, so the host's mics
disappear. To record your local mic, open Remote Desktop Connection and go to **Show Options → Local
Resources → Remote audio → Settings → Remote audio recording → Record from this computer**, then
reconnect.

The first time voices are split, about 32 MB of models are downloaded to `%LOCALAPPDATA%\Transcriber\models`.

## Tips for better speaker separation

- If you know how many people are on the remote side, set **Voices per split source**. That's the most
  reliable setting. On Auto, the slider in Settings trades finding more voices against merging similar ones.
- Leave **Split voices** off for your own headset mic. It only ever hears you, and its label ("Me") is
  already right.
- Wear headphones if you can. Echo removal catches most leakage, but a clean mic track is better.

## Where things live

| What | Path |
| --- | --- |
| Settings (keys DPAPI-encrypted) | `%APPDATA%\Transcriber\settings.json` |
| Recordings in progress, or ones that failed to transcribe | `%LOCALAPPDATA%\Transcriber\recordings` |
| Diarization models | `%LOCALAPPDATA%\Transcriber\models` |
| Notes Obsidian couldn't accept | `%LOCALAPPDATA%\Transcriber\unsent` |
| Crash log | `%LOCALAPPDATA%\Transcriber\error.log` |

Recordings are deleted once the note is saved. If transcription fails, the audio is kept and **Retry**
sends it again.

## Android app

`src/Transcriber.Mobile` is an Android version (.NET 10 MAUI, Android 10 and later) that shares the core
library: the same Whisper client, on-device speaker separation, naming, Markdown and Obsidian outputs.

- **Microphone:** choose the phone's own mic or a connected Bluetooth, wired or USB headset. Bluetooth
  headsets are switched to their call (hands-free) mic while recording; they appear in the list once
  connected for calls, not just media.
- **Speakers:** one mic in a room usually hears several people, so **Split voices** is on by default and
  the naming screen appears before saving.
- **Background recording:** a notification keeps recording and transcription running with the screen off
  or while you use other apps.
- **Outputs:** Obsidian through an MCP server, the Local REST API over LAN/WireGuard (with the same
  certificate pinning), or a folder on the phone picked with Android's folder picker, for example the vault
  folder Obsidian mobile or Syncthing uses.
- API keys and sign-in tokens are encrypted with a key held in the Android Keystore.

Install the APK attached to an `android-v*` GitHub release by opening it on the phone (allow installs from
that source when asked). Release APKs are signed with the release key described under
[Versions and releases](#versions-and-releases).

Build (needs the .NET 10 SDK with the `maui-android` workload, the Android SDK and a JDK):

```powershell
dotnet publish src/Transcriber.Mobile -c Release -f net10.0-android `
  -p:AndroidSdkDirectory="C:\Program Files (x86)\Android\android-sdk" `
  -p:JavaSdkDirectory="C:\Program Files\Android\openjdk\jdk-21.0.8" -o publish/android
```

Add `-p:PhoneOnly=true` for a smaller phone-only (arm64) APK without the emulator libraries.

## Versions and releases

Windows and Android have separate versions, kept in `Versions.props` and tagged `windows-vX.Y.Z` and
`android-vX.Y.Z`. Both apps show their version and build commit in Settings, and every note records the
app and version that made it (`app:` in the frontmatter). Android's versionCode is derived from the
version (0.2.0 → 200), so updates always install over older builds.

As you make changes, add a line under **Unreleased** in `CHANGELOG.md`, in the Windows or Android
subsection. To release:

```powershell
./scripts/release.ps1 android              # 0.2.0 → 0.2.1
./scripts/release.ps1 windows -Bump minor  # 0.1.1 → 0.2.0
./scripts/release.ps1 android -DryRun      # build only; changes nothing
```

The script checks the repository is clean and up to date on `main`, runs the tests, bumps the version,
moves the changelog entries into a dated section and commits. It then builds the self-contained exe or
the signed APK, checks the version (and for Android the signing key) inside it, tags, pushes and creates a
GitHub release with the file attached and the changelog entries as notes. If the build fails, the release
commit is undone and nothing is pushed.

### Android signing key

Release APKs are signed with a dedicated key so updates install from any PC. It lives outside the repo in
`%APPDATA%\TranscriberSigning`, with its password encrypted for your Windows account. Back up the
`transcriber-release.keystore` file and its password together (`./scripts/new-signing-key.ps1 -ShowPassword`).
Without both, future APKs can't update the installed app. The only fix then is to uninstall it and lose
its settings. On a new PC, copy the keystore into that folder and run
`./scripts/new-signing-key.ps1 -ImportPassword` to save the password there.

## Building

Requires the .NET 8 SDK.

```powershell
dotnet test                                   # unit tests
dotnet run --project src/Transcriber.App      # run from source
dotnet publish src/Transcriber.App -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o publish
```

Opt-in integration tests:

```powershell
$env:TRANSCRIBER_STT_URL = "http://192.168.1.100:8000"
$env:TRANSCRIBER_TEST_AUDIO = "C:\path\to\two-speakers.wav"   # end-to-end pipeline
$env:TRANSCRIBER_LOOPBACK_TEST = "1"                          # plays TTS and records it via loopback
dotnet test
```

## Layout

```
src/Transcriber.Core           portable: STT client, diarization, transcript merging, Markdown, destinations
src/Transcriber.Audio.Windows  WASAPI microphone and loopback capture
src/Transcriber.App            Windows UI (WPF, WPF-UI Fluent controls)
src/Transcriber.Mobile         Android app (.NET MAUI)
tests/Transcriber.Tests
```

## Limitations

- Transcription starts after you press Stop. There is no live transcript.
- System audio is captured per output device, not per app.
- Diarization is a best guess. Very short turns and people with similar voices can be merged or split
  wrongly, which is why the naming dialog lets you merge speakers by giving them the same name.

## Licence

Transcriber is free software under the [GNU General Public License v3.0](LICENSE): you may use, change and
share it, and anything you distribute that builds on it must be released under the same licence.
