using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Microsoft.Extensions.Logging;

namespace MareSynchronos.Interop;

public unsafe class BlockedCharacterHandler
{
    private sealed record CharaData(ulong AccId, ulong ContentId);
    private readonly Dictionary<CharaData, bool> _blockedCharacterCache = new();
    // 1221: a status-0 (unknown yet) result is remembered for a short time so the native call isn't repeated every frame
    private readonly Dictionary<CharaData, long> _unknownUntil = new();
    private const long UnknownTtlMs = 2000;

    private readonly ILogger<BlockedCharacterHandler> _logger;

    public BlockedCharacterHandler(ILogger<BlockedCharacterHandler> logger, IGameInteropProvider gameInteropProvider)
    {
        gameInteropProvider.InitializeFromAttributes(this);
        _logger = logger;
    }

    private static CharaData GetIdsFromPlayerPointer(nint ptr)
    {
        if (ptr == nint.Zero) return new(0, 0);
        var castChar = ((BattleChara*)ptr);
        return new(castChar->Character.AccountId, castChar->Character.ContentId);
    }

    public bool IsCharacterBlocked(nint ptr, out bool firstTime)
    {
        firstTime = false;
        var combined = GetIdsFromPlayerPointer(ptr);
        if (_blockedCharacterCache.TryGetValue(combined, out var isBlocked))
            return isBlocked;

        long now = Environment.TickCount64;
        if (_unknownUntil.TryGetValue(combined, out var until))
        {
            if (now < until) return false;
            _unknownUntil.Remove(combined);
        }

        firstTime = true;
        var blockStatus = InfoProxyBlacklist.Instance()->GetBlockResultType(combined.AccId, combined.ContentId);
        _logger.LogTrace("CharaPtr {ptr} is BlockStatus: {status}", ptr, blockStatus);
        if ((int)blockStatus == 0)
        {
            if (_unknownUntil.Count > 2000) _unknownUntil.Clear();
            _unknownUntil[combined] = now + UnknownTtlMs;
            return false;
        }
        return _blockedCharacterCache[combined] = blockStatus != InfoProxyBlacklist.BlockResultType.NotBlocked;
    }
}
