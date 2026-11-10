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

Picks are refreshed daily, soon after the user finishes a movie, and when you
change their description. Each refresh is one AI call per user; classifying a new
movie costs a share of one call per 20 movies.

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
