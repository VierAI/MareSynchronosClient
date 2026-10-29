# PSync changelog

Cumulative list of changes from upstream PlayerSync. Updated as changes land; the newest testing build includes everything below.

## Features

- Noises window and Noises folder in the main window: see who is playing modded sounds, hover a row to target them, mute or pause from the list
- Sound icon next to pairs (active or fading) with a speaker wave; optional speaker on name tags
- Looping emote sounds restart after unmute or Reapply Last Data
- Download priority: five tiers (listed players, in-game friends, party and alliance members, direct pairs, everyone else), Priority settings tab, Prioritize in the Pair Data menu
- Downloads window shows only active transfers with names; bars show Waiting when idle
- Texture size cap (off by default): shrinks oversized textures on tiny models, judged by the model's real size; eyes keep at least 2K
- Syncshells show how many members you can only reach through that shell, as a green (N)
- Noises: configurable fade time (10-300 s) and muted row hide delay (5-60 s); stable row order; buttons on hover; red icon for muted rows
- Unmuting a player restores their music and speaker icon
- Right-click menu: Override auto pause for players paused by the VRAM, triangle or height threshold
- Settings > Debug: 'Allow local debug tools to read pair info' (off by default); the PSync.GetPairStates IPC returns nothing unless it is on

## Fixes

- Wine/macOS-safe file reads; file validation fails closed on read errors
- Warning when the cache folder is inside a cloud-synced folder
- Fixed an error in the height check when Glamourer data is missing
- Freeze mitigations: wait for draw again, skip redundant redraws, one apply per character at a time
- Customize+ and Heels reapply after a redraw once the skeleton is loaded (fixes half-loaded players)
- Noise detection counts only a player's own sounds; any looping sound counts as active
- No more endless retry loop after Penumbra restarts; retries back off up to 60 s
- Crash guard: mods and redraws for a player wait until the game has finished drawing them, instead of changing them mid-draw
- Outfit changes that arrive while a redraw is still pending get their own redraw (fixes stale outfits and missing legs)
- Data for players not on your list is ignored quietly instead of logging an error; a mass reapply is spread over about 3 seconds
- Duplicate applies are debounced; download progress no longer flashes Waiting between file groups
- Busy areas: finished downloads are kept when their batch is cancelled, players stuck not drawing back off instead of retrying constantly, busy sound files are re-read instead of counting as failed, the compactor retries files in use
- Noises: a song tied to an emote fades when the emote ends; a song started outside an emote ends when the player walks away

## Performance

- Pair data is applied across several frames instead of one
- Lighter per-frame work for name plates, sound polling, the Noises list and the Penumbra callback
- Logins and plugin start hash at most 8 new players per frame
- Downloads use free slots in strict priority order

## Security

- Server file hashes and file paths are validated so files stay inside the cache folder

## Other

- Fork-only settings live in their own vierai-config.json (migrated automatically once)
- New plugin icon and AI-generated code disclosure
