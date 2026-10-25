# PSync changelog

Cumulative list of changes from upstream PlayerSync. Updated as changes land; 1.15.5.1209 includes everything below.

## Features

- Noises window and Noises folder in the main window: see who is playing modded sounds, hover a row to target them, mute or pause from the list
- Sound icon next to pairs (active or fading) with a speaker wave; optional speaker on name tags
- Looping emote sounds restart after unmute or Reapply Last Data
- Download priority: four tiers (listed players, in-game friends, direct pairs, everyone else), Priority settings tab, Prioritize in the Pair Data menu
- Downloads window shows only active transfers with names; bars show Waiting when idle
- Texture size cap (off by default): shrinks oversized textures on tiny models, judged by the model's real size; eyes keep at least 2K
- Syncshells show how many members you can only reach through that shell, as a green (N)
- Noises: configurable fade time (10-300 s) and muted row hide delay (5-60 s); stable row order; buttons on hover; red icon for muted rows
- Unmuting a player restores their music and speaker icon
- Right-click menu: Reapply Last Data at the top level, Send Pair Request in the Pair Data submenu

## Fixes

- Wine/macOS-safe file reads; file validation fails closed on read errors
- Warning when the cache folder is inside a cloud-synced folder
- Fixed an error in the height check when Glamourer data is missing
- Freeze mitigations: wait for draw again, skip redundant redraws, one apply per character at a time
- Customize+ and Heels reapply after a redraw once the skeleton is loaded (fixes half-loaded players)
- Noise detection counts only a player's own sounds; any looping sound counts as active
- No more endless retry loop after Penumbra restarts; retries back off up to 60 s
- Duplicate applies are debounced; download progress no longer flashes Waiting between file groups

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
