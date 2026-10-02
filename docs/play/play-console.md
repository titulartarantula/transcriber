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

## 2. App signing: let Google hold the key

When Play asks at the first release, keep the default: **Google generates and manages the app signing
key**. Play signs what it delivers with that key. Your release keystore
(`%APPDATA%\TranscriberSigning\transcriber-release.keystore`) becomes the **upload key**, which only proves
to Play that an upload came from you. `scripts/release.ps1` signs every `.aab` with it and checks that.

Keep the keystore and its password backed up. If it's lost, Play can reset the upload key after an
identity check, but it's slower than not losing it.

What this means:
- **Play installs and the GitHub APKs are signed with different keys**, so one can't update the other.
  Switching between them means uninstalling first, which loses settings (server address, Obsidian sign-ins
  and keys) and any kept recordings.
- The first move to Play is one of those switches: note your settings, then uninstall the sideloaded copy
  before installing from Play.

## 3. Build and upload

```powershell
./scripts/release.ps1 android -Bump minor   # tags, publishes the APK on GitHub, and builds the .aab
```

Or `-DryRun` to build without releasing. The bundle is `artifacts\Transcriber-android-<version>.aab`.

**Test and release → Testing → Internal testing**
- **Testers** tab: create a list with your Google account and save. Copy the **opt-in link**.
- **Create new release** → upload the `.aab` → release notes (the CHANGELOG entries) → **Save → Review →
  Start rollout**.
- On the phone, uninstall the sideloaded copy (see step 2), then open the opt-in link, accept, and install
  from Play. Updates arrive from Play from then on.

## 4. App content (Policy → App content)

| Section | Answer |
| --- | --- |
| Privacy policy | https://gist.github.com/titulartarantula/b35e876388257be2cb12e2c4a9cb6ff1 (public gist of `privacy-policy.md`; update both together) |
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
- **App category:** Productivity. **Contact email:** titulartarantula@gmail.com (shown to testers).

## Automatic uploads

With a service account key saved, `release.ps1 android` uploads the App Bundle to internal testing itself,
with the changelog entries as release notes. One-time setup:

1. [Google Cloud console](https://console.cloud.google.com): pick or create a project, then
   APIs & Services → Library → **Google Play Android Developer API** → Enable.
2. IAM & Admin → Service accounts → **Create service account** (e.g. `play-release`). It needs no Cloud roles.
   Open it → Keys → Add key → Create new key → **JSON**.
3. Play Console → **Users and permissions** → Invite new users: the service account's email
   (`…@….iam.gserviceaccount.com`). Under App permissions add Transcriber with **Release to testing tracks**.
4. `./scripts/import-play-key.ps1 <downloaded .json>`, then delete the downloaded file.

`./scripts/play-upload.ps1 <bundle.aab>` uploads a bundle by hand, e.g. to retry a failed upload.
