# Pickyfin

[![Latest release](https://img.shields.io/github/v/release/Sternpaul/pickyfin?label=release)](https://github.com/Sternpaul/pickyfin/releases)
[![License: GPL-2.0](https://img.shields.io/badge/license-GPL--2.0-blue.svg)](../LICENSE)
[![Based on Jellyfin Android TV](https://img.shields.io/badge/based%20on-Jellyfin%20Android%20TV-00a4dc.svg)](https://github.com/jellyfin/jellyfin-androidtv)

**The official Jellyfin Android TV app, a little pickier.** Same look, same player,
same features, plus three things for small private servers:

- 🔑 **Access header.** For servers behind a reverse proxy that only lets requests
  with a secret header through. Enter it once when adding the server.
- ✨ **"Recommended for you" on the home screen.** Personal rows for each user, right
  after Continue Watching. No playlists to open, no settings.
- 🔄 **In-app updates.** New versions are offered inside the app and install with
  one click, also on Fire TV.

Everything else is the official app, kept in step with every official stable
release automatically.

> Pickyfin is an independent project and not affiliated with the Jellyfin project.

## Install

On a Fire TV or Android TV device:

1. Install the **Downloader** app from the app store.
2. In Downloader, enter the code **`3704094`** and install. It always gives the newest
   version. (Or the full address:
   `https://github.com/Sternpaul/pickyfin/releases/latest/download/pickyfin-tv.apk`)
3. Open Pickyfin, enter your server address and, if your server owner gave you one,
   the access header.

Pickyfin installs next to the official Jellyfin app; you can keep both.

## Recommendations need a server plugin

The app shows the recommendations, but doesn't compute them. That's done on the
server by the [AI Movie Recommender](https://github.com/Sternpaul/jellyfin-smart-playlists)
plugin, which writes private playlists for each user. Pickyfin shows these as rows:

| Playlist written by the plugin | Row in Pickyfin |
| --- | --- |
| **For You** (or **Recommended for you**) | Recommended for you |
| **Because You Watched …** | Because You Watched … |

At most three rows in total, in this order.

Install the plugin in Jellyfin under *Dashboard → Plugins → Repositories* with
`https://sternpaul.github.io/jellyfin-smart-playlists/repo/manifest.json`.
Without it, Pickyfin works like the official app, just without these rows.

## What's changed compared to the official app

| Addition | Code |
| --- | --- |
| Access header: optional header name and value on the "add server" screen, sent on every request to that server (API, images, video, live updates) and never anywhere else | `pickyfin/AccessHeader.kt`, wired in `di/AppModule.kt` and `ServerAddFragment.kt` |
| Recommendation rows after Continue Watching / Next Up, up to three; watched items drop out | `pickyfin/RecommendationRows.kt`, wired in `HomeRowsFragment.kt` |
| Own identity: package `io.github.sternpaul.pickyfin.tv`, name "Pickyfin" | `app/build.gradle.kts` |
| Updater: checks this repository's newest `tv-v…` release at most every 6 hours and hands the APK to the system installer | `pickyfin/Updater.kt` |

New code lives in `app/src/main/java/org/jellyfin/androidtv/pickyfin/`. Official
files are touched only in a few lines marked `// Pickyfin:`.

## How releases work

[`pickyfin-release.yaml`](workflows/pickyfin-release.yaml) runs daily:

1. If Jellyfin published a new stable Android TV release, the code becomes that
   release and our changes are applied on top again
   ([`scripts/update-official.sh`](../scripts/update-official.sh)). The official
   release we're based on is recorded in [`official-version`](official-version).
2. It builds a signed APK and publishes it as `tv-v<official version>-pickyfin.<n>`.
3. If our changes don't fit the new release, it opens an issue instead.

It can also be started by hand (Actions → Pickyfin release → Run workflow) to
publish changes of our own.

## Building

Needs JDK 21 and the Android SDK.

```bash
./gradlew assembleDebug
```

Local builds don't check for updates unless `PICKYFIN_UPDATE_REPOSITORY=owner/name` is set.

## Credits and license

Pickyfin is built on the [Jellyfin Android TV app](https://github.com/jellyfin/jellyfin-androidtv)
by the Jellyfin contributors (see [CONTRIBUTORS.md](../CONTRIBUTORS.md)). Like the
official app, it's licensed under [GPL-2.0](../LICENSE).
