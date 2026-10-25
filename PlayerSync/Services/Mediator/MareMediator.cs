using MareSynchronos.MareConfiguration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

namespace MareSynchronos.Services.Mediator;

public sealed class MareMediator : IHostedService
{
    private readonly object _addRemoveLock = new();
    private readonly ConcurrentDictionary<object, DateTime> _lastErrorTime = [];
    private readonly ILogger<MareMediator> _logger;
    private readonly CancellationTokenSource _loopCts = new();
    private readonly ConcurrentQueue<MessageBase> _messageQueue = new();
    private readonly PerformanceCollectorService _performanceCollector;
    private readonly MareConfigService _mareConfigService;
    private readonly ConcurrentDictionary<Type, HashSet<SubscriberAction>> _subscriberDict = [];
    private bool _processQueue = false;
    // Fork perf: per message type, a pre-sorted copy of the subscribers (high priority first), rebuilt only when someone
    // subscribes/unsubscribes, and a typed executor delegate built once per type instead of MethodInfo.Invoke per publish.
    private readonly ConcurrentDictionary<Type, SubscriberAction[]> _snapshots = new();
    private readonly ConcurrentDictionary<Type, Action<MareMediator, SubscriberAction[], MessageBase>> _executors = new();
    private static readonly MethodInfo ExecuteTypedMethod = typeof(MareMediator).GetMethod(nameof(ExecuteTyped), BindingFlags.NonPublic | BindingFlags.Static)!;
    public MareMediator(ILogger<MareMediator> logger, PerformanceCollectorService performanceCollector, MareConfigService mareConfigService)
    {
        _logger = logger;
        _performanceCollector = performanceCollector;
        _mareConfigService = mareConfigService;
    }

    public void PrintSubscriberInfo()
    {
        foreach (var subscriber in _subscriberDict.SelectMany(c => c.Value.Select(v => v.Subscriber))
            .DistinctBy(p => p).OrderBy(p => p.GetType().FullName, StringComparer.Ordinal).ToList())
        {
            _logger.LogInformation("Subscriber {type}: {sub}", subscriber.GetType().Name, subscriber.ToString());
            StringBuilder sb = new();
            sb.Append("=> ");
            foreach (var item in _subscriberDict.Where(item => item.Value.Any(v => v.Subscriber == subscriber)).ToList())
            {
                sb.Append(item.Key.Name).Append(", ");
            }

            if (!string.Equals(sb.ToString(), "=> ", StringComparison.Ordinal))
                _logger.LogInformation("{sb}", sb.ToString());
            _logger.LogInformation("---");
        }
    }

    public void Publish<T>(T message) where T : MessageBase
    {
        if (message.KeepThreadContext)
        {
            ExecuteMessage(message);
        }
        else
        {
            _messageQueue.Enqueue(message);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting SyncMediator");

        _ = Task.Run(async () =>
        {
            while (!_loopCts.Token.IsCancellationRequested)
            {
                while (!_processQueue)
                {
                    await Task.Delay(100, _loopCts.Token).ConfigureAwait(false);
                }

                await Task.Delay(100, _loopCts.Token).ConfigureAwait(false);

                HashSet<MessageBase> processedMessages = [];
                while (_messageQueue.TryDequeue(out var message))
                {
                    if (processedMessages.Contains(message)) { continue; }
                    processedMessages.Add(message);

                    ExecuteMessage(message);
                }
            }
        });

        _logger.LogInformation("Started SyncMediator");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _messageQueue.Clear();
        _loopCts.Cancel();
        _loopCts.Dispose();
        return Task.CompletedTask;
    }

    public void Subscribe<T>(IMediatorSubscriber subscriber, Action<T> action) where T : MessageBase
    {
        lock (_addRemoveLock)
        {
            _subscriberDict.TryAdd(typeof(T), []);

            if (!_subscriberDict[typeof(T)].Add(new(subscriber, action)))
            {
                throw new InvalidOperationException("Already subscribed");
            }
            _snapshots.TryRemove(typeof(T), out _);
        }
    }

    public void Unsubscribe<T>(IMediatorSubscriber subscriber) where T : MessageBase
    {
        lock (_addRemoveLock)
        {
            if (_subscriberDict.ContainsKey(typeof(T)))
            {
                _subscriberDict[typeof(T)].RemoveWhere(p => p.Subscriber == subscriber);
                _snapshots.TryRemove(typeof(T), out _);
            }
        }
    }

    internal void UnsubscribeAll(IMediatorSubscriber subscriber)
    {
        lock (_addRemoveLock)
        {
            foreach (Type kvp in _subscriberDict.Select(k => k.Key))
            {
                int unSubbed = _subscriberDict[kvp]?.RemoveWhere(p => p.Subscriber == subscriber) ?? 0;
                if (unSubbed > 0)
                {
                    _snapshots.TryRemove(kvp, out _);
                    _logger.LogDebug("{sub} unsubscribed from {msg}", subscriber.GetType().Name, kvp.Name);
                }
            }
        }
    }

    private void ExecuteMessage(MessageBase message)
    {
        var msgType = message.GetType();
        var subscribers = GetSnapshot(msgType);
        if (subscribers.Length == 0) return;

        var executor = _executors.GetOrAdd(msgType, static t =>
            (Action<MareMediator, SubscriberAction[], MessageBase>)ExecuteTypedMethod.MakeGenericMethod(t)
                .CreateDelegate(typeof(Action<MareMediator, SubscriberAction[], MessageBase>)));
        executor(this, subscribers, message);
    }

    // The snapshot is an immutable array: handlers that subscribe/unsubscribe while a message is being delivered only
    // affect the next publish (same as the old copy-per-publish). Built and invalidated under _addRemoveLock, so the
    // subscriber HashSet is never read outside the lock.
    private SubscriberAction[] GetSnapshot(Type msgType)
    {
        if (_snapshots.TryGetValue(msgType, out var snapshot)) return snapshot;
        lock (_addRemoveLock)
        {
            if (_snapshots.TryGetValue(msgType, out snapshot)) return snapshot;
            snapshot = _subscriberDict.TryGetValue(msgType, out var subscribers) && subscribers != null
                ? subscribers.Where(s => s.Subscriber != null).OrderBy(k => k.Subscriber is IHighPriorityMediatorSubscriber ? 0 : 1).ToArray()
                : [];
            _snapshots[msgType] = snapshot;
            return snapshot;
        }
    }

    private static void ExecuteTyped<T>(MareMediator mediator, SubscriberAction[] subscribers, MessageBase message) where T : MessageBase
        => mediator.ExecuteReflected(subscribers, (T)message);
    private void ExecuteReflected<T>(SubscriberAction[] subscribers, T message) where T : MessageBase
    {
        foreach (SubscriberAction subscriber in subscribers)
        {
            try
            {
                if (_mareConfigService.Current.LogPerformance)
                {
                    var isSameThread = message.KeepThreadContext ? "$" : string.Empty;
                    _performanceCollector.LogPerformance(this, $"{isSameThread}Execute>{message.GetType().Name}+{subscriber.Subscriber.GetType().Name}>{subscriber.Subscriber}",
                        () => ((Action<T>)subscriber.Action).Invoke(message));
                }
                else
                {
                    ((Action<T>)subscriber.Action).Invoke(message);
                }
            }
            catch (Exception ex)
            {
                if (_lastErrorTime.TryGetValue(subscriber, out var lastErrorTime) && lastErrorTime.Add(TimeSpan.FromSeconds(10)) > DateTime.UtcNow)
                    continue;

                _logger.LogError(ex.InnerException ?? ex, "Error executing {type} for subscriber {subscriber}",
                    message.GetType().Name, subscriber.Subscriber.GetType().Name);
                _lastErrorTime[subscriber] = DateTime.UtcNow;
            }
        }
    }

    public void StartQueueProcessing()
    {
        _logger.LogInformation("Starting Message Queue Processing");
        _processQueue = true;
    }

    private sealed class SubscriberAction
    {
        public SubscriberAction(IMediatorSubscriber subscriber, object action)
        {
            Subscriber = subscriber;
            Action = action;
        }

        public object Action { get; }
        public IMediatorSubscriber Subscriber { get; }
    }
}