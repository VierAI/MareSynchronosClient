# PlayerSync (VierAI-fied)

> **Disclaimer:** this is an unofficial fork of PlayerSync. Its changes are AI-generated ("vibe coded"). It is not made, endorsed or supported by the PlayerSync team, so please don't ask them for help with it. Use it at your own risk. The plugin's internal name (`MareSempiterne`), config and server API are unchanged.

> **!!! YOU MUST UNINSTALL THE OFFICIAL PLAYERSYNC RELEASE FIRST !!!** Running both at once is not supported.

## Official PlayerSync

Available at [This dalamud Repo](https://playersync.io/download/plugin/repo.json)

# [PlayerSync Discord](https://discord.gg/playersync)

Original source: [universalconquistador/MareSynchronosClient](https://github.com/universalconquistador/MareSynchronosClient)

A fork of the late great Mare Synchronos that synchronizes your FFXIV mods with other players.

This fork is not compatible with the final version of the classic Mare API, we have added extensions and changes to the API.

## Changes from official PlayerSync

Per-build notes are on the [releases page](https://github.com/VierAI/MareSynchronosClient/releases). To install this fork, add the custom repo URL `https://raw.githubusercontent.com/VierAI/DalamudPlugins/main/vierai.json` in Dalamud (listing source: [VierAI/DalamudPlugins](https://github.com/VierAI/DalamudPlugins)). Fork maintained by VierAI. Also kept in [CHANGELOG.md](CHANGELOG.md). This list covers 1.15.5.1416. The fork's own changes come first; the official PlayerSync team's changes are listed separately below them.

### PSync (VierAI) changes

Everything in this section is VierAI's fork work, not part of the official PlayerSync. This list covers 1.15.5.1416. Items marked *(in testing)* were added after the first public release (1.15.5.1209); the version in brackets is the build that added them, and all of them are included in 1.15.5.1416.

#### Features

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
- *(in testing, 1413)* Pair request window in the Noises style: transparent until hovered, shows avatar and name; hovering shows the ID and icon buttons
- *(in testing, 1414)* Hovering a pair targets them only while hovered (builds on upstream PlayerSync's "target pairs on hover" option, which is the official team's work)

#### Fixes

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

#### Performance

- Pair data is applied across several frames instead of one
- Lighter per-frame work for name plates, sound polling, the Noises list and the Penumbra callback
- Logins and plugin start hash at most 8 new players per frame
- Downloads use free slots in strict priority order

#### Security

- Server file hashes and file paths are validated so files stay inside the cache folder
- *(in testing, 1217/1218)* Downloads are limited to game mod file types, and files that look like programs or archives are refused and logged

#### Other

- Fork-only settings live in their own vierai-config.json (migrated automatically once)
- New plugin icon and AI-generated code disclosure
- *(in testing, 1414)* Missing files are no longer re-downloaded automatically (use Force re-download for one player instead)
### Upstream PlayerSync changes

Changes made by the official PlayerSync developers, included in this fork through merges from upstream. They are not VierAI's work; credit goes to the PlayerSync team. Summarized from the upstream history.

#### 1.15.6 (experimental, October 2026)

- Data application moved further off the game thread: async refactors, memory cache preload (including Penumbra), a fast path for full data applications, fewer database calls
- Max data applications default changed from 0 to 10
- Redraws are tracked per player; the old redraw manager and census code were removed; pets and minions no longer wait for render
- Waits for the initial redraw before calling Glamourer; downloaded bytes are hashed before the file is saved
- Competitive alternates: atex support and other improvements
- Fixes for stale migrations, partial data updates during uploads and large uploads; unused help tabs disabled

#### 1.15.5 (August to October 2026)

- Option to target pairs on hover in the main window (upstream PlayerSync; the VierAI hover-only targeting builds on it)
- Service gateways to work around Cloudflare connectivity issues, with connection checks and a token refresh notification
- Phase two UI: hub window, location chooser, subscription and find tabs, change notifications
- Client-side pause list; option to turn off name plate colors in dungeons and PvP
- Stagehand stages: all-world stages, a banner and disabled buttons when Stagehand is missing, owner list limited to owned or moderated shells, too-new file format check
- Syncshell admin changes; upload timeout
- Fixes for stuck downloads, forbidden transfers, loading cached pair data, MCDF loading, and cache paths with any FFXIV-readable characters

#### Earlier (July 2026)

- AVFX expansion validation with unit tests; more reliable expansion checks
- Concurrent download handling, partial data updates, empty file checks
- Dalamud v4 compatibility; fixes for addon plugin data and mod filtering; hover targeting for multiple target types

## For the PlayerSync team

If the original PlayerSync authors would like any of these changes sent back upstream as pull requests, please open an issue on this repository or contact the fork maintainer, and we'll gladly prepare them.
