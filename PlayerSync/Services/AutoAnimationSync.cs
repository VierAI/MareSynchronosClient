using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Lumina.Excel.Sheets;
using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.Services.Mediator;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Numerics;
using GameCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace MareSynchronos.Services;

/// <summary>
/// Fork (1.15.5.1210, VierAI 2026-10-25): "Auto sync nearby animations". When you and a visible synced pair within the radius
/// (VierAIConfig.AutoSyncNearbyRadius, 0.1-3 m) are both in a looping emote/pose, both animations are restarted together:
/// - when either of you starts (or switches to) a looping emote while the other is already in one;
/// - when either of you is redrawn (data reapply etc.), once the new draw object has settled.
/// The restart is the SimpleHeels "/heels emotesync" approach: set the Havok animation control's LocalTime to 0 on the human
/// skeleton, in place, with no redraw and nothing sent to the server. Only on your screen; the other player sees it synced only
/// if they run this with the option on too. A per-pair cooldown stops the triggers from setting each other off.
///
/// Experimental sub-option (VierAIConfig.AutoSyncIntroReplay, off by default): if an emote has an intro stage (Emote sheet
/// ActionTimeline slot 1, the "_start" timeline; slot 0 is the loop, checked against the current sheet 2026-10-25), that
/// character replays intro + loop with Timeline.PlayActionTimeline instead, so both go through the intro together.
/// Untested in game: the game may snap back to the loop or the local replay may disagree with its emote state.
/// </summary>
public sealed class AutoAnimationSync : DisposableMediatorSubscriberBase, IHostedService
{
    private const long TickMs = 100;
    private const long StartDelayMs = 200;   // let the game set up the new emote's timeline first
    private const long RedrawDelayMs = 700;  // let the new draw object finish loading
    private const long CooldownMs = 4000;

    private readonly DalamudUtilService _dalamudUtil;
    private readonly PairManager _pairManager;
    private readonly IDataManager _gameData;
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly List<string> _remove = new();
    private readonly Dictionary<ushort, (ushort Loop, ushort Intro)> _emoteTimelines = new();
    private State _self = new();
    private long _nextTick;

    private sealed class State
    {
        public bool InLoop;
        public int Key;
        public nint DrawObject;
        public long PendingAt;
        public long LastSyncTick = long.MinValue / 2;
        public bool Known;
    }

    public AutoAnimationSync(ILogger<AutoAnimationSync> logger, MareMediator mediator, DalamudUtilService dalamudUtil,
        PairManager pairManager, IDataManager gameData) : base(logger, mediator)
    {
        _dalamudUtil = dalamudUtil;
        _pairManager = pairManager;
        _gameData = gameData;
        Mediator.Subscribe<FrameworkUpdateMessage>(this, _ => Tick());
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public static float Radius => Math.Clamp(VierAIConfigStore.Current.AutoSyncNearbyRadius, 0.1f, 3f);

    private unsafe void Tick()
    {
        var cfg = VierAIConfigStore.Current;
        if (!cfg.AutoSyncNearbyAnimations)
        {
            if (_states.Count > 0) _states.Clear();
            _self = new();
            return;
        }

        var now = Environment.TickCount64;
        if (now < _nextTick) return;
        _nextTick = now + TickMs;

        try
        {
            var selfPtr = _dalamudUtil.GetPlayerPtr();
            if (selfPtr == nint.Zero) return;
            var self = (GameCharacter*)selfPtr;
            Read(self, out var selfLoop, out var selfKey, out var selfDraw);
            var selfStarted = _self.Known && selfLoop && (!_self.InLoop || selfKey != _self.Key);
            var selfRedrawn = _self.Known && selfDraw != nint.Zero && _self.DrawObject != nint.Zero && selfDraw != _self.DrawObject;
            _self.InLoop = selfLoop; _self.Key = selfKey; _self.DrawObject = selfDraw; _self.Known = true;

            var radius = Radius;
            Vector3 selfPos = self->GameObject.Position;
            _seen.Clear();
            foreach (var pair in _pairManager.GetVisiblePairs())
            {
                var addr = pair.Address;
                if (addr == nint.Zero || addr == selfPtr || pair.IsPaused || !_dalamudUtil.IsLiveObject(addr)) continue;
                var uid = pair.UserData.UID;
                _seen.Add(uid);
                var chara = (GameCharacter*)addr;
                Read(chara, out var loop, out var key, out var draw);

                if (!_states.TryGetValue(uid, out var st)) _states[uid] = st = new State();
                var started = st.Known && loop && (!st.InLoop || key != st.Key);
                var redrawn = st.Known && draw != nint.Zero && st.DrawObject != nint.Zero && draw != st.DrawObject;
                st.InLoop = loop; st.Key = key; st.DrawObject = draw; st.Known = true;

                Vector3 pos = chara->GameObject.Position;
                if (!selfLoop || !loop || Vector3.Distance(selfPos, pos) > radius)
                {
                    st.PendingAt = 0;
                    continue;
                }

                if (selfRedrawn || redrawn) st.PendingAt = Math.Max(st.PendingAt, now + RedrawDelayMs);
                else if ((selfStarted || started) && st.PendingAt == 0) st.PendingAt = now + StartDelayMs;

                if (st.PendingAt == 0 || now < st.PendingAt) continue;
                st.PendingAt = 0;
                if (now - st.LastSyncTick < CooldownMs) continue;
                st.LastSyncTick = now;
                Sync(self, chara, cfg.AutoSyncIntroReplay);
            }

            if (_states.Count > _seen.Count)
            {
                _remove.Clear();
                foreach (var k in _states.Keys) if (!_seen.Contains(k)) _remove.Add(k);
                foreach (var k in _remove) _states.Remove(k);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Auto animation sync failed; skipping this tick");
        }
    }

    /// <summary>Looping emote/pose (not walking, combat, mounts or performance) with a current emote id.</summary>
    private static unsafe void Read(GameCharacter* chara, out bool inLoop, out int key, out nint drawObject)
    {
        var mode = chara->Mode;
        var emoteId = chara->EmoteController.EmoteId;
        inLoop = (mode is CharacterModes.EmoteLoop or CharacterModes.InPositionLoop) && emoteId != 0;
        key = inLoop ? ((int)mode << 16) | emoteId : 0;
        drawObject = (nint)chara->GameObject.DrawObject;
    }

    private unsafe void Sync(GameCharacter* self, GameCharacter* other, bool introReplay)
    {
        if (introReplay)
        {
            var a = Intro(self->EmoteController.EmoteId);
            var b = Intro(other->EmoteController.EmoteId);
            if (a.Intro != 0 || b.Intro != 0)
            {
                Restart(self, a);
                Restart(other, b);
                Logger.LogDebug("Auto animation sync: intro replay (object {idx})", other->GameObject.ObjectIndex);
                return;
            }
        }
        ResetLocalTime(self);
        ResetLocalTime(other);
        Logger.LogDebug("Auto animation sync: restarted both animations (object {idx})", other->GameObject.ObjectIndex);
    }

    private static unsafe void Restart(GameCharacter* chara, (ushort Loop, ushort Intro) t)
    {
        if (t.Intro != 0) chara->Timeline.PlayActionTimeline(t.Intro, t.Loop);
        else ResetLocalTime(chara);
    }

    /// <summary>Emote sheet slots 0 (loop) and 1 (intro, "_start"). Intro is 0 unless it is a distinct "emote/" timeline
    /// (chair/ground sits use event_base intros, which are left alone).</summary>
    private (ushort Loop, ushort Intro) Intro(ushort emoteId)
    {
        if (emoteId == 0) return (0, 0);
        if (_emoteTimelines.TryGetValue(emoteId, out var cached)) return cached;
        (ushort, ushort) result = (0, 0);
        try
        {
            var sheet = _gameData.GetExcelSheet<Emote>();
            if (sheet.TryGetRow(emoteId, out var row) && row.ActionTimeline.Count > 1)
            {
                var loop = row.ActionTimeline[0];
                var intro = row.ActionTimeline[1];
                var introKey = intro.RowId != 0 && intro.IsValid ? intro.Value.Key.ToString() : string.Empty;
                if (loop.RowId != 0 && intro.RowId != 0 && intro.RowId != loop.RowId && introKey.StartsWith("emote/", StringComparison.Ordinal))
                    result = ((ushort)loop.RowId, (ushort)intro.RowId);
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Emote sheet lookup failed for {id}", emoteId);
        }
        _emoteTimelines[emoteId] = result;
        return result;
    }

    /// <summary>SimpleHeels DoEmoteSync: LocalTime = 0 on the first animation control of the first partial skeleton.</summary>
    private static unsafe void ResetLocalTime(GameCharacter* chara)
    {
        var drawObject = chara->GameObject.DrawObject;
        if (drawObject == null || drawObject->Object.GetObjectType() != ObjectType.CharacterBase) return;
        var cb = (CharacterBase*)drawObject;
        if (cb->GetModelType() != CharacterBase.ModelType.Human) return;
        var skeleton = cb->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletonCount < 1) return;
        var animated = skeleton->PartialSkeletons[0].GetHavokAnimatedSkeleton(0);
        if (animated == null || animated->AnimationControls.Length < 1) return;
        var control = animated->AnimationControls[0].Value;
        if (control == null) return;
        control->hkaAnimationControl.LocalTime = 0f;
    }
}
