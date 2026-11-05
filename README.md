# Scratcharr

Manages the downloads folder (`/downloads` on the `scratch` pool): it finds what nothing claims any more,
says who will remove each download and when, unpacks archived ISOs for Ripparr, copies files and folders in
from USB disks and mounted shares (External), and cleans up through a 7-day recycle folder. Same look as Ripparr and the Muxarr fork, shared code from
[arrkit](https://github.com/yannb2/arrkit).

**Read-only by default.** It reads qBittorrent, SABnzbd, Radarr, Sonarr and Lidarr and walks the folders; it
never changes a file, a torrent or a setting, and the downloads volume can be mounted `:ro`. Unpack, External
and Clean up need `Scratcharr__AllowChanges=true` and `/downloads` mounted read-write; even then the archives and
the external disks and shares are only read, and nothing is deleted before 7 days in the recycle folder.

## What it shows

| Page | |
| --- | --- |
| Overview | Space used and free, totals per verdict, the biggest items to review, what leaves in the next 24 h, every folder |
| Downloads | Every item with its verdict, client state, arr app, seeding time (of its limit), size, age and when it leaves. Every column sorts both ways (click it again to turn it around). Filters and sorting live in the URL. Tick items and Recycle them |
| Item | The evidence: torrent, SABnzbd job, arr queue and history, the largest files |
| Unpack | Every archive set (`.partNN.rar`, `.rar` + `.r00`, `.7z`, `.7z.001`, `.zip`) per item: parts, size, Look inside (7-Zip's listing: ISOs, BDMV, videos, unpacked size), a folder name, Unpack |
| External | The USB disks and mounted shares in `/mnt/pc`, then their folders: what each entry holds (disc, video, other, archives with an Unpack link), its size, where it goes, and whether it was copied already. Anything that can't be read (Windows' System Volume Information...) is listed in red: click it for the error. Tick, pick Auto / import / discs, Copy |
| Recycle | What Clean up moved away: days left, Restore, Delete, Empty now; the action log; the weekly ntfy summary (Send now) |
| Jobs | Unpacks and copies one at a time with a progress bar, the time left and speed, the log, Cancel, and Open in Ripparr for each ISO or BDMV folder that came out |
| Logs | The last 2000 lines since the app started, newest first, with search and a level filter (shared arrkit code) |
| Tools | 7-Zip's version, its pin and the newest release |
| Settings | Integrations: connections (saved in the database, they win over the environment variables). Protected folders: add or remove the folders Scratcharr never touches (saved in the database, they win over `Protected__N`). Auto clean-up: opt-in rules and the low space alert. Settings check (with the folder structure card): per download client category, its folder, which arr app uses it, who removes finished downloads, with warnings about gaps (the count shows on the tab and next to Settings in the menu). Status: the running values |

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
| Manual | In the `manual` or `prowlarr` category (added by hand, or grabbed from a Prowlarr search) or a folder filled by hand (`import`, `discs`, `firefox` = the Firefox container's downloads) |
| Half-finished | `.!qB` files that no torrent owns |
| Stray | Nothing claims it at all |
| Can't tell | The client that would know (qBittorrent for torrents, SABnzbd for usenet) could not be read |
| Protected | Ripparr's folders, the disc rips (`rips`, Spinarr's albums for Lidarr) and the recycle bin: never touched |

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
| `Folders__N__Name/Path/Kind` | torrents, usenet/complete, usenet/incomplete, import, discs, firefox | Watched folders; `Kind` = Torrents, Usenet, UsenetIncomplete or Manual; paths relative to the root |
| `Protected__N` | `ripparr`, `rips`, `.recycle` | Never judged or touched |
| `CategoryFolders__N` | radarr, sonarr, lidarr, books, manual, prowlarr | Opened even when no client has the category yet |
| `ScanMinutes` | 15 | Minutes between scans (Scan now starts one at any time) |
| `AllowChanges` | false | Off: read-only. On: Unpack and External copies work (mount `/downloads` read-write) |
| `DiscsFolder` | `discs` | Where Unpack puts each archive's contents, one folder each; Ripparr finds discs there |
| `ImportFolder` | `import` | Where External copies put videos and other files, for Manual Import in Radarr or Sonarr |
| `UsbRoot` | `/mnt/pc` | The External page's root: USB disks and mounted shares, one folder each; mount it `:ro,rslave` |
| `UsbSkip__N` | `iso-mounts` | Folders in `/mnt/pc` that are not disks (Ripparr's ISO mounts): not listed |
| `MoviesLibrary` / `TvLibrary` | `/movies` / `/tv` | The library, for Copy as extras: mount them read-write at the same paths as Radarr and Sonarr |
| `VerifyCopies` | true | Read each copy back and compare its xxHash with the original's before it gets its real name |
| `RecycleFolder` | `.recycle` | Where Recycle moves items, `<date>/<path inside /downloads>` |
| `RecycleDays` | 7 | Days before a recycled item is deleted for good (checked hourly) |
| `Ntfy__Url/Token` | | ntfy topic URL (`NTFY_URL`, as for the scripts) and optional token: the weekly summary |
| `Ntfy__Day/Hour` | Monday, 9 | When the summary goes out (local time) |
| `RipparrUrl` | | Ripparr as the browser reaches it (`http://10.1.72.11:8186`), for Open in Ripparr links |
| `SpecarrUrl` | | Specarr as the browser reaches it (`http://10.1.72.11:8185`), for Open in Specarr links (item page, its largest files, External files): tracks, bit rates, Dolby Vision, source guess |
| `Qbittorrent__Url/ApiKey/Username/Password` | | `http://gluetun:8080`; the API key (qBittorrent 5.2+, Options > WebUI) wins over the login; leave both empty when the subnet is whitelisted |
| `Sabnzbd__Url/ApiKey` | | `http://sabnzbd:8080` |
| `Radarr__`, `Sonarr__`, `Lidarr__` `Url/ApiKey` | | |
| `DatabasePath` | `/config/scratcharr.db` | Saved connections, first-seen times |
| `Updates__Enabled` | true | Look up the newest 7-Zip once a day (nothing is installed) |

See [compose.example.yaml](compose.example.yaml). Port 8188.

`/api/stats` (no key) gives `items`, `strays`, `strayBytes`, `toReview`, `toReviewBytes`, `active`, `manual`,
`usedBytes`, `freeBytes`, `scanning`, `lastScan`, `toolUpdates`, `archives`, `jobs`, `recycled`, `recycledBytes`
for a Homepage tile.

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

### External (USB disks and shares)

The page lists the folders in `/mnt/pc` (one per USB disk or PC share, except `iso-mounts`; an empty mount point
shows as *not mounted*) and lets you browse them. A file or folder the container can't read, such as Windows'
`System Volume Information`, is listed in red and can't be ticked; click its name to see the error. **Auto** sends ISO files and folders holding a BDMV folder or an ISO to `/downloads/discs/` (Ripparr),
and everything else to `/downloads/import/` (Manual Import in Radarr or Sonarr); the menu can force one or the other.
Each ticked entry becomes one copy job, keeping its name.

The copy goes to `<name>.copying` and is renamed when complete. **A restart doesn't lose a copy:** each file that is
written and flushed is listed in the job (its length, modified time and xxHash). When Scratcharr stops or the container is
restarted or killed mid-copy, the job goes back to the queue and carries on: files on the list whose original hasn't
changed are kept (and still checked against their hash), the file it was in and the rest are copied. An unpack cut
short starts again (7-Zip can't resume). Each file is hashed (xxHash3) as it is read from the
source; with `VerifyCopies` on, the copy is then read back and compared, so a bad cable or a failing disk ends in a
failed job, not a broken file. Modified times are kept; symbolic links are skipped. Copy only: nothing is ever
deleted from the source, which stays mounted `:ro`. Before queuing, and again right before it runs:

- Changes are on.
- The source is under `/mnt/pc` and still there.
- Nothing by that name exists in the target folder, and no job is copying it already.
- The size plus 1 GiB fits in the free space.

**Unpack from External:** an archive set on an external disk or share (a `.rar` set, `.7z` or `.zip`, or a folder
holding some) shows an *Unpack* link in the Holds column. It opens the Unpack page for just those archives, which
unpack into `/downloads/discs/<name>` exactly like the ones in the downloads. The archive is only read.

### Copy as extras

Bonus features (behind the scenes, deleted scenes, trailers, interviews…) that came as a download or sit on an external
disk can go straight into a film's or series' folder in the library, each in the folder Jellyfin shows as extras:
`Heat (1995)/featurettes/Making Of.mkv`, or `Season 01/featurettes/` for a season's extra. **Copy as extras…** is on an
item's page (when it holds videos) and next to each folder or video on the **External** page.

The page lists the videos, ticks all but samples, guesses each one's type from its name ("Teaser" is a trailer, "Making
Of" behind the scenes, a file in a `Deleted Scenes` folder a deleted scene) and a clean name (`Heat.1995.Making.Of.1080p`
gives `Making Of`): both can be changed. Pick the film or series (the search starts with the title from the release
name; Movies or TV); for a series, a season number puts the extra in that season's folder (0 = Specials). The plan shows
where each file goes. It runs as a job: the source is only read, each file is written under a hidden `.Name.mkv.copying`
name, read back and compared (`VerifyCopies`), then renamed. Nothing in the library is overwritten: the same name and
size counts as already there, a different file makes `Name (2).mkv`. A job stopped by a restart carries on, skipping what
is already there. The types are the folder names Jellyfin and Radarr/Sonarr agree on (`featurettes`, `behind the
scenes`, `deleted scenes`, `interviews`, `scenes`, `shorts`, `trailers`, `other`, `extras`; not `clips`, which Radarr
would take for the film). The plan and the copy are arrkit's (`Arrkit.Library`), shared with Ripparr's Extras page.

### Clean up

The item page says what Clean up can do with an item, or why not. On the Downloads page, tick the items that
can be cleaned and press Recycle.

- **Recycle** moves the item to `/downloads/.recycle/<date>/<path inside /downloads>`. That is a rename, so it is
  instant. An item on another mount inside `/downloads` goes to a `.recycle` folder at the top of that mount.
  A finished SABnzbd job also loses its history entry (its files are only moved).
- **Remove torrent and recycle** applies when a torrent still owns the item. It removes the torrent in qBittorrent
  without its files, then recycles the files, so qBittorrent never sees files vanish under a torrent it lists.
- **Not offered** for these items:
  - protected folders;
  - SABnzbd's incomplete folder;
  - anything an arr app is waiting to import;
  - a running SABnzbd job;
  - a torrent whose content holds more than the item;
  - items whose client could not be read.
- **Right before acting**, Scratcharr scans again and re-applies those rules.
- **Restore** moves an item back unless something new sits at its old path.
- **After `RecycleDays`**, items are deleted for good. Delete and Empty now do it sooner.
- **Action log:** every change, and every refused one, goes into it.

### Auto clean-up and the low space alert (Settings > Auto clean-up)

- **Auto rules** recycle items on their own, once an hour, exactly as the Recycle button would. There is one rule
  per verdict (imported leftover, seeding done, failed, half-finished, stray), each with its own number of days.
  Every rule starts **off**. An item's age counts from its last change on disk, never from before Scratcharr first
  saw it. Manual and protected items are never touched, a round takes at most 25 items, and every move goes through
  the recycle folder with "Auto rule: ..." in the action log. The tab shows what is due now, with a Run now button.
  Nothing runs while `AllowChanges` is off.
- **Low space alert** (off by default): an ntfy message when less than the share you set (10% by default) of the
  downloads pool is free, listing the 5 biggest items to review. It goes out once, then at most once a day while
  the space stays low; it re-arms when there is enough space again. It never removes anything.

The **Settings check** tab also has a **Folder structure** card: the folders the download clients, the watched
folders and the protected folders expect under `/downloads`, which ones are missing, and a Create missing button.

Once a week (Monday 9:00 by default) a summary goes to ntfy. It covers:
- what needs a look, by verdict;
- what is new this week;
- what nothing will remove;
- the recycle folder, and how many items the auto rules recycled;
- free space.

## Roadmap

1. v0.1 Find (read-only)
2. v0.2 Unpack: `.rar`/`.7z`/`.zip` sets into `/downloads/discs/<name>/` for Ripparr, the torrent keeps seeding
3. v0.3 From USB: browse `/mnt/pc/usbxx`, copy into `/downloads/import` or `/downloads/discs`, checked after
4. v0.4 Clean up: recycle to `/downloads/.recycle` (7 days), remove through qBittorrent and SABnzbd, weekly ntfy summary — this version

## Development

```bash
git submodule update --init
dotnet test
dotnet run --project src/Scratcharr      # http://localhost:8188, dev/ folders from appsettings.Development.json
```

Tools are pinned as `ARG` lines in the Dockerfile; the weekly *Tool updates* workflow opens a pull request
when a newer one exists (needs Settings > Actions > "Allow GitHub Actions to create and approve pull requests").

GPLv3, like Ripparr and Muxarr whose layout it reuses.
