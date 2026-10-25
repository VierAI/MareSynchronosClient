using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace MareSynchronos.Services;

// Fork (VierAI-fied): skeleton readiness check used after a redraw, before Customize+/Heels are re-applied.
public partial class DalamudUtilService
{
    /// <summary>
    /// True when the model's skeleton and every partial skeleton (.sklb) are loaded and posed. Must run on the framework thread.
    /// Any failure (null model, struct read error) counts as ready, so callers never loop on it.
    /// </summary>
    public unsafe bool ForkIsSkeletonReady(CharacterBase* model)
    {
        try
        {
            if (model == null) return true;
            var skeleton = model->Skeleton;
            if (skeleton == null) return false;
            var count = skeleton->PartialSkeletonCount;
            if (count <= 0) return false;
            for (int i = 0; i < count; i++)
            {
                ref var partial = ref skeleton->PartialSkeletons[i];
                var handle = partial.SkeletonResourceHandle;
                if (handle == null) continue; // unused slot (e.g. no hair/face skeleton)
                if ((byte)handle->LoadState < 7) return false; // 7 = loaded; higher states are failures, treated as done
                if (partial.GetHavokPose(0) == null) return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ForkIsSkeletonReady failed, treating as ready");
            return true;
        }
    }

    /// <summary>
    /// Waits (one framework tick per check) until the player's skeleton is ready, up to timeoutMs. A timeout, a missing
    /// player or an error all return so the caller just proceeds.
    /// </summary>
    public async Task<bool> ForkWaitForSkeletonReadyAsync(ulong contentId, int timeoutMs = 3000, CancellationToken ct = default)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            var ready = await RunOnFrameworkThread(() =>
            {
                unsafe
                {
                    if (!TryGetPlayerAddressAndIndexOnFrameworkByContentId(contentId, out var address, out _) || address == nint.Zero) return (bool?)null;
                    var model = ((GameObject*)address)->GetCharacterBase();
                    return (bool?)ForkIsSkeletonReady(model);
                }
            }).ConfigureAwait(false);
            if (ready != false) return ready == true;
            await WaitFrameworkTicksAsync(1, ct).ConfigureAwait(false);
        }
        _logger.LogDebug("Skeleton ready wait timed out for {cid} after {ms}ms, proceeding", contentId, timeoutMs);
        return false;
    }
}
