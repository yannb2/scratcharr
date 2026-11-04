# Scratcharr

Manages the downloads folder (`/downloads` on the `scratch` pool): it finds what nothing claims any more,
says who will remove each download and when, unpacks archived ISOs for Ripparr, copies files and folders in
from USB disks, and (in a later version) cleans up. Same look as Ripparr and the Muxarr fork, shared code from
[arrkit](https://github.com/yannb2/arrkit).

**Read-only by default.** It reads qBittorrent, SABnzbd, Radarr, Sonarr and Lidarr and walks the folders; it
never changes a file, a torrent or a setting, and the downloads volume can be mounted `:ro`. Unpacking (v0.2)
and copying from USB (v0.3) need `Scratcharr__AllowChanges=true` and `/downloads` mounted read-write; even then
the archives and the USB disks are only read.

## What it shows

| Page | |
| --- | --- |
| Overview | Space used and free, totals per verdict, the biggest items to review, what leaves in the next 24 h, every folder |
| Downloads | Every item with its verdict, client state, arr app, size, age and when it leaves. Filters live in the URL |
| Item | The evidence: torrent, SABnzbd job, arr queue and history, the largest files |
| Unpack | Every archive set (`.partNN.rar`, `.rar` + `.r00`, `.7z`, `.7z.001`, `.zip`) per item: parts, size, Look inside (7-Zip's listing: ISOs, BDMV, videos, unpacked size), a folder name, Unpack |
| From USB | The disks in `/mnt/pc`, then their folders: what each entry holds (disc, video, other), its size, where it goes, and whether it was copied already. Tick, pick Auto / import / discs, Copy |
| Jobs | Unpacks and copies one at a time with a progress bar, the log, Cancel, and Open in Ripparr for each ISO or BDMV folder that came out |
| Settings check | Per download client category: its folder, which arr app uses it, who removes finished downloads. Warnings about gaps |
| Tools | 7-Zip's version, its pin and the newest release |
| Settings | Connections (saved in the database, they win over the environment variables) and the running values |

An **item** is one entry of a watched folder: a torrent's folder or file, a usenet job folder, something copied
by hand. Category folders (`radarr`, `sonarr`, `manual`, `prowlarr`, and every category qBittorrent or SABnzbd reports)
are opened, so each torrent is its own item. Loose entries at the top of `/downloads` are listed too.

### Verdicts

| Verdict | When |
| --- | --- |
| Active | A torrent or SABnzbd job still owns it: downloading, seeding, queued |
| Waiting for import | An arr app has it in its queue (import pending) |
| Seeding done | Stopped at its seeding limit, still listed in qBittorrent |
| Imported, leftover | An arr app imported it (a copy) and no client owns it any more |
| Failed | The download or the import failed |
| Manual | In the `manual` or `prowlarr` category (added by hand, or grabbed from a Prowlarr search) or a folder filled by hand (`import`, `discs`) |
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
| `CategoryFolders__N` | radarr, sonarr, lidarr, books, manual, prowlarr | Opened even when no client has the category yet |
| `ScanMinutes` | 15 | Minutes between scans (Scan now starts one at any time) |
| `AllowChanges` | false | Off: read-only. On: Unpack and From USB work (mount `/downloads` read-write) |
| `DiscsFolder` | `discs` | Where Unpack puts each archive's contents, one folder each; Ripparr finds discs there |
| `ImportFolder` | `import` | Where From USB puts videos and other files, for Manual Import in Radarr or Sonarr |
| `UsbRoot` | `/mnt/pc` | The USB disks, one folder each; mount it `:ro,rslave` |
| `UsbSkip__N` | `iso-mounts` | Folders in `/mnt/pc` that are not disks (Ripparr's ISO mounts): not listed |
| `VerifyCopies` | true | Read each copy back and compare its xxHash with the original's before it gets its real name |
| `RipparrUrl` | | Ripparr as the browser reaches it (`http://10.1.72.11:8186`), for Open in Ripparr links |
| `Qbittorrent__Url/ApiKey/Username/Password` | | `http://gluetun:8080`; the API key (qBittorrent 5.2+, Options > WebUI) wins over the login; leave both empty when the subnet is whitelisted |
| `Sabnzbd__Url/ApiKey` | | `http://sabnzbd:8080` |
| `Radarr__`, `Sonarr__`, `Lidarr__` `Url/ApiKey` | | |
| `DatabasePath` | `/config/scratcharr.db` | Saved connections, first-seen times |
| `Updates__Enabled` | true | Look up the newest 7-Zip once a day (nothing is installed) |

See [compose.example.yaml](compose.example.yaml). Port 8188.

`/api/stats` (no key) gives `items`, `strays`, `strayBytes`, `toReview`, `toReviewBytes`, `active`, `manual`,
`usedBytes`, `freeBytes`, `scanning`, `lastScan`, `toolUpdates`, `archives`, `jobs` for a Homepage tile.

### Unpack

7-Zip extracts into `/downloads/discs/<name>.unpacking`, and the folder is renamed to `<name>` only when
everything is out, so Ripparr never lists half a disc. A failed or cancelled job removes that `.unpacking`
folder (and nothing else). Before queuing, and again right before it runs, Scratcharr checks:

- Changes are on.
- The name has no slashes.
- The target folder doesn't exist yet.
- No other job uses that target.
- The archive is still there.
- The unpacked size plus 1 GiB fits in the free space.

Password-protected archives fail at once with a clear message. SABnzbd's incomplete folder is not offered,
because SABnzbd unpacks its own jobs.

### From USB

The page lists the folders in `/mnt/pc` (one per USB disk or PC share, except `iso-mounts`; an empty mount point
shows as *not mounted*) and lets you browse them. **Auto** sends ISO files and folders holding a BDMV folder or an ISO to `/downloads/discs/` (Ripparr),
and everything else to `/downloads/import/` (Manual Import in Radarr or Sonarr); the menu can force one or the other.
Each ticked entry becomes one copy job, keeping its name.

The copy goes to `<name>.copying` and is renamed when complete. Each file is hashed (xxHash3) as it is read from the
USB disk; with `VerifyCopies` on, the copy is then read back and compared, so a bad cable or a failing disk ends in a
failed job, not a broken file. Modified times are kept; symbolic links are skipped. Copy only: nothing is ever
deleted from the USB disk, which stays mounted `:ro`. Before queuing, and again right before it runs:

- Changes are on.
- The source is under `/mnt/pc` and still there.
- Nothing by that name exists in the target folder, and no job is copying it already.
- The size plus 1 GiB fits in the free space.

## Roadmap

1. v0.1 Find (read-only)
2. v0.2 Unpack: `.rar`/`.7z`/`.zip` sets into `/downloads/discs/<name>/` for Ripparr, the torrent keeps seeding
3. v0.3 From USB: browse `/mnt/pc/usbxx`, copy into `/downloads/import` or `/downloads/discs`, checked after — this version
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
