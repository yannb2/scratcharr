# Scratcharr

Manages the downloads folder (`/downloads` on the `scratch` pool): it finds what nothing claims any more,
says who will remove each download and when, and (in later versions) unpacks archived ISOs for Ripparr,
copies folders in from USB drives and cleans up. Same look as Ripparr and the Muxarr fork, shared code from
[arrkit](https://github.com/yannb2/arrkit).

**v0.1 is read-only.** It reads qBittorrent, SABnzbd, Radarr, Sonarr and Lidarr and walks the folders; it
never changes a file, a torrent or a setting. The downloads volume can be mounted `:ro`.

## What it shows

| Page | |
| --- | --- |
| Overview | Space used and free, totals per verdict, the biggest items to review, what leaves in the next 24 h, every folder |
| Downloads | Every item with its verdict, client state, arr app, size, age and when it leaves. Filters live in the URL |
| Item | The evidence: torrent, SABnzbd job, arr queue and history, the largest files |
| Settings check | Per download client category: its folder, which arr app uses it, who removes finished downloads. Warnings about gaps |
| Tools | 7-Zip's version, its pin and the newest release |
| Settings | Connections (saved in the database, they win over the environment variables) and the running values |

An **item** is one entry of a watched folder: a torrent's folder or file, a usenet job folder, something copied
by hand. Category folders (`radarr`, `sonarr`, `manual`, and every category qBittorrent or SABnzbd reports)
are opened, so each torrent is its own item. Loose entries at the top of `/downloads` are listed too.

### Verdicts

| Verdict | When |
| --- | --- |
| Active | A torrent or SABnzbd job still owns it: downloading, seeding, queued |
| Waiting for import | An arr app has it in its queue (import pending) |
| Seeding done | Stopped at its seeding limit, still listed in qBittorrent |
| Imported, leftover | An arr app imported it (a copy) and no client owns it any more |
| Failed | The download or the import failed |
| Manual | In the `manual` category or a folder filled by hand (`import`, `discs`) |
| Half-finished | `.!qB` files that no torrent owns |
| Stray | Nothing claims it at all |
| Can't tell | The client that would know (qBittorrent for torrents, SABnzbd for usenet) could not be read |
| Protected | Ripparr's folders and the recycle bin: never touched |

Matching uses paths (qBittorrent's content path, SABnzbd's storage folder, the arr queue's output path and
history's dropped path) and download IDs (torrent hash, SABnzbd nzo id). All apps must see the downloads at
the same path (`/downloads` everywhere in the compose file).

**Leaves** says who removes an item and when: the torrent's seeding limit (its own or qBittorrent's global
one), then what happens at the limit (stop, remove, remove with files) and whether the arr app for that
category has *Remove Completed* on. "Never" means nothing will remove it on its own.

## Settings

Environment variables (`Scratcharr__...`), or `appsettings.json`:

| Setting | Default | |
| --- | --- | --- |
| `DownloadsRoot` | `/downloads` | Only items under it are listed |
| `Folders__N__Name/Path/Kind` | torrents, usenet/complete, usenet/incomplete, import, discs | Watched folders; `Kind` = Torrents, Usenet, UsenetIncomplete or Manual; paths relative to the root |
| `Protected__N` | `ripparr`, `.recycle` | Never judged or touched |
| `CategoryFolders__N` | radarr, sonarr, lidarr, books, manual | Opened even when no client has the category yet |
| `ScanMinutes` | 15 | Minutes between scans (Scan now starts one at any time) |
| `AllowChanges` | false | Off: read-only. For the unpack, copy and clean-up buttons of later versions |
| `Qbittorrent__Url/Username/Password` | | `http://gluetun:8080`; leave the login empty when the subnet is whitelisted |
| `Sabnzbd__Url/ApiKey` | | `http://sabnzbd:8080` |
| `Radarr__`, `Sonarr__`, `Lidarr__` `Url/ApiKey` | | |
| `DatabasePath` | `/config/scratcharr.db` | Saved connections, first-seen times |
| `Updates__Enabled` | true | Look up the newest 7-Zip once a day (nothing is installed) |

See [compose.example.yaml](compose.example.yaml). Port 8188.

`/api/stats` (no key) gives `items`, `strays`, `strayBytes`, `toReview`, `toReviewBytes`, `active`, `manual`,
`usedBytes`, `freeBytes`, `scanning`, `lastScan`, `toolUpdates` for a Homepage tile.

## Roadmap

1. v0.1 Find (read-only) — this version
2. v0.2 Unpack: `.rar`/`.7z`/`.zip` sets into `/downloads/discs/<name>/` for Ripparr, the torrent keeps seeding
3. v0.3 From USB: browse `/mnt/pc/usbxx`, copy into `/downloads/import` or `/downloads/discs`, checked after
4. v0.4 Clean up: recycle to `/downloads/.recycle` (7 days), remove through qBittorrent and SABnzbd

## Development

```bash
git submodule update --init
dotnet test
dotnet run --project src/Scratcharr      # http://localhost:8188, dev/ folders from appsettings.Development.json
```

Tools are pinned as `ARG` lines in the Dockerfile; the weekly *Tool updates* workflow opens a pull request
when a newer one exists (needs Settings > Actions > "Allow GitHub Actions to create and approve pull requests").

GPLv3, like Ripparr and Muxarr whose layout it reuses.
