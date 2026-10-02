# Changelog

Windows and Android are versioned separately and tagged `windows-vX.Y.Z` and `android-vX.Y.Z`.
Add changes under **Unreleased** as you make them; `scripts/release.ps1` moves an app's entries into
its release and uses them as the GitHub release notes.

## Unreleased

### Windows

- Recording is never blocked by transcription. Stopping a recording queues it, and the new **Transcripts**
  list shows each one's progress with Name speakers, Cancel, Retry, Reprocess, Open note and Delete.
- A transcript waiting for speaker names no longer holds up the next one. The naming dialog's new **Later**
  button leaves it waiting; **Keep labels** saves with the automatic labels.
- Unfinished transcripts, and failed or cancelled ones, survive closing the app. Retry works after a restart.
- Speaker separation runs on the Whisper server when it offers `/v1/audio/diarization` (pyannote
  community-1 on its GPU). Otherwise it runs on this PC as before. New setting: **Separate voices on the
  server when it supports it**.
- New setting **Keep the audio after the note is saved**: keeps each recording's 16 kHz copy so it can be
  reprocessed with different settings later.
- Each transcript shows how long transcription and voice separation took, and where voices were separated.
- Recordings with no voices to tell apart are saved without stopping for the naming dialog.
- Recordings are compressed to AAC (48 kHz mono, 96 kbps, about 43 MB an hour) before they're sent to the
  server and kept, instead of WAV (up to about 1.4 GB an hour while recording). Imported m4a, aac and mp3
  files that are already small are sent as they are.
- Short remarks like "yep" or "yeah okay" made while someone else is talking appear in that person's line
  as "(Speaker 2: yep)" instead of splitting it into separate turns.
- Fewer sentences chopped between speakers when two people talk at once: a fragment under a second in the
  middle of someone's sentence goes back to them.

### Android

- Short remarks like "yep" or "yeah okay" made while someone else is talking appear in that person's line
  as "(Speaker 2: yep)" instead of splitting it into separate turns.
- Fewer sentences chopped between speakers when two people talk at once: a fragment under a second in the
  middle of someone's sentence goes back to them.

## Android 0.3.1 - 2026-10-02

- Transcripts that kept their audio have a **Share audio** button. It sends the original recording through
  Android's share sheet (Drive, Quick Share, email…) to get it off the phone.

## Android 0.3.0 - 2026-09-29

- Recording is never blocked by transcription. Stopping a recording queues it, and a **Transcripts** list
  shows each one's progress with Name speakers, Cancel, Retry, Reprocess and Delete.
- A transcript waiting for speaker names no longer holds up the next one. A notification says when one is
  ready to name. Going back from the naming screen leaves it waiting.
- Unfinished transcripts survive the app being closed or killed, and resume the next time it's opened.
- Speaker separation runs on the Whisper server when it offers `/v1/audio/diarization`, which is faster
  and easier on the battery. Otherwise it runs on the phone as before.
- New setting **Keep audio to reprocess later**.
- Records straight to AAC (16 kHz mono, 48 kbps, about 21 MB an hour) instead of WAV (about 115 MB an
  hour), and sends that file to the server, which also cuts mobile data. A crash still leaves a playable
  file.
- While only transcribing, the background service runs as a data sync service rather than a microphone one.
- **Allow running in background** became **Open battery settings**, which opens Transcriber's App info
  page (Battery → Unrestricted). The app no longer asks for the permission behind the one-tap prompt, which
  Google Play restricts.

## Windows 0.1.1 - 2026-09-26

- Recordings are read without Media Foundation, and finished recordings are no longer left locked, which
  could stop them being cleaned up after a note was saved.
- Notes record which app and version made them (`app:` in the frontmatter).
- Settings shows the version and the commit it was built from.

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
