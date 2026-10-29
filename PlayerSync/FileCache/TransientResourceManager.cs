using MareSynchronos.API.Data.Enum;
using MareSynchronos.MareConfiguration;
using MareSynchronos.MareConfiguration.Configurations;
using MareSynchronos.PlayerData.Data;
using MareSynchronos.PlayerData.Handlers;
using MareSynchronos.Services;
using MareSynchronos.Services.Mediator;
using MareSynchronos.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace MareSynchronos.FileCache;

public sealed class TransientResourceManager : DisposableMediatorSubscriberBase
{
    private readonly object _cacheAdditionLock = new();
    private readonly HashSet<(ObjectKind Kind, string Path)> _cachedHandledPaths = [];
    private static readonly ObjectKind[] _objectKinds = Enum.GetValues<ObjectKind>();
    private readonly TransientConfigService _configurationService;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly string[] _handledFileTypes = ["tmb", "pap", "avfx", "atex", "sklb", "eid", "phyb", "scd", "skp", "shpk"];
    private readonly string[] _handledRecordingFileTypes = ["tex", "mdl", "mtrl"];
    private readonly ConcurrentDictionary<GameObjectHandler, byte> _playerRelatedPointers = [];
    private ConcurrentDictionary<ObjectKind, HashSet<string>>? _semiTransientResources = null;
    // 1221: one lock for every HashSet inside TransientResources/SemiTransientResources. Never nested with
    // _cacheAdditionLock, and never held while touching PlayerConfig (that can block on the framework thread).
    private readonly object _setLock = new();
    // 1221: owned handlers snapshot, rebuilt on add/remove, so hot paths don't copy ConcurrentDictionary.Keys.
    private readonly object _handlerLock = new();
    private volatile GameObjectHandler[] _ownedHandlers = [];
    private uint _lastClassJobId = uint.MaxValue;
    public bool IsTransientRecording { get; private set; } = false;

    public TransientResourceManager(ILogger<TransientResourceManager> logger, TransientConfigService configurationService,
            DalamudUtilService dalamudUtil, MareMediator mediator) : base(logger, mediator)
    {
        _configurationService = configurationService;
        _dalamudUtil = dalamudUtil;

        Mediator.Subscribe<PenumbraResourceLoadMessage>(this, Manager_PenumbraResourceLoadEvent);
        Mediator.Subscribe<PenumbraModSettingChangedMessage>(this, (_) => Manager_PenumbraModSettingChanged());
        Mediator.Subscribe<PriorityFrameworkUpdateMessage>(this, (_) => DalamudUtil_FrameworkUpdate());
        Mediator.Subscribe<GameObjectHandlerCreatedMessage>(this, (msg) =>
        {
            if (!msg.OwnedObject) return;
            lock (_handlerLock)
            {
                _playerRelatedPointers.TryAdd(msg.GameObjectHandler, 0);
                _ownedHandlers = _playerRelatedPointers.Keys.ToArray();
            }
        });
        Mediator.Subscribe<GameObjectHandlerDestroyedMessage>(this, (msg) =>
        {
            if (!msg.OwnedObject) return;
            lock (_handlerLock)
            {
                _playerRelatedPointers.TryRemove(msg.GameObjectHandler, out _);
                _ownedHandlers = _playerRelatedPointers.Keys.ToArray();
            }
        });
    }

    private TransientConfig.TransientPlayerConfig PlayerConfig
    {
        get
        {
            var key = PlayerPersistentDataKey;
            if (!_configurationService.Current.TransientConfigs.TryGetValue(key, out var transientConfig))
            {
                _configurationService.Current.TransientConfigs[key] = transientConfig = new();
            }

            return transientConfig;
        }
    }

    private string PlayerPersistentDataKey => _dalamudUtil.RunOnFrameworkThread(() => _dalamudUtil.GetPlayerName() + "_" + _dalamudUtil.GetHomeWorldId()).GetAwaiter().GetResult();
    private ConcurrentDictionary<ObjectKind, HashSet<string>> SemiTransientResources
    {
        get
        {
            var current = _semiTransientResources;
            if (current != null) return current;

            var fresh = new ConcurrentDictionary<ObjectKind, HashSet<string>>();
            PlayerConfig.JobSpecificCache.TryGetValue(_dalamudUtil.ClassJobId, out var jobSpecificData);
            fresh[ObjectKind.Player] = PlayerConfig.GlobalPersistentCache.Concat(jobSpecificData ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            PlayerConfig.JobSpecificPetCache.TryGetValue(_dalamudUtil.ClassJobId, out var petSpecificData);
            fresh[ObjectKind.Pet] = new HashSet<string>(petSpecificData ?? [], StringComparer.OrdinalIgnoreCase);
            return Interlocked.CompareExchange(ref _semiTransientResources, fresh, null) ?? fresh;
        }
    }
    private ConcurrentDictionary<ObjectKind, HashSet<string>> TransientResources { get; } = new();

    public void CleanUpSemiTransientResources(ObjectKind objectKind, List<FileReplacement>? fileReplacement = null)
    {
        if (!SemiTransientResources.TryGetValue(objectKind, out HashSet<string>? value))
            return;

        if (fileReplacement == null)
        {
            lock (_setLock) value.Clear();
            return;
        }

        int removedPaths = 0;
        foreach (var replacement in fileReplacement.Where(p => !p.HasFileReplacement).SelectMany(p => p.GamePaths).ToList())
        {
            removedPaths += PlayerConfig.RemovePath(replacement, objectKind);
            lock (_setLock) value.Remove(replacement);
        }

        if (removedPaths > 0)
        {
            Logger.LogTrace("Removed {amount} of SemiTransient paths during CleanUp, Saving from {name}", removedPaths, nameof(CleanUpSemiTransientResources));
            // force reload semi transient resources
            _configurationService.Save();
        }
    }

    public HashSet<string> GetSemiTransientResources(ObjectKind objectKind)
    {
        SemiTransientResources.TryGetValue(objectKind, out var result);
        if (result == null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 1221: hand out a copy; callers enumerate it while loads may still add.
        lock (_setLock) return new HashSet<string>(result, StringComparer.OrdinalIgnoreCase);
    }

    public void PersistTransientResources(ObjectKind objectKind)
    {
        var semiTransientResources = SemiTransientResources.GetOrAdd(objectKind, _ => new(StringComparer.OrdinalIgnoreCase));

        if (!TransientResources.TryGetValue(objectKind, out var resources))
        {
            return;
        }

        List<string> transientResources;
        List<string> newlyAddedGamePaths;
        try
        {
            lock (_setLock)
            {
                transientResources = resources.ToList();
                newlyAddedGamePaths = resources.Except(semiTransientResources, StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var gamePath in transientResources)
                {
                    semiTransientResources.Add(gamePath);
                }
            }
            Logger.LogDebug("Persisting {count} transient resources", transientResources.Count);
        }
        catch
        {
            Logger.LogError("Transient Resource Manager failure! Was the game modified with something like Tex Tools?");
            throw;
        }

        bool saveConfig = false;
        if (objectKind == ObjectKind.Player && newlyAddedGamePaths.Count != 0)
        {
            saveConfig = true;
            foreach (var item in newlyAddedGamePaths.Where(f => !string.IsNullOrEmpty(f)))
            {
                PlayerConfig.AddOrElevate(_dalamudUtil.ClassJobId, item);
            }
        }
        else if (objectKind == ObjectKind.Pet && newlyAddedGamePaths.Count != 0)
        {
            saveConfig = true;

            if (!PlayerConfig.JobSpecificPetCache.TryGetValue(_dalamudUtil.ClassJobId, out var petPerma))
            {
                PlayerConfig.JobSpecificPetCache[_dalamudUtil.ClassJobId] = petPerma = [];
            }

            foreach (var item in newlyAddedGamePaths.Where(f => !string.IsNullOrEmpty(f)))
            {
                petPerma.Add(item);
            }
        }

        if (saveConfig)
        {
            Logger.LogTrace("Saving transient.json from {method}", nameof(PersistTransientResources));
            _configurationService.Save();
        }

        if (TransientResources.TryGetValue(objectKind, out var toClear))
        {
            lock (_setLock) toClear.Clear();
        }
    }

    public void RemoveTransientResource(ObjectKind objectKind, string path)
    {
        if (SemiTransientResources.TryGetValue(objectKind, out var resources))
        {
            lock (_setLock) resources.RemoveWhere(f => string.Equals(path, f, StringComparison.OrdinalIgnoreCase));
            if (objectKind == ObjectKind.Player)
            {
                PlayerConfig.RemovePath(path, objectKind);
                Logger.LogTrace("Saving transient.json from {method}", nameof(RemoveTransientResource));
                _configurationService.Save();
            }
        }
    }

    internal bool AddTransientResource(ObjectKind objectKind, string item)
    {
        var semi = SemiTransientResources;
        var transientResource = TransientResources.GetOrAdd(objectKind, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        lock (_setLock)
        {
            if (semi.TryGetValue(objectKind, out var semiTransient) && semiTransient != null && semiTransient.Contains(item))
                return false;

            return transientResource.Add(item.ToLowerInvariant());
        }
    }

    internal void ClearTransientPaths(ObjectKind objectKind, List<string> list)
    {
        // ignore all recording only datatypes
        int recordingOnlyRemoved = list.RemoveAll(entry => _handledRecordingFileTypes.Any(ext => entry.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));
        if (recordingOnlyRemoved > 0)
        {
            Logger.LogTrace("Ignored {0} game paths when clearing transients", recordingOnlyRemoved);
        }

        var listSet = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
        if (TransientResources.TryGetValue(objectKind, out var set))
        {
            int removed;
            lock (_setLock)
            {
                foreach (var file in set.Where(listSet.Contains))
                {
                    Logger.LogTrace("Removing From Transient: {file}", file);
                }

                removed = set.RemoveWhere(listSet.Contains);
            }
            Logger.LogDebug("Removed {removed} previously existing transient paths", removed);
        }

        bool reloadSemiTransient = false;
        if (objectKind == ObjectKind.Player && SemiTransientResources.TryGetValue(objectKind, out var semiset))
        {
            List<string> toRemove;
            int removed;
            lock (_setLock)
            {
                toRemove = semiset.Where(listSet.Contains).ToList();
                removed = semiset.RemoveWhere(listSet.Contains);
            }
            foreach (var file in toRemove)
            {
                Logger.LogTrace("Removing From SemiTransient: {file}", file);
                PlayerConfig.RemovePath(file, objectKind);
            }

            Logger.LogDebug("Removed {removed} previously existing semi transient paths", removed);
            if (removed > 0)
            {
                reloadSemiTransient = true;
                Logger.LogTrace("Saving transient.json from {method}", nameof(ClearTransientPaths));
                _configurationService.Save();
            }
        }

        if (reloadSemiTransient)
            _semiTransientResources = null;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        TransientResources.Clear();
        SemiTransientResources.Clear();
    }

    private void DalamudUtil_FrameworkUpdate()
    {
        lock (_cacheAdditionLock)
        {
            _cachedHandledPaths.Clear();
        }

        if (_lastClassJobId != _dalamudUtil.ClassJobId)
        {
            _lastClassJobId = _dalamudUtil.ClassJobId;
            if (SemiTransientResources.TryGetValue(ObjectKind.Pet, out HashSet<string>? value) && value != null)
            {
                lock (_setLock) value.Clear();
            }

            // reload config for current new classjob (new sets are built outside the lock, then swapped in)
            PlayerConfig.JobSpecificCache.TryGetValue(_dalamudUtil.ClassJobId, out var jobSpecificData);
            SemiTransientResources[ObjectKind.Player] = PlayerConfig.GlobalPersistentCache.Concat(jobSpecificData ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            PlayerConfig.JobSpecificPetCache.TryGetValue(_dalamudUtil.ClassJobId, out var petSpecificData);
            SemiTransientResources[ObjectKind.Pet] = new HashSet<string>(petSpecificData ?? [], StringComparer.OrdinalIgnoreCase);
        }

        //foreach (var kind in _objectKinds)
        //{
        //    if (!_playerRelatedPointers.Keys.Any(handler => handler.ObjectKind == kind && handler.ModelReady) && TransientResources.Remove(kind, out _))
        //    {
        //        Logger.LogDebug("Object not present anymore: {kind}", kind.ToString());
        //    }
        //}
        var handlers = _ownedHandlers;
        foreach (var kind in _objectKinds)
        {
            bool present = false;
            foreach (var handler in handlers)
            {
                if (handler.ObjectKind == kind && handler.GetAddressFromFramework() != nint.Zero) { present = true; break; }
            }
            if (!present && TransientResources.Remove(kind, out _))
            {
                Logger.LogDebug("Object not present anymore: {kind}", kind);
            }
        }
    }

    private void Manager_PenumbraModSettingChanged()
    {
        _ = Task.Run(() =>
        {
            Logger.LogDebug("Penumbra Mod Settings changed, verifying SemiTransientResources");
            foreach (var item in _ownedHandlers)
            {
                Mediator.Publish(new TransientResourceChangedMessage(item.ObjectKind));
            }
        });
    }

    public void RebuildSemiTransientResources()
    {
        _semiTransientResources = null;
    }

    private static bool EndsWithAny(string path, string[] types)
    {
        foreach (var type in types)
        {
            if (path.EndsWith(type, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private void Manager_PenumbraResourceLoadEvent(PenumbraResourceLoadMessage msg)
    { 
        var gameObjectAddress = msg.GameObject;

        // Fork perf: owner check first, so loads for untracked objects don't allocate a lowercased path.
        if (gameObjectAddress == nint.Zero) return;
        GameObjectHandler? owner = null;
        foreach (var handler in _ownedHandlers)
        {
            if (handler.Address == gameObjectAddress) { owner = handler; break; }
        }
        if (owner == null)
        {
            return;
        }

        var gamePath = msg.GamePath.ToLowerInvariant();

        var objectKind = owner.ObjectKind;
        var filePath = msg.FilePath;

        // ignore files already processed this frame (fork: check-and-add under the lock; loads also arrive off the framework thread)
        lock (_cacheAdditionLock)
        {
            if (!_cachedHandledPaths.Add((objectKind, gamePath)))
            {
                return;
            }
        }

        // replace individual mtrl stuff
        if (filePath.StartsWith("|", StringComparison.OrdinalIgnoreCase))
        {
            filePath = filePath.Split("|")[2];
        }
        // replace filepath
        filePath = filePath.ToLowerInvariant().Replace("\\", "/", StringComparison.OrdinalIgnoreCase);

        // ignore files that are the same
        var replacedGamePath = gamePath.Replace("\\", "/", StringComparison.Ordinal); // 1225: gamePath is already lowercased
        if (string.Equals(filePath, replacedGamePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // ignore files to not handle
        // 1225: plain loops, no Concat/closure allocation for every resource load
        if (!EndsWithAny(gamePath, _handledFileTypes) && !(IsTransientRecording && EndsWithAny(gamePath, _handledRecordingFileTypes)))
        {
            lock (_cacheAdditionLock)
            {
                _cachedHandledPaths.Add((objectKind, gamePath));
            }
            return;
        }

        // ^ all of the code above is just to sanitize the data
        var transientResources = TransientResources.GetOrAdd(objectKind, _ => new(StringComparer.OrdinalIgnoreCase));
        var semiResources = SemiTransientResources;

        bool alreadyTransient = false;
        bool transientContains;
        bool semiTransientContains = false;
        bool isAdded = false;
        lock (_setLock)
        {
            transientContains = transientResources.Contains(replacedGamePath);
            // 1221: hashed lookups (sets are OrdinalIgnoreCase) instead of a linear scan over every semi-transient path
            foreach (var kv in semiResources)
            {
                if (kv.Value.Contains(gamePath)) { semiTransientContains = true; break; }
            }
            if (!transientContains && !semiTransientContains && !IsTransientRecording)
            {
                isAdded = transientResources.Add(replacedGamePath);
            }
            if (IsTransientRecording)
            {
                bool already = transientContains || semiTransientContains;
                _recordedTransients.Add(new TransientRecord(owner, replacedGamePath, filePath, already) { AddTransient = !already });
            }
        }
        if (transientContains || semiTransientContains)
        {
            if (!IsTransientRecording)
                Logger.LogTrace("Not adding {replacedPath} => {filePath}, Reason: Transient: {contains}, SemiTransient: {contains2}", replacedGamePath, filePath,
                    transientContains, semiTransientContains);
            alreadyTransient = true;
        }
        else
        {
            if (!IsTransientRecording)
            {
                if (isAdded)
                {
                    Logger.LogDebug("Adding {replacedGamePath} for {gameObject} ({filePath})", replacedGamePath, owner?.ToString() ?? gameObjectAddress.ToString("X"), filePath);
                    SendTransients(objectKind);
                }
            }
        }

        _ = alreadyTransient;
    }

    private void SendTransients(ObjectKind objectKind)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                _sendTransientCts?.Cancel();
                _sendTransientCts?.Dispose();
                _sendTransientCts = new();
                var token = _sendTransientCts.Token;
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                bool any = false;
                if (TransientResources.TryGetValue(objectKind, out var values))
                {
                    lock (_setLock) any = values.Count > 0;
                }
                if (any)
                {
                    Logger.LogTrace("Sending Transients for {kind}", objectKind);
                    Mediator.Publish(new TransientResourceChangedMessage(objectKind));
                }
            }
            catch (ObjectDisposedException ex)
            {
                Logger.LogWarning(ex, "Cancellation token was disposed! We're not entirely sure why this happens...");
            }
        });
    }

    public void StartRecording(CancellationToken token)
    {
        if (IsTransientRecording) return;
        lock (_setLock) _recordedTransients.Clear();
        IsTransientRecording = true;
        RecordTimeRemaining.Value = TimeSpan.FromSeconds(150);
        _ = Task.Run(async () =>
        {
            try
            {
                while (RecordTimeRemaining.Value > TimeSpan.Zero && !token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                    RecordTimeRemaining.Value = RecordTimeRemaining.Value.Subtract(TimeSpan.FromSeconds(1));
                }
            }
            finally
            {
                IsTransientRecording = false;
            }
        });
    }

    public async Task WaitForRecording(CancellationToken token)
    {
        while (IsTransientRecording)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
        }
    }

    internal void SaveRecording()
    {
        HashSet<ObjectKind> addedTransients = [];
        lock (_setLock)
        {
            foreach (var item in _recordedTransients)
            {
                if (!item.AddTransient || item.AlreadyTransient) continue;
                var transient = TransientResources.GetOrAdd(item.Owner.ObjectKind, _ => new(StringComparer.OrdinalIgnoreCase));

                Logger.LogTrace("Adding recorded: {gamePath} => {filePath}", item.GamePath, item.FilePath);

                transient.Add(item.GamePath);
                addedTransients.Add(item.Owner.ObjectKind);
            }

            _recordedTransients.Clear();
        }

        foreach (var item in addedTransients)
        {
            Mediator.Publish(new TransientResourceChangedMessage(item));
        }
    }

    private readonly HashSet<TransientRecord> _recordedTransients = [];
    public IReadOnlySet<TransientRecord> RecordedTransients => _recordedTransients;

    public ValueProgress<TimeSpan> RecordTimeRemaining { get; } = new();
    private CancellationTokenSource _sendTransientCts = new();

    public record TransientRecord(GameObjectHandler Owner, string GamePath, string FilePath, bool AlreadyTransient)
    {
        public bool AddTransient { get; set; }
    }
}