# MuseFM

Self-hosted listening statistics, in the spirit of Last.fm and Maloja. Music players send scrobbles to MuseFM through
**ListenBrainz**- or **Last.fm**-compatible APIs, and every user gets charts, activity graphs and their full history.

## Screenshots
<img width="2239" height="1321" alt="image" src="https://github.com/user-attachments/assets/3be3aa56-41e5-43e7-86fa-9a380c32ef5b" />
<img width="2231" height="1328" alt="image" src="https://github.com/user-attachments/assets/6ebb4d6b-be23-43b5-ae26-d297fd539799" />
<img width="2243" height="1251" alt="image" src="https://github.com/user-attachments/assets/6f4a8701-7ff7-4f34-9ad6-e4d8122ec00a" />
<img width="2258" height="1339" alt="image" src="https://github.com/user-attachments/assets/afd156c3-2c62-4944-81d9-a0e88e86beb7" />
<img width="2250" height="1333" alt="image" src="https://github.com/user-attachments/assets/84cd8faa-3fb8-416f-b12d-9214afe8fb72" />
<img width="2253" height="1323" alt="image" src="https://github.com/user-attachments/assets/9bbe67df-bcf9-439d-be13-16eaf9845dfb" />
<img width="2256" height="1336" alt="image" src="https://github.com/user-attachments/assets/4dd2f9de-0eae-4eaa-87ea-3ceb2f580cb6" />



## Features
- User accounts (first account becomes admin, registration can be closed); admins create, edit and delete accounts
- Per-user preferences: eight themes (system, light, dark, newsprint, kraft, midnight, forest, black), six accent colors, 12/24-hour clock, first day of the week, artwork on/off
  and the default period (Settings → Preferences)
- Public or private profiles with a profile picture and banner (Settings → Profile; JPEG, PNG, GIF or WebP up to 10 MB),
  user search, following other users, a feed of what they play, and taste compatibility
- Charts and statistics: top artists/albums/tracks, listening activity, a year calendar with streaks, time-of-day and
  weekday patterns, artist discovery and concentration, and day/week/month/year comparisons with rank movement
- Pages for every artist, album and track; rolling periods, calendar years and months, or any span of days
- Loved tracks: from the pages, from Last.fm and ListenBrainz clients, and from Last.fm and ListenBrainz imports
- Obsessions, as on Last.fm: pick the track you can't stop playing on its page and choose how long your profile shows it
  (a day up to three months, or until you change it)
- MusicBrainz ids sent by clients are kept, used for artwork lookups and loves, and exported
- Admin catalog editor: rename and merge artists, albums and tracks; old names keep working as aliases
- ListenBrainz API (`submit-listens`, `validate-token`, `listens`, `playing-now`, `listen-count`, recording feedback)
- Last.fm / Audioscrobbler 2.0 API: scrobbling, now playing and loves, plus the read methods widgets and tools use
  (`user.getRecentTracks`, `user.getTopArtists/Albums/Tracks`, `user.getLovedTracks`, `user.getFriends`, `track.getInfo`)
- Per-client API keys (stored hashed, revocable)
- Import: Maloja export, Last.fm CSV, lastfmstats.com JSON, ListenBrainz export, Spotify streaming history, or directly from a Maloja server or a Last.fm account — all of it, or only a timeframe you pick
- Export: Maloja JSON (re-importable into Maloja), Last.fm CSV, and a ListenBrainz-style .zip with MusicBrainz ids and loves
- Album covers, artist photos and biographies from Navidrome, ListenBrainz (MusicBrainz, Cover Art Archive, Wikipedia) and Last.fm, configured by the admin
- One SQLite file, one Docker container

Built with ASP.NET Core 10 (Razor Pages, minimal APIs), EF Core + SQLite and ASP.NET Core Identity.
The interface is set in Archivo and Fraunces (SIL Open Font License), served from `wwwroot/fonts`: pages load nothing
from other servers.

## Quick start

```bash
docker compose up -d
```

This pulls the published image, `ghcr.io/zortexsenpai/musefm`. Update with `docker compose pull && docker compose up -d`.
To run your own checkout instead, replace the `image:` line in `docker-compose.yml` with `build: .` and add `--build`.

Open http://localhost:8080 and sign up. The first account is the administrator.

All data (database, cookie keys, pending uploads, cached artwork, profile pictures and banners) lives in the `musefm-data`
volume, mounted at `/data`.

### Configuration

Environment variables (double underscore = config section):

| Variable | Default | Purpose |
| --- | --- | --- |
| `MuseFM__AllowRegistration` | `true` | Set to `false` to close sign-ups. The very first account can always be created, and admins can add accounts under *Admin → Users*. |
| `MuseFM__LastFmApiKey` | *(empty)* | Last.fm API key for “Import from Last.fm” and the Last.fm metadata connector. Get a free key at https://www.last.fm/api/account/create. A key saved under *Admin → Metadata* takes precedence. |
| `MuseFM__ImportFromPrivateAddresses` | `false` | Importing from a Maloja server on a private or local address (`http://maloja:42010`, `192.168.…`) is for admins only, so that an account is not a way into the server's network; everyone else can import from servers on the public internet. Set to `true` when everyone with an account is trusted. |
| `MuseFM__TrustedProxies` | *(empty)* | The reverse proxies whose `X-Forwarded-*` headers are believed: IP addresses or networks, separated by commas, e.g. `172.18.0.0/16` for a Docker network. Empty believes them from anyone; set it whenever MuseFM can also be reached without going through the proxy. |
| `MuseFM__NowPlayingTimeoutSeconds` | `90` | How long “now playing” stays up when the client sent no track length. Clients cannot report that playback stopped, so this is how soon a stopped track disappears; raise it for a client that sends “now playing” only once per track and without a length. With a length, the track is shown for that long. |
| `MuseFM__DataDirectory` | `/data` (in Docker) | Where the SQLite database and keys are stored. |
| `ASPNETCORE_HTTP_PORTS` | `8080` | Port inside the container. |

Run it behind a reverse proxy (Caddy, Traefik, nginx) for HTTPS. `X-Forwarded-For/Proto/Host` headers are honored,
so the URLs shown under *Settings → API keys* match your public address. Make sure visitors can't get around the proxy:
publish the port on the proxy's host only (`127.0.0.1:8080:8080`) or keep both in one Docker network without publishing
it, and name the proxy in `MuseFM__TrustedProxies`. Over HTTPS, MuseFM sends an HSTS header. Health check: `GET /healthz`.

**Backups:** the volume contains `musefm.db` (SQLite in WAL mode). Stop the container or use `sqlite3 musefm.db ".backup backup.db"`
for a consistent copy, or use *Settings → Export* per user.

## Connecting scrobblers

Create one API key per client under **Settings → API keys**. The key is shown once.

### ListenBrainz-compatible clients (recommended)

| Setting | Value |
| --- | --- |
| API URL | `https://your-host/apis/listenbrainz` |
| Token | your API key |

- **Navidrome:** `ND_LISTENBRAINZ_BASEURL=https://your-host/apis/listenbrainz/1/`, then link ListenBrainz in your Navidrome profile using the API key as token.
- **Jellyfin** (ListenBrainz plugin), **Pano Scrobbler**, **Web Scrobbler**, **multi-scrobbler:** choose ListenBrainz / custom ListenBrainz URL and use the URL above.

### Last.fm-compatible clients

For clients with a custom Last.fm / GNU FM / Libre.fm server option:

| Setting | Value |
| --- | --- |
| API URL | `https://your-host/apis/audioscrobbler/2.0/` |
| Username | your MuseFM username |
| Password | an API key (your account password also works) |
| API key / secret | anything — they are not checked |

Responses are XML by default and JSON with `format=json`, like Last.fm. Duplicate scrobbles (same timestamp) are acknowledged as
accepted so client queues drain.

Besides `track.scrobble` and `track.updateNowPlaying`, clients can love and unlove tracks (`track.love`, `track.unlove`), and
tools that read Last.fm — "now playing" widgets, Discord status, collage makers — can point at the same URL:
`user.getRecentTracks` (with the track playing now, `from`/`to` and `extended=1`), `user.getTopArtists`, `user.getTopAlbums`
and `user.getTopTracks` (`period` = `overall`, `7day`, `1month`, `3month`, `6month` or `12month`), `user.getLovedTracks`,
`user.getFriends` (the users someone follows, with their profile pictures) and `track.getInfo` (with `username`: that user's play count and love). Public
profiles can be read without logging in; private ones only with their own session key (`sk`). Lists are always JSON arrays,
even with a single item. Image URLs point at MuseFM's cached artwork when a metadata connector is active.

ListenBrainz clients can send loves and hates to `POST /apis/listenbrainz/1/feedback/recording-feedback` and read them from
`GET /apis/listenbrainz/1/feedback/user/{user}/get-feedback`. ListenBrainz names recordings by MusicBrainz id, so feedback
is stored when a scrobble of that recording carried the id; feedback for other recordings is accepted but not stored.

## Covers, artist photos, biographies, genres and years

An administrator can enable **metadata connectors** for the whole server under **Admin → Metadata**:

| Connector | Album covers | Artist photos | Biographies | Genres, years and lengths | Needs |
| --- | --- | --- | --- | --- | --- |
| Navidrome | from your library | from Navidrome's agents | from Navidrome's agents | album genres, release year and track lengths, as your files are tagged | server URL, username, password |
| Maloja | pictures in your Maloja (added there or found by Maloja) | same | — | — | server URL |
| ListenBrainz | Cover Art Archive | Wikimedia Commons (via Wikidata) | English Wikipedia (via Wikidata) | MusicBrainz genres of artists and albums, release year | ListenBrainz user token |
| Last.fm | yes | — (Last.fm no longer serves artist photos) | yes | artist tags as genres, track lengths | API key, or `MuseFM__LastFmApiKey` |

Genres, release years and track lengths feed the Insights page: top genres, release decades and listening time. A scrobble
takes the genres of its album, or else of its artist; a track gets its length from the track list of an album it was scrobbled
with, unless a client reported one. Track pages show them too: the genres, the length, and the year of the earliest album
the track was scrobbled with (so a song only ever played from a best-of shows the best-of's year, next to its title).

Connectors are asked in the order shown on the page (rearrange with the arrows). For each artist or album the first connector
with an answer wins, field by field — for example a biography from Navidrome and a photo from Wikimedia Commons. Saving a
connector runs a connection test.

Artists and albums are looked up when they are scrobbled for the first time (in the background; history imports are left
out) or, failing that, the first time they are shown. Everything is cached: text in the database, images in
`/data/images`. To fill the cache up front, use **Fetch missing** in the *Cache* section: it looks up every artist and album that
has no cover, photo or biography yet in the background, most listened first, with live progress. It also catches up artists
and albums that were cached before a MuseFM version started looking up genres, years and track lengths, keeping their pictures. **Refetch all** looks up
everything again (e.g. after changing the connector order). Found metadata is refreshed after 90 days; items without a result
are retried after 14 days (one hour after a connector error). Navidrome is queried with Subsonic token
authentication, so the password is never sent; stored passwords and tokens are encrypted with the keys in `/data/keys`.
ListenBrainz is paced at one request per second, so a long list fills in gradually the first time it is shown.
Maloja only knows artists and albums in its own database, so it helps most when it receives the same scrobbles; when Maloja
is still looking for a picture the item is asked again within the hour, and Maloja's placeholder pictures are ignored.

## Import and export

**Settings → Import** accepts:

| Source | File |
| --- | --- |
| Maloja | `maloja_export_*.json` |
| Last.fm | CSV from [lastfm-to-csv](https://benjaminbenben.com/lastfm-to-csv/) (headerless `artist,album,track,date` or with a `uts,…` header), or `lastfmstats-*.json` from lastfmstats.com |
| ListenBrainz | export `.zip`, `.jsonl` or `.json` |
| Spotify | the `.zip` Spotify sends when you request your data at [spotify.com/account/privacy](https://www.spotify.com/account/privacy/), or single `.json` files from it: the *Extended streaming history* (`Streaming_History_Audio_*.json`, complete, with albums) or the *Account data* (`StreamingHistory_music_*.json`, the last year) |
| Maloja server | direct import through Maloja's API — enter the server address, e.g. `http://maloja:42010`; no key needed |
| Last.fm account | direct API import (needs `MuseFM__LastFmApiKey`) |

Imports run in the background. A scrobble is a duplicate when the user already has one with the same timestamp,
so re-importing a file — or importing overlapping sources — is safe. Each user can have three imports waiting at a
time; an export that is one JSON document, and each file inside a `.zip`, can be up to 256 MB. A Maloja server is read one month at a time, newest
first, which keeps large histories fast; the address must be reachable from the MuseFM server. Only admins can import
from a server on a private or local address, unless `MuseFM__ImportFromPrivateAddresses` is set.

Every import can be limited to a **timeframe**: pick a first day, a last day, or both (days in your time zone, the last
one included), and only scrobbles from then are imported. A Last.fm account and a Maloja server are then only asked for
that part of the history. Loved tracks are imported whatever the timeframe.

Spotify lists everything you started, so plays of under 30 seconds (what Spotify does not count as a stream either),
podcast episodes and audiobooks are left out. Spotify notes when a play ended; the scrobble is dated to when it began.
The two Spotify exports date plays differently (to the second and to the minute), so import only one of them for the
same time, or the same plays arrive twice.

Loved tracks come along with a Last.fm account import (`user.getLovedTracks`) and with a ListenBrainz export `.zip`
(its `feedback.jsonl`; ListenBrainz feedback names recordings by MusicBrainz id, so it is matched to recordings the listens
in the same export identified). MusicBrainz ids in ListenBrainz files, Last.fm CSVs with `*_mbid` columns and Last.fm
account imports are kept.

**Settings → Export** downloads your complete history as Maloja JSON (importable with `maloja import`), Last.fm CSV, or a
ListenBrainz-style `.zip` (`listens.jsonl` with MusicBrainz ids where known, and `feedback.jsonl` with your loves) that MuseFM
can import again, loves included.

## How data is modelled

- Artists, albums and tracks form a shared catalog. Names are compared by a matching key that ignores case, spacing,
  punctuation, apostrophe styles, accents on Latin letters, a leading "The" (artists) and `&`/`+` vs "and": *Björk* = *Bjork*,
  *AC/DC* = *AC DC*, *Guns N' Roses* = *Guns N’ Roses*. Marks that change a word in other scripts (Japanese dakuten,
  Devanagari vowel signs) are kept. The first spelling seen is the one displayed.
- Featured artists are split out of artist fields (`A feat. B & C`, `A (feat. B)`) and titles (`Song (feat. B)`,
  `Song [ft B]`, `Song (with B)`, `Song feat. B`). Plain `&` (e.g. *Simon & Garfunkel*) is left alone.
- A scrobble belongs to an existing track when the titles match and the **main artist of either side is credited on the
  other**. So *Saku* by *Bicep*, by *Bicep feat. Clara La San* and *Saku (with Clara La San)* are one track (credited
  to both), while *Intro* by *A feat. C* and *Intro* by *B feat. C* stay two. Combined names count by their parts for
  this (*A & B* matches *A*). Albums match the same way; without an album artist, an album belongs to the track's main artist.
- Trailing release tags don't make a new track: *Song - 2011 Remaster*, *(Radio Edit)*, *(Single/Album Version)*,
  *[Mono]*, *(Original/Extended Mix)*, *(Re-Recorded)*. Different recordings do: *(Live)*, *(Acoustic)*, *(Instrumental)*,
  remixes. Album editions are folded: *(Deluxe Edition)*, *[Remastered]*, *(20th Anniversary Edition)*. There is no fuzzy
  matching, so *Pt. 1* and *Pt. 2* never merge.
- When these rules change, the catalog is re-keyed once at startup and rows that now match are merged (scrobbles move to
  the surviving row).
- MusicBrainz ids (recording, release, artist) that clients send are stored with the first track, album or artist that
  gets them. They don't affect matching, but metadata lookups use them before searching by name, ListenBrainz loves find
  recordings by them, and the read APIs and the ListenBrainz export return them. Artist ids are only kept where they pair
  up unambiguously with the artist names.
- **Admin → Catalog** fixes what the rules get wrong, for every user: rename an entry, set its MusicBrainz id, or merge a
  duplicate into the right entry (scrobbles, loves and obsessions move over). The old name is kept as an **alias**, so scrobbles that
  still use it land on the merged or renamed entry; aliases of tracks and albums only apply to the same artist. Merging two
  artists also merges the tracks and albums that were apart only because of the two names, and an artist left without
  any track (say, after merging a misattributed track into the right one) is removed. Admins get an *Edit* link on every
  artist, album and track page.
- Statistics are always per user. Charts use the profile owner's time zone (Settings → Profile).

## Managing users

**Admin → Users** lists every account with its scrobbles and latest scrobble. *Add user* creates an account (also while
sign-up is closed) with a username, password, time zone, public or private profile, and optionally admin rights; the user
can change the password under Settings. *Edit* changes the username (which also changes the profile address and the name
used to sign in; the user is signed out and signs in again with the new name), profile visibility, time zone and admin
role, sets a new password for someone who forgot theirs (their scrobblers keep working with their API keys), or deletes the
account with all of its data after typing the username to confirm. A new role, a new password and a deleted account take
effect at once: the user is signed out everywhere and signs in again. A new password, whoever sets it, also ends the
Last.fm-style sessions of apps that logged in with the old one. It can also remove a profile picture or banner that
doesn't belong on the server (the user can upload a new one). Admins can't remove their own admin role or delete
themselves here, so there is always an admin; they change their own password under Settings.

## Statistics pages

Every profile has these tabs:

- **Overview** – the current obsession, totals with the change against the previous period, listening activity, top
  artists, albums (a cover wall when a metadata connector is active) and tracks, recent scrobbles. Owners set an
  obsession under *Set as obsession* on a track's page, with how long it stays (1 day, 3 days, 1 or 2 weeks, 1 or 3
  months, or until changed); picking another track replaces it, and *Remove* takes it down early. Past obsessions are
  kept in the database.
- **Scrobbles**, **Artists**, **Albums**, **Tracks** – the full history and the full charts. Every artist, album and track
  has its own page with first and last scrobble, listening activity, its tracks or albums, and its scrobbles.
- **Loved** – loved tracks, newest first (and tracks marked as disliked on ListenBrainz, if any). Owners love and unlove
  with the ♥ on track pages and in scrobble lists.
- **Insights** – the past year as a calendar heatmap with current and longest streak; for the chosen range: scrobbles per
  day, active days, time of day, day of the week, a weekday × hour heatmap, newly discovered artists, and how much of the
  listening goes to the top artists.
- **Compare** – a day, week (Monday–Sunday), month or year against the one before, with arrows to go back in time.
  While the current period is still running, it is compared with the previous one *up to the same point*, so the changes
  are like for like. Shows a running-total chart, per hour/day/month columns, and the top 10 artists, albums and tracks
  with their movement (▲ up, ▼ down, new).
- **Friends** – who the user follows, with what each is playing now or played last, and who follows them.

**Periods.** Overview, the charts, Insights and the artist, album and track pages share a period picker: the rolling 7 days,
30 days and 12 months, all time, or under *Dates* a calendar year, a calendar month, or any span of days (with ‹ › to step
to the year, month or span before or after). Periods are part of the URL: `?range=week`, `?range=2024`, `?range=2024-03`
or `?range=2024-03-01..2024-04-15`. Overview compares a calendar period with the one before it (a running one up to the
same day), and a span of days with as many days before it.

**Social.** Signed-in users can follow public profiles (*Follow* on the profile), see a **Feed** of the scrobbles of
everyone they follow (with what they are playing right now), and **Compare taste** with anyone they can see: a
compatibility score (cosine similarity of log-scaled artist play counts, so a few heavily played artists don't decide it),
the artists they share, and each one's top artists the other has never played.

Every chart has hover tooltips and a *Show as table* view with the exact numbers.

Pages follow the **viewer's** preferences (Settings → Preferences), not the profile owner's: theme (system, light, dark, or
one of newsprint, kraft, midnight, forest and true black), accent color (also used by the charts; each accent is
contrast-checked in every theme), 12- or 24-hour clock, weeks
starting Monday or Sunday, showing artwork, and the period Overview and the top lists open with. Visitors who aren't
signed in get the defaults and follow their device's light/dark setting. Charts always use the profile owner's time zone.

## Development

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) (`sudo dnf install dotnet-sdk-10.0` on Fedora).

```bash
dotnet run --project src/MuseFM          # http://localhost:5080, data in src/MuseFM/data
dotnet test                              # integration tests run the app against a temp SQLite DB
```

Schema changes:

```bash
dotnet tool install --global dotnet-ef --version 10.0.15
dotnet ef migrations add <Name> -p src/MuseFM -o Data/Migrations
```

Migrations are applied automatically on startup.

### Releases

Every push to `main` runs the tests and, if the commits since the last `vX.Y.Z` tag call for it, publishes
`ghcr.io/zortexsenpai/musefm` as `X.Y.Z`, `X.Y` and `latest`, then tags the commit and creates a GitHub release. The
version follows [Conventional Commits](https://www.conventionalcommits.org):

| Commit                                                  | Bump  |
|---------------------------------------------------------|-------|
| `feat!: …`, or a `BREAKING CHANGE: …` footer            | major |
| `feat: …`                                               | minor |
| `fix: …`, `perf: …`, `revert: …`                        | patch |
| anything else (`docs`, `chore`, `refactor`, `test`, …)  | none, nothing is published |

Pull requests run the tests and build the image without pushing it; the run's summary says which version merging
would publish. The rules live in `.github/scripts/next-version.sh`.

```
src/MuseFM
├── Api/                 ListenBrainz + Audioscrobbler endpoints, export downloads, cached artwork
├── Data/                EF Core context, entities, migrations
├── Services/            ScrobbleService (single write path), StatsService/StatPeriod, feedback (loves),
│                        social (follows, feed, taste), CatalogEditor (rename/merge/aliases), API keys, now playing
│   ├── Import/          File formats, Last.fm and Maloja clients, background import worker, export
│   └── Metadata/        Metadata connectors (Navidrome, Maloja, ListenBrainz, Last.fm) and their cache
└── Pages/               Razor Pages UI (profiles, charts, settings, admin)
tests/MuseFM.Tests       xUnit integration tests (WebApplicationFactory)
```

## Limitations

- “Now playing” is kept in memory and is cleared on restart.
- The legacy Audioscrobbler 1.2 handshake protocol and Last.fm's browser-based auth flow (`auth.getToken`/`auth.getSession`) are not implemented; clients must use `auth.getMobileSession`.
- The activity charts on Overview and artist, album and track pages place day boundaries with the time zone's current UTC
  offset, so they can be off by an hour around DST changes. Insights and Compare convert every scrobble with its own
  offset, and calendar periods start and end at local midnight.
- A track is album-independent, so two different songs with the same title by the same artist (an *Intro* on two albums)
  are one track; the catalog editor can merge but not split.
- ListenBrainz feedback that names a recording only by MessyBrainz id (`recording_msid`), or by a MusicBrainz id no scrobble
  carried, can't be matched to a track and is not stored.
