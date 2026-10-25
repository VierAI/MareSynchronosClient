using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Dalamud.Plugin.Services;
using MareSynchronos.Services.Mediator;
using MareSynchronos.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Frozen;

namespace MareSynchronos.Services;

/// <summary>
/// Keeps a thread-safe snapshot of the in-game (FFXIV) friends list, for the download priority tiers.
/// Only the real friends list counts (InfoProxyFriendList); PlayerSync pairs never do.
/// The game memory is read on the framework thread only, on a throttled tick; other threads only read the
/// immutable snapshot. Friends are matched by content id: PlayerSync's pair ident is the SHA256 hash of the
/// character's content id (the same hash the plugin sends as its own ident), so the snapshot holds those hashes.
/// </summary>
public sealed unsafe class InGameFriendService : DisposableMediatorSubscriberBase, IHostedService
{
    private const int MaxFriends = 200; // the game's friend list limit
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan EmptyRetryInterval = TimeSpan.FromSeconds(3);

    private readonly IFramework _framework;
    private readonly DalamudUtilService _dalamudUtil;
    private volatile FrozenSet<string> _friendIdents = FrozenSet<string>.Empty;
    private DateTime _nextRefresh = DateTime.MinValue;
    private int _lastLoggedCount = -1;

    /// <summary>Number of friends in the last snapshot. Shown in the Priority tab.</summary>
    public static volatile int CurrentCount;

    public InGameFriendService(ILogger<InGameFriendService> logger, MareMediator mediator, IFramework framework, DalamudUtilService dalamudUtil)
        : base(logger, mediator)
    {
        _framework = framework;
        _dalamudUtil = dalamudUtil;
        Mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, (_) => Tick());
        Mediator.Subscribe<DalamudLoginMessage>(this, (_) => _nextRefresh = DateTime.MinValue);
        Mediator.Subscribe<DalamudLogoutMessage>(this, (_) =>
        {
            _friendIdents = FrozenSet<string>.Empty;
            CurrentCount = 0;
            _lastLoggedCount = -1;
        });
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>True if this pair ident (hashed content id) belongs to someone on the in-game friends list. Safe from any thread.</summary>
    public bool IsFriend(string? ident) => !string.IsNullOrEmpty(ident) && _friendIdents.Contains(ident);

    private void Tick()
    {
        if (!_framework.IsInFrameworkUpdateThread) return;
        var now = DateTime.UtcNow;
        if (now < _nextRefresh) return;
        _nextRefresh = now + (_friendIdents.Count == 0 ? EmptyRetryInterval : RefreshInterval);
        try
        {
            if (!_dalamudUtil.IsLoggedIn) return;
            Refresh();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not read the in-game friends list");
            _nextRefresh = now + RefreshInterval;
        }
    }

    private void Refresh()
    {
        var module = InfoModule.Instance();
        if (module == null) return;
        var proxy = (InfoProxyCommonList*)module->GetInfoProxyById(InfoProxyId.FriendList);
        if (proxy == null) return;
        var data = proxy->CharData;
        var count = (int)Math.Min(proxy->EntryCount, (uint)MaxFriends);
        if (data == null || count <= 0) return; // not loaded yet (or really empty): keep the previous snapshot

        var set = new HashSet<string>(count, StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var entry = data + i;
            if (entry->ContentId == 0) continue;
            if (entry->State.HasFlag(InfoProxyCommonList.CharacterData.OnlineStatus.WaitingForFriendListApproval)) continue; // pending request, not a friend yet
            set.Add(entry->ContentId.ToString().GetHash256());
        }

        var previous = _friendIdents;
        _friendIdents = set.ToFrozenSet(StringComparer.Ordinal);
        CurrentCount = set.Count;
        // The friend set changed: re-tier waiting and running downloads.
        if (!previous.SetEquals(_friendIdents)) Mediator.Publish(new PriorityChangedMessage());
        if (set.Count != _lastLoggedCount)
        {
            _lastLoggedCount = set.Count;
            Logger.LogInformation("In-game friends list: {count} friend(s) known for download priority", set.Count);
        }
    }
}
