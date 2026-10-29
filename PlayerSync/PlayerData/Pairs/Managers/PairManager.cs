using MareSynchronos.API.Data;
using MareSynchronos.API.Data.Comparer;
using MareSynchronos.API.Data.Extensions;
using MareSynchronos.API.Dto.Group;
using MareSynchronos.API.Dto.User;
using MareSynchronos.MareConfiguration;
using MareSynchronos.MareConfiguration.Models;
using MareSynchronos.PlayerData.Factories;
using MareSynchronos.Services;
using MareSynchronos.Services.Mediator;
using MareSynchronos.Services.ServerConfiguration;
using MareSynchronos.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using static MareSynchronos.Services.DalamudUtilService;

namespace MareSynchronos.PlayerData.Pairs;

public sealed class PairManager : DisposableMediatorSubscriberBase
{
    private readonly TimeSpan PausedPairCheckInterval = TimeSpan.FromMinutes(1);

    private readonly MareConfigService _configurationService;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly PairFactory _pairFactory;
    private readonly DalamudUtilService _dalamudUtilService;

    private readonly ConcurrentDictionary<UserData, Pair> _allClientPairs = new(UserDataComparer.Instance);
    private readonly ConcurrentDictionary<GroupData, GroupFullInfoDto> _allGroups = new(GroupDataComparer.Instance);
    private readonly ConcurrentDictionary<string, Pair> _identToUserPairs = new();
    private Lazy<List<Pair>> _directPairsInternal;
    private Lazy<Dictionary<GroupFullInfoDto, List<Pair>>> _groupPairsInternal;
    private Lazy<Dictionary<Pair, List<GroupFullInfoDto>>> _pairsWithGroupsInternal;
    private readonly CancellationTokenSource? _unpausePairCts;

    private Task? _unpausePairTask;
    private readonly ConcurrentDictionary<string, byte> _initializingPairs = new(StringComparer.Ordinal);
    private bool _isConnected = false;
    private bool _isZoning = false;

    public PairManager(ILogger<PairManager> logger, PairFactory pairFactory, MareConfigService configurationService, DalamudUtilService dalamudUtilService,
        ServerConfigurationManager serverConfigurationManager, MareMediator mediator)
        : base(logger, mediator)
    {
        _pairFactory = pairFactory;
        _configurationService = configurationService;
        _serverConfigurationManager = serverConfigurationManager;
        _dalamudUtilService = dalamudUtilService;

        _dalamudUtilService.FrameworkCharactersChanged += OnFrameworkCharactersChanged;

        Mediator.Subscribe<ConnectedMessage>(this, (__) =>
        {
            _isConnected = true;
            _ = UpdateVisiblePairs();
        });

        Mediator.Subscribe<ZoneSwitchEndMessage>(this, (__) =>
        {
            _isZoning = false;
            _ = UpdateVisiblePairs();
        });

        Mediator.Subscribe<DisconnectedMessage>(this, (_) =>
        {
            _isConnected = false;
            ClearPairs();
        });

        Mediator.Subscribe<ZoneSwitchStartMessage>(this, (_) => _isZoning = true);
        Mediator.Subscribe<CutsceneEndMessage>(this, (_) => ReapplyPairData());
        Mediator.Subscribe<ChangeFilterMessage>(this, (_) => ReapplyPairData());

        _directPairsInternal = DirectPairsLazy();
        _groupPairsInternal = GroupPairsLazy();
        _pairsWithGroupsInternal = PairsWithGroupsLazy();

        _unpausePairCts?.Dispose();
        _unpausePairCts = new CancellationTokenSource();
        _unpausePairTask = PausedPairExpiredTimerCheckTask(_unpausePairCts.Token);

        Logger.LogTrace("{class} created.", nameof(PairManager));
    }

    public List<Pair> DirectPairs => _directPairsInternal.Value;
    public Dictionary<GroupFullInfoDto, List<Pair>> GroupPairs => _groupPairsInternal.Value;
    public Dictionary<GroupData, GroupFullInfoDto> Groups => _allGroups.ToDictionary(k => k.Key, k => k.Value);
    public Pair? LastAddedUser { get; internal set; }
    public Dictionary<Pair, List<GroupFullInfoDto>> PairsWithGroups => _pairsWithGroupsInternal.Value;
    public bool DeferRecreate { get; set; } = true;

    public void AddGroup(GroupFullInfoDto dto)
    {
        _allGroups[dto.Group] = dto;
        RecreateLazy();
    }

    public void AddGroupPair(GroupPairFullInfoDto dto)
    {
        if (!_allClientPairs.ContainsKey(dto.User))
            _allClientPairs[dto.User] = _pairFactory.Create(new UserFullPairDto(dto.User, API.Data.Enum.IndividualPairStatus.None,
                [dto.Group.GID], dto.SelfToOtherPermissions, dto.OtherToSelfPermissions));
        else _allClientPairs[dto.User].UserPair.Groups.Add(dto.GID);
        RecreateLazy();
    }

    public Pair? GetPairByUID(string uid)
    {
        var existingPair = _allClientPairs.FirstOrDefault(f => f.Key.UID == uid);
        if (!Equals(existingPair, default(KeyValuePair<UserData, Pair>)))
        {
            return existingPair.Value;
        }

        return null;
    }

    public Pair? GetPairByAliasOrUID(string aliasOrUid)
    {
        var existingPair = _allClientPairs.FirstOrDefault(f => string.Equals(f.Key.UID, aliasOrUid, StringComparison.OrdinalIgnoreCase)
            || string.Equals(f.Key.Alias, aliasOrUid, StringComparison.OrdinalIgnoreCase));

        if (!Equals(existingPair, default(KeyValuePair<UserData, Pair>)))
        {
            return existingPair.Value;
        }

        return null;
    }

    public Pair? GetPairByCID(string cid)
    {
        var existingPair = _allClientPairs.FirstOrDefault(f => f.Value.HashedCID == cid);
        if (!Equals(existingPair, default(KeyValuePair<UserData, Pair>)))
        {
            return existingPair.Value;
        }

        return null;
    }

    public bool TryGetPairByIdent(string ident, out Pair? pair)
    {
        return _identToUserPairs.TryGetValue(ident, out pair);
    }

    public bool TryGetPairByUserData(UserData userData, out Pair? pair)
    {
        return _allClientPairs.TryGetValue(userData, out pair);
    }

    public void AddUserPair(UserFullPairDto dto)
    {
        if (!_allClientPairs.ContainsKey(dto.User))
        {
            _allClientPairs[dto.User] = _pairFactory.Create(dto);
        }
        else
        {
            _allClientPairs[dto.User].UserPair.IndividualPairStatus = dto.IndividualPairStatus;
            _allClientPairs[dto.User].ReapplyCachedData();
        }

        RecreateLazy();
    }

    public void AddUserPair(UserPairDto dto, bool addToLastAddedUser = true)
    {
        if (!_allClientPairs.ContainsKey(dto.User))
        {
            _allClientPairs[dto.User] = _pairFactory.Create(dto);
        }
        else
        {
            addToLastAddedUser = false;
        }

        _allClientPairs[dto.User].UserPair.IndividualPairStatus = dto.IndividualPairStatus;
        _allClientPairs[dto.User].UserPair.OwnPermissions = dto.OwnPermissions;
        _allClientPairs[dto.User].UserPair.OtherPermissions = dto.OtherPermissions;
        if (addToLastAddedUser)
            LastAddedUser = _allClientPairs[dto.User];
        _allClientPairs[dto.User].ReapplyCachedData();
        RecreateLazy();
    }

    public void ClearPairs()
    {
        Logger.LogDebug("Clearing all Pairs");

        DisposePairs(); // this must come first as it references _allClientPairs
        _allClientPairs.Clear();
        _allGroups.Clear();
        _identToUserPairs.Clear();
        _initializingPairs.Clear();
        RecreateLazy();
    }

    public List<Pair> GetOnlineUserPairs() => _allClientPairs.Where(p => !string.IsNullOrEmpty(p.Value.GetPlayerNameHash())).Select(p => p.Value).ToList();

    public List<Pair> GetAllPairs() => [.. _allClientPairs.Values];

    public int GetVisibleUserCount() => _allClientPairs.Count(p => p.Value.IsVisible);

    public List<UserData> GetVisibleUsers() => [.. _allClientPairs.Where(p => p.Value.IsVisible).Select(p => p.Key)];

    // Fork perf: GetVisiblePairs is called from several per-frame paths (name plates, Noises, menus). On the framework thread
    // the scan over every pair runs once per frame; each caller still gets its own list. Off-thread callers always scan.
    private Pair[] _visiblePairsFrameCache = [];
    private long _visiblePairsFrameCacheFrame = -1;

    public List<Pair> GetVisiblePairs()
    {
        if (!_dalamudUtilService.IsOnFrameworkThread)
        {
            return [.. _allClientPairs.Where(p => p.Value.IsVisible).Select(p => p.Value)];
        }

        var frame = _dalamudUtilService.FrameworkUpdateCount;
        if (_visiblePairsFrameCacheFrame != frame)
        {
            _visiblePairsFrameCache = [.. _allClientPairs.Where(p => p.Value.IsVisible).Select(p => p.Value)];
            _visiblePairsFrameCacheFrame = frame;
        }

        return [.. _visiblePairsFrameCache];
    }

    private void InvalidateVisiblePairsFrameCache() => _visiblePairsFrameCacheFrame = -1;

    public void MarkPairOffline(UserData user)
    {
        if (_allClientPairs.TryGetValue(user, out var pair))
        {
            Mediator.Publish(new ClearProfileDataMessage(pair.UserData));
            ResetCachedPlayerInitialization(pair);
            pair.MarkOffline();
        }

        RecreateLazy();
    }

    public void MarkPairOnline(OnlineUserIdentDto dto, bool sendNotif = true)
    {
        if (!_allClientPairs.ContainsKey(dto.User)) throw new InvalidOperationException("No user found for " + dto);

        var pair = _allClientPairs[dto.User];
        if (pair.HasCachedPlayer) // we already have a PairHandler assigned
        {
            RecreateLazy();
            return;
        }

        if (sendNotif && _configurationService.Current.ShowOnlineNotifications
            && (_configurationService.Current.ShowOnlineNotificationsOnlyForIndividualPairs && pair.IsDirectlyPaired && !pair.IsOneSidedPair
            || !_configurationService.Current.ShowOnlineNotificationsOnlyForIndividualPairs)
            && (_configurationService.Current.ShowOnlineNotificationsOnlyForNamedPairs && !string.IsNullOrEmpty(pair.GetNote())
            || !_configurationService.Current.ShowOnlineNotificationsOnlyForNamedPairs))
        {
            string? note = pair.GetNote();
            var msg = !string.IsNullOrEmpty(note) ? $"{note} ({pair.UserData.AliasOrUID}) is now online" : $"{pair.UserData.AliasOrUID} is now online";
            Mediator.Publish(new NotificationMessage("User online", msg, NotificationType.Info, TimeSpan.FromSeconds(5)));
        }

        bool wasAbleToSetPlayerDto = pair.SetOnlinePlayerDto(dto);
        if (!wasAbleToSetPlayerDto)
        {
            Logger.LogWarning("Failed to set OnlinePlayerDto for {pair}", pair.PairUIDName);
            ResetCachedPlayerInitialization(pair);
        }
        else
        {
            pair.IsOnline = true;

            _identToUserPairs[dto.Ident] = pair;
            _ = UpdateVisiblePairs(dto.Ident);
        }

        if (!DeferRecreate)
        {
            RecreateLazy();
        }
    }

    public void RemoveGroup(GroupData data)
    {
        _allGroups.TryRemove(data, out _);

        foreach (var item in _allClientPairs.ToList())
        {
            item.Value.UserPair.Groups.Remove(data.GID);

            if (!item.Value.HasAnyConnection())
            {
                item.Value.MarkOffline();
                ResetCachedPlayerInitialization(item.Value);
                _allClientPairs.TryRemove(item.Key, out _);
            }
        }

        RecreateLazy();
    }

    public void RemoveGroupPair(GroupPairDto dto)
    {
        if (_allClientPairs.TryGetValue(dto.User, out var pair))
        {
            pair.UserPair.Groups.Remove(dto.Group.GID);

            if (!pair.HasAnyConnection())
            {
                ResetCachedPlayerInitialization(pair);
                pair.MarkOffline();
                _allClientPairs.TryRemove(dto.User, out _);
            }
        }

        RecreateLazy();
    }

    public void RemoveUserPair(UserDto dto)
    {
        if (_allClientPairs.TryGetValue(dto.User, out var pair))
        {
            pair.UserPair.IndividualPairStatus = API.Data.Enum.IndividualPairStatus.None;

            if (!pair.HasAnyConnection())
            {
                ResetCachedPlayerInitialization(pair);
                pair.MarkOffline();
                _allClientPairs.TryRemove(dto.User, out _);
            }
        }

        RecreateLazy();
    }

    public void SetGroupInfo(GroupInfoDto dto)
    {
        _allGroups[dto.Group].Group = dto.Group;
        _allGroups[dto.Group].Owner = dto.Owner;
        _allGroups[dto.Group].GroupPermissions = dto.GroupPermissions;
        _allGroups[dto.Group].PublicData = dto.PublicData;

        Mediator.Publish(new GroupInfoChanged(dto));

        RecreateLazy();
    }

    public void UpdatePairPermissions(UserPermissionsDto dto)
    {
        if (!_allClientPairs.TryGetValue(dto.User, out var pair))
        {
            _serverConfigurationManager.RemovePauseReasonForUid(dto.User.UID);
            _serverConfigurationManager.RemovePendingPauseForUid(dto.User.UID);
            throw new InvalidOperationException("No such pair for " + dto);
        }

        if (pair.UserPair == null)
        {
            _serverConfigurationManager.RemovePauseReasonForUid(dto.User.UID);
            _serverConfigurationManager.RemovePendingPauseForUid(dto.User.UID);
            throw new InvalidOperationException("No direct pair for " + dto);
        }

        if (pair.UserPair.OtherPermissions.IsPaused() != dto.Permissions.IsPaused())
        {
            Mediator.Publish(new ClearProfileDataMessage(dto.User));
        }

        pair.UserPair.OtherPermissions = dto.Permissions;

        Logger.LogTrace("Paused: {paused}, Anims: {anims}, Sounds: {sounds}, VFX: {vfx}",
            pair.UserPair.OtherPermissions.IsPaused(),
            pair.UserPair.OtherPermissions.IsDisableAnimations(),
            pair.UserPair.OtherPermissions.IsDisableSounds(),
            pair.UserPair.OtherPermissions.IsDisableVFX());

        if (!pair.IsPaused)
        {
            _serverConfigurationManager.RemovePauseReasonForUid(pair.UserData.UID);
            _serverConfigurationManager.RemovePendingPauseForUid(pair.UserData.UID);
            pair.ReapplyCachedData();
        }

        RecreateLazy();
    }

    public void UpdateSelfPairPermissions(UserPermissionsDto dto)
    {
        if (!_allClientPairs.TryGetValue(dto.User, out var pair))
        {
            _serverConfigurationManager.RemovePauseReasonForUid(dto.User.UID);
            _serverConfigurationManager.RemovePendingPauseForUid(dto.User.UID);
            throw new InvalidOperationException("No such pair for " + dto);
        }

        if (pair.UserPair.OwnPermissions.IsPaused() != dto.Permissions.IsPaused())
        {
            Mediator.Publish(new ClearProfileDataMessage(dto.User));
        }

        // Fork: remember whether this change unmutes the pair, so the reapply below can restore their sound in full.
        var unmuted = pair.UserPair.OwnPermissions.IsDisableSounds() && !dto.Permissions.IsDisableSounds();
        pair.UserPair.OwnPermissions = dto.Permissions;

        Logger.LogTrace("Paused: {paused}, Anims: {anims}, Sounds: {sounds}, VFX: {vfx}",
            pair.UserPair.OwnPermissions.IsPaused(),
            pair.UserPair.OwnPermissions.IsDisableAnimations(),
            pair.UserPair.OwnPermissions.IsDisableSounds(),
            pair.UserPair.OwnPermissions.IsDisableVFX());

        if (!pair.IsPaused)
        {
            _serverConfigurationManager.RemovePauseReasonForUid(pair.UserData.UID);
            _serverConfigurationManager.RemovePendingPauseForUid(pair.UserData.UID);
            pair.ClearLastKnownIdentity(); // 1.15.5.1411: identity follows the (auto-)unpause
            UnmuteSoundRestore.Reapply(pair, unmuted);
        }

        RecreateLazy();
    }

    internal void ReceiveUploadStatus(UserDto dto)
    {
        if (_allClientPairs.TryGetValue(dto.User, out var existingPair) && existingPair.IsVisible)
        {
            existingPair.SetIsUploading();
        }
    }

    internal void SetGroupPairStatusInfo(GroupPairUserInfoDto dto)
    {
        _allGroups[dto.Group].GroupPairUserInfos[dto.UID] = dto.GroupUserInfo;
        RecreateLazy();
    }

    internal void SetGroupPermissions(GroupPermissionDto dto)
    {
        _allGroups[dto.Group].GroupPermissions = dto.Permissions;
        RecreateLazy();
    }

    internal void SetGroupStatusInfo(GroupPairUserInfoDto dto)
    {
        _allGroups[dto.Group].GroupUserInfo = dto.GroupUserInfo;
        RecreateLazy();
    }

    internal void UpdateGroupPairPermissions(GroupPairUserPermissionDto dto)
    {
        _allGroups[dto.Group].GroupUserPermissions = dto.GroupPairPermissions;
        RecreateLazy();
    }

    internal void UpdateIndividualPairStatus(UserIndividualPairStatusDto dto)
    {
        if (_allClientPairs.TryGetValue(dto.User, out var pair))
        {
            pair.UserPair.IndividualPairStatus = dto.IndividualPairStatus;
            RecreateLazy();
        }
    }

    public void ResetCachedPlayerInitialization(Pair pair)
    {
        _identToUserPairs.TryRemove(pair.HashedCID, out _);
    }

    protected override void Dispose(bool disposing)
    {
        _dalamudUtilService.FrameworkCharactersChanged -= OnFrameworkCharactersChanged;

        base.Dispose(disposing);

        _unpausePairCts?.CancelDispose();

        _isConnected = false;
        DisposePairs();
    }

    private void OnFrameworkCharactersChanged(IReadOnlyList<ObjectTablePlayersFrameworkUpdate> changes)
    {
        InvalidateVisiblePairsFrameCache();
        foreach (var change in changes)
        {
            UpdatePairVisibility(change);
        }
    }

    private Lazy<List<Pair>> DirectPairsLazy() => new(() => _allClientPairs.Select(k => k.Value)
        .Where(k => k.IndividualPairStatus != API.Data.Enum.IndividualPairStatus.None).ToList());

    private void DisposePairs()
    {
        Logger.LogDebug("Disposing all Pairs");
        Parallel.ForEach(_allClientPairs, item =>
        {
            ResetCachedPlayerInitialization(item.Value);
            item.Value.MarkOffline(wait: false);
        });

        RecreateLazy();
    }

    private Lazy<Dictionary<GroupFullInfoDto, List<Pair>>> GroupPairsLazy()
    {
        return new Lazy<Dictionary<GroupFullInfoDto, List<Pair>>>(() =>
        {
            Dictionary<GroupFullInfoDto, List<Pair>> outDict = [];
            foreach (var group in _allGroups)
            {
                outDict[group.Value] = _allClientPairs.Select(p => p.Value).Where(p => p.UserPair.Groups.Exists(g => GroupDataComparer.Instance.Equals(group.Key, new(g)))).ToList();
            }
            return outDict;
        });
    }

    private Lazy<Dictionary<Pair, List<GroupFullInfoDto>>> PairsWithGroupsLazy()
    {
        return new Lazy<Dictionary<Pair, List<GroupFullInfoDto>>>(() =>
        {
            Dictionary<Pair, List<GroupFullInfoDto>> outDict = [];

            foreach (var pair in _allClientPairs.Select(k => k.Value))
            {
                outDict[pair] = _allGroups.Where(k => pair.UserPair.Groups.Contains(k.Key.GID, StringComparer.Ordinal)).Select(k => k.Value).ToList();
            }

            return outDict;
        });
    }

    // Fork: a mass reapply (cutscene end, filter change) used to start every pair's cached apply in the same tick, bypassing the
    // apply queue. Visible pairs go first, then the rest, spread over about 3 s (at least 50 ms apart); a new sweep replaces a running one.
    private CancellationTokenSource? _reapplySweepCts;

    private void ReapplyPairData()
    {
        var pairs = _allClientPairs.Values.Where(p => p.HasCachedData).OrderByDescending(p => p.IsVisible).ToList();
        _reapplySweepCts?.Cancel();
        var cts = _reapplySweepCts = new CancellationTokenSource();
        if (pairs.Count == 0) return;
        var gap = TimeSpan.FromMilliseconds(Math.Max(50, 3000 / pairs.Count));
        Logger.LogDebug("Reapplying cached data for {count} pair(s), {gap} ms apart", pairs.Count, (int)gap.TotalMilliseconds);
        _ = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < pairs.Count; i++)
                {
                    if (i > 0) await Task.Delay(gap, cts.Token).ConfigureAwait(false);
                    pairs[i].ReapplyCachedData(forced: true);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.LogWarning(ex, "Staggered reapply failed"); }
        });
    }

    public void RecreateLazy()
    {
        _directPairsInternal = DirectPairsLazy();
        _groupPairsInternal = GroupPairsLazy();
        _pairsWithGroupsInternal = PairsWithGroupsLazy();
        Mediator.Publish(new RefreshUiMessage());
    }

    private void UpdatePairVisibility(ObjectTablePlayersFrameworkUpdate change)
    {
        if (!_identToUserPairs.TryGetValue(change.Ident, out var pair))
        {
            return;
        }

        if (pair.CachedPlayer != null)
        {
            pair.UpdateVisibility(change.IsVisible);
        }

        if (change.IsVisible && !pair.HasCachedPlayer)
        {
            InitializePair(change.Ident, change.Name, change.ContentId, pair);
        }
    }

    private void InitializePair(string ident, string name, ulong contentId, Pair pair)
    {
        if (_isZoning || !_isConnected || pair.HasCachedPlayer || !_initializingPairs.TryAdd(ident, 0))
        {
            return;
        }

        // push this off onto another thread since this originated from a SameThreadMessage
        // especially since this calls RunOnFrameworkThread and we can't await that from an awaited Framework Task
        _ = Task.Run(async () =>
        {
            try
            {
                if (_isZoning || !_isConnected || !_identToUserPairs.ContainsKey(ident) || pair.HasCachedPlayer)
                {
                    return;
                }

                Logger.LogDebug("One-Time Initializing {uid}:{name}", pair.UserData.UID, name);
                await pair.Initialize(name, contentId).ConfigureAwait(false);

                bool visible = await _dalamudUtilService.RunOnFrameworkThread(() =>
                {
                    var currentVisibility = _dalamudUtilService.GetFrameworkCharacterVisibilityByContentId(contentId);

                    return currentVisibility?.Visible == true;
                }).ConfigureAwait(false);

                pair.UpdateVisibility(visible);
                Logger.LogDebug("One-Time Initialized {uid}:{name}", pair.UserData.UID, name);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to initialize {ident}", ident);
            }
            finally
            {
                _initializingPairs.TryRemove(ident, out _);
            }
        });
    }

    private async Task UpdateVisiblePairs(string? ident = null)
    {
        try
        {
            var changes = await _dalamudUtilService.RunOnFrameworkThread(() =>
            {
                var idents = ident == null ? _identToUserPairs.Keys.ToArray() : [ident];
                List<ObjectTablePlayersFrameworkUpdate> visible = [];
                foreach (var playerIdent in idents)
                {
                    var visibility = _dalamudUtilService.GetFrameworkCharacterVisibilityByIdent(playerIdent);
                    if (visibility?.Visible == true)
                    {
                        visible.Add(new(visibility.Value.ContentId, playerIdent, visibility.Value.Name, true));
                    }
                }

                return visible;
            }).ConfigureAwait(false);

            foreach (var change in changes)
            {
                UpdatePairVisibility(change);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to update visible pairs");
        }
    }

    private async Task PausedPairExpiredTimerCheckTask(CancellationToken ct)
    {
        try
        {
            Logger.LogDebug("Starting checks for paused players to unpause.");
            var initDelay = TimeSpan.FromMinutes(1);
            await Task.Delay(initDelay, ct).ConfigureAwait(false); // init delay for plugin load

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_isConnected)
                    {
                        Logger.LogTrace("Checking for paused pairs to unpause...");

                        var onlinePairs = GetOnlineUserPairs();

                        MareSynchronos.Services.VierAITempPauses.PruneExpired(); // 1.15.5.1411
                        var pendingPausedPairs = _serverConfigurationManager.GetPendingPausedPairUIDs();
                        List<string> pairsToUnPause = new List<string>();

                        foreach (var pausedPair in pendingPausedPairs)
                        {
                            var uid = pausedPair.Key;
                            var pair = GetPairByUID(uid);
                            if (onlinePairs.Any(p => string.Equals(p.UserData.UID, uid, StringComparison.OrdinalIgnoreCase)) || pair == null || !pair.IsPaused)
                            {
                                Logger.LogDebug("Removing stale paused pair entry for UID: {uid}", uid);
                                _serverConfigurationManager.RemovePauseReasonForUid(uid);
                                _serverConfigurationManager.RemovePendingPauseForUid(uid);
                                if (pair != null && !pair.IsPaused) pair.ClearLastKnownIdentity(); else MareSynchronos.Services.VierAITempPauses.Remove(uid); // 1.15.5.1411
                                continue;
                            }

                            if (DateTimeOffset.UtcNow >= pausedPair.Value)
                            {
                                pairsToUnPause.Add(uid);
                            }
                        }

                        foreach (var uidToUnpause in pairsToUnPause)
                        {
                            Logger.LogDebug("Automatically removing paused status for UID: {uid}", uidToUnpause);
                            Mediator.Publish(new UnPauseMessage(new(uidToUnpause), true));
                            await Task.Delay(250).ConfigureAwait(false);
                        }
                    }

                    await Task.Delay(PausedPairCheckInterval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // normal shutdown
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, " paused pair.");

                    await Task.Delay(PausedPairCheckInterval, ct).ConfigureAwait(false);
                }
            }
        }
        catch (TaskCanceledException) { }
        catch (Exception ex)
        {
            Logger.LogCritical(ex, "Error while checking for paused pairs. This background task will now stop.");
        }
        finally
        {
            _unpausePairTask = null;
        }
    }
}