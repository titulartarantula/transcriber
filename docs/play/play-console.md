# Publishing Transcriber for Android on Google Play (internal testing)

Internal testing puts the app in the Play Store for up to 100 invited testers (just you, to start).
Installs and updates then come from Play, with no "unknown app" or Play Protect warnings. It doesn't need
the 12-tester, 14-day closed test that new personal accounts must pass before a public (production) release.

## 1. Create the app

Play Console → **Create app**
- App name: `Transcriber`, default language English, **App**, **Free**.
- Accept the declarations.

The package name is claimed by the first upload: `dev.transcriber.app`. No public app uses it. If Play says
it's taken, stop there: changing it means a new app that can't update the one on your phone.

## 2. App signing: keep the key your phone's copy already uses

Play re-signs what it delivers. By default it generates a new key, and then **the Play version can't install
over the sideloaded one**. You'd have to uninstall first, losing settings, sign-ins and kept recordings.
Give Play the existing release key instead:

1. **Test and release → App integrity → App signing**, or the prompt when you create the first release:
   choose **Use a different key → Export and upload a key from Java keystore**.
2. Download **pepk.jar** and the **encryption public key** it offers, into `artifacts\play\`.
3. Run this, and upload the `transcriber-signing-key.zip` it makes (asks for the keystore password):

   ```powershell
   $jdk = 'C:\Program Files\Android\openjdk\jdk-21.0.8'
   & "$jdk\bin\java.exe" -jar artifacts\play\pepk.jar `
       --keystore="$env:APPDATA\TranscriberSigning\transcriber-release.keystore" --alias=transcriber `
       --output=artifacts\play\transcriber-signing-key.zip --include-cert --rsa-aes-encryption `
       --encryption-key-path=artifacts\play\encryption_public_key.pem
   ```

   The key never leaves the machine unencrypted. The zip is readable only by Google.

The same key also signs the bundles you upload (the "upload key"). `scripts/release.ps1` checks that every
`.aab` it builds is signed with it.

## 3. Build and upload

```powershell
./scripts/release.ps1 android -Bump minor   # tags, publishes the APK on GitHub, and builds the .aab
```

Or `-DryRun` to build without releasing. The bundle is `artifacts\Transcriber-android-<version>.aab`.

**Test and release → Testing → Internal testing**
- **Testers** tab: create a list with your Google account and save. Copy the **opt-in link**.
- **Create new release** → upload the `.aab` → release notes (the CHANGELOG entries) → **Save → Review →
  Start rollout**.
- On the phone, open the opt-in link, accept, then install from Play. The first time, uninstall the
  sideloaded copy only if Play refuses to install over it (it won't, if step 2 was done).

## 4. App content (Policy → App content)

| Section | Answer |
| --- | --- |
| Privacy policy | URL of the published `privacy-policy.md` (it must be public; the repo is private) |
| Ads | No ads |
| App access | "Some functionality is restricted" — see below |
| Content rating | Questionnaire, category *Utility, productivity, communication or other*; no to every content question → rated Everyone |
| Target audience | 18 and over |
| News app | No |
| Government app | No |
| Financial features | None |
| Health | None |
| Data safety | See below |
| Foreground service permissions | See below |

**App access instructions** (reviewers may look even at internal releases):
> Recording works without setup. Transcription needs a speech-to-text server the user runs, entered in
> Settings → Speech to text (any OpenAI-compatible /v1/audio/transcriptions server, e.g.
> faster-whisper-server). There is no account or login. Notes are saved to a folder chosen in
> Settings → Output.

### Data safety

Google counts anything "transmitted off the device" as collected, even to a server the user runs. So:

- Does the app collect or share required data types? **Yes.**
- Is all user data encrypted in transit? **No.** Self-hosted servers on a home network often use plain HTTP,
  and the app allows it.
- Can users request that data be deleted? **No.** The developer holds no data; it sits on the user's own
  server and notes destination. Recordings on the phone can be deleted in the app.

| Data type | Collected | Shared | Processed ephemerally | Required | Purpose |
| --- | --- | --- | --- | --- | --- |
| Audio → Voice or sound recordings | Yes | No | No | Yes | App functionality |
| App activity → Other user-generated content (transcripts) | Yes | No | No | Yes | App functionality |

"Shared: No" relies on the exception for transfers the user starts, to destinations the user configures.

### Foreground service permissions

Needed because the app targets Android 14+. Each type wants a description and a short video link. A phone
screen recording uploaded unlisted to YouTube is fine.

- **Microphone (media recording):** "Records a meeting the user starts with Start recording, continuing
  with the screen off or while other apps are used. A notification with a Stop button is shown throughout.
  Deferring or stopping it would cut the user's recording short."
- **Data sync (network transfer):** "After a recording stops, uploads the user's audio to the
  speech-to-text server the user configured and waits for the transcript, so a long meeting keeps
  transcribing with the screen off. Starts only from a user action (stopping a recording, Retry or
  Reprocess) and ends when the transcript is saved."

Video: start a recording, lock the screen, unlock, stop from the notification, and show the transcript
being made in the Transcripts list.

## 5. Store listing (Grow → Store presence → Main store listing)

Only testers see it, but Play asks for it before the first rollout.

- **Short description** (80 max): `Record meetings, transcribe on your own Whisper server, save notes to Obsidian.`
- **Full description:**

  > Transcriber records meetings and lectures on your phone and turns them into Markdown notes with each
  > speaker labelled.
  >
  > • Your server, your data: audio goes to the speech-to-text server you run (any OpenAI-compatible
  >   Whisper server), never to the developer.
  > • Speaker separation, on your server's GPU when it supports it, otherwise on the phone, with names
  >   suggested from the conversation.
  > • Keep recording while earlier meetings are still being transcribed.
  > • Record from the phone, a wired or USB headset, or a Bluetooth headset's call mic, with the screen off.
  > • Save notes to a folder (for example your Obsidian vault) or straight into Obsidian through its Local
  >   REST API or an MCP server.
  > • Recordings are compact AAC (about 21 MB an hour) and can be kept for reprocessing.

- **App icon:** `docs/play/assets/icon-512.png`
- **Feature graphic:** `docs/play/assets/feature-1024x500.png`
- **Phone screenshots:** `docs/play/assets/screenshot-*.png` (at least two)
- **App category:** Productivity. **Contact email:** required, and shown to testers.

## Later

Uploads can be automated from `release.ps1` with the Play Developer API. That needs a Google Cloud service
account with release access in Play Console → Users and permissions. Worth doing once releases are routine;
the first upload has to be manual anyway.
