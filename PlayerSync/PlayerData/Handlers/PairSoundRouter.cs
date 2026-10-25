using MareSynchronos.Services.Mediator;
using System.Collections.Concurrent;

namespace MareSynchronos.PlayerData.Handlers;

/// <summary>
/// Fork perf: routes Penumbra sound (.scd) loads straight to the one PairHandler of that player (by content id) instead of
/// publishing to every PairHandler and letting all but one discard it. Handlers register in Initialize and unregister on dispose.
/// </summary>
public static class PairSoundRouter
{
    private static readonly ConcurrentDictionary<ulong, Action<PenumbraSoundResourceLoadMessage>> Handlers = new();

    public static void Register(ulong contentId, Action<PenumbraSoundResourceLoadMessage> handler)
    {
        if (contentId != 0) Handlers[contentId] = handler;
    }

    /// <summary>Removes the entry only if it still belongs to this handler (a newer handler for the same player may have replaced it).</summary>
    public static void Unregister(ulong contentId, Action<PenumbraSoundResourceLoadMessage> handler)
    {
        if (contentId != 0) Handlers.TryRemove(new KeyValuePair<ulong, Action<PenumbraSoundResourceLoadMessage>>(contentId, handler));
    }

    public static bool HasHandler(ulong contentId) => contentId != 0 && Handlers.ContainsKey(contentId);

    public static bool HasAnyHandler => !Handlers.IsEmpty;

    public static void Dispatch(PenumbraSoundResourceLoadMessage msg)
    {
        if (Handlers.TryGetValue(msg.ContentId, out var handler)) handler(msg);
    }
}