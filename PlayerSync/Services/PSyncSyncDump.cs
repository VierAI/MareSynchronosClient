#if PSYNC_INTERNAL // 1.15.6.001: internal builds only (pass -p:PSyncInternal=true); public releases leave the dump out
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Sound;
using MareSynchronos.API.Data.Enum;
using MareSynchronos.API.Data.Extensions;
using MareSynchronos.Interop.Ipc;
using MareSynchronos.MareConfiguration;
using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.Services.Events;
using MareSynchronos.Services.Mediator;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Numerics;
using System.Text;
using DalamudObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace MareSynchronos.Services;

/// <summary>1.15.5.1703: write a "Dump all sync info" file (Settings > Debug button, or /psync dump).</summary>
public record PSyncDumpSyncInfoMessage : MessageBase;

/// <summary>
/// 1.15.5.1703 (build list PRIORITY "Ghost clapping plus a Dump all sync info button"): one click writes a timestamped text
/// file to the plugin config folder (dumps\PSyncDump-yyyyMMdd-HHmmss.txt) with the full sync, pair and sound state:
/// game sound settings, every player in the object table, every pair (permissions, noise state, files received and
/// applied grouped by type with game path -> resolved path and source, Penumbra's resource tree of what is loaded on the
/// object now), recent .scd (and .avfx/.tmb/.pap) loads per object with modded flag and source, and the sound instances
/// the game is playing right now (SoundManager active list), each matched to the nearest object.
/// Read-only: nothing is muted, stripped, redrawn or sent anywhere. The file stays on this PC.
/// </summary>
public sealed class PSyncSyncDump : DisposableMediatorSubscriberBase, IHostedService
{
    private static readonly string[] TypeOrder = [".scd", ".avfx", ".tmb", ".pap", ".mtrl", ".shpk", ".tex", ".mdl"];
    private const float LiveSoundMatchMeters = 3f;
    private const int MaxLiveSoundNodes = 1024;

    private readonly MareConfigService _configService;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly PairManager _pairManager;
    private readonly IpcManager _ipcManager;
    private readonly SoundActivityTracker _soundTracker;
    private readonly EventAggregator _events;
    private readonly IObjectTable _objectTable;
    private readonly IClientState _clientState;
    private readonly IGameConfig _gameConfig;
    private readonly IChatGui _chatGui;
    private int _running;

    /// <summary>Full path of the last dump written this session (null before the first one).</summary>
    public static string? LastDumpPath { get; private set; }
    public static string? LastDumpError { get; private set; }
    public static bool IsRunning { get; private set; }

    public PSyncSyncDump(ILogger<PSyncSyncDump> logger, MareMediator mediator, MareConfigService configService, DalamudUtilService dalamudUtil,
        PairManager pairManager, IpcManager ipcManager, SoundActivityTracker soundTracker, EventAggregator events,
        IObjectTable objectTable, IClientState clientState, IGameConfig gameConfig, IChatGui chatGui) : base(logger, mediator)
    {
        _configService = configService;
        _dalamudUtil = dalamudUtil;
        _pairManager = pairManager;
        _ipcManager = ipcManager;
        _soundTracker = soundTracker;
        _events = events;
        _objectTable = objectTable;
        _clientState = clientState;
        _gameConfig = gameConfig;
        _chatGui = chatGui;
        Mediator.Subscribe<PSyncDumpSyncInfoMessage>(this, (msg) => _ = DumpAsync());
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task DumpAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        IsRunning = true;
        try
        {
            var now = DateTime.Now;
            var text = await _dalamudUtil.RunOnFrameworkThread(() => Build(now)).ConfigureAwait(false);
            var dir = Path.Combine(_configService.ConfigurationDirectory, "dumps");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "PSyncDump-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false)).ConfigureAwait(false);
            LastDumpPath = path;
            LastDumpError = null;
            Logger.LogInformation("Sync info dump written: {path} ({len} chars)", path, text.Length);
            _ = _dalamudUtil.RunOnFrameworkThread(() => _chatGui.Print("[PlayerSync] Sync info dump written: " + path));
        }
        catch (Exception ex)
        {
            LastDumpError = ex.Message;
            Logger.LogWarning(ex, "Sync info dump failed");
            _ = _dalamudUtil.RunOnFrameworkThread(() => _chatGui.PrintError("[PlayerSync] Sync info dump failed: " + ex.Message));
        }
        finally
        {
            IsRunning = false;
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private sealed record ObjInfo(int Index, nint Address, string Name, DalamudObjectKind Kind, Vector3 Position, float Distance, uint HomeWorld);

    private string Build(DateTime localNow)
    {
        var sb = new StringBuilder(256 * 1024);
        var cacheDir = Normalize(_configService.Current.CacheFolder);
        var penumbraDir = Normalize(_ipcManager.Penumbra.ModDirectory);
        var local = _objectTable.LocalPlayer;
        var localPos = local?.Position ?? Vector3.Zero;

        // ---- object table snapshot ----
        var objects = new List<ObjInfo>();
        var byAddress = new Dictionary<nint, ObjInfo>();
        for (int i = 0; i < _objectTable.Length; i++)
        {
            var o = _objectTable[i];
            if (o == null || o.Address == nint.Zero) continue;
            uint world = o is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter pc ? pc.HomeWorld.RowId : 0;
            var info = new ObjInfo(i, o.Address, o.Name.TextValue, o.ObjectKind, o.Position, local == null ? -1 : Vector3.Distance(localPos, o.Position), world);
            objects.Add(info);
            byAddress.TryAdd(o.Address, info);
        }

        var liveSounds = ReadLiveSounds(out var liveNote);

        sb.AppendLine("PlayerSync sync info dump (VierAI fork " + typeof(PSyncSyncDump).Assembly.GetName().Version + ")");
        sb.AppendLine("Written: " + localNow.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) + " (local), " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC");
        sb.AppendLine("Territory: " + _clientState.TerritoryType + ", local player: " + (local?.Name.TextValue ?? "(none)") + " obj " + (local?.ObjectIndex.ToString(CultureInfo.InvariantCulture) ?? "-") + " at " + Fmt(localPos));
        sb.AppendLine("Wine: " + _dalamudUtil.IsWine + ", cache folder: " + _configService.Current.CacheFolder + ", Penumbra mod folder: " + (_ipcManager.Penumbra.ModDirectory ?? "(unknown)"));
        sb.AppendLine("Live sound instances: " + liveNote);
        sb.AppendLine("Recent loads: last " + PSyncResourceLoadLog.ScdCapacity + " .scd and " + PSyncResourceLoadLog.OtherCapacity + " .avfx/.tmb/.pap loads Penumbra attributed to a game object (since plugin start: "
            + PSyncResourceLoadLog.ScdTotal + " .scd, " + PSyncResourceLoadLog.OtherTotal + " other). Only loads that go through Penumbra's path resolution are seen;");
        sb.AppendLine("  a sound already looping makes no new load, so check the live sound list for those.");
        sb.AppendLine("Source labels: PS cache = PlayerSync cache file, local mod = your own Penumbra mods folder, file swap = redirected to another game path,");
        sb.AppendLine("  raw = not redirected (base game file or resolved outside Penumbra), other = redirected somewhere else.");
        sb.AppendLine("This dump is read-only: it does not mute, strip, redraw or send anything.");
        sb.AppendLine();

        AppendSoundSettings(sb);

        // ---- pairs ----
        var pairs = _pairManager.GetAllPairs();
        var visible = pairs.Where(p => p.IsVisible).OrderBy(p => p.PlayerName, StringComparer.OrdinalIgnoreCase).ToList();
        var pairByAddress = new Dictionary<nint, Pair>();
        foreach (var p in visible)
        {
            if (p.ContendId != 0 && _dalamudUtil.TryGetPlayerAddressAndIndexOnFrameworkByContentId(p.ContendId, out var addr, out _))
                pairByAddress.TryAdd(addr, p);
        }

        var scdLoads = PSyncResourceLoadLog.Snapshot(scd: true);
        var otherLoads = PSyncResourceLoadLog.Snapshot(scd: false);

        // ---- players in the object table ----
        sb.AppendLine("== Players in the object table (" + objects.Count(o => o.Kind == DalamudObjectKind.Pc) + ")");
        foreach (var o in objects.Where(o => o.Kind == DalamudObjectKind.Pc).OrderBy(o => o.Distance))
        {
            pairByAddress.TryGetValue(o.Address, out var pair);
            sb.Append("  [").Append(o.Index).Append("] ").Append(o.Name).Append(" (world ").Append(o.HomeWorld).Append(") dist ").Append(Dist(o.Distance)).Append(' ')
              .Append(CharState(o.Address));
            sb.Append(pair == null ? "  not a visible pair" : "  pair " + pair.UserData.AliasOrUID + PermShort(pair));
            sb.AppendLine();
        }
        sb.AppendLine();

        sb.AppendLine("== Visible pairs (" + visible.Count + ")");
        foreach (var pair in visible)
        {
            AppendPair(sb, pair, byAddress, scdLoads, otherLoads, cacheDir, penumbraDir, liveSounds);
        }

        sb.AppendLine("== Pairs not visible (" + (pairs.Count - visible.Count) + ")");
        foreach (var pair in pairs.Where(p => !p.IsVisible).OrderBy(p => p.UserData.AliasOrUID, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("  ").Append(pair.UserData.AliasOrUID).Append(" (").Append(pair.UserData.UID).Append(')');
            var note = pair.GetNote();
            if (!string.IsNullOrEmpty(note)) sb.Append(" note \"").Append(note).Append('"');
            if (!string.IsNullOrEmpty(pair.LastKnownPlayerName)) sb.Append(" last name ").Append(pair.LastKnownPlayerName);
            sb.Append(" online ").Append(pair.IsOnline).Append(PermShort(pair)).AppendLine();
        }
        sb.AppendLine();

        // ---- recent loads by object (every object, pair or not) ----
        sb.AppendLine("== Recent .scd loads by object (oldest first, all objects)");
        AppendLoadsByObject(sb, scdLoads, byAddress, pairByAddress, cacheDir, penumbraDir);
        sb.AppendLine();
        sb.AppendLine("== Recent .avfx/.tmb/.pap loads by object (oldest first, all objects)");
        AppendLoadsByObject(sb, otherLoads, byAddress, pairByAddress, cacheDir, penumbraDir);
        sb.AppendLine();

        // ---- live sounds ----
        sb.AppendLine("== Sounds playing now (game SoundManager active list): " + liveNote);
        foreach (var s in liveSounds)
        {
            var near = Nearest(objects, s.Position, s.Positional);
            sb.Append("  ").Append(s.File).Append("  cat ").Append(s.Category).Append(" vol ").Append(s.Volume.ToString("0.00", CultureInfo.InvariantCulture))
              .Append(s.Positional ? " positional at " + Fmt(s.Position) : " non-positional").Append(s.LocalPlayer ? " [local player]" : string.Empty)
              .Append(s.Active ? string.Empty : " [inactive flag]").Append(s.Loading ? " [loading]" : string.Empty);
            if (near != null)
            {
                pairByAddress.TryGetValue(near.Address, out var np);
                sb.Append("  nearest [").Append(near.Index).Append("] ").Append(near.Name).Append(' ').Append(near.Kind)
                  .Append(" (").Append(Vector3.Distance(near.Position, s.Position).ToString("0.0", CultureInfo.InvariantCulture)).Append(" m from sound, ")
                  .Append(Dist(near.Distance)).Append(" from you)").Append(np == null ? " not a pair" : " pair " + np.UserData.AliasOrUID + PermShort(np));
            }
            else if (s.Positional)
            {
                sb.Append("  no object within ").Append(LiveSoundMatchMeters.ToString(CultureInfo.InvariantCulture)).Append(" m (orphaned or world sound)");
            }
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine("== End of dump");
        return sb.ToString();
    }

    private void AppendSoundSettings(StringBuilder sb)
    {
        sb.AppendLine("== Game sound settings (System config)");
        string[] uints = ["SoundMaster", "SoundBgm", "SoundSe", "SoundVoice", "SoundEnv", "SoundSystem", "SoundPerform", "SoundPlayer", "SoundParty", "SoundOther"];
        string[] bools = ["IsSndMaster", "IsSndBgm", "IsSndSe", "IsSndVoice", "IsSndEnv", "IsSndSystem", "IsSndPerform"];
        sb.Append("  volume:");
        foreach (var n in uints)
        {
            try { sb.Append(' ').Append(n).Append('=').Append(_gameConfig.System.TryGet(n, out uint v) ? v.ToString(CultureInfo.InvariantCulture) : "?"); }
            catch { sb.Append(' ').Append(n).Append("=?"); }
        }
        sb.AppendLine();
        sb.Append("  muted (IsSnd* true = muted):");
        foreach (var n in bools)
        {
            try { sb.Append(' ').Append(n).Append('=').Append(_gameConfig.System.TryGet(n, out bool b) ? b.ToString() : "?"); }
            catch { sb.Append(' ').Append(n).Append("=?"); }
        }
        sb.AppendLine();
        try
        {
            unsafe
            {
                var sm = SoundManager.Instance();
                if (sm != null)
                {
                    sb.Append("  SoundManager: disabled ").Append(sm->Disabled).Append(", master ").Append(sm->MasterVolume.ToString("0.00", CultureInfo.InvariantCulture))
                      .Append(", active ").Append(sm->ActiveVolume.ToString("0.00", CultureInfo.InvariantCulture))
                      .Append(", plays when window inactive ").Append(sm->PlaySoundsWhenWindowInactive).Append(", window inactive ").Append(sm->WindowInactive).AppendLine();
                }
            }
        }
        catch (Exception ex) { sb.AppendLine("  SoundManager: not readable (" + ex.Message + ")"); }
        sb.AppendLine();
    }

    private void AppendPair(StringBuilder sb, Pair pair, Dictionary<nint, ObjInfo> byAddress, PSyncResourceLoadLog.Entry[] scdLoads,
        PSyncResourceLoadLog.Entry[] otherLoads, string? cacheDir, string? penumbraDir, List<LiveSound> liveSounds)
    {
        var now = DateTimeOffset.UtcNow;
        ObjInfo? obj = null;
        if (pair.ContendId != 0 && _dalamudUtil.TryGetPlayerAddressAndIndexOnFrameworkByContentId(pair.ContendId, out var addr, out _))
            byAddress.TryGetValue(addr, out obj);

        sb.Append("-- ").Append(pair.PlayerName).Append("  ").Append(pair.UserData.AliasOrUID).Append(" (").Append(pair.UserData.UID).Append(')');
        var note = pair.GetNote();
        if (!string.IsNullOrEmpty(note)) sb.Append(" note \"").Append(note).Append('"');
        sb.AppendLine();
        sb.Append("   object: ").AppendLine(obj == null ? "(not found in object table)" : "[" + obj.Index + "] dist " + Dist(obj.Distance) + " " + CharState(obj.Address));
        var own = pair.UserPair.OwnPermissions;
        var other = pair.UserPair.OtherPermissions;
        sb.Append("   pair: status ").Append(pair.IndividualPairStatus).Append(", syncshells ").Append(pair.UserPair.Groups.Count)
          .Append(", online ").Append(pair.IsOnline).Append(", paused ").Append(pair.IsPaused);
        if (pair.IsPaused) sb.Append(" (").Append(pair.GetPauseReason()).Append(')');
        sb.Append(", model ready ").Append(pair.IsModelReady).Append(", applying ").Append(pair.HasDataApplicationTaskRunning)
          .Append(", last apply ").AppendLine(pair.LastDataApplicationTime > DateTimeOffset.MinValue ? pair.LastDataApplicationTime.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " local" : "never");
        sb.Append("   your permissions: ").Append(PermFlags(own)).Append("; theirs: ").Append(PermFlags(other))
          .Append("; effective: sounds ").Append(own.IsDisableSounds() || other.IsDisableSounds() ? "OFF" : "on")
          .Append(", animations ").Append(own.IsDisableAnimations() || other.IsDisableAnimations() ? "OFF" : "on")
          .Append(", VFX ").AppendLine(own.IsDisableVFX() || other.IsDisableVFX() ? "OFF" : "on");
        try
        {
            var ev = _events.GetLatestDataEventByUid(pair.UserData.UID);
            if (ev != null) sb.Append("   latest status: ").Append(ev.ApplicationStatus).Append(" - ").AppendLine(ev.Message?.ToString());
        }
        catch { /* ignore */ }
        sb.Append("   noise: state ").Append(pair.SoundState).Append(" since ").Append(pair.SoundStateSince?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "-")
          .Append(", listed ").Append(SoundActivity.IsListed(pair, now)).Append(", in loop ").Append(pair.SoundInLoop).Append(", loop has sound ").Append(pair.SoundLoopHasSound)
          .Append(", noisy at mute ").Append(pair.SoundNoisyAtMute).Append(", end reason ").Append(pair.SoundEndReason ?? "-")
          .Append(", last load ").Append(pair.SoundLastLoadAt?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "-")
          .Append(" (").Append(pair.SoundLastLoadSeconds.ToString("0.0", CultureInfo.InvariantCulture)).Append(" s, looped ").Append(pair.SoundLastLoadLooped).Append(')').AppendLine();
        if (!string.IsNullOrEmpty(pair.LastLoadedSoundSinceRedraw)) sb.Append("   last loaded sound since redraw: ").AppendLine(pair.LastLoadedSoundSinceRedraw);
        var ownScd = pair.SoundOwnScdPaths;
        var refScd = pair.PSyncReferencedScdPaths;
        sb.Append("   .scd paths referenced by their applied .tmb/.pap/.avfx (counted as theirs since 1704): ")
          .AppendLine(refScd == null ? "(not scanned yet)" : refScd.Count == 0 ? "(none)" : string.Join(", ", refScd.OrderBy(s => s, StringComparer.OrdinalIgnoreCase)));
        var muteRedirects = pair.PSyncMuteRedirectPaths;
        sb.Append("   mute redirects active (-> ").Append(PSyncForeignSounds.MuteTarget).Append("): ").AppendLine(muteRedirects.Count == 0 ? "(none)" : string.Join(", ", muteRedirects));
        sb.Append("   own synced .scd game paths (always counted by Noises): ").AppendLine(ownScd == null || ownScd.Count == 0 ? "(none)" : string.Join(", ", ownScd.OrderBy(s => s, StringComparer.OrdinalIgnoreCase)));
        try
        {
            var recent = _soundTracker.GetRecentSounds(pair.UserData.UID);
            if (recent.Count > 0) sb.Append("   recent noises: ").AppendLine(string.Join(", ", recent.Select(r => r.GamePath + " @" + r.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture))));
        }
        catch { /* ignore */ }
        if (pair.SoundFailedPaths.Count > 0) sb.Append("   failed sound paths: ").AppendLine(string.Join(", ", pair.SoundFailedPaths.Select(k => k.Key + " x" + k.Value.Count)));

        // files received vs applied
        var applied = pair.PSyncLastAppliedModPaths;
        var swaps = pair.PSyncLastAppliedSwapPaths;
        sb.Append("   files PlayerSync applied (last mod apply ").Append(pair.PSyncLastAppliedModPathsAt?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "never").Append(", ")
          .Append(applied?.Count ?? 0).AppendLine(" game paths), by type, from the received data:");
        var data = pair.GetCopyOfPairCharacterData();
        if (data == null)
        {
            sb.AppendLine("     (no data received)");
        }
        else
        {
            var rows = new List<(string Type, string Line)>();
            foreach (var kind in data.FileReplacements)
            {
                foreach (var fr in kind.Value)
                {
                    foreach (var gp in fr.GamePaths)
                    {
                        string target;
                        if (applied != null && applied.TryGetValue(gp, out var resolved))
                        {
                            var isSwap = swaps != null && swaps.Contains(gp);
                            target = resolved + "  [" + (isSwap ? "file swap" : Source(gp, resolved, cacheDir, penumbraDir)) + "]";
                        }
                        else
                        {
                            target = (string.IsNullOrEmpty(fr.FileSwapPath) ? "hash " + fr.Hash : "swap " + fr.FileSwapPath) + "  [NOT APPLIED: filtered by permissions, blocked, missing or not applied yet]";
                        }
                        rows.Add((TypeOf(gp), "     " + kind.Key + " " + gp + " -> " + target));
                    }
                }
            }
            AppendGrouped(sb, rows);
        }
        var missing = pair.MissingFiles;
        if (missing.Count > 0) sb.Append("   missing files: ").AppendLine(string.Join(", ", missing.Select(m => m.Key + " -> " + m.Value)));

        // what Penumbra says is loaded on the object now
        if (obj != null)
        {
            sb.AppendLine("   loaded on the object now (Penumbra resource tree), by type:");
            try
            {
                var tree = _ipcManager.Penumbra.GetCharacterDataOnFramework((ushort)obj.Index);
                if (tree == null) sb.AppendLine("     (Penumbra not available)");
                else
                {
                    var rows = new List<(string Type, string Line)>();
                    foreach (var kv in tree)
                    {
                        foreach (var gp in kv.Value)
                            rows.Add((TypeOf(gp), "     " + gp + " -> " + kv.Key + "  [" + Source(gp, kv.Key, cacheDir, penumbraDir) + "]"));
                    }
                    AppendGrouped(sb, rows);
                }
            }
            catch (Exception ex) { sb.AppendLine("     (resource tree failed: " + ex.Message + ")"); }

            AppendObjectLoads(sb, "   recent .scd loads on this object:", scdLoads, obj.Address, cacheDir, penumbraDir);
            AppendObjectLoads(sb, "   recent .avfx/.tmb/.pap loads on this object:", otherLoads, obj.Address, cacheDir, penumbraDir);

            var mine = liveSounds.Where(s => s.Positional && Vector3.Distance(s.Position, obj.Position) <= LiveSoundMatchMeters).ToList();
            sb.Append("   sounds playing within ").Append(LiveSoundMatchMeters.ToString(CultureInfo.InvariantCulture)).Append(" m of this object now: ").AppendLine(mine.Count == 0 ? "(none)" : string.Empty);
            foreach (var s in mine)
                sb.Append("     ").Append(s.File).Append(" (").Append(Vector3.Distance(s.Position, obj.Position).ToString("0.0", CultureInfo.InvariantCulture)).Append(" m, cat ").Append(s.Category).AppendLine(")");
        }
        sb.AppendLine();
    }

    private static void AppendGrouped(StringBuilder sb, List<(string Type, string Line)> rows)
    {
        if (rows.Count == 0) { sb.AppendLine("     (none)"); return; }
        foreach (var group in rows.GroupBy(r => r.Type, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => { var i = Array.IndexOf(TypeOrder, g.Key); return i < 0 ? 100 : i; }).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("    ").Append(group.Key).Append(" (").Append(group.Count()).AppendLine(")");
            foreach (var r in group.OrderBy(r => r.Line, StringComparer.OrdinalIgnoreCase)) sb.AppendLine(r.Line);
        }
    }

    private static void AppendObjectLoads(StringBuilder sb, string title, PSyncResourceLoadLog.Entry[] loads, nint address, string? cacheDir, string? penumbraDir)
    {
        var mine = loads.Where(l => l.GameObject == address).ToList();
        sb.Append(title).AppendLine(mine.Count == 0 ? " (none)" : " " + mine.Count);
        foreach (var l in mine.TakeLast(200)) sb.AppendLine("     " + LoadLine(l, cacheDir, penumbraDir));
    }

    private static void AppendLoadsByObject(StringBuilder sb, PSyncResourceLoadLog.Entry[] loads, Dictionary<nint, ObjInfo> byAddress, Dictionary<nint, Pair> pairByAddress,
        string? cacheDir, string? penumbraDir)
    {
        if (loads.Length == 0) { sb.AppendLine("  (none recorded)"); return; }
        foreach (var group in loads.GroupBy(l => l.GameObject).OrderByDescending(g => g.Max(l => l.UtcTicks)))
        {
            string who;
            if (group.Key == nint.Zero) who = "(no game object)";
            else if (byAddress.TryGetValue(group.Key, out var o))
                who = "[" + o.Index + "] " + o.Name + " " + o.Kind + " dist " + Dist(o.Distance) + (pairByAddress.TryGetValue(group.Key, out var p) ? " pair " + p.UserData.AliasOrUID + PermShort(p) : " not a visible pair");
            else who = "(object no longer in the table, 0x" + group.Key.ToString("X", CultureInfo.InvariantCulture) + ")";
            var list = group.ToList();
            sb.Append("  ").Append(who).Append(": ").Append(list.Count).Append(" loads, ")
              .Append(list.Count(l => !IsSame(l.GamePath, l.ResolvedPath))).Append(" modded, ")
              .Append(list.Count(l => l.GamePath.StartsWith("vfx/", StringComparison.OrdinalIgnoreCase))).AppendLine(" under vfx/");
            foreach (var l in list.TakeLast(100)) sb.AppendLine("     " + LoadLine(l, cacheDir, penumbraDir));
        }
    }

    private static string LoadLine(PSyncResourceLoadLog.Entry l, string? cacheDir, string? penumbraDir)
    {
        var at = new DateTime(l.UtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var modded = !IsSame(l.GamePath, l.ResolvedPath);
        return at + " " + l.GamePath + (modded ? " -> " + l.ResolvedPath : string.Empty) + "  modded=" + (modded ? "true" : "false")
            + " [" + Source(l.GamePath, l.ResolvedPath, cacheDir, penumbraDir) + "]"
            + (l.GamePath.StartsWith("vfx/", StringComparison.OrdinalIgnoreCase) ? " [vfx path]" : string.Empty) + (l.OnFramework ? string.Empty : " [off-thread]");
    }

    // ---- live sound instances ----
    private sealed record LiveSound(string File, Vector3 Position, bool Positional, float Volume, string Category, bool LocalPlayer, bool Active, bool Loading);

    private static unsafe List<LiveSound> ReadLiveSounds(out string note)
    {
        var list = new List<LiveSound>();
        try
        {
            var sm = SoundManager.Instance();
            if (sm == null) { note = "not available (SoundManager not found)"; return list; }
            var seen = new HashSet<nint>();
            var node = sm->ActiveSoundDataListHead;
            int count = 0;
            while (node != null && count < MaxLiveSoundNodes && seen.Add((nint)node))
            {
                count++;
                string file = "(no resource)";
                var handle = node->SoundResourceHandle;
                if (handle != null)
                {
                    var len = handle->ResourceHandle.FileName.Length;
                    file = len is > 0 and < 1024 ? handle->ResourceHandle.FileName.ToString() : "(unreadable name)";
                }
                list.Add(new LiveSound(file, new Vector3(node->PosX, node->PosY, node->PosZ), node->IsPositional, node->Volume,
                    node->VolumeCategory.ToString(), node->IsLocalPlayer, node->ISoundData.IsActive, node->IsLoadingSoundResource));
                node = (SoundData*)node->ISoundData.Next;
            }
            note = "included, " + list.Count + " instance(s) (read from the game's active sound list without its lock, as a one-off snapshot; "
                + "the owner is the nearest object within " + LiveSoundMatchMeters.ToString(CultureInfo.InvariantCulture) + " m of the sound's position, the game stores no owner)"
                + (count >= MaxLiveSoundNodes ? "; stopped at " + MaxLiveSoundNodes + " entries" : string.Empty);
        }
        catch (Exception ex)
        {
            note = "not available (" + ex.Message + ")";
        }
        return list;
    }

    private static ObjInfo? Nearest(List<ObjInfo> objects, Vector3 pos, bool positional)
    {
        if (!positional) return null;
        ObjInfo? best = null;
        float bestDist = LiveSoundMatchMeters;
        foreach (var o in objects)
        {
            var d = Vector3.Distance(o.Position, pos);
            if (d <= bestDist) { best = o; bestDist = d; }
        }
        return best;
    }

    // ---- helpers ----
    private static unsafe string CharState(nint address)
    {
        if (address == nint.Zero) return string.Empty;
        var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)address;
        return "mode " + chara->Mode + " param " + chara->ModeParam + " emote " + chara->EmoteController.EmoteId;
    }

    private static string PermFlags(UserPermissions p)
        => "paused " + p.IsPaused() + ", sounds off " + p.IsDisableSounds() + ", animations off " + p.IsDisableAnimations() + ", VFX off " + p.IsDisableVFX() + ", sticky " + p.IsSticky();

    private static string PermShort(Pair pair)
    {
        var own = pair.UserPair.OwnPermissions;
        var other = pair.UserPair.OtherPermissions;
        var flags = new List<string>();
        if (pair.IsPaused) flags.Add("paused");
        if (own.IsDisableSounds()) flags.Add("you muted sounds");
        if (other.IsDisableSounds()) flags.Add("they disabled sounds");
        if (own.IsDisableAnimations() || other.IsDisableAnimations()) flags.Add("animations off");
        if (own.IsDisableVFX() || other.IsDisableVFX()) flags.Add("VFX off");
        return flags.Count == 0 ? string.Empty : " (" + string.Join(", ", flags) + ")";
    }

    private static string TypeOf(string gamePath)
    {
        var ext = Path.GetExtension(gamePath);
        return string.IsNullOrEmpty(ext) ? "(no ext)" : ext.ToLowerInvariant();
    }

    private static string? Normalize(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : path.Replace('/', '\\').TrimEnd('\\') + "\\";

    private static string StripPenumbraPrefix(string path)
    {
        // Penumbra may prefix resolved paths with |...| (cache-busting); not part of the file path.
        if (path.Length > 1 && path[0] == '|')
        {
            var end = path.IndexOf('|', 1);
            if (end > 0) return path[(end + 1)..];
        }
        return path;
    }

    private static bool IsSame(string gamePath, string resolved)
        => string.IsNullOrEmpty(resolved) || string.Equals(gamePath, StripPenumbraPrefix(resolved), StringComparison.OrdinalIgnoreCase);

    private static string Source(string gamePath, string resolved, string? cacheDir, string? penumbraDir)
    {
        if (IsSame(gamePath, resolved)) return "raw";
        var r = StripPenumbraPrefix(resolved).Replace('/', '\\');
        if (cacheDir != null && r.StartsWith(cacheDir, StringComparison.OrdinalIgnoreCase)) return "PS cache";
        if (penumbraDir != null && r.StartsWith(penumbraDir, StringComparison.OrdinalIgnoreCase)) return "local mod";
        if (!r.Contains(':', StringComparison.Ordinal) && !r.StartsWith('\\')) return "file swap";
        return "other";
    }

    private static string Dist(float d) => d < 0 ? "?" : d.ToString("0.0", CultureInfo.InvariantCulture) + " m";

    private static string Fmt(Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"({v.X:0.0}, {v.Y:0.0}, {v.Z:0.0})");
}

#endif
