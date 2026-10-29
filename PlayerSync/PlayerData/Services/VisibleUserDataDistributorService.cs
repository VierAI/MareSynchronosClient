using MareSynchronos.API.Data;
using MareSynchronos.API.Data.Enum;
using MareSynchronos.FileCache;
using MareSynchronos.MareConfiguration;
using MareSynchronos.Services;
using MareSynchronos.Services.Mediator;
using MareSynchronos.Utils;
using MareSynchronos.WebAPI;
using MareSynchronos.WebAPI.Files;
using Microsoft.Extensions.Logging;

namespace MareSynchronos.PlayerData.Pairs;

public class VisibleUserDataDistributorService : DisposableMediatorSubscriberBase
{
    private readonly TimeSpan _cacheCreationDelay = TimeSpan.FromSeconds(5);

    private readonly ApiController _apiController;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly FileUploadManager _fileTransferManager;
    private readonly PairManager _pairManager;
    private readonly MareConfigService _configService;
    private readonly FileCacheManager _fileCacheManager;

    private readonly List<UserData> _previouslyVisiblePlayers = [];
    private readonly object _pushDataLock = new();
    private readonly HashSet<UserData> _usersToPushDataTo = [];
    private readonly HashSet<UserData> _usersToPushPartialDataTo = [];
    private readonly SemaphoreSlim _pushDataSemaphore = new(1, 1);
    private readonly CancellationTokenSource _runtimeCts = new();
    private readonly HashSet<string> _filesTooLargeOrEmptyHasBeenNotified = new HashSet<string>();

    private Task<CharacterData>? _fileUploadTask = null;
    private CharacterData? _lastCreatedData;
    private CharacterData? _uploadingCharacterData = null;
    private int _cacheCreationRequestCount = 0;
    private DateTimeOffset _lastCacheCreationRequest = DateTimeOffset.MinValue;
    // 1.15.5.1224: UID -> (hash, time) of the last push, so a visibility flap doesn't re-push identical data.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Hash, DateTimeOffset At)> _lastPushed = new(StringComparer.Ordinal);
    private static readonly TimeSpan RepushWindow = TimeSpan.FromMinutes(5);
    private DateTimeOffset _nextLastPushedPrune = DateTimeOffset.MinValue;


    public VisibleUserDataDistributorService(ILogger<VisibleUserDataDistributorService> logger, ApiController apiController, DalamudUtilService dalamudUtil,
        PairManager pairManager, MareMediator mediator, FileUploadManager fileTransferManager, MareConfigService mareConfigService, FileCacheManager fileCacheManager) : base(logger, mediator)
    {
        _apiController = apiController;
        _dalamudUtil = dalamudUtil;
        _pairManager = pairManager;
        _fileTransferManager = fileTransferManager;
        _configService = mareConfigService;
        _fileCacheManager = fileCacheManager;

        Mediator.Subscribe<CharacterDataCreatedMessage>(this, (msg) =>
        {
            var newData = msg.CharacterData;
            if (_lastCreatedData == null || (!string.Equals(newData.DataHash.Value, _lastCreatedData.DataHash.Value, StringComparison.Ordinal)))
            {
                _lastCreatedData = newData;
                _lastPushed.Clear(); // 1.15.5.1224: new hash, everyone needs it again
                Logger.LogTrace("Storing new data hash {hash}", newData.DataHash.Value);
                PushToAllVisibleUsers(forced: true);
            }
            else
            {
                Logger.LogTrace("Data hash {hash} equal to stored data", newData.DataHash.Value);
            }
        });
        Mediator.Subscribe<PairOfflineMessage>(this, (msg) =>
        {
            lock (_pushDataLock)
            {
                _previouslyVisiblePlayers.Remove(msg.Pair.UserData);
                _usersToPushDataTo.Remove(msg.Pair.UserData);
                _usersToPushPartialDataTo.Remove(msg.Pair.UserData);
            }
            
        });
        Mediator.Subscribe<DisconnectedMessage>(this, (_) =>
        {
            lock (_pushDataLock)
            {
                _previouslyVisiblePlayers.Clear();
                _usersToPushDataTo.Clear();
                _usersToPushPartialDataTo.Clear();
            }
        });
        Mediator.Subscribe<DalamudLogoutMessage>(this, (_) => // useful if you get disconnected from the game but are able to log back in
        {
            lock (_pushDataLock)
            {
                _previouslyVisiblePlayers.Clear();
                _usersToPushDataTo.Clear();
                _usersToPushPartialDataTo.Clear();
            }
        });
        Mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, (_) => FrameworkOnUpdate());
        Mediator.Subscribe<AddonPluginChangesCreatedMessage>(this, (msg) => _ = PushAddonPluginPlayerChanges(msg.PlayerChanges, msg.Data));
        Mediator.Subscribe<ConnectedMessage>(this, (_) => PushToAllVisibleUsers());
        
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _runtimeCts.Cancel();
            _runtimeCts.Dispose();
        }

        base.Dispose(disposing);
    }

    // 1.15.5.1217: mutual hold. Users we hold for the memory budget or have blocked do not get our data either.
    // They are left out of "previously visible", so when the hold lifts they count as newly visible and get our data again.
    private List<UserData> PSyncPushTargets() => _pairManager.GetVisibleUsers().Where(u => !MareSynchronos.Services.PSyncCrowd.HoldsPush(u.UID)).ToList();

    private void PushToAllVisibleUsers(bool forced = false)
    {
        foreach (var user in PSyncPushTargets())
        {
            if (!forced && SkipRecentlyPushed(user)) continue;
            _usersToPushDataTo.Add(user);
        }

        if (_usersToPushDataTo.Count > 0)
        {
            Logger.LogDebug("Pushing data {hash} for {count} visible players", _lastCreatedData?.DataHash.Value ?? "UNKNOWN", _usersToPushDataTo.Count);
            PushCharacterData(forced);
        }
    }

    private void FrameworkOnUpdate()
    {
        if (!_dalamudUtil.GetIsPlayerPresent() || !_apiController.IsConnected)
        {
            return;
        }

        var allVisibleUsers = PSyncPushTargets();
        List<UserData> newVisibleUsers;

        lock (_pushDataLock)
        {
            newVisibleUsers = allVisibleUsers.Except(_previouslyVisiblePlayers).ToList();

            _previouslyVisiblePlayers.Clear();
            _previouslyVisiblePlayers.AddRange(allVisibleUsers);
        }

        if (newVisibleUsers.Count == 0)
        {
            return;
        }

        Logger.LogDebug("Scheduling character data push of {data} to {users}", _lastCreatedData?.DataHash.Value ?? string.Empty, string.Join(", ", newVisibleUsers.Select(k => k.AliasOrUID)));
        foreach (var user in newVisibleUsers)
        {
            if (SkipRecentlyPushed(user)) continue;
            _usersToPushDataTo.Add(user);
        }

        PushCharacterData();
    }

    // 1.15.5.1224: true when this UID already got the current hash within RepushWindow. Read-only: a push is only recorded after it succeeds (RecordPushed).
    private bool SkipRecentlyPushed(UserData user)
    {
        // 1.15.5.1412: ReloadDedupe reverted to upstream: never skip a re-push
        return false;
#pragma warning disable CS0162
        var hash = _lastCreatedData?.DataHash.Value;
        if (string.IsNullOrEmpty(hash)) return false;
        var now = DateTimeOffset.UtcNow;
        if (now >= _nextLastPushedPrune)
        {
            _nextLastPushedPrune = now + TimeSpan.FromMinutes(1);
            foreach (var kv in _lastPushed)
                if (now - kv.Value.At >= RepushWindow) _lastPushed.TryRemove(kv.Key, out _);
        }
        if (_lastPushed.TryGetValue(user.UID, out var last) && string.Equals(last.Hash, hash, StringComparison.Ordinal) && now - last.At < RepushWindow)
        {
            Logger.LogDebug("Skipping re-push of {hash} to {user}: already sent {secs:0}s ago", hash, user.AliasOrUID, (now - last.At).TotalSeconds);
            return true;
        }
        return false;
    }

    private void RecordPushed(string hash, IEnumerable<UserData> users)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var u in users) _lastPushed[u.UID] = (hash, now);
    }

    private bool HasCacheOrCreateIfEmpty()
    {
        if (_lastCreatedData == null || string.IsNullOrEmpty(_lastCreatedData?.DataHash.Value))
        {
            _cacheCreationRequestCount++;
            Logger.LogDebug("Requested to push character data but character data was null. Total requests: {count}.", _cacheCreationRequestCount);

            var now = DateTimeOffset.UtcNow;
            if (now - _lastCacheCreationRequest < _cacheCreationDelay)
            {
                return false;
            }

            _lastCacheCreationRequest = now;
            Logger.LogDebug("Sending request to create player cache");
            Mediator.Publish(new CreateCacheForEverythingMessage()); // this will eventually call back here via CharacterDataCreatedMessage

            return false;
        }

        return true;
    }

    private void PushCharacterData(bool forced = false)
    {
        if (_usersToPushDataTo.Count == 0 || !HasCacheOrCreateIfEmpty())
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            //forced |= _uploadingCharacterData?.DataHash != _lastCreatedData!.DataHash;
            forced |= !string.Equals(_uploadingCharacterData?.DataHash.Value, _lastCreatedData!.DataHash.Value, StringComparison.Ordinal);

            if (_fileUploadTask == null || (_fileUploadTask?.IsCompleted ?? false) || forced)
            {
                var uploadingCharacterData = _lastCreatedData.DeepClone();
                _uploadingCharacterData = uploadingCharacterData;
                var visiblePlayers = _usersToPushDataTo.ToList();
                var cancellationToken = _runtimeCts.Token;
                Logger.LogDebug("Starting UploadTask for {hash}, Reason: TaskIsNull: {task}, TaskIsCompleted: {taskCpl}, Forced: {frc}",
                    _lastCreatedData.DataHash, _fileUploadTask == null, _fileUploadTask?.IsCompleted ?? false, forced);
                _fileUploadTask = Task.Run(async () =>
                {
                    await RemoveFilesThatAreTooLargeOrEmpty(uploadingCharacterData, cancellationToken).ConfigureAwait(false);

                    return await _fileTransferManager.UploadFiles(uploadingCharacterData, visiblePlayers).ConfigureAwait(false);
                }, cancellationToken);
            }

            if (_fileUploadTask != null)
            {
                CharacterData dataToSend;
                try
                {
                    dataToSend = await _fileUploadTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Logger.LogDebug("Character data upload was cancelled");

                    return;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Character data upload failed");

                    return;
                }

                await _pushDataSemaphore.WaitAsync(_runtimeCts.Token).ConfigureAwait(false);

                try
                {
                    if (_usersToPushDataTo.Count == 0)
                    {
                        return;
                    }

                    // We need to update our addon data just in case there was a partial sent while we were waiting on uploads
                    // this may seem hacky but it's way easier than versioning and merging partial changes into a full on the receiver
                    var latestData = _lastCreatedData!;
                    dataToSend.HeelsData = latestData.HeelsData;
                    dataToSend.HonorificData = latestData.HonorificData;
                    dataToSend.MoodlesData = latestData.MoodlesData;
                    dataToSend.PetNamesData = latestData.PetNamesData;
                    Logger.LogDebug("Pushing {data} to {users}", dataToSend.DataHash, string.Join(", ", _usersToPushDataTo.Select(k => k.AliasOrUID)));

                    _usersToPushDataTo.RemoveWhere(u => MareSynchronos.Services.PSyncCrowd.HoldsPush(u.UID));
                    if (_usersToPushDataTo.Count == 0) return;
                    var sentTo = _usersToPushDataTo.ToList();
                    var pushed = await _apiController.PushCharacterData(dataToSend, sentTo).ConfigureAwait(false);
                    if (pushed) RecordPushed(dataToSend.DataHash.Value, sentTo); // 1.15.5.1224: failed pushes record nothing, so they're retried

                    _usersToPushDataTo.Clear();
                }
                finally
                {
                    _pushDataSemaphore.Release();
                }
            }
        });
    }

    private async Task RemoveFilesThatAreTooLargeOrEmpty(CharacterData uploadingCharacterData, CancellationToken cancellationToken)
    {
        try
        {
            var hashes = uploadingCharacterData.FileReplacements.Values
                .SelectMany(replacements => replacements)
                .Select(replacement => replacement.Hash)
                .Where(hash => !string.IsNullOrWhiteSpace(hash))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var fileCachesByHash = await _fileCacheManager.GetFirstFileCacheEntitiesByHashes(hashes, cancellationToken).ConfigureAwait(false);
            Dictionary<ObjectKind, List<FileReplacementData>> filesTooLargeorEmpty = new();
            foreach (var fileReplacements in uploadingCharacterData.FileReplacements)
            {
                foreach (var replacement in fileReplacements.Value)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(replacement.Hash)
                        || !fileCachesByHash.TryGetValue(replacement.Hash, out var cache) || cache == null)
                    {
                        continue;
                    }

                    bool mustRemoveReplacementData = false;
                    // check for empty files
                    if (cache.Size == 0)
                    {
                        mustRemoveReplacementData = true;
                    }

                    // check for files that are larger than what the server allows for
                    else if (cache.Size > 300 * 1024 * 1024 || cache.CompressedSize > 200 * 1024 * 1024)
                    {
                        Logger.LogWarning("File {file} exceeds the size limit to sync and will not be sent. (300 MiB, or 200 MiB compressed)", cache.ResolvedFilepath);

                        if (!_filesTooLargeOrEmptyHasBeenNotified.Contains(cache.Hash) && _configService.Current.ShowFileUnableToSyncNotification)
                        {
                            var isTexFile = cache.ResolvedFilepath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase);
                            var texInfo = isTexFile ? " Ensure it is compressed and consider reducing its resolution." : "";
                            Mediator.Publish(new NotificationMessage("File Size Error", $"The file {cache.ResolvedFilepath} exceeds the size limit and will not sync. (300 MiB, or 200 MiB compressed)"
                                + texInfo, MareConfiguration.Models.NotificationType.Warning));
                            _filesTooLargeOrEmptyHasBeenNotified.Add(cache.Hash);
                        }

                        mustRemoveReplacementData = true;
                    }

                    if (mustRemoveReplacementData)
                    {
                        if (!filesTooLargeorEmpty.ContainsKey(fileReplacements.Key))
                        {
                            // create an empty replacements list for this ObjectKind if ObjectKind not already in the dictionary
                            filesTooLargeorEmpty[fileReplacements.Key] = [];
                        }
                        filesTooLargeorEmpty[fileReplacements.Key].Add(replacement);
                        Logger.LogWarning("File with hash {hash} is too large, or empty (0.00B), and will not be uploaded to the server.", replacement.Hash);
                    }
                }
            }

            // check each ObjectKind that is present for any items in its replacement list to remove
            foreach (var replacements in filesTooLargeorEmpty)
            {
                foreach (var replacement in replacements.Value)
                {
                    uploadingCharacterData.FileReplacements[replacements.Key].Remove(replacement);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Failed to properly detect if files to upload are too large or empty.");
        }
    }

    private async Task PushAddonPluginPlayerChanges(PlayerChanges playerChanges, string data)
    {
        if (!HasCacheOrCreateIfEmpty())
        {
            return;
        }

        var updatedData = _lastCreatedData!.DeepClone();

        CharacterData characterData = new();
        AddonPlugin plugin;
        
        switch (playerChanges)
        {
            case PlayerChanges.Honorific:
                characterData.HonorificData = data;
                updatedData.HonorificData = data;
                plugin = AddonPlugin.Honorific;
                break;

            case PlayerChanges.Heels:
                characterData.HeelsData = data;
                updatedData.HeelsData = data;
                plugin = AddonPlugin.Heels;
                break;

            case PlayerChanges.Moodles:
                characterData.MoodlesData = data;
                updatedData.MoodlesData = data;
                plugin = AddonPlugin.Moodles;
                break;

            case PlayerChanges.PetNames:
                characterData.PetNamesData = data;
                updatedData.PetNamesData = data;
                plugin = AddonPlugin.PetNames;
                break;

            default:
                return;
        }

        _lastCreatedData = updatedData; // do this so we don't push again on next DelayedFrameworkUpdate

        await _pushDataSemaphore.WaitAsync(_runtimeCts.Token).ConfigureAwait(false);
        try
        {
            foreach (var user in PSyncPushTargets())
            {
                _usersToPushPartialDataTo.Add(user);
            }

            if (_usersToPushPartialDataTo.Count == 0)
            {
                return;
            }

            Logger.LogTrace("Pushing only addon plugin changes for {count} users", _usersToPushPartialDataTo.Count);

            await _apiController.UserPushData(new([.. _usersToPushPartialDataTo], characterData, null, plugin)).ConfigureAwait(false);
        }
        finally
        {
            _usersToPushPartialDataTo.Clear();
            _pushDataSemaphore.Release();
        }
    }
}