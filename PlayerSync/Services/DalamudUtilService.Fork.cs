using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace MareSynchronos.Services;

// Fork additions (VierAI-fied): live object checks used by the crash fixes.
public partial class DalamudUtilService
{
    // A saved character address stays non-zero after a despawn or data-center hop.
    // Reading that memory is the access violation. Match the address against the live object table instead.
    public bool IsLiveObject(nint address) => LiveObjectIndex(address) >= 0;

    public unsafe bool TryGetEntityId(nint address, out uint entityId)
    {
        entityId = 0;
        if (LiveObjectIndex(address) < 0) return false;
        entityId = ((GameObject*)address)->EntityId;
        return true;
    }

    private int LiveObjectIndex(nint address)
    {
        if (address == 0) return -1;

        var length = _objectTable.Length;
        for (var i = 0; i < length; i++)
        {
            if (_objectTable.GetObjectAddress(i) == address)
                return i;
        }

        return -1;
    }
}
