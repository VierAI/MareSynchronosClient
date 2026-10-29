using MareSynchronos.API.Data;
using MareSynchronos.API.Data.Enum;
using MareSynchronos.API.Data.Extensions;
using MareSynchronos.API.Dto.User;
using MareSynchronos.MareConfiguration;
using MareSynchronos.MareConfiguration.Models;
using MareSynchronos.PlayerData.Factories;
using MareSynchronos.Services.Events;
using MareSynchronos.Services.Mediator;
using MareSynchronos.Services.ServerConfiguration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace MareSynchronos.PlayerData.Pairs;

public class Pair
{
    private readonly ILogger<Pair> _logger;
    private readonly PairHandlerFactory _cachedPlayerFactory;
    private readonly PairDataApplicationHandlerFactory _pairDataApplicationHandlerFactory;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly MareConfigService _configService;
    private readonly MareMediator _mediator;

    private readonly SemaphoreSlim _creationSemaphore = new(1);

    private OnlineUserIdentDto? _onlineUserIdentDto = null;
    private bool? _hasProfile = null;

    public Pair(ILogger<Pair> logger, UserFullPairDto userPair, PairHandlerFactory cachedPlayerFactory, PairDataApplicationHandlerFactory pairDataApplicationHandlerFactory,
        MareMediator mediator, ServerConfigurationManager serverConfigurationManager, MareConfigService mareConfigService)
    {
        _logger = logger;
        UserPair = userPair;
        _cachedPlayerFactory = cachedPlayerFactory;
        _pairDataApplicationHandlerFactory = pairDataApplicationHandlerFactory;
        _serverConfigurationManager = serverConfigurationManager;
        _configService = mareConfigService;
        _mediator = mediator;
    }

    public bool HasCachedPlayer => CachedPlayer != null && !string.IsNullOrEmpty(CachedPlayer.PlayerName) && _onlineUserIdentDto != null;
    public bool HasDataHandler => DataHandler != null;
    public bool HasCachedData => DataHandler?.HasCachedData ?? false;
    public IndividualPairStatus IndividualPairStatus => UserPair.IndividualPairStatus;
    public bool IsDirectlyPaired => IndividualPairStatus != IndividualPairStatus.None;
    public bool IsOneSidedPair => IndividualPairStatus == IndividualPairStatus.OneSided;
    public bool IsOnline { get; set; } = false;
    public bool IsModelReady => CachedPlayer?.IsModelReady ?? false;
    public bool IsPaired => IndividualPairStatus == IndividualPairStatus.Bidirectional || UserPair.Groups.Any();
    public bool IsZoneSyncOnlyPair => IndividualPairStatus != IndividualPairStatus.Bidirectional && UserPair.Groups.All(g => g.StartsWith(Constants.GroupZoneSyncPrefix));
    public bool IsPaused => UserPair.OwnPermissions.IsPaused();
    public bool IsVisible => CachedPlayer?.IsVisible ?? false;
    public bool CanApplyModdedData => HasCachedPlayer && HasDataHandler && IsVisible && IsModelReady && !HasDataApplicationTaskRunning &&
        DateTimeOffset.UtcNow - LastDataApplicationTime > TimeSpan.FromMilliseconds(250) && !MareSynchronos.Services.PSyncCrowd.HoldsApply(this);
    public string? PlayerName => CachedPlayer?.PlayerName ?? string.Empty;
    public string PairUIDName => $"{UserData.UID}:{(string.IsNullOrWhiteSpace(PlayerName) ? "NULLPLAYER" : PlayerName)}";
    public ConcurrentDictionary<string, string> ActiveCompressionRedirects => DataHandler?.ActiveCompressionRedirects ?? [];
    public bool HasDataApplicationTaskRunning => DataHandler?.HasDataApplicationTaskRunning ?? false;
    public long PendingDataSequenceNumber => DataHandler?.PendingDataSequenceNumber ?? 0;
    public long LastAppliedDataBytes => DataHandler?.LastAppliedDataBytes ?? -1;
    public DateTimeOffset LastDataApplicationTime => DataHandler?.LastDataApplicationTime ?? DateTimeOffset.MinValue;
    public IReadOnlyDictionary<ObjectKind, ConcurrentDictionary<PlayerChanges, double>>? DataApplicationTiming => DataHandler?.DataApplicationTiming;
    public ConcurrentDictionary<string, string> MissingFiles => DataHandler?.MissingFiles ?? [];
    public long LastAppliedDataTris { get; set; } = -1;
    public long LastAppliedApproximateVRAMBytes { get; set; } = -1;
    public int LastAppliedCompressedAlternates { get; set; } = -1;
    public DateTimeOffset? LastLoadedSoundSinceRedrawTime { get; set; } = null;
    public string LastLoadedSoundSinceRedraw { get; set; } = string.Empty;
    public string HashedCID => _onlineUserIdentDto?.Ident ?? string.Empty;
    // 1.15.5.1410: kept after MarkOffline (a paused pair goes offline and loses its ident and cached player), so the
    // right-click auto-pause override can still find a paused pair by the clicked character's hashed content id or name.
    public string LastKnownHashedCID { get; private set; } = string.Empty;
    public string LastKnownPlayerName { get; private set; } = string.Empty;

    /// <summary>1.15.5.1411: forget the remembered identity once the pause is lifted (auto-unpause on expiry or any unpause
    /// confirmed by the server), at the same moment the pause reason and expiry are removed.</summary>
    public void ClearLastKnownIdentity()
    {
        LastKnownHashedCID = string.Empty;
        LastKnownPlayerName = string.Empty;
        MareSynchronos.Services.VierAITempPauses.Remove(UserData.UID);
    }
    public ulong ContendId => CachedPlayer?.ContentId ?? 0;

    // Fork: sound activity state machine, see Services/SoundActivity.cs.
    private DateTimeOffset? _soundStateSince;
    /// <summary>When the current sound state began: sound load while ACTIVE, or the start of the fade. Setting null resets the sound state.</summary>
    public DateTimeOffset? SoundStateSince
    {
        get => _soundStateSince;
        set
        {
            _soundStateSince = value;
            if (value == null)
            {
                SoundState = MareSynchronos.Services.SoundState.None;
                SoundLoopHasSound = false;
                SoundLastLoadAt = null;
            }
        }
    }
    public MareSynchronos.Services.SoundState SoundState { get; set; } = MareSynchronos.Services.SoundState.None;
    public bool SoundInLoop { get; set; }
    public bool SoundLoopHasSound { get; set; }
    /// <summary>Fork: was making noise when muted (for the unmute restore mark).</summary>
    public bool SoundNoisyAtMute { get; set; }
    /// <summary>Fork: .scd game paths in the pair's last applied data; loads outside it are not their own sound (null = nothing applied yet).</summary>
    public HashSet<string>? SoundOwnScdPaths { get; set; }
    public DateTimeOffset? SoundLastLoadAt { get; set; }
    /// <summary>Fork 1205: last loaded sound was sustained (looping or long).</summary>
    public bool SoundLastLoadSustained { get; set; }
    /// <summary>Fork 1205: ACTIVE from one-shot sounds ends at this time (null = sustained, lasts while emoting).</summary>
    public DateTimeOffset? SoundActiveUntil { get; set; }
    public long SoundNextPollTick { get; set; }
    /// <summary>Fork 1210: estimated length (s) of the last successfully loaded sound and whether it has loop points.</summary>
    public double SoundLastLoadSeconds { get; set; }
    public bool SoundLastLoadLooped { get; set; }
    /// <summary>The last sound loaded while the character was in an emote loop (a song tied to that emote).</summary>
    public bool SoundLastLoadInLoop { get; set; }
    /// <summary>Why the current fade started (loopEnded, oneShot, timeEnded, soundRemoved, moved), or null.</summary>
    public string? SoundEndReason { get; set; }
    /// <summary>Position when a song outside an emote started (a move ends it), or null.</summary>
    public System.Numerics.Vector3? SoundSongPos { get; set; }
    /// <summary>Fork 1210: mode + emote id of the loop the pair is in (a different loop starts the sound check over).</summary>
    public int SoundLoopKey { get; set; }
    /// <summary>Fork 1210: last time one of the pair's sound files failed to load (ignored for ACTIVE; tooltip only).</summary>
    public DateTimeOffset? SoundLastFailedAt { get; set; }
    /// <summary>Next build: when the current fade first started (a repeating one-shot may extend the fade only up to one extra fade length past this).</summary>
    public DateTimeOffset? SoundFadeFirstStart { get; set; }
    /// <summary>Next build: consecutive failed loads per sound game path, and until when a repeatedly failing path is ignored.</summary>
    public Dictionary<string, (int Count, DateTimeOffset? IgnoredUntil)> SoundFailedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public nint Address => CachedPlayer?.PlayerCharacter ?? nint.Zero;
    public UserData UserData => UserPair.User;
    public ulong PlayerCharacterId => CachedPlayer?.PlayerCharacterId ?? ulong.MaxValue;
    public UserFullPairDto UserPair { get; set; }
    public PairHandler? CachedPlayer { get; private set; }
    private PairDataApplicationHandler? DataHandler { get; set; }

    public bool HasProfile
    {
        get
        {
            if (_hasProfile == null)
                return UserPair.User.HasProfile ?? false;

            return _hasProfile.Value;
        }
        set
        {
            _hasProfile = value;
        }
    }

    public override string ToString()
    {
        return UserData.AliasOrUID + ":" + PlayerName + ":" + (IsVisible ? "HasChar" : "NoChar");
    }

    public void UpdateVisibility(bool visible)
    {
        CachedPlayer?.UpdateVisibility(visible);
        DataHandler?.UpdateVisibility(visible);
    }

    public async Task Initialize(string playerName, ulong contendId)
    {
        await _creationSemaphore.WaitAsync().ConfigureAwait(false);
        bool hadErrors = false;
        try
        {
            CreateCachedPlayer();
            CreateDataHandler();
            var cachedPlayer = CachedPlayer;
            if (cachedPlayer != null && !string.IsNullOrEmpty(playerName))
            {
                await cachedPlayer.Initialize(playerName, contendId).ConfigureAwait(false);
                var dataHandler = DataHandler;
                if (dataHandler != null)
                {
                    var handler = cachedPlayer.CharacterHandler;
                    if (handler != null)
                    {
                        await dataHandler.Initialize(handler).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to Initialize pair!");
            hadErrors = true;
            try
            {
                DataHandler?.Dispose();
                CachedPlayer?.Dispose();
            }
            finally
            {
                DataHandler = null;
                CachedPlayer = null;
            }

            throw;
        }
        finally
        {
            _creationSemaphore.Release();
        }
        if (!hadErrors)
        {
            _mediator.Publish(new EventMessage(new DataApplicationEvent(Guid.Empty, DataApplicationStatus.Waiting, PlayerName, UserData,
                nameof(Initialize), EventSeverity.Informational, $"Initialized {PairUIDName}, waiting for data from pair...")));
        }
    }

    public Dictionary<PlayerChanges, bool> GetPairPlayerChanges()
    {
        return DataHandler?.GetPairPlayerChanges() ?? [];
    }

    public CharacterData? GetCopyOfPairCharacterData()
    {
        return DataHandler?.GetCopyOfPairCharacterData();
    }

    /// <summary>1.15.5.1226: hash of the cached data, without copying it (worked out once per data change).</summary>
    public string? PSyncGetCachedDataHash()
    {
        return DataHandler?.PSyncGetCachedDataHash();
    }

    public Task<Dictionary<ObjectKind, List<FileReplacementData>>?> TryGetMissingFileReplacementData()
    {
        return DataHandler!.TryGetMissingFileReplacementData();
    }

    public bool TryStartFullDataApplication(Guid applId, bool overwriteRunningApplication = false)
    {
        return DataHandler?.TryStartDataApplication(applId, overwriteRunningApplication) ?? false;
    }

    public void EndDataApplication(Guid appId)
    {
        if (DataHandler != null)
        {
            DataHandler.EndDataApplcation(appId);
        }
    }

    public void ApplyAddonPluginUpdate(OnlineUserCharaDataDto data)
    {
        if (CachedPlayer == null || DataHandler == null || _configService.Current.FilterMods) // don't try and apply addon data since the cachedData will be null (and we don't want to anyway)
        {
            return;
        }

        _ = DataHandler.HandleOptionalPluginDataAsync(data.AddonPlugin!.Value, data.CharaData);
    }

    public Task ApplyDataAsync(Guid applicationBase, OnlineUserCharaDataDto data, long sequence, CancellationToken topLevelToken, bool forced = false)
    {
        if (CachedPlayer == null || DataHandler == null)
        {
            _logger.LogDebug("Called to apply player data but CachedPlayer is null: {player} or DataHandler is null: {hanlder}", CachedPlayer == null, DataHandler == null);

            throw new PairDataApplicationException(PairDataExceptionReason.InvalidCharacterState, $"Cannot apply data for {PairUIDName}, no cached player exists!");
        }

        return DataHandler.ApplyCharacterDataAsync(applicationBase, data.CharaData, sequence, topLevelToken, forced);
    }

    /// <summary>Reapply Last Data from a menu or button: forced, and never skipped by the duplicate-apply debounce.</summary>
    public void ReapplyCachedDataByUser()
    {
        DataHandler?.ForkBypassDebounceOnce();
        ReapplyCachedData(forced: true);
    }

    /// <summary>1.15.5.1217: revert this pair's applied mods (budget hold / block) but keep their data for a later reapply.</summary>
    public async Task ReleaseAppliedDataAsync(string why)
    {
        await _creationSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var old = DataHandler;
            if (old == null) return;
            var data = old.PSyncGetAnyData();
            _logger.LogInformation("PSync: reverting {pair} ({why})", PairUIDName, why);
            if (_onlineUserIdentDto == null) return;
            // Audit L3: build the replacement first and swap it in, so DataHandler is never null for other callers.
            var dataHandler = _pairDataApplicationHandlerFactory.Create(this);
            DataHandler = dataHandler;
            old.Dispose();
            var charaHandler = CachedPlayer?.CharacterHandler;
            if (dataHandler != null && charaHandler != null)
            {
                await dataHandler.Initialize(charaHandler).ConfigureAwait(false);
            }
            if (data != null) dataHandler?.PSyncSeedPendingData(data);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PSync: failed to revert {pair}", PairUIDName);
        }
        finally
        {
            _creationSemaphore.Release();
        }
    }

    /// <summary>1.15.5.1217: drop this pair's files from the PSync cache and download + apply them again.</summary>
    public async Task<int> ForceRedownloadAsync()
    {
        var handler = DataHandler;
        if (handler == null) return 0;
        var removed = await handler.PSyncForceRedownloadAsync().ConfigureAwait(false);
        ReapplyCachedDataByUser();
        return removed;
    }

    /// <summary>1.15.5.1218: hashes this pair currently uses (cached or pending data), for shared-file checks.</summary>
    public IEnumerable<string> PSyncGetFileHashes()
    {
        var data = DataHandler?.PSyncGetAnyData();
        if (data == null) return Array.Empty<string>();
        return data.FileReplacements.SelectMany(k => k.Value).Select(r => r.Hash).Where(h => !string.IsNullOrWhiteSpace(h));
    }

    public void ReapplyCachedData(bool forced = false)
    {
        if (HasCachedData)
        {
            _mediator.Publish(new ReapplyCachedPairDataMessage(this, forced));
        }
    }

    public Task ApplyCachedDataAync(Guid applicationBase, CancellationToken topLevelToken, bool forceApplyCustomization = false)
    {
        if (CachedPlayer == null || DataHandler == null)
        {
            _logger.LogDebug("Called to apply player data but CachedPlayer is null: {player} or DataHandler is null: {hanlder}", CachedPlayer == null, DataHandler == null);

            throw new PairDataApplicationException(PairDataExceptionReason.InvalidCharacterState, "Pair was in an invalid state during cached data application");
        }

        return DataHandler.ApplyCachedCharacterDataASync(applicationBase, topLevelToken, forceApplyCustomization);
    }

    public bool SetOnlinePlayerDto(OnlineUserIdentDto? dto = null)
    {
        _creationSemaphore.Wait();

        try
        {
            if (CachedPlayer != null)
            {
                return true;
            }

            if (dto == null && _onlineUserIdentDto == null)
            {
                CachedPlayer?.Dispose();
                CachedPlayer = null;
                DataHandler?.Dispose();
                DataHandler = null;

                return false;
            }
            if (dto != null)
            {
                _onlineUserIdentDto = dto;
                if (!string.IsNullOrEmpty(dto.Ident)) LastKnownHashedCID = dto.Ident;
            }
        }
        finally
        {
            _creationSemaphore.Release();
        }

        return true;
    }

    public string? GetNote()
    {
        return _serverConfigurationManager.GetNoteForUid(UserData.UID);
    }

    public string GetPauseReason()
    {
        return PauseReasonText.FromPauseReason(_serverConfigurationManager.GetPauseReasonForUid(UserData.UID));
    }

    public string GetPlayerNameHash()
    {
        return CachedPlayer != null ? CachedPlayer.PlayerNameHash : HashedCID ?? string.Empty;
    }

    public bool HasAnyConnection()
    {
        return UserPair.Groups.Any() || UserPair.IndividualPairStatus != IndividualPairStatus.None;
    }

    public void MarkOffline(bool wait = true)
    {
        if (wait)
            _creationSemaphore.Wait();

        try
        {
            var dataHandler = DataHandler;
            DataHandler = null;
            dataHandler?.Dispose();

            var player = CachedPlayer;
            if (!string.IsNullOrEmpty(_onlineUserIdentDto?.Ident)) LastKnownHashedCID = _onlineUserIdentDto!.Ident;
            if (!string.IsNullOrEmpty(player?.PlayerName)) LastKnownPlayerName = player!.PlayerName!;
            CachedPlayer = null;
            player?.Dispose();

            _onlineUserIdentDto = null;
            IsOnline = false;
        }
        finally
        {
            if (wait)
                _creationSemaphore.Release();
        }
    }

    public void SetNote(string note)
    {
        _serverConfigurationManager.SetNoteForUid(UserData.UID, note);
    }

    internal void SetIsUploading()
    {
        DataHandler?.SetUploading();
    }

    private void CreateCachedPlayer()
    {
        if (CachedPlayer != null || _onlineUserIdentDto == null)
        {
            return;
        }

        CachedPlayer?.Dispose();
        _logger.LogTrace("Creating cached player for {pair}", HashedCID);
        CachedPlayer = _cachedPlayerFactory.Create(this);
    }

    private void CreateDataHandler()
    {
        if (DataHandler != null || _onlineUserIdentDto == null)
        {
            return;
        }

        DataHandler?.Dispose();
        _logger.LogTrace("Creating data handler for {pair}", HashedCID);
        DataHandler = _pairDataApplicationHandlerFactory.Create(this);
    }
}
