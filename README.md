# Spotify Lyrics Presence

A small Windows 11 tray app that watches the Spotify desktop player and shows the **current lyric line**
on your Discord profile as a Rich Presence activity card:

```
Playing My Music                  <- activity name (see below)
[cover image]  (hover: "Song — Artist")
Song Title — Artist                    <- "details"
the line being sung right now          <- "state"
```

It reads Spotify through the Windows media-session API, fetches time-stamped lyrics from
[LRCLIB](https://lrclib.net/docs), and talks to the Discord desktop client over its documented local IPC pipe.
It does **not** touch your custom status, use a user token, or modify Discord.

## Custom status vs. activity card (important)

Discord shows two different things on your profile, and this app only controls one of them.

| | Custom status | Rich Presence activity |
|---|---|---|
| Where | The text/emoji bubble beside your avatar | The activity card ("Playing …") under your name |
| Who sets it | You, in Discord's own status menu | An application, through Discord's Rich Presence |
| Fields | Emoji + text | `details`, `state`, timestamps, images, buttons |
| Controlled by this app? | **No** | **Yes** |

Setting the Rich Presence `state` field puts the lyric on the **activity card**. It does **not** change your custom status,
and no Rich Presence field does. Switching the activity type (for example Playing → Listening) only changes how the card is
labelled; it does not move text into the custom-status bubble either.

As far as this project's sources go (Discord's [RPC documentation](https://docs.discord.com/developers/topics/rpc)), local Rich Presence has no
call for writing the custom status. Automating it would mean acting as your user account with an account token (a "self-bot"),
which Discord's terms prohibit and which risks your account. This project deliberately does not do that and never will.

If you want lyrics in the bubble you would have to update it by hand; there is no supported way to automate it.
**The custom-status requirement is therefore not met by this project.**

### Possible alternative (not implemented, not verified): `status_display_type`

Discord's activity object has a `status_display_type` field that chooses which activity field is shown as the one-line activity
text in the **server member list** (0 = app name, 1 = `state`, 2 = `details`; see the
[activity object](https://docs.discord.com/developers/events/gateway-events#activity-object) reference).
Pointing it at `state` would show the lyric there. Note this is a *different place* from the custom-status bubble beside your avatar: it
is still activity text, not your custom status. Before relying on it: the official pages consulted did not show the field, so the values above
come from secondary sources, and it is untested whether the local IPC `SET_ACTIVITY` command accepts it or how the desktop client renders it.
It is not used by the app today.

## Status of each feature

| Area | State |
|---|---|
| LRC parsing, sync timeline, seek / track-change / repeat / pause logic, update throttling, reconnect logic, Discord text limits, LRCLIB matching (mocked HTTP), cache, Arabic normalisation | **Verified by automated tests** (52 tests) |
| Windows media-session access | **Verified** on this machine (API enumerates sessions); Spotify-specific fields **not yet checked** – run `--probe` while Spotify plays |
| Real LRCLIB responses | Not exercised live (tests use mocked responses) |
| Discord IPC handshake, Listening type, image asset, second-based timestamps, seek / pause / resume / next-song behaviour | **Verified live** against Spotify + Discord by comparing sent payloads and Discord's replies with Spotify's media session |
| Visual look of the card, application name, reconnect after Discord restart | Not covered by automated tests; check by eye |
| Tray icon / window behaviour | Built, not clicked through |

## Requirements

* Windows 10 (19041) or 11
* [.NET 8 SDK](https://dotnet.microsoft.com/download) to build (or .NET 8 Desktop Runtime to run a framework-dependent build)
* Spotify **desktop** app and the Discord **desktop** app (the browser version has no IPC pipe)

## Install and build

```powershell
git clone https://github.com/8ccs/spotify-lyrics-presence.git
cd spotify-lyrics-presence
dotnet build -c Release
dotnet test
```

## Discord application setup

1. Open the [Discord Developer Portal](https://discord.com/developers/applications) and choose **New Application**.
2. Name it **Spotify Lyrics**. The card header ("Listening to <name>") always shows the *application's name*; the app cannot
   change it. To rename: Developer Portal → your application → **General Information → Name** → save. The Application ID stays the same.
3. On **General Information**, copy the **Application ID** (a long number).
4. Paste it into the app's *Discord application ID* box.

5. Open **Rich Presence → Art Assets**, upload your image and give it the key `music_cover`. Every update sends it as
   `assets.large_image` . The asset must live in the *same* application whose ID you enter,
   and Discord can take a few minutes to serve a newly uploaded asset. A wrong key shows no image.

Optional: paste a **public direct https link** to an image or GIF into the app's *Image URL* box to use it instead of `music_cover`
(max 256 characters; leave empty for the art asset). Host it somewhere separate from this code, for example a small public repo,
and use the `raw.githubusercontent.com` link. Discord fetches and caches the file itself; an invalid or non-https value silently
falls back to `music_cover`. Whether a GIF animates is up to the Discord client.

No secret, token or OAuth setup is needed for local Rich Presence.

## Activity-sharing settings in Discord

*User Settings → Activity Privacy → Share my activity* must be **on**, or others won't see the card.
(You always see your own card.) Discord only shows activity for the account signed in to the desktop client that is running.

## Launch

```powershell
dotnet run --project src/SpotifyLyricsPresence.App -c Release
```

1. Start Spotify and Discord, and play a track.
2. Enter the application ID and press **Start sharing**.
   Tick **Start sharing when the app opens** to skip this step on later launches.
3. Adjust **Offset (ms)** if lyrics feel early (increase) or late (decrease). Settings are saved automatically.
4. Closing the window sends the app to the tray; use tray → **Quit** to exit (this clears the card).

**Import LRC…** attaches a local `.lrc` file to the currently playing track and takes priority over downloaded lyrics.

Diagnostics: `SpotifyLyricsPresence.exe --probe > probe.txt` lists media sessions and what each exposes.
Set the environment variable `SLP_TRACE` to a file path before launching to log every `SET_ACTIVITY` sent and Discord's reply (off by default; may contain song titles, don't share it).

## Behaviour notes

* The card is a **Playing** activity (`type: 0`, sent in every update). `details` = "Song — Artist", `state` = current lyric. The image is sent without `large_text`, so the song title is not repeated. Limitation: the progress *bar* is a Listening-card feature; a Playing card shows the same timestamps as an "elapsed" / "left" timer text instead.
* **Timer / progress bar:** `timestamps.start/end` are sent in Unix **seconds** (the unit used in Discord's RPC example), computed from Spotify's real position and duration. The start is only recomputed on a track change, seek (> 2 s jump), or resume, so a lyric change never restarts the timer. Pausing clears the card; resuming shows it at the right position.
* While lyrics are loading, missing, unsynced-only or the lyrics service is down, the card says **"Listening now"**; the technical reason is shown only in the desktop app. **"Instrumental"** appears only when LRCLIB confirms it.
* **Card layout:** Discord writes "<activity-type prefix> <name>" as the header (Playing for type 0); details and state follow. Discord's RPC docs list no `name` field, so the header name comes from Discord's record of the application (Developer Portal → General Information). Discord's reply can keep an older name for a while (seen: "music" although the portal already said otherwise); the optional **Activity name** setting sends an undocumented `name` with every update (lyric changes and reconnects included). Discord echoes it back in its reply; whether the card header actually shows it must be checked by eye. Leave the box empty to send no name.
* **Spotify ads (free accounts):** detected from the media session: during an ad Spotify turns **both** the Next and Previous controls off and reports **no album** (a real ad measured: album empty, skip controls off, 15 s, window title = advertiser name). Song duration, missing lyrics and the advertiser are *not* used. During an ad the card shows `Ad` / `Wait a sec…` with your name and image, no timer, no lyric, and no lyric lookups; a lookup that finishes late is discarded. Music returns to the card after two consecutive non-ad samples (~0.5 s), so back-to-back ads never flash the previous song, and the timer is recomputed from Spotify's current position. Limitation: a track with no album that is alone in the queue (both skip buttons disabled) would be mistaken for an ad; premium accounts have no ads. Ad detection was checked against real Spotify samples; see the test list for the covered cases.
* A track change replaces the card within the update spacing; a lyric is only ever taken from the current track's lyrics.
* Long lines are cut to 128 characters at a word boundary with "…" (Arabic included).

* Discord updates are spaced at least 4 seconds apart (a conservative reading of Discord's published limit of 5 updates per 20 seconds – Discord's RPC page itself gives no figure). Only the newest line is sent; lines that became outdated while waiting are dropped.
* Pausing clears the card; resuming restores it. Stopping or quitting clears it immediately.
* If Discord closes, the app retries every 5 seconds and re-sends the current line on reconnect.
* Unsynced-only lyrics are not shown, since there is no honest timing.
* Fields are trimmed to Discord's 128-character limit without splitting characters; one-character lines are padded with an invisible character (Discord requires ≥ 2).
* English and Arabic lyrics are passed through unchanged (UTF-8). For matching, Arabic diacritics and letter variants (أ/إ/آ→ا, ى→ي, ة→ه) are folded. The card's RTL rendering is up to Discord.
* Lyrics are matched by title, artist and duration (±3 s); a version with a different length is rejected.

## Data locations

`%APPDATA%\SpotifyLyricsPresence\settings.json` and `lyrics-cache\` (downloaded lyrics, imported LRC files). Delete the folder to reset. Both are excluded from git.

## Troubleshooting

| Symptom | Try |
|---|---|
| "Discord is not running (no IPC pipe found)" | Start the desktop Discord client; the app retries automatically. |
| "Discord rejected the application ID" | Re-copy the Application ID; no spaces. |
| Connected but no card | Check Activity Privacy; disable/enable *Display current activity as a status message*; make sure no other tool is using the same application ID. |
| "Spotify: nothing playing" | Spotify must be the desktop app with a media session. Run `--probe`; ads are ignored. |
| Lyrics missing / card says "Listening now" | The app window's **Lyrics:** line gives the reason: *no entry on LRCLIB*, *only unsynced lyrics*, *request failed (timeout / HTTP 503 …)*, or *no artist (advertisement)*. Failed requests are retried automatically (after 3 s, 10 s, 30 s); when LRCLIB simply has no timed lyrics, use **Import LRC…**. Set `SLP_TRACE` to log each lookup (`LYR` lines). |
| Lyrics early/late | Change the offset. |

## Limitations

* Spotify's position updates are coarse; the app extrapolates from the media session's last-updated time, so timing can drift slightly after some seeks.
* One Discord update per ~4 s means fast lines are skipped.
* Uses the desktop Spotify only; a Spotify web player or other apps are ignored.
* Discord's RPC documentation describes the IPC handshake and frames but not update limits; the interval is an assumption.
* Windows only.
* Lyrics appear on the activity card, not in the custom-status bubble beside your avatar (see above).

## Packaging (Windows)

Self-contained single-file build (no .NET install required on the target):

```powershell
./scripts/publish.ps1            # output in artifacts/win-x64
```

which runs `dotnet publish src/SpotifyLyricsPresence.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`.
Zip `artifacts/win-x64` to distribute. For an installer, point [Inno Setup](https://jrsoftware.org/isinfo.php) or MSIX packaging at that folder.

## Project layout

```
src/SpotifyLyricsPresence.Core   parser, timeline, clock, throttle, LRCLIB client, cache, Discord IPC, engine
src/SpotifyLyricsPresence.App    WinForms UI, tray, media-session source
tests/SpotifyLyricsPresence.Tests
```

## Dependencies and licences

Only the .NET 8 platform (WinForms, `Windows.Media.Control` via the Windows SDK projection) is used at runtime; tests use xUnit
(Apache-2.0) and Microsoft.NET.Test.Sdk (MIT). Lyrics come from the LRCLIB service under its own terms; check them
before redistributing lyric data. Lyrics are copyrighted by their owners – this app only displays one line at a time and caches locally.
The code is MIT-licensed (see `LICENSE`). Not affiliated with Spotify or Discord.
