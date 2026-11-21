using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace MareSynchronos.PlayerData.Pairs;

/// <summary>
/// 1.15.6.215 (VierAI): reads a drawn character's body, legs, head and face models to see whether one is missing or failed to
/// load (a Penumbra load failure leaves the slot empty or the material in a failed state, so a part of the body is invisible).
/// Framework thread only. Read-only: it never changes anything in the game.
/// </summary>
internal static unsafe class PSyncModelSlotCheck
{
    internal enum SlotState { Ok, NoDrawObject, Loading, Missing }

    // Human model slots: 0 head, 1 body, 3 legs, 11 face.
    // 1.15.6.217: an empty material slot is normal (the 215/216 check counted it as missing and fired on healthy pairs). Only a
    // model or material whose resource is in a failed load state counts, and an empty body/legs/face slot only when the pair's
    // applied data has a model for that slot (see SlotsFromGamePaths). The head may be empty on purpose (hidden headgear).
    private static readonly (int Slot, string Name)[] Slots =
    [
        (0, "head"),
        (1, "body"),
        (3, "legs"),
        (11, "face"),
    ];

    internal const int HeadBit = 1 << 0, BodyBit = 1 << 1, LegsBit = 1 << 3, FaceBit = 1 << 11;

    // Resource LoadState: 7 = loaded; 9 and above (except 0xFF = none) are failures (Failure, FailedSubResource, ...).
    private static bool IsFailed(byte state) => state >= 9 && state != 0xFF;

    /// <summary>Slot bits (body, legs, face) for which the applied data replaces a model.</summary>
    internal static int SlotsFromGamePaths(IEnumerable<string> gamePaths)
    {
        var bits = 0;
        foreach (var raw in gamePaths)
        {
            if (!raw.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) continue;
            var p = raw.Replace('\\', '/');
            if (p.EndsWith("_top.mdl", StringComparison.OrdinalIgnoreCase)) bits |= BodyBit;
            else if (p.EndsWith("_dwn.mdl", StringComparison.OrdinalIgnoreCase)) bits |= LegsBit;
            else if (p.EndsWith("_fac.mdl", StringComparison.OrdinalIgnoreCase) || p.Contains("/obj/face/", StringComparison.OrdinalIgnoreCase)) bits |= FaceBit;
        }
        return bits;
    }

    internal static SlotState Check(nint address, int requiredSlotBits, out string detail)
    {
        detail = string.Empty;
        if (address == nint.Zero) return SlotState.NoDrawObject;
        var gameObject = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)address;
        if (gameObject->DrawObject == null) return SlotState.NoDrawObject;
        var model = gameObject->GetCharacterBase();
        if (model == null) return SlotState.NoDrawObject;
        if (model->GetModelType() != CharacterBase.ModelType.Human) return SlotState.Ok;
        if (model->HasModelInSlotLoaded != 0 || model->HasModelFilesInSlotLoaded != 0) return SlotState.Loading;
        if (model->Models == null) return SlotState.NoDrawObject;

        foreach (var (slot, name) in Slots)
        {
            if (slot >= model->SlotCount) continue;
            var part = model->Models[slot];
            if (part == null)
            {
                if ((requiredSlotBits & (1 << slot)) != 0) { detail = name + " model missing"; return SlotState.Missing; }
                continue;
            }

            var mdl = part->ModelResourceHandle;
            if (mdl != null && IsFailed((byte)mdl->LoadState)) { detail = name + " model failed to load"; return SlotState.Missing; }

            if (part->Materials == null) continue;
            for (var i = 0; i < part->MaterialCount; i++)
            {
                var material = part->Materials[i];
                if (material == null) continue; // empty material slot: normal
                var handle = material->MaterialResourceHandle;
                if (handle != null && IsFailed((byte)handle->LoadState)) { detail = name + " material " + i + " failed to load"; return SlotState.Missing; }
            }
        }

        return SlotState.Ok;
    }
}
