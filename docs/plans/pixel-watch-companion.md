# Pixel Watch companion (not started)

A Wear OS app that records with the watch's own mic and hands the recording to the phone app, which
transcribes it like any other. Parked on 2026-10-03; nothing below is built yet.

## First, check the mic is good enough

The Pixel Watch mic is made for the wearer's voice at arm's length. It should be fine for someone talking
near you, but people across a table may come out too quiet for Whisper and pyannote to do well. Before
building anything, record the same conversation on the watch (its built-in Recorder app is enough) and on the
phone, transcribe both through the desktop app's **Import**, and compare the transcript quality and speaker
separation. If the watch loses the far side of the conversation, the companion is only worth it for
dictation-style use.

## Decisions made

- **Record on the watch, send to the phone.** Not a live remote mic (needs a constant connection, more
  battery, more dropouts) and not just a remote control for the phone's mic (doesn't use the watch's mic).
- **Kotlin with Compose for Wear OS**, in a `wear/` folder with its own Gradle build. MAUI doesn't support
  Wear OS, and Compose for Wear has the first-class watch components.

## Design

**Watch**
- One screen: a big record/stop button and a timer. Recording runs in a foreground service
  (`foregroundServiceType="microphone"`) so it continues with the screen off.
- 16 kHz mono AAC in an ADTS stream, as the phone writes (`Platforms/Android/AdtsAacWriter.cs`), so a
  recording cut short by a dead battery is still readable.
- Finished recordings queue on the watch. When `CapabilityClient` finds a node with the
  `transcriber_phone` capability, `ChannelClient` sends the file, with its start time, duration and title
  in the channel path or a preceding `MessageClient` message. The watch deletes its copy only after the
  phone acknowledges. WorkManager retries while the phone is out of reach.

**Phone (Transcriber.Mobile)**
- Declare the `transcriber_phone` capability (`res/values/wear.xml`).
- A `WearableListenerService` (via the `Xamarin.GooglePlayServices.Wearable` binding) handles
  `CHANNEL_EVENT` on `/recording/…`, receives the file into a new folder under `AppPaths.Recordings`,
  acknowledges, and enqueues it on the job queue with source label "Watch" and the current Voices setting.

**Hard constraints**
- The Data Layer only connects apps with the **same application ID** (`dev.transcriber.app`) and the
  **same signing key**, so the watch build signs with the release keystore in `%APPDATA%\TranscriberSigning`.
- Play ships it as a separate App Bundle in the same listing, on the Wear OS form factor's own tracks.

## Setup needed from you

1. Play Console → Transcriber → Advanced settings → **Form factors** → add **Wear OS** and accept its terms.
   `release.ps1` would then also upload the watch bundle to the Wear internal-testing track.
2. Optional, for quicker testing: on the watch, enable developer options and **Wi-Fi debugging** so builds
   can be installed with adb (needs `platform-tools` added to the Android SDK here).

## Open questions

- Battery cost of an hour's recording on the watch.
- Whether the phone should notify when a watch recording arrives, or just add it to the Transcripts list.
- Start from a watch tile or complication, as well as the app?
