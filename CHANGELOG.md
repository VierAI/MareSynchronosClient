# PSync changelog

The fork's own changes (VierAI) come first, split into features, testing items and fixes. The official PlayerSync team's changes are listed separately at the bottom.

## PSync (VierAI) changes

Everything in this section is VierAI's fork work, not part of the official PlayerSync. Items marked *(in testing)* were added after the first public release (1.15.5.1209); the version in brackets is the build that added them, and all of them are included in 1.15.5.1700.

**1.15.6.001** is based on the official PlayerSync 1.15.6.0 (API 40) and includes every VierAI change listed below.

### Features

- Noises window and Noises folder in the main window: see who is playing modded sounds, hover a row to target them, mute or pause from the list; detection counts only a player's own sounds; unmuting or Reapply Last Data restores their music, looping emote sounds and speaker icon
- Sound icon next to pairs (active or fading) with a speaker wave; optional speaker on name tags
- Download priority: five tiers (listed players, in-game friends, party and alliance members, direct pairs, everyone else), Priority settings tab, Prioritize in the Pair Data menu; nearer players first within a tier (whole alliance in the party tier *(in testing, 1216)*)
- Downloads window shows only active transfers with names; bars show Waiting when idle and keep a transfer's progress between file groups
- Texture size cap (off by default): shrinks oversized textures on tiny models, judged by the model's real size; eyes keep at least 2K
- Syncshells show how many members you can only reach through that shell, as a green (N)
- Noises: configurable fade time (10-300 s) and muted row hide delay (5-60 s); stable row order; buttons on hover; red icon for muted rows
- Right-click menu: Reapply Last Data moved to the top level and Send Pair Request moved into the Pair Data submenu (the Reapply and Send Pair Request actions themselves are upstream PlayerSync; VierAI only moved them)
- *(in testing, 1216)* Right-click menu: Override auto pause for players paused by the VRAM, triangle or height threshold
- *(in testing, 1216)* Settings > Debug: 'Allow local debug tools to read pair info' (off by default)
- *(in testing, 1217)* Players the game is not drawing show a "Not drawn" icon; their downloads wait unless they are listed, in-game friends or party/alliance members
- *(in testing, 1217)* Pair tooltip shows why a player is waiting (tier, queue position, downloading, not drawn, budget, waiting for data) and where their files came from
- *(in testing, 1217)* Visible pairs show their approximate VRAM and triangles inline (e.g. "312 MB, 450k tris"), yellow or red over your thresholds
- *(in testing, 1217)* Force re-download for one player (main window menu and right-click Pair Data menu)
- *(in testing, 1217)* Optional memory budget (off by default): manual MB cap, stricter cap in crowded areas, or Auto from live GPU memory (manual on Wine/Mac and integrated GPUs); holds the lowest-priority, farthest players first, both ways, and resumes them automatically
- *(in testing, 1217; UID-only 1218)* Blocklist: block a player (by UID) so nothing of theirs downloads or applies (separate from pause, kept across restarts)
- *(in testing, 1413; list layout 1417)* Pair request window: one line per request with avatar and name, accept, decline and block buttons inline, opens automatically when a request arrives
- *(in testing, 1417)* Pair request window: hover a request to focus that player, right-click to target them
- *(in testing, 1414)* Hovering a pair targets them only while hovered (builds on upstream PlayerSync's "target pairs on hover" option, which is the official team's work)
- *(in testing, 1417)* Player names in PSync chat warnings are clickable
- *(in testing, 1417)* Settings > Sync: count of blocked files, with a button to clear them
- *(in testing, 1424)* Help > Their Pair Data > Workload tab: how many pairs are online, visible, loaded, waiting, applying or paused, with total file size, estimated VRAM and triangles, and a sortable top list
- *(in testing, 1423)* Local debug tools (only with 'Allow local debug tools to read pair info' on) can read per-pair file size, VRAM and triangle estimates
- *(internal debug builds only, not in public releases)* Settings > Debug > "Dump all sync info" (or /psync dump): writes one local text file with every player and pair in view (permissions incl. sounds/animations/VFX off, noise state), the files received and applied per pair by type (game path -> file and source: PS cache, local mod, file swap or raw), what Penumbra has loaded on them now, recent sound (.scd, incl. VFX-bound under vfx/) and VFX/animation loads per object with a modded flag, and the sounds the game is playing right now matched to the nearest object. Read-only; mute behavior is unchanged

### Fixes

- Wine/macOS-safe file reads; file validation fails closed on read errors
- Warning when the cache folder is inside a cloud-synced folder
- Fixed an error in the height check when Glamourer data is missing
- Freeze mitigations: wait for draw again, skip redundant redraws, one apply per character at a time
- Customize+ and Heels reapply after a redraw once the skeleton is loaded (fixes half-loaded players)
- No more endless retry loop after Penumbra restarts; retries back off up to 60 s
- Duplicate applies are debounced
- *(in testing, 1210-1216)* Busy areas: finished downloads are kept when their batch is cancelled, players stuck not drawing back off, busy sound files are re-read, the compactor retries files in use
- *(in testing, 1210-1216)* Noises: a song tied to an emote fades when the emote ends; a song started outside an emote ends when the player walks away
- *(in testing, 1216)* Outfit changes that arrive while a redraw is still pending get their own redraw (fixes stale outfits and missing legs)
- *(in testing, 1216)* Data for players not on your list is ignored quietly; a mass reapply is spread over about 3 seconds
- *(in testing, 1225)* Crash guard: mods and redraws for a player wait until the game has finished drawing them
- *(in testing, 1414)* A player whose draw timed out is retried once the game has finished drawing them
- *(in testing, 1414)* Follow-up redraws and Customize+/Heels reapplies are capped
- *(in testing, 1417)* Model (.mdl) files are checked for a valid structure before use; bad files are remembered in the file cache database and not applied again
- *(in testing, 1417)* Crash-loop quarantine: files tied to repeated game crashes are held back, with a chat warning
- *(in testing, 1421; 25 files 1425)* Mac/Wine: download batches are split into groups of at most 25 files, with a short pause between groups
- *(in testing, 1426)* Mac/Wine: downloaded files are saved to the cache one at a time, with a short pause after each, to limit disk load
- *(in testing, 1701)* Blocked files: a file cache database restore from its backup or a database reset no longer brings back cleared blocked files, loses the blocked list, or falsely quarantines files from stale apply markers
- *(in testing, 1701)* Animation (.pap) check: the C198 SummonId warning no longer fires for the valid values 0 and 1 (upstream check was always true)
- *(in testing, 1702)* Pair request list: hovering or right-clicking a request now targets the player who sent it even when their pair entry points at another character or they were not hashed yet (falls back to the request's ident, then the character name)
- *(in testing, 1704)* Noises: sounds a pair's synced animations or VFX play under paths they did not sync (vanilla sounds, .scd under vfx/, raw not-redirected loads) now count as that pair's noise, not only their own synced .scd files
- *(in testing, 1704)* Mute / sounds disabled now also silences those sounds for that pair only (redirected in their own Penumbra collection to a path that does not exist); other players and your own sounds are untouched
- *(in testing, 1702)* Pair request list: Escape no longer hides it (it has no title bar, so it could not be found again); right-click a player who sent you a request and pick "View Pair Request" to open the list and target them, and the chat link now always opens it

### Performance

- *(in testing, 1705)* Mac/Linux (Wine): at most 2 downloads run at once for the whole plugin, and every batch spaces file starts 50 ms apart, to stop crashes during download bursts; Windows is unchanged
- *(in testing, 1706)* Noises window: on by default and fully visible (new Only show window on hover option in Interface > Game UI), gear button opens those settings, and names glow while a player plays a matching sound (configurable rules, defaults plap red and bpmloop green)
- *(in testing, 1706)* Texture size cap: smaller copies are swapped in right away (the pair is re-applied once they are made), so In use and VRAM saved show real numbers; Capped counts only textures with a real smaller copy
- *(in testing, 1706)* Fixed a KeyNotFoundException in the RSP height check for race and gender combinations without a height factor; the check is skipped for them

- *(in testing, 1701)* File cache database backup: skipped at startup when the last backup is under an hour old, and no longer holds up downloads and cache writes while it copies
- *(in testing, 1701)* Database rebuild: counts the files once instead of re-reading the whole folder tree after every batch
- Pair data is applied across several frames instead of one
- Lighter per-frame work for name plates, sound polling, the Noises list and the Penumbra callback
- Logins and plugin start hash at most 8 new players per frame
- Downloads use free slots in strict priority order
- *(in testing, 1419)* Pair data applies and redraws run two at a time instead of four

### Security

- Server file hashes and file paths are validated so files stay inside the cache folder
- *(in testing, 1217/1218)* Downloads are limited to game mod file types, and files that look like programs or archives are refused and logged

### Other

- Fork-only settings live in their own vierai-config.json (migrated automatically once)
- New plugin icon and AI-generated code disclosure
- *(in testing, 1414)* Missing files are no longer re-downloaded automatically (use Force re-download for one player instead)
## Upstream PlayerSync changes

Changes made by the official PlayerSync developers, included in this fork through merges from upstream. They are not VierAI's work; credit goes to the PlayerSync team. Summarized from the upstream history.

### 1.15.6.0 (November 2026)

- Server API version 40 (required to connect to the current PlayerSync servers)
- Build updated to Dalamud.NET.Sdk 15 and .NET 10; Stagehand 0.5.5
- Intro wording: the file cache is now FileCache.db


### 1.15.5.16 and 1.15.5.17 (November 2026)

Fixes:

- Animation (.pap) files are now validated before they are applied, like .avfx and .tmb files
- Database lifecycle and recovery reworked; database driver updated
- Fix for mod filtering
- Fix for the server connection not shutting down cleanly
- Turning off height auto-pause now unpauses the players it paused
- NUL characters are cleaned up during the CSV cache migration
- Startup waits correctly when PlayerSync and Penumbra are updated at the same time
- A draw-wait timeout while building character data is now a warning and no longer stops the build
- File upload checks (FilesSend) are retried up to 3 times on network errors

All of these are merged into PSync 1.15.5.1700.

### 1.15.5.15 (November 2026)

Fixes:

- A new upload no longer starts while an earlier one is still running; the earlier upload is cancelled first (merged into PSync 1.15.5.1500)

### 1.15.6 (experimental, October 2026)

- Data application moved further off the game thread: async refactors, memory cache preload (including Penumbra), a fast path for full data applications, fewer database calls
- Max data applications default changed from 0 to 10
- Redraws are tracked per player; the old redraw manager and census code were removed; pets and minions no longer wait for render
- Waits for the initial redraw before calling Glamourer; downloaded bytes are hashed before the file is saved
- Competitive alternates: atex support and other improvements
- Fixes for stale migrations, partial data updates during uploads and large uploads; unused help tabs disabled

### 1.15.5 (August to October 2026)

- Option to target pairs on hover in the main window (upstream PlayerSync; the VierAI hover-only targeting builds on it)
- Service gateways to work around Cloudflare connectivity issues, with connection checks and a token refresh notification
- Phase two UI: hub window, location chooser, subscription and find tabs, change notifications
- Client-side pause list; option to turn off name plate colors in dungeons and PvP
- Stagehand stages: all-world stages, a banner and disabled buttons when Stagehand is missing, owner list limited to owned or moderated shells, too-new file format check
- Syncshell admin changes; upload timeout
- Fixes for stuck downloads, forbidden transfers, loading cached pair data, MCDF loading, and cache paths with any FFXIV-readable characters

### Earlier (July 2026)

- AVFX expansion validation with unit tests; more reliable expansion checks
- Concurrent download handling, partial data updates, empty file checks
- Dalamud v4 compatibility; fixes for addon plugin data and mod filtering; hover targeting for multiple target types
