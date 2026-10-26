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

Everything this fork changes compared to the official PlayerSync. Items marked *(testing)* are only in testing builds; the rest are also in stable 1.15.5.1209. Per-build notes are on the [releases page](https://github.com/VierAI/MareSynchronosClient/releases). Also kept in [CHANGELOG.md](CHANGELOG.md).
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
- Usage window: transfer totals, per-minute graph and monthly totals *(testing)*
- Auto sync nearby animations (off by default) in Settings > Interface > Animations *(testing)*
- Pair Analysis Sound column; click the Noises speaker icon to mute or unmute *(testing)*
- Right-click menu: Override auto pause for players paused by the VRAM, triangle or height threshold *(testing)*
- Settings > Debug: 'Allow local debug tools to read pair info' (off by default) *(testing)*

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

### Other

- Fork-only settings live in their own vierai-config.json (migrated automatically once)
- New plugin icon and AI-generated code disclosure

## Testing builds since stable 1.15.5.1209

### 1.15.5.1216
- Download priority: the party tier now covers the whole alliance (normal and cross-world groups), yourself excluded
- Outfit changes that arrive while a redraw is still pending get their own queued redraw (fixes stale outfits and missing legs)
- Data for players not on your list is dropped quietly instead of logging an error
- A mass reapply of cached data is staggered over about 3 seconds
- Settings > Debug: new 'Allow local debug tools to read pair info' switch (off by default); the read-only PSync.GetPairStates IPC returns nothing unless it is on

### 1.15.5.1215
- Crash guard: a player's draw state is re-checked in the same tick right before mods are assigned (and again when their handler starts), so mods are not changed mid-draw
- Redraws for a player who is still being drawn are deferred, and redraws requested close together are merged into one

### 1.15.5.1214
- Download priority: new party members tier between in-game friends and direct pairs (five tiers in total)
- Right-click menu: Override auto pause for players paused by the VRAM, triangle or height threshold; shown only on auto-paused players, and the override is remembered across restarts
- Busy areas: finished downloads are kept when their batch is cancelled; players stuck not drawing back off instead of retrying constantly (draw-wait backoff); busy sound files are re-read instead of counting as failed (noise read retries); the compactor retries files that are in use
- Noises: a song tied to an emote fades when the emote ends, using the Noises fade time setting; a song started outside an emote ends when the player walks away
- Apply log shows the priority tier and download queue wait

### 1.15.5.1213
- Right-click menu restored to the default 1.15.5.1209 layout (the menu editor from 1.15.5.1211 is shelved)
- Usage: monthly totals bar graph (last 12 months) under the per-minute graph; older usage files are migrated automatically

### 1.15.5.1212
- Usage: per-minute line graph with download and upload; each transfer is counted once, when it finishes or is cancelled; per-player top list removed

### 1.15.5.1211
- Usage window: download and upload totals, graph and past sessions (Settings > Transfers, or the chart icon in the main window title bar)
- Settings > Interface > Animations tab
- Pair Analysis: the Sound column works like Noises (state, failed-load tooltip, click to mute or unmute, sortable)
- Syncshell (N) count setting moved to Interface; Texture Size Cap tab sits next to Auto Texture Compression

### 1.15.5.1210
- Noises: click the speaker icon to mute or unmute
- Noises: failed sound loads no longer count as active (the tooltip mentions them); short sounds stay active 20 s after the last load, songs for their length or while the loop runs; a player leaving clears their row at once
- Auto sync nearby animations (off by default, radius 0.1-3 m): restarts both players' looping emotes together; experimental intro replay sub-option
- Read-only debug IPC for local tools (PSync.GetPairStates); off by default since 1.15.5.1216
## For the PlayerSync team

If the original PlayerSync authors would like any of these changes sent back upstream as pull requests, please open an issue on this repository or contact the fork maintainer, and we'll gladly prepare them.
