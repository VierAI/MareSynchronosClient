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

Everything this fork changes compared to the official PlayerSync. Items marked *(testing)* are only in testing builds; the rest are also in stable 1.15.5.1209. Per-build notes are on the [releases page](https://github.com/VierAI/MareSynchronosClient/releases). To install this fork, add the custom repo URL `https://raw.githubusercontent.com/VierAI/DalamudPlugins/main/vierai.json` in Dalamud (listing source: [VierAI/DalamudPlugins](https://github.com/VierAI/DalamudPlugins)). Fork maintained by VierAI. Also kept in [CHANGELOG.md](CHANGELOG.md).
### Features

- Noises window and Noises folder in the main window: see who is playing modded sounds, hover a row to target them, mute or pause from the list
- Sound icon next to pairs (active or fading) with a speaker wave; optional speaker on name tags
- Looping emote sounds restart after unmute or Reapply Last Data
- Download priority: five tiers (listed players, in-game friends, party and alliance members, direct pairs, everyone else), Priority settings tab, Prioritize in the Pair Data menu (party and alliance tier *(testing)*)
- Downloads window shows only active transfers with names; bars show Waiting when idle
- Texture size cap (off by default): shrinks oversized textures on tiny models, judged by the model's real size; eyes keep at least 2K
- Syncshells show how many members you can only reach through that shell, as a green (N)
- Noises: configurable fade time (10-300 s) and muted row hide delay (5-60 s); stable row order; buttons on hover; red icon for muted rows
- Unmuting a player restores their music and speaker icon
- Right-click menu: Reapply Last Data at the top level, Send Pair Request in the Pair Data submenu
- Right-click menu: Override auto pause for players paused by the VRAM, triangle or height threshold *(testing)*
- Settings > Debug: 'Allow local debug tools to read pair info' (off by default) *(testing)*
- Players the game is not drawing show a "Not drawn" icon; their downloads wait unless they are listed, in-game friends or party/alliance members *(testing)*
- Pair tooltip shows why a player is waiting (tier, queue position, downloading, not drawn, budget, waiting for data) and where their files came from *(testing)*
- Visible pairs show their approximate VRAM and triangles inline (e.g. "312 MB · 450k tris"), yellow or red over your thresholds *(testing)*
- Force re-download for one player (main window menu and right-click Pair Data menu); missing files are re-downloaded automatically (at most 3 times per 10 minutes per player) *(testing)*
- Optional memory budget (off by default): manual MB cap, stricter cap in crowded areas, or Auto from live GPU memory (manual on Wine/Mac and integrated GPUs); holds the lowest-priority, farthest players first, both ways, and resumes them automatically *(testing)*
- Blocklist: block a player (by UID) so nothing of theirs downloads or applies (separate from pause, kept across restarts) *(testing)*

### Fixes

- Wine/macOS-safe file reads; file validation fails closed on read errors
- Warning when the cache folder is inside a cloud-synced folder
- Fixed an error in the height check when Glamourer data is missing
- Freeze mitigations: wait for draw again, skip redundant redraws, one apply per character at a time
- Customize+ and Heels reapply after a redraw once the skeleton is loaded (fixes half-loaded players)
- Noise detection counts only a player's own sounds; any looping sound counts as active
- No more endless retry loop after Penumbra restarts; retries back off up to 60 s
- Duplicate applies are debounced; download progress no longer flashes Waiting between file groups
- Crash guard: mods and redraws for a player wait until the game has finished drawing them *(testing)*
- Outfit changes that arrive while a redraw is still pending get their own redraw (fixes stale outfits and missing legs) *(testing)*
- Data for players not on your list is ignored quietly; a mass reapply is spread over about 3 seconds *(testing)*
- Busy areas: finished downloads are kept when their batch is cancelled, players stuck not drawing back off, busy sound files are re-read, the compactor retries files in use *(testing)*
- Noises: a song tied to an emote fades when the emote ends; a song started outside an emote ends when the player walks away *(testing)*

### Performance

- Pair data is applied across several frames instead of one
- Lighter per-frame work for name plates, sound polling, the Noises list and the Penumbra callback
- Logins and plugin start hash at most 8 new players per frame
- Downloads use free slots in strict priority order

### Security

- Server file hashes and file paths are validated so files stay inside the cache folder
- Downloads are limited to game mod file types, and files that look like programs or archives are refused and logged *(testing)*

### Other

- Fork-only settings live in their own vierai-config.json (migrated automatically once)
- New plugin icon and AI-generated code disclosure

## For the PlayerSync team

If the original PlayerSync authors would like any of these changes sent back upstream as pull requests, please open an issue on this repository or contact the fork maintainer, and we'll gladly prepare them.
