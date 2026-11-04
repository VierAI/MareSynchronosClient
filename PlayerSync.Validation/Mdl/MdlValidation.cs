using System.Buffers.Binary;

namespace PlayerSync.Validation.Mdl;

/// <summary>
/// 1.15.5.1415 (VierAI): structural checks for .mdl (V5/V6) files before they are handed to Penumbra. The game's
/// ModelResourceHandle.Load trusts the counts and offsets in the file, so a corrupt string offset or buffer range can
/// crash it (C0000005). Layout follows MdlFile / MdlBoundsReader (Penumbra.GameData). Never throws.
/// </summary>
public static class MdlValidation
{
    public const uint V5 = 0x01000005;
    public const uint V6 = 0x01000006;

    private const int FileHeaderSize = 0x44;
    private const int VertexDeclarationSize = 17 * 8;
    private const int ModelHeaderSize = 56;
    private const int ElementIdSize = 32;
    private const int LodSize = 60;
    private const int ExtraLodSize = 40;
    private const int MeshSize = 36;
    private const int TerrainShadowMeshSize = 20;
    private const int SubmeshSize = 16;
    private const int TerrainShadowSubmeshSize = 12;
    private const int BoneTableV5Size = 132;
    private const int ShapeSize = 16;
    private const int ShapeMeshSize = 12;
    private const int ShapeValueSize = 4;

    public static readonly ValidationMessage MDL000A = new(nameof(MDL000A), "MDL file invalid", "The MDL file is too short or could not be read.", MessageLevel.Crash);
    public static readonly ValidationMessage MDL000B = new(nameof(MDL000B), "MDL version unsupported", "The MDL file version is not 5 or 6.", MessageLevel.Crash);
    public static readonly ValidationMessage MDL001A = new(nameof(MDL001A), "MDL header sizes invalid", "The MDL header sizes or section counts point past the end of the file header data.", MessageLevel.Crash);
    public static readonly ValidationMessage MDL002A = new(nameof(MDL002A), "MDL string table invalid", "The MDL string table is out of bounds or not terminated.", MessageLevel.Crash);
    public static readonly ValidationMessage MDL002B = new(nameof(MDL002B), "MDL string offset invalid", "An MDL attribute, bone, material or shape name points outside the string table.", MessageLevel.Crash);
    // Some valid files (seen in the wild) store something other than a string offset here, so this is only a warning.
    public static readonly ValidationMessage MDL002C = new(nameof(MDL002C), "MDL element bone name unusual", "An MDL element id parent bone name points outside the string table.", MessageLevel.Warning);
    public static readonly ValidationMessage MDL003A = new(nameof(MDL003A), "MDL LOD count invalid", "The MDL file has more than 3 LODs.", MessageLevel.Crash);
    public static readonly ValidationMessage MDL004A = new(nameof(MDL004A), "MDL buffer out of bounds", "An MDL vertex or index buffer lies outside the file.", MessageLevel.Crash);
    public static readonly ValidationMessage MDL005A = new(nameof(MDL005A), "MDL mesh ranges invalid", "An MDL LOD or mesh refers to meshes or submeshes that do not exist.", MessageLevel.Crash);
    public static readonly ValidationMessage MDL005B = new(nameof(MDL005B), "MDL material index invalid", "An MDL mesh refers to a material index that does not exist.", MessageLevel.Warning);
    public static readonly ValidationMessage MDL006A = new(nameof(MDL006A), "MDL tail unreadable", "The bone table / shape section of the MDL file could not be read; shape names were not checked.", MessageLevel.Warning);

    public static IEnumerable<ValidationMessage> ValidateMdlFile(byte[] fileData)
    {
        var results = new List<ValidationMessage>();
        try
        {
            Validate(fileData, results);
        }
        catch
        {
            results.Add(MDL000A);
        }
        return results.Distinct().ToArray();
    }

    private static ushort U16(ReadOnlySpan<byte> d, long pos) => BinaryPrimitives.ReadUInt16LittleEndian(d.Slice((int)pos, 2));
    private static uint U32(ReadOnlySpan<byte> d, long pos) => BinaryPrimitives.ReadUInt32LittleEndian(d.Slice((int)pos, 4));

    private static void Validate(ReadOnlySpan<byte> d, List<ValidationMessage> r)
    {
        long len = d.Length;
        if (len < FileHeaderSize) { r.Add(MDL000A); return; }

        var version = U32(d, 0);
        if (version != V5 && version != V6) { r.Add(MDL000B); return; }

        long stackSize = U32(d, 4);
        long runtimeSize = U32(d, 8);
        int vertexDeclarationCount = U16(d, 12);
        var lodCount = d[64];

        long headerEnd = FileHeaderSize + stackSize + runtimeSize;
        if (headerEnd > len) { r.Add(MDL001A); return; }
        if (lodCount > 3) { r.Add(MDL003A); return; }

        // Vertex / index buffers per LOD (absolute file offsets)
        for (int i = 0; i < lodCount; i++)
        {
            long vOff = U32(d, 16 + i * 4), iOff = U32(d, 28 + i * 4);
            long vSize = U32(d, 40 + i * 4), iSize = U32(d, 52 + i * 4);
            if ((vSize > 0 && (vOff < headerEnd || vOff + vSize > len)) || (iSize > 0 && (iOff < headerEnd || iOff + iSize > len)))
            {
                r.Add(MDL004A);
                break;
            }
        }

        long pos = FileHeaderSize + (long)vertexDeclarationCount * VertexDeclarationSize;
        if (pos + 8 > headerEnd) { r.Add(MDL001A); return; }

        // String table
        int stringCount = U16(d, pos);
        long stringSize = U32(d, pos + 4);
        pos += 8;
        if (pos + stringSize > headerEnd) { r.Add(MDL002A); return; }
        var strings = d.Slice((int)pos, (int)stringSize);
        {
            int start = 0;
            for (int i = 0; i < stringCount; i++)
            {
                if (start >= strings.Length) { r.Add(MDL002A); return; }
                int idx = strings[start..].IndexOf((byte)0);
                if (idx < 0) { r.Add(MDL002A); return; }
                start += idx + 1;
            }
        }
        pos += stringSize;

        bool StrOk(uint off, ReadOnlySpan<byte> s) => off < (uint)s.Length && s[(int)off..].IndexOf((byte)0) >= 0;
        bool Fits(long p, long size) => p + size <= headerEnd;

        // Model header
        if (!Fits(pos, ModelHeaderSize)) { r.Add(MDL001A); return; }
        var h = d.Slice((int)pos, ModelHeaderSize);
        int meshCount = U16(h, 4);
        int attributeCount = U16(h, 6);
        int submeshCount = U16(h, 8);
        int materialCount = U16(h, 10);
        int boneCount = U16(h, 12);
        int boneTableCount = U16(h, 14);
        int shapeCount = U16(h, 16);
        int shapeMeshCount = U16(h, 18);
        int shapeValueCount = U16(h, 20);
        int modelLodCount = h[22];
        int elementIdCount = U16(h, 24);
        int terrainShadowMeshCount = h[26];
        int flags2 = h[27];
        int terrainShadowSubmeshCount = U16(h, 38);
        int boneTableArrayCountTotal = U16(h, 44);
        pos += ModelHeaderSize;
        if (modelLodCount > 3) { r.Add(MDL003A); return; }

        // Element ids: parent bone name is a string offset
        if (!Fits(pos, (long)elementIdCount * ElementIdSize)) { r.Add(MDL001A); return; }
        for (int i = 0; i < elementIdCount; i++)
            if (!StrOk(U32(d, pos + i * ElementIdSize + 4), strings)) { r.Add(MDL002C); break; }
        pos += (long)elementIdCount * ElementIdSize;

        // LODs (always 3 stored)
        if (!Fits(pos, 3L * LodSize)) { r.Add(MDL001A); return; }
        int usedLods = Math.Max(lodCount, modelLodCount);
        for (int i = 0; i < usedLods; i++)
        {
            int meshIndex = U16(d, pos + i * LodSize);
            int lodMeshCount = U16(d, pos + i * LodSize + 2);
            if (meshIndex + lodMeshCount > meshCount) { r.Add(MDL005A); break; }
        }
        pos += 3L * LodSize;
        if ((flags2 & 0x10) != 0) pos += 3L * ExtraLodSize;

        // Meshes
        if (!Fits(pos, (long)meshCount * MeshSize)) { r.Add(MDL001A); return; }
        for (int i = 0; i < meshCount; i++)
        {
            long m = pos + (long)i * MeshSize;
            int materialIndex = U16(d, m + 8);
            int subIndex = U16(d, m + 10);
            int subCount = U16(d, m + 12);
            if (subIndex + subCount > submeshCount) { r.Add(MDL005A); }
            if (materialIndex >= materialCount && materialCount > 0) { r.Add(MDL005B); }
        }
        pos += (long)meshCount * MeshSize;

        // Attribute names
        if (!Fits(pos, attributeCount * 4L)) { r.Add(MDL001A); return; }
        for (int i = 0; i < attributeCount; i++)
            if (!StrOk(U32(d, pos + i * 4L), strings)) { r.Add(MDL002B); break; }
        pos += attributeCount * 4L;

        pos += terrainShadowMeshCount * (long)TerrainShadowMeshSize + submeshCount * (long)SubmeshSize + terrainShadowSubmeshCount * (long)TerrainShadowSubmeshSize;

        // Material names
        if (!Fits(pos, materialCount * 4L)) { r.Add(MDL001A); return; }
        for (int i = 0; i < materialCount; i++)
            if (!StrOk(U32(d, pos + i * 4L), strings)) { r.Add(MDL002B); break; }
        pos += materialCount * 4L;

        // Bone names
        if (!Fits(pos, boneCount * 4L)) { r.Add(MDL001A); return; }
        for (int i = 0; i < boneCount; i++)
            if (!StrOk(U32(d, pos + i * 4L), strings)) { r.Add(MDL002B); break; }
        pos += boneCount * 4L;

        // Tail: bone tables, then shapes. Layout varies a little between writers, so problems here are warnings only.
        pos += version == V5 ? boneTableCount * (long)BoneTableV5Size : boneTableCount * 4L + boneTableArrayCountTotal * 2L;
        if (!Fits(pos, shapeCount * (long)ShapeSize + shapeMeshCount * (long)ShapeMeshSize + shapeValueCount * (long)ShapeValueSize))
        {
            r.Add(MDL006A);
            return;
        }
        for (int i = 0; i < shapeCount; i++)
            if (!StrOk(U32(d, pos + i * (long)ShapeSize), strings)) { r.Add(MDL002B); break; }
    }
}
