using MareSynchronos.FileCache;
using MareSynchronos.Interop.GameModel;
using System.Collections.Concurrent;
using System.Text;

namespace MareSynchronos.Services.TextureCap;

/// <summary>
/// Fork (texture size cap): decides a size cap for each texture in a pair's file set from the physical size of the
/// models it is drawn on. It follows the pair's own replacements: .mdl (bounding box + material names) -> .mtrl
/// (texture paths) -> .tex. Slot and path are never used to pick a cap, because model swaps put any model on any path.
/// A texture gets the cap of the LARGEST model using it. Textures that can't be tied to a modded model (their material
/// isn't referenced by any modded model in the set) are left alone, as are index/ID maps, VFX/UI and shared textures.
/// Parsed models and materials are cached by file hash.
/// </summary>
public sealed class TextureSizeClassifier
{
    public sealed record MtrlInfo(string ShaderPackage, string[] TexturePaths);

    /// <summary>One texture file. <see cref="Cap"/> 0 means no cap.</summary>
    public sealed record TextureDecision(string Hash, string FilePath, int Cap, float LargestModelMetres, bool Eye, string Reason);

    // Below this the bounding box is treated as missing (some tools write zero boxes).
    private const float MinValidDimension = 0.0005f;

    private readonly ConcurrentDictionary<string, MdlBoundsReader.Result?> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MtrlInfo?> _materials = new(StringComparer.OrdinalIgnoreCase);

    public int CachedModels => _models.Count;
    public int CachedMaterials => _materials.Count;

    public static string? HashOf(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        return FileCacheManager.IsValidFileHash(name) ? name.ToUpperInvariant() : null;
    }

    public static int CapFor(float metres, VierAIConfig c)
    {
        var cm = metres * 100f;
        if (cm <= c.TextureCapTinyMaxCm) return c.TextureCapTinyPx;
        if (cm <= c.TextureCapSmallMaxCm) return c.TextureCapSmallPx;
        if (cm <= c.TextureCapMediumMaxCm) return c.TextureCapMediumPx;
        return 0;
    }

    /// <summary>Index/ID maps, VFX/UI, shared common textures and dummies are never resized.</summary>
    public static bool IsProtected(string texGamePath)
    {
        var p = texGamePath.Replace('\\', '/').ToLowerInvariant();
        if (p.StartsWith("vfx/", StringComparison.Ordinal) || p.StartsWith("ui/", StringComparison.Ordinal)
            || p.StartsWith("bg/", StringComparison.Ordinal) || p.StartsWith("bgcommon/", StringComparison.Ordinal)
            || p.Contains("/vfx/", StringComparison.Ordinal) || p.StartsWith("chara/common/texture/", StringComparison.Ordinal))
            return true;
        var name = Path.GetFileNameWithoutExtension(p);
        return name.EndsWith("_id", StringComparison.Ordinal) || name.Contains("_id_", StringComparison.Ordinal)
            || name.Contains("index", StringComparison.Ordinal) || name.Contains("dummy", StringComparison.Ordinal);
    }

    public static bool IsEye(string texGamePath, string shaderPackage)
    {
        var p = texGamePath.Replace('\\', '/').ToLowerInvariant();
        return shaderPackage.StartsWith("iris", StringComparison.OrdinalIgnoreCase)
            || p.Contains("_iri", StringComparison.Ordinal) || p.Contains("catchlight", StringComparison.Ordinal)
            || p.Contains("/eye/", StringComparison.Ordinal) || p.Contains("_eye", StringComparison.Ordinal);
    }

    public static MtrlInfo? ReadMtrl(string path)
    {
        try
        {
            var b = File.ReadAllBytes(path);
            if (b.Length < 16) return null;
            int stringTableSize = BitConverter.ToUInt16(b, 8);
            int shaderOffset = BitConverter.ToUInt16(b, 10);
            int textureCount = b[12], uvSetCount = b[13], colorSetCount = b[14];
            var stringStart = 16 + 4 * (textureCount + uvSetCount + colorSetCount);
            if (stringStart + stringTableSize > b.Length) return null;

            string Str(int offset)
            {
                var s = stringStart + offset;
                if (offset < 0 || offset >= stringTableSize) return string.Empty;
                var end = Array.IndexOf(b, (byte)0, s, stringStart + stringTableSize - s);
                if (end < 0) end = stringStart + stringTableSize;
                return Encoding.UTF8.GetString(b, s, end - s);
            }

            var textures = new string[textureCount];
            for (var i = 0; i < textureCount; i++)
                textures[i] = Str(BitConverter.ToUInt16(b, 16 + 4 * i));
            return new MtrlInfo(Str(shaderOffset), textures);
        }
        catch
        {
            return null;
        }
    }

    public List<TextureDecision> Classify(IReadOnlyDictionary<string, string> gamePathToFile, VierAIConfig cfg, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in gamePathToFile) map[kv.Key.Replace('\\', '/')] = kv.Value;

        var mtrlByName = map.Keys.Where(k => k.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
            .GroupBy(k => k[(k.LastIndexOf('/') + 1)..], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // 1) material game path -> largest model using it
        var mtrlLargest = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var (gamePath, file) in map)
        {
            ct.ThrowIfCancellationRequested();
            if (!gamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) continue;
            var hash = HashOf(file);
            if (hash == null) continue;
            var model = _models.GetOrAdd(hash, _ => MdlBoundsReader.TryRead(file));
            if (model == null || model.MaxDimension < MinValidDimension) continue;
            foreach (var material in model.Materials)
            {
                if (string.IsNullOrEmpty(material)) continue;
                IEnumerable<string> targets = material.StartsWith('/')
                    ? (mtrlByName.TryGetValue(material.TrimStart('/'), out var list) ? list : [])
                    : (map.ContainsKey(material) ? [material] : []);
                foreach (var t in targets)
                    mtrlLargest[t] = Math.Max(mtrlLargest.GetValueOrDefault(t), model.MaxDimension);
            }
        }

        // 2) texture game path -> largest model, blocked, eye
        var texLargest = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var texReason = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var eyes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (gamePath, file) in map)
        {
            ct.ThrowIfCancellationRequested();
            if (!gamePath.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase)) continue;
            var hash = HashOf(file);
            if (hash == null) continue;
            var mtrl = _materials.GetOrAdd(hash, _ => ReadMtrl(file));
            if (mtrl == null) continue;
            var owned = mtrlLargest.TryGetValue(gamePath, out var dim);
            foreach (var texPath in mtrl.TexturePaths)
            {
                if (string.IsNullOrEmpty(texPath)) continue;
                var normal = texPath.Replace('\\', '/');
                var slash = normal.LastIndexOf('/');
                var dx11 = slash >= 0 ? normal[..(slash + 1)] + "--" + normal[(slash + 1)..] : "--" + normal;
                foreach (var candidate in new[] { normal, dx11 })
                {
                    if (!map.ContainsKey(candidate)) continue;
                    if (!owned) { blocked.Add(candidate); texReason[candidate] = "material not used by a modded model"; continue; }
                    if (IsProtected(candidate)) { blocked.Add(candidate); texReason[candidate] = "index/ID, VFX/UI or shared texture"; continue; }
                    texLargest[candidate] = Math.Max(texLargest.GetValueOrDefault(candidate), dim);
                    if (IsEye(candidate, mtrl.ShaderPackage)) eyes.Add(candidate);
                }
            }
        }

        // 3) per texture file (several game paths can share one file); any blocked path blocks the file
        var result = new List<TextureDecision>();
        foreach (var group in map.Where(kv => kv.Key.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)
                     && (texLargest.ContainsKey(kv.Key) || blocked.Contains(kv.Key)))
                 .GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase))
        {
            var hash = HashOf(group.Key);
            if (hash == null) continue;
            var paths = group.Select(g => g.Key).ToList();
            var blockedPath = paths.FirstOrDefault(blocked.Contains);
            if (blockedPath != null)
            {
                result.Add(new TextureDecision(hash, group.Key, 0, 0, false, texReason.GetValueOrDefault(blockedPath, "blocked")));
                continue;
            }
            var largest = paths.Max(p => texLargest[p]);
            var eye = paths.Exists(eyes.Contains);
            var cap = CapFor(largest, cfg);
            if (cap > 0 && eye && cap < cfg.TextureCapEyeFloorPx) cap = cfg.TextureCapEyeFloorPx;
            result.Add(new TextureDecision(hash, group.Key, cap, largest, eye, cap > 0 ? "capped by model size" : "model too large for a cap"));
        }

        return result;
    }
}
