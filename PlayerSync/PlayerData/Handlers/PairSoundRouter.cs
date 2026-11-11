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

    // 1.15.5.1704: object address -> content id of visible players with a handler, rebuilt on the framework thread every
    // 500 ms and swapped in whole (never mutated after publishing), so the resource load hook can check it from any thread.
    private static readonly Dictionary<nint, ulong> EmptyAddressMap = new();
    private static Dictionary<nint, ulong> s_addressMap = EmptyAddressMap;

    public static void SetAddressMap(Dictionary<nint, ulong>? map) => Volatile.Write(ref s_addressMap, map ?? EmptyAddressMap);

    public static bool TryGetContentIdForAddress(nint address, out ulong contentId)
        => Volatile.Read(ref s_addressMap).TryGetValue(address, out contentId);

    public static void Dispatch(PenumbraSoundResourceLoadMessage msg)
    {
        if (Handlers.TryGetValue(msg.ContentId, out var handler)) handler(msg);
    }
}