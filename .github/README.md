# Pickyfin

The official [Jellyfin Android TV app](https://github.com/jellyfin/jellyfin-androidtv)
for Android TV and Fire TV, with a few additions for small, private servers.
Everything else is unchanged and follows the official stable releases automatically.

Pickyfin is not affiliated with the Jellyfin project.

## What's different

| Addition | Where |
| --- | --- |
| **Access header.** The "add server" screen has optional fields for a header name and value. It's sent on every request to that server (API, images, video), and never to anything else. For servers behind a reverse proxy that only lets requests with that header through. | `pickyfin/AccessHeader.kt`, wired in `di/AppModule.kt` and `ServerAddFragment.kt` |
| **"Recommended for you" rows on the home screen**, after Continue Watching / Next Up. They show the user's private recommendation playlists written by a server plugin ("For You", "Because You Watched …"), up to three rows. No playlists, no rows. | `pickyfin/RecommendationRows.kt`, wired in `HomeRowsFragment.kt` |
| **Own app identity.** Package `io.github.sternpaul.pickyfin.tv`, name "Pickyfin", so it installs next to the official app. | `app/build.gradle.kts` |
| **In-app updates.** On start (at most every 6 hours) it checks this repository's newest `tv-v…` release and offers to install it. The system asks the user to confirm; the first time it also asks to allow this app to install updates. | `pickyfin/Updater.kt` |

All new code lives in `org.jellyfin.androidtv.pickyfin` (under `app/src/main/java/org/jellyfin/androidtv/`).
Official files are touched only in a few lines marked `// Pickyfin:`.

## Install on a Fire TV

Download the newest `pickyfin-tv-v….apk` from [Releases](../../releases), for example
with the Downloader app, and install it. Later updates are offered inside the app.

## Releases

`.github/workflows/pickyfin-release.yaml` runs daily and on demand:

- moves the app to a new official stable release, if there is one, and re-applies our
  changes (`scripts/update-official.sh`)
- builds a signed APK and publishes it as release `tv-v<official version>-pickyfin.<n>`
- if our changes don't apply cleanly, it opens an issue instead of publishing

Repository secrets it needs: `PICKYFIN_KEYSTORE_BASE64`, `PICKYFIN_KEYSTORE_PASSWORD`,
`PICKYFIN_KEY_ALIAS`, `PICKYFIN_KEY_PASSWORD`. The signing key must never change:
installed apps only accept updates signed with the same key.

## Building locally

Needs JDK 21 and the Android SDK.

```bash
./gradlew assembleDebug
```

Local builds don't check for updates unless `PICKYFIN_UPDATE_REPOSITORY=owner/name` is set.

## License

GPL-2.0, like the official app.
