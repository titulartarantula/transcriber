# Transcriber for Android: privacy policy

_Last updated: [date of publishing]_

Transcriber records meetings on your phone, sends the audio to a speech-to-text server **that you set up
and choose**, and saves the transcript where **you choose**. The developer runs no servers for the app and
receives none of your data.

## What the app handles, and where it goes

**Microphone audio.** The app records only after you press **Start recording**, and stops when you press
Stop (or the notification's Stop button). A notification is shown for as long as it records.
- The recording is stored in the app's private storage on your phone.
- It is sent to the speech-to-text server whose address you enter in Settings (for example a
  faster-whisper server on your own computer). If that server offers speaker separation, the audio is
  also sent there for that. Otherwise speakers are separated on the phone.
- It is deleted from the phone once the transcript is saved, unless you turn on **Keep audio to reprocess
  later**. You can delete kept recordings at any time from the transcript list.

**Transcripts.** The text and speaker names are saved to the destination you pick in Settings: a folder on
your phone, an Obsidian vault through its Local REST API plugin, or an Obsidian MCP server. Those
destinations are yours; the app only writes to them.

**Settings and keys.** Server addresses, API keys and sign-in tokens stay on the phone. Keys and tokens are
encrypted with a key held in the Android Keystore.

**Model downloads.** The first time speakers are separated on the phone, the app downloads two open-source
speech models (about 32 MB) from Hugging Face and GitHub. Like any download, those sites see the phone's
IP address. No recording or transcript is sent to them.

## What the app does not do

- No advertising, analytics, tracking or crash-reporting services.
- No account with the developer, and no data sent to the developer.
- No selling or sharing of data with anyone. Data goes only to the servers and destinations you configure.

## Security

Connections use HTTPS when the addresses you enter use HTTPS. The app also allows plain HTTP, because
self-hosted servers on a home network often use it. Use HTTPS, or a private network such as a VPN,
whenever the connection leaves networks you trust.

## Permissions

| Permission | Why |
| --- | --- |
| Microphone | Recording, only while you're recording |
| Notifications | The recording notification and its Stop button, and alerts when a transcript is ready |
| Foreground service (microphone, data sync) | Keeps recording and transcribing with the screen off |
| Network | Talking to your speech-to-text server and your note destination |
| Bluetooth | Recording from a connected headset's microphone |

## Children

The app isn't directed at children.

## Changes and contact

If this policy changes, the new version will be posted at this address with a new date.
Questions: [contact email].
