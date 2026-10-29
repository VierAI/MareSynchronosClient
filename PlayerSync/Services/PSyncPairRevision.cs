namespace MareSynchronos.Services;

/// <summary>
/// 1.15.5.1226: a counter behind the PSync.GetPairStatesRevision IPC. It goes up whenever a pair's cached data is
/// replaced or changed; the IPC also bumps it when the visible pair states it reports have changed since the last call.
/// </summary>
public static class PSyncPairRevision
{
    private static long s_revision = 1;

    public static long Current => Interlocked.Read(ref s_revision);

    public static void Bump() => Interlocked.Increment(ref s_revision);
}
