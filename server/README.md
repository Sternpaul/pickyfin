# Pickyfin Server

Personal "Recommended for you" rows for every Jellyfin user. Runs as its own Docker
container next to Jellyfin and talks to it only through Jellyfin's web API: no
Jellyfin plugin, so Jellyfin updates can't break it.

## What it does

For each user it writes up to three private playlists:

- **Recommended for you**
- **Because You Watched …** (up to two, from the user's latest watches)

The [Pickyfin TV app](../.github/README.md) shows them as rows on the home screen.
Every other Jellyfin app shows them as normal playlists. Without this container,
there are no playlists, so no rows; everything else in Pickyfin works as usual.

How the picks are made:

1. **The library is classified once** by an AI, with a fixed vocabulary (81
   subcategories like *Heist* or *Folk Horror*, 28 moods, themes, intensity), plus
   TMDB keywords if you add a TMDB key.
2. **You describe each person** in a few sentences on the admin page, e.g. *"Loves
   slow-burn Korean thrillers and heist movies, lately into 70s sci-fi. No
   musicals."* This is the main signal. It's translated into the same vocabulary.
3. **You decide which watch history counts**: all of it, or only from a date on.
   Useful for shared TVs, where old history mixes several people.
4. **A shortlist** (around 150 unwatched movies the user may see) is scored against
   the description, the counted history and ratings, without AI.
5. **One AI call per user** picks the "Recommended for you" row from that
   shortlist. "Because You Watched" rows come from similarity to the seed movie.

**Letterboxd** (optional, per person): their public diary via the username
(read twice a day), and the full Letterboxd export (ZIP upload: all ratings,
watched films and the watchlist). Films they've
watched there are never recommended, good ratings shape their taste, films close
to ones they disliked move down, and watchlist films move up.

**Tuning page**: six plain settings for everyone: description or watch history,
safe picks or discoveries, variety, new arrivals, how much rows change between
refreshes, and skipping weakly rated films; plus the number and size of rows.

**Movies page**: browse everything the AI tagged, filter by tag or library, and
correct tags by hand. Movies you edit are never re-tagged by the AI.

Picks are refreshed daily, soon after the user finishes a movie, and when you
change their description. Each refresh is one AI call per user; classifying a new
movie costs a share of one call per 20 movies.

## Checking the AI's work

Tags can be wrong, especially for lesser-known films. Pickyfin catches that in three ways, and lists what it finds on the **Issues** page:

- **The AI says when it doesn't know a film.** It gets every fact Jellyfin and TMDB have (genres, original title, countries, director, cast, keywords, plot) and answers "I know it", "partly" or "no".
- **A genre check.** Subcategories that don't fit any of Jellyfin's genres (a "Slasher" with only Crime and Drama) are flagged. Nothing is changed; you decide.
- **A second opinion** (AI page, off by default). A different model reviews every movie's tags, 40 per call, and names only what looks wrong. A free OpenRouter model works.

With an [Exa](https://exa.ai) key, flagged films are looked up on the web (Wikipedia, Letterboxd, TMDB, …) and described again from what was found. That costs under one cent per film; the free plan includes $10 a month, and a monthly limit on the AI page stops lookups before that.

To go further, "Look up the whole library" (AI page) works through every film a few at a time, up to a number per day, flagged and lesser-known films first. The page shows how long it will take and what it costs per month; the spend shown is what Exa reports.

Every movie shows how far its tags can be trusted: checked by you, looked up on the web, second opinion agrees, or AI only. The Movies page can filter by that, and any movie can be marked as checked.

On the Issues page, each movie can be opened and fixed by hand, looked up again, or marked as fine. Any movie can be looked up by hand from its page under Movies ("Look up on the web, then describe"). Movies you edit or mark as fine are never flagged again.

## Run it

```yaml
services:
  pickyfin-server:
    image: ghcr.io/sternpaul/pickyfin-server:latest
    restart: unless-stopped
    ports:
      - "8097:8097"
    environment:
      JELLYFIN_URL: http://jellyfin:8096   # how the container reaches Jellyfin
      JELLYFIN_API_KEY: "..."              # Jellyfin: Dashboard → API Keys → +
      TZ: Europe/Berlin   # log times in your time zone
    volumes:
      - ./pickyfin-data:/data
```

### With Portainer

**Easiest: add it to the stack Jellyfin is in**, so it can reach Jellyfin by its
service name.

1. Portainer → *Stacks* → your Jellyfin stack → *Editor*.
2. Add this under `services:` (same indentation as the Jellyfin service). Replace
   `jellyfin` in `JELLYFIN_URL` with the name of the Jellyfin service in that
   file, if it's different, and paste your API key:

   ```yaml
     pickyfin-server:
       image: ghcr.io/sternpaul/pickyfin-server:latest
       restart: unless-stopped
       ports:
         - "8097:8097"
       environment:
         JELLYFIN_URL: http://jellyfin:8096
         JELLYFIN_API_KEY: "..."
         TZ: Europe/Berlin   # log times in your time zone
       volumes:
         - pickyfin-data:/data
   ```

3. At the bottom of the file add (or extend an existing `volumes:` section):

   ```yaml
   volumes:
     pickyfin-data:
   ```

4. *Update the stack*. Jellyfin isn't restarted unless its own part changed.

**Or as its own stack:** same service, but `JELLYFIN_URL` then has to be an
address the container can reach, e.g. `http://<home server IP>:8096`.

**Updating later:** Stack → *Update the stack* with *Re-pull image* switched on.

Then open `http://<server>:8097` and sign in with a Jellyfin administrator account:

1. **AI**: choose a provider (OpenRouter, Google AI, OpenAI or Anthropic) and enter
   its key, or click *Take over settings* if the AI Movie Recommender plugin is
   installed. *Test connection* checks it.
2. **Libraries**: switch off libraries that should never be recommended from.
   Their movies aren't sent to the AI either.
3. **Users**: everyone starts switched off. Turn on the people who should get
   recommendations and write a line or two about each.

The first start tags every movie once, which takes a while for a big library
(20 movies per AI call); the Status page shows the progress.

Keep port 8097 on your home network; the admin page doesn't need to be public.

## Moving from the AI Movie Recommender plugin

The plugin's "For You" playlists would show up next to these rows. Turn the
plugin's playlists off (or uninstall it) once Pickyfin Server has made its first
picks.

## Development

```bash
cd server/src
JELLYFIN_URL=http://localhost:8096 JELLYFIN_API_KEY=... PICKYFIN_DATA=./data dotnet run
```

The AI providers, the classification vocabulary, the classifier and TMDB
enrichment come from the AI Movie Recommender plugin.
