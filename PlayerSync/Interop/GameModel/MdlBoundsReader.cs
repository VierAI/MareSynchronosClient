using System.Text;

namespace MareSynchronos.Interop.GameModel;

/// <summary>
/// Fork (texture size cap): reads only the model bounding box and the material names from a .mdl (V5 and V6),
/// following the same layout as <see cref="MdlFile"/> / Penumbra.GameData's MdlFile but skipping everything else.
/// Units are game units (1.0 = 1 metre). Returns null if the file can't be read or looks inconsistent.
/// </summary>
public static class MdlBoundsReader
{
    public sealed record Result(float MaxDimension, string[] Materials);

    private const uint V5 = 0x01000005;
    private const uint V6 = 0x01000006;
    private const int FileHeaderSize = 0x44;

    public static Result? TryRead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var r = new BinaryReader(fs);
            var version = r.ReadUInt32();
            if (version != V5 && version != V6) return null;
            var stackSize = r.ReadUInt32();
            var runtimeSize = r.ReadUInt32();
            var vertexDeclarationCount = r.ReadUInt16();
            long headerEnd = FileHeaderSize + (long)stackSize + runtimeSize;
            if (headerEnd > fs.Length) return null;

            fs.Position = FileHeaderSize + vertexDeclarationCount * 17L * 8;

            var stringCount = r.ReadUInt16();
            r.ReadUInt16();
            var stringSize = (int)r.ReadUInt32();
            if (stringSize < 0 || fs.Position + stringSize > headerEnd) return null;
            var stringData = r.ReadBytes(stringSize);
            var strings = new Dictionary<uint, string>();
            var start = 0;
            for (var i = 0; i < stringCount && start < stringData.Length; i++)
            {
                var idx = Array.IndexOf(stringData, (byte)0, start);
                if (idx < 0) idx = stringData.Length;
                strings[(uint)start] = Encoding.UTF8.GetString(stringData, start, idx - start);
                start = idx + 1;
            }

            // ModelHeader (56 bytes), see MdlFile.ModelHeader
            var h = r.ReadBytes(56);
            if (h.Length < 56) return null;
            int meshCount = BitConverter.ToUInt16(h, 4);
            int attributeCount = BitConverter.ToUInt16(h, 6);
            int submeshCount = BitConverter.ToUInt16(h, 8);
            int materialCount = BitConverter.ToUInt16(h, 10);
            int boneCount = BitConverter.ToUInt16(h, 12);
            int boneTableCount = BitConverter.ToUInt16(h, 14);
            int shapeCount = BitConverter.ToUInt16(h, 16);
            int shapeMeshCount = BitConverter.ToUInt16(h, 18);
            int shapeValueCount = BitConverter.ToUInt16(h, 20);
            int elementIdCount = BitConverter.ToUInt16(h, 24);
            int terrainShadowMeshCount = h[26];
            int flags2 = h[27];
            int terrainShadowSubmeshCount = BitConverter.ToUInt16(h, 38);
            int boneTableArrayCountTotal = BitConverter.ToUInt16(h, 44);

            fs.Position += elementIdCount * 32L + 3 * 60L; // element ids, 3 LODs
            if ((flags2 & 0x10) != 0) fs.Position += 3 * 40L; // extra LODs
            fs.Position += meshCount * 36L + attributeCount * 4L + terrainShadowMeshCount * 20L + submeshCount * 16L + terrainShadowSubmeshCount * 12L;
            if (fs.Position + materialCount * 4L > headerEnd) return null;

            var materials = new string[materialCount];
            for (var i = 0; i < materialCount; i++)
                materials[i] = strings.TryGetValue(r.ReadUInt32(), out var s) ? s : string.Empty;

            fs.Position += boneCount * 4L;
            fs.Position += version == V5 ? boneTableCount * 132L : boneTableCount * 4L + boneTableArrayCountTotal * 2L;
            fs.Position += shapeCount * 16L + shapeMeshCount * 12L + shapeValueCount * 4L;
            if (fs.Position + 4 > headerEnd) return null;
            var submeshBoneMapSize = r.ReadUInt32();
            fs.Position += submeshBoneMapSize;
            if (fs.Position + 1 > headerEnd) return null;
            var padding = r.ReadByte();
            fs.Position += padding;
            fs.Position += 32; // first bounding box (unused)
            if (fs.Position + 32 > headerEnd) return null;

            // ModelBoundingBoxes: min xyzw, max xyzw
            var min = new float[4];
            var max = new float[4];
            for (var i = 0; i < 4; i++) min[i] = r.ReadSingle();
            for (var i = 0; i < 4; i++) max[i] = r.ReadSingle();

            float dim = 0;
            for (var i = 0; i < 3; i++)
            {
                if (!float.IsFinite(min[i]) || !float.IsFinite(max[i])) return null;
                var d = max[i] - min[i];
                if (d < 0 || d > 1000) return null;
                dim = Math.Max(dim, d);
            }

            return new Result(dim, materials);
        }
        catch
        {
            return null;
        }
    }
}
