using Dalamud.Game.Config;
using Dalamud.Plugin.Services;
using System.Collections.Concurrent;

namespace MareSynchronos.Services;

/// <summary>
/// 1.15.6.202: the game sound mixer channel(s) a sound plays on, so Noises can hide sounds on channels muted in game.
/// </summary>
[Flags]
public enum SoundChannels : byte
{
    None = 0,
    Bgm = 1,
    Se = 2,
    Voice = 4,
    System = 8,
    Ambient = 16,
    Performance = 32,
    /// <summary>A bus number we have not mapped: never hidden (except by Master).</summary>
    Unknown = 128,
}

/// <summary>
/// Maps an SCD bus number or a game path to a mixer channel.
///
/// Evidence (2026-11-14, 10,309 vanilla SCDs read from the game data, bus = byte 1 of each SCD sound entry):
/// - bus 1 / 16 / 8: music/ (2,173 files) -> BGM; bus 9: sound/zingle (jingle) -> BGM.
/// - bus 2: all sound/vfx/*, sound/battle/* (~8,800 files) -> Sound Effects; bus 6: sound/foot/*, bus 6/7: sound/vfx/lbk -> Sound Effects.
/// - bus 3: sound/voice/vo_battle and sound/voice/vo_emote (128 files, 100%) -> Voice.
/// - bus 4: sound/system/se_ui.scd -> System Sounds.
/// - bus 12: sound/strm/gaya_* (ambient streams) -> Ambient Sounds.
/// - Performance: no vanilla sample found; its bus is not mapped (shown as unknown, never hidden).
/// The bus is what the file itself asks the game mixer for, so it is followed even when a mod's folder name suggests otherwise
/// (many sound mods ship bus 4 = System Sounds).
/// </summary>
public static class SoundChannelMap
{
    public static SoundChannels FromBus(byte bus) => bus switch
    {
        1 or 8 or 9 or 16 => SoundChannels.Bgm,
        2 or 6 or 7 => SoundChannels.Se,
        3 => SoundChannels.Voice,
        4 => SoundChannels.System,
        12 => SoundChannels.Ambient,
        _ => SoundChannels.Unknown,
    };

    /// <summary>Fallback for game files without a readable header (vanilla files): by path prefix, matching the bus evidence above.</summary>
    public static SoundChannels FromGamePath(string? gamePath)
    {
        if (string.IsNullOrEmpty(gamePath)) return SoundChannels.Unknown;
        var p = gamePath.Replace('\\', '/').ToLowerInvariant();
        if (p.StartsWith("music/", StringComparison.Ordinal) || p.StartsWith("sound/zingle/", StringComparison.Ordinal)) return SoundChannels.Bgm;
        if (p.StartsWith("sound/voice/", StringComparison.Ordinal)) return SoundChannels.Voice;
        if (p.StartsWith("sound/system/", StringComparison.Ordinal)) return SoundChannels.System;
        if (p.StartsWith("sound/strm/", StringComparison.Ordinal) || p.StartsWith("sound/env/", StringComparison.Ordinal)) return SoundChannels.Ambient;
        if (p.StartsWith("sound/vfx/", StringComparison.Ordinal) || p.StartsWith("sound/battle/", StringComparison.Ordinal)
            || p.StartsWith("sound/foot/", StringComparison.Ordinal)) return SoundChannels.Se;
        return SoundChannels.Unknown;
    }

    // 1.15.6.205: game path -> bus number with no mapped channel (from the file header), so the tag can say "bus 10"
    // instead of "?" and the number can be mapped once we know which game volume slider controls it.
    private static readonly ConcurrentDictionary<string, int> s_unmappedBus = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<int, byte> s_unmappedSeen = new();

    /// <summary>Remembers the unmapped bus of a sound. True the first time this bus number is seen this session (to log it once).</summary>
    public static bool NoteUnmappedBus(string gamePath, int bus)
    {
        if (bus < 0 || string.IsNullOrEmpty(gamePath)) return false;
        if (s_unmappedBus.Count > 4096) s_unmappedBus.Clear();
        s_unmappedBus[gamePath] = bus;
        return s_unmappedSeen.TryAdd(bus, 0);
    }

    /// <summary>Like <see cref="Label(SoundChannels)"/>, with "bus N" instead of "?" when the file named a bus we have not mapped.</summary>
    public static string Label(SoundChannels ch, string? gamePath)
    {
        var label = Label(ch);
        if (gamePath != null && ch.HasFlag(SoundChannels.Unknown) && s_unmappedBus.TryGetValue(gamePath, out var bus))
            label = label.Replace("?", "bus " + bus.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return label;
    }

    /// <summary>Short tag: "SE", "Voice", "SE+Voice", "?".</summary>
    public static string Label(SoundChannels ch)
    {
        if (ch == SoundChannels.None) return "?";
        var parts = new List<string>(3);
        if (ch.HasFlag(SoundChannels.Bgm)) parts.Add("BGM");
        if (ch.HasFlag(SoundChannels.Se)) parts.Add("SE");
        if (ch.HasFlag(SoundChannels.Voice)) parts.Add("Voice");
        if (ch.HasFlag(SoundChannels.System)) parts.Add("System");
        if (ch.HasFlag(SoundChannels.Ambient)) parts.Add("Ambient");
        if (ch.HasFlag(SoundChannels.Performance)) parts.Add("Perform");
        if (ch.HasFlag(SoundChannels.Unknown)) parts.Add("?");
        return string.Join('+', parts);
    }
}

/// <summary>
/// The game's sound settings (System Configuration > Sound): per channel mute checkbox (IsSnd*) and volume (Sound*).
/// Re-read when the game reports a system config change, and at least every <see cref="RefreshMs"/> ms as a fallback,
/// so un-muting a channel brings its sounds back in Noises right away.
/// </summary>
public static class SoundChannelState
{
    private const long RefreshMs = 500;
    private static IGameConfig? s_config;
    private static long s_lastRead = long.MinValue;
    private static volatile bool s_dirty = true;
    private static bool s_masterOff;
    private static SoundChannels s_off;

    public static void Attach(IGameConfig config)
    {
        Detach();
        s_config = config;
        config.SystemChanged += OnSystemChanged;
        s_dirty = true;
    }

    public static void Detach()
    {
        if (s_config != null) s_config.SystemChanged -= OnSystemChanged;
        s_config = null;
    }

    private static void OnSystemChanged(object? sender, ConfigChangeEvent e) => s_dirty = true;

    /// <summary>Master muted or at 0.</summary>
    public static bool MasterOff { get { Refresh(); return s_masterOff; } }

    /// <summary>Channels that are muted or at volume 0.</summary>
    public static SoundChannels OffChannels { get { Refresh(); return s_off; } }

    /// <summary>True when every channel the sound can play on is off (or Master is). Unknown/None is only off with Master.</summary>
    public static bool IsInaudible(SoundChannels ch)
    {
        Refresh();
        if (s_masterOff) return true;
        if (ch == SoundChannels.None || ch.HasFlag(SoundChannels.Unknown)) return false;
        return (ch & ~s_off) == SoundChannels.None;
    }

    private static void Refresh()
    {
        var cfg = s_config;
        if (cfg == null) return;
        var now = Environment.TickCount64;
        if (!s_dirty && now - s_lastRead < RefreshMs) return;
        s_dirty = false;
        s_lastRead = now;
        try
        {
            s_masterOff = Off(cfg, SystemConfigOption.IsSndMaster, SystemConfigOption.SoundMaster);
            var off = SoundChannels.None;
            if (Off(cfg, SystemConfigOption.IsSndBgm, SystemConfigOption.SoundBgm)) off |= SoundChannels.Bgm;
            if (Off(cfg, SystemConfigOption.IsSndSe, SystemConfigOption.SoundSe)) off |= SoundChannels.Se;
            if (Off(cfg, SystemConfigOption.IsSndVoice, SystemConfigOption.SoundVoice)) off |= SoundChannels.Voice;
            if (Off(cfg, SystemConfigOption.IsSndSystem, SystemConfigOption.SoundSystem)) off |= SoundChannels.System;
            if (Off(cfg, SystemConfigOption.IsSndEnv, SystemConfigOption.SoundEnv)) off |= SoundChannels.Ambient;
            if (Off(cfg, SystemConfigOption.IsSndPerform, SystemConfigOption.SoundPerform)) off |= SoundChannels.Performance;
            s_off = off;
        }
        catch
        {
            s_masterOff = false;
            s_off = SoundChannels.None;
        }
    }

    // IsSnd* = 1 is the "Mute" checkbox (seen in FFXIV.cfg: IsSndSe 1 / SoundSe 100 for a muted channel).
    private static bool Off(IGameConfig cfg, SystemConfigOption mute, SystemConfigOption volume)
    {
        if (cfg.TryGet(mute, out uint m) && m != 0) return true;
        return cfg.TryGet(volume, out uint v) && v == 0;
    }
}

/// <summary>Recent sound loads per pair with their channels (newest first), shared by the Noises window, folder and dumps.</summary>
public static class SoundChannelLog
{
    private const int MaxPerPair = 32;
    private static readonly ConcurrentDictionary<string, List<(string GamePath, DateTimeOffset At, SoundChannels Channels)>> s_recent = new(StringComparer.Ordinal);

    public static void Record(string uid, string gamePath, DateTimeOffset at, SoundChannels channels)
    {
        var list = s_recent.GetOrAdd(uid, _ => []);
        lock (list)
        {
            list.RemoveAll(e => string.Equals(e.GamePath, gamePath, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, (gamePath, at, channels));
            if (list.Count > MaxPerPair) list.RemoveRange(MaxPerPair, list.Count - MaxPerPair);
        }
    }

    public static SoundChannels? ChannelsOf(string uid, string gamePath)
    {
        if (!s_recent.TryGetValue(uid, out var list)) return null;
        lock (list)
        {
            foreach (var e in list) if (string.Equals(e.GamePath, gamePath, StringComparison.OrdinalIgnoreCase)) return e.Channels;
        }
        return null;
    }

    /// <summary>
    /// True when the pair's current noise is inaudible because of your game sound settings: every sound loaded in the last fade
    /// window (and always the newest one) is on a muted / zero-volume channel, or Master is off. No recorded sound: false (shown).
    /// </summary>
    public static bool AllRecentInaudible(string uid, DateTimeOffset now)
    {
        if (!s_recent.TryGetValue(uid, out var list)) return false;
        var window = SoundActivity.FadeSeconds;
        lock (list)
        {
            if (list.Count == 0) return false;
            for (var i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (i > 0 && (now - e.At).TotalSeconds > window) break;
                if (!SoundChannelState.IsInaudible(e.Channels)) return false;
            }
        }
        return true;
    }

    /// <summary>Noises option on and the pair's noise is on muted channels.</summary>
    public static bool HiddenByGameMute(string uid, DateTimeOffset now)
        => VierAIConfigStore.Current.NoisesHideGameMutedChannels && AllRecentInaudible(uid, now);
}
