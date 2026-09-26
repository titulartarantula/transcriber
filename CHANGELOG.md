# Changelog

Windows and Android are versioned separately and tagged `windows-vX.Y.Z` and `android-vX.Y.Z`.
Add changes under **Unreleased** as you make them; `scripts/release.ps1` moves an app's entries into
its release and uses them as the GitHub release notes.

## Unreleased

### Windows

- Recordings are read without Media Foundation, and finished recordings are no longer left locked, which
  could stop them being cleaned up after a note was saved.
- Notes record which app and version made them (`app:` in the frontmatter).
- Settings shows the version and the commit it was built from.

### Android

## Android 0.2.0 - 2026-09-26

- Release builds are signed with a dedicated release key. Uninstall 0.1.x once before installing this
  version; later updates install over it normally.
- Notes record which app and version made them (`app:` in the frontmatter).
- Settings shows the version, build number and commit.

## Android 0.1.1 - 2026-09-25

- Stop recording from the notification without opening the app.
- A "Name the speakers" notification when naming is needed while the app is in the background, instead of
  the naming screen trying to open over other apps.
- Recording and transcription belong to the app rather than the screen, so they survive Android recycling
  the screen during long meetings.
- "Transcript saved" and "Transcription failed" notifications while you're in other apps.
- Settings → Background asks Android to exempt the app from battery optimization.

## Android 0.1.0 - 2026-09-25

- First Android release: record from the phone mic or a Bluetooth, wired or USB headset, transcribe with
  your Whisper server, separate and name speakers on the phone, and save to Obsidian (MCP server or Local
  REST API) or a folder on the phone.

## Windows 0.1.0 - 2026-09-25

- First release: record several microphones and outputs at once, transcribe with your Whisper server,
  separate and name speakers locally, and save to a Markdown folder, the Obsidian Local REST API (with
  certificate pinning for LAN and WireGuard) or an Obsidian MCP server.
