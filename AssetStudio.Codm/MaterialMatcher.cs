using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AssetStudio.Codm
{
    /// <summary>
    /// CoD Mobile renderers carry no usable material reference: the game picks the skin or camo material
    /// at runtime (the base DL Q33 bundle holds one material per camo), and the stored reference points at
    /// path id 0 of an unrelated bundle. Materials are found by name instead, as part + skin:
    /// <c>Wea01_DLQ33BlackNoirBLP_1P</c> takes <c>Wea01_DLQ33BlackNoirBLP_mtl</c>, <c>DLQ33Black_1P</c>
    /// takes <c>wea_DLQ33Black_mtl</c>, <c>Wea01_DL_Q33_UI_lod0</c> takes <c>wea_DLQ33_mtl</c>. Parts without
    /// a material of their own (<c>Bar01_...</c>, <c>MAG01_...</c>) share a sheet of the skin: parts packed
    /// on one sheet take separate regions of it, so the sheet whose own mesh (<c>Rai01_...</c> for the
    /// <c>Rai01</c> sheet) leaves the part's UV area free is the one (the Black Noir magazine sits on the
    /// rail sheet); when no sheet is clearly free, the skin's <c>wea</c> material. Further submeshes take the
    /// material's variants (<c>..._mtl_SightPupil</c>). Only the colour map (<c>_MainTex</c>) is exported,
    /// written once into the texture folder; the models reference it there.
    /// </summary>
    public sealed class MaterialMatcher
    {
        private sealed class Entry
        {
            public Material Material;
            public string Part;
            public string Skin;
            public string Variant;
            public bool HasMtl;
            public Texture2D ColorMap;
        }

        private static readonly Regex PartToken = new Regex(@"^([a-z]+)\d*$", RegexOptions.Compiled);
        private static readonly Regex RoleToken = new Regex(@"^(1p|3p|ui|br|lod\d+)$", RegexOptions.Compiled);
        private static readonly Dictionary<string, string> PartSynonyms = new Dictionary<string, string> { { "sig", "sco" }, { "sco", "sig" } };

        /// <summary>UV grid for the sheet test, and the share of a part's UV area another mesh may cover on its sheet.</summary>
        private const int Grid = 128;
        private const double MaxOverlap = 0.1;

        private readonly Dictionary<string, List<Entry>> bySkin = new Dictionary<string, List<Entry>>();
        /// <summary>Per "part|skin": the mesh with the most vertices (the sheet's own mesh for a material of that part).</summary>
        private readonly Dictionary<string, Mesh> meshByPart = new Dictionary<string, Mesh>();
        private readonly Dictionary<Mesh, bool[]> ownerMasks = new Dictionary<Mesh, bool[]>();
        private readonly Dictionary<Material, ImportedMaterial> converted = new Dictionary<Material, ImportedMaterial>();
        private readonly Dictionary<string, ImportedMaterial> byMesh = new Dictionary<string, ImportedMaterial>();
        private readonly Dictionary<Texture2D, string> written = new Dictionary<Texture2D, string>();
        private readonly Dictionary<string, string> writtenNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly string textureDir;
        private readonly ImageFormat format;

        /// <summary>Colour maps written so far (full paths).</summary>
        public IEnumerable<string> WrittenTextures => writtenNames.Values;
        /// <summary>Meshes (name, submesh) for which no material was found.</summary>
        public SortedSet<string> Unmatched { get; } = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Parts (name without role and LOD suffix) whose sheet was chosen by UV layout, with that material.</summary>
        public SortedDictionary<string, string> LayoutChoices { get; } = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public MaterialMatcher(AssetsManager am, string textureDir, ImageFormat format)
        {
            this.textureDir = Path.GetFullPath(textureDir);
            this.format = format;
            foreach (var file in am.assetsFileList)
            {
                foreach (var mesh in file.Objects.OfType<Mesh>())
                {
                    var (part, skin) = Splits(MeshTokens(mesh.m_Name)).FirstOrDefault();
                    if (skin == null)
                        continue;
                    var key = part + "|" + skin;
                    if (!meshByPart.TryGetValue(key, out var best) || best.m_VertexCount < mesh.m_VertexCount)
                        meshByPart[key] = mesh;
                }
                foreach (var m in file.Objects.OfType<Material>())
                {
                    var toks = Tokens(m.m_Name);
                    var mtl = toks.IndexOf("mtl");
                    var baseToks = mtl >= 0 ? toks.Take(mtl).ToList() : toks;
                    while (baseToks.Count > 1 && RoleToken.IsMatch(baseToks[baseToks.Count - 1]))
                        baseToks.RemoveAt(baseToks.Count - 1);
                    foreach (var (part, skin) in Splits(baseToks))
                    {
                        if (!bySkin.TryGetValue(skin, out var list))
                            bySkin[skin] = list = new List<Entry>();
                        list.Add(new Entry
                        {
                            Material = m,
                            Part = part,
                            Skin = skin,
                            Variant = mtl >= 0 ? string.Join("_", toks.Skip(mtl + 1)) : "",
                            HasMtl = mtl >= 0,
                            ColorMap = ColorMapOf(m),
                        });
                    }
                }
            }
        }

        /// <summary>The material of one submesh, or null when nothing fits.</summary>
        public ImportedMaterial Resolve(Mesh mesh, int submesh)
        {
            var key = mesh.m_Name + "#" + submesh;
            if (byMesh.TryGetValue(key, out var cached))
                return cached;
            ImportedMaterial result = null;
            foreach (var (part, skin) in Splits(MeshTokens(mesh.m_Name)))
            {
                if (!bySkin.TryGetValue(skin, out var list))
                    continue;
                var primary = Pick(list, e => e.Part == part)
                    ?? Pick(list, e => part.StartsWith(e.Part) || e.Part.StartsWith(part)
                        || (PartSynonyms.TryGetValue(part, out var syn) && e.Part == syn));
                if (primary == null)
                {
                    primary = ByLayout(list, mesh, submesh) ?? Pick(list, e => e.Part == "wea");
                    if (primary == null)
                        continue;
                }
                else if (submesh > 0)
                {
                    var variants = list.Where(e => e.Part == primary.Part && e.HasMtl && e.Variant.Length > 0)
                        .GroupBy(e => e.Material.m_Name, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(g => Pick(g.ToList(), _ => true)).ToList();
                    if (submesh - 1 < variants.Count)
                        primary = variants[submesh - 1];
                }
                result = Convert(primary);
                break;
            }
            if (result == null)
                Unmatched.Add(submesh == 0 ? mesh.m_Name : $"{mesh.m_Name} (submesh {submesh})");
            byMesh[key] = result;
            return result;
        }

        /// <summary>Best of the entries that satisfy the filter: plain material over variants, _mtl names, a colour map that resolves.</summary>
        private static Entry Pick(List<Entry> list, Func<Entry, bool> filter)
        {
            return list.Where(filter)
                .OrderBy(e => e.Variant.Length > 0)
                .ThenBy(e => !e.HasMtl)
                .ThenBy(e => e.ColorMap == null)
                .FirstOrDefault();
        }

        /// <summary>
        /// The skin's sheet (plain material with a colour map) whose own mesh covers the least of this
        /// submesh's UV area, when that share is small enough to mean "packed next to it".
        /// </summary>
        private Entry ByLayout(List<Entry> list, Mesh mesh, int submesh)
        {
            var part = UvMask(mesh, submesh);
            var area = part?.Count(x => x) ?? 0;
            if (area == 0)
                return null;
            Entry best = null;
            var bestShare = double.MaxValue;
            foreach (var sheet in list.Where(e => e.HasMtl && e.Variant.Length == 0 && e.ColorMap != null).GroupBy(e => e.ColorMap))
            {
                var e = Pick(sheet.ToList(), _ => true);
                if (!meshByPart.TryGetValue(e.Part + "|" + e.Skin, out var owner) || owner == mesh)
                    continue;
                if (!ownerMasks.TryGetValue(owner, out var mask))
                    ownerMasks[owner] = mask = UvMask(owner, -1);
                if (mask == null)
                    continue;
                var covered = 0;
                for (var i = 0; i < part.Length; i++)
                    if (part[i] && mask[i]) covered++;
                var share = (double)covered / area;
                if (share < bestShare)
                {
                    bestShare = share;
                    best = e;
                }
            }
            if (best == null || bestShare > MaxOverlap)
                return null;
            var partName = string.Join("_", (mesh.m_Name ?? "").Trim().Split('_').Take(MeshTokens(mesh.m_Name).Count));
            LayoutChoices[submesh > 0 ? $"{partName} (submesh {submesh})" : partName] = best.Material.m_Name;
            return best;
        }

        /// <summary>Grid cells covered by the UV0 triangles of one submesh (all with -1); null without UVs.</summary>
        private static bool[] UvMask(Mesh mesh, int onlySubmesh)
        {
            var uv = mesh.m_UV0;
            if (uv == null || mesh.m_VertexCount == 0 || mesh.m_SubMeshes == null)
                return null;
            var dim = uv.Length / mesh.m_VertexCount;
            if (dim < 2)
                return null;
            var mask = new bool[Grid * Grid];
            var firstFace = 0;
            for (var s = 0; s < mesh.m_SubMeshes.Length; s++)
            {
                var faces = (int)mesh.m_SubMeshes[s].indexCount / 3;
                if (onlySubmesh < 0 || s == onlySubmesh)
                {
                    for (var f = firstFace; f < firstFace + faces && f * 3 + 2 < mesh.m_Indices.Count; f++)
                    {
                        var a = (int)mesh.m_Indices[f * 3];
                        var b = (int)mesh.m_Indices[f * 3 + 1];
                        var c = (int)mesh.m_Indices[f * 3 + 2];
                        if (Math.Max(a, Math.Max(b, c)) >= mesh.m_VertexCount)
                            continue;
                        FillTriangle(mask, uv[a * dim], uv[a * dim + 1], uv[b * dim], uv[b * dim + 1], uv[c * dim], uv[c * dim + 1]);
                    }
                }
                firstFace += faces;
            }
            return mask;
        }

        private static void FillTriangle(bool[] mask, float u0, float v0, float u1, float v1, float u2, float v2)
        {
            // tiled UVs: move the triangle into the unit square by its lower corner
            var du = Math.Floor(Math.Min(u0, Math.Min(u1, u2)));
            var dv = Math.Floor(Math.Min(v0, Math.Min(v1, v2)));
            double x0 = (u0 - du) * Grid, y0 = (v0 - dv) * Grid, x1 = (u1 - du) * Grid, y1 = (v1 - dv) * Grid, x2 = (u2 - du) * Grid, y2 = (v2 - dv) * Grid;
            var d = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2);
            if (Math.Abs(d) < 1e-12)
                return;
            var minX = Math.Max(0, (int)Math.Min(x0, Math.Min(x1, x2)));
            var maxX = Math.Min(Grid - 1, (int)Math.Max(x0, Math.Max(x1, x2)));
            var minY = Math.Max(0, (int)Math.Min(y0, Math.Min(y1, y2)));
            var maxY = Math.Min(Grid - 1, (int)Math.Max(y0, Math.Max(y1, y2)));
            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    double px = x + 0.5, py = y + 0.5;
                    var a = ((y1 - y2) * (px - x2) + (x2 - x1) * (py - y2)) / d;
                    var b = ((y2 - y0) * (px - x2) + (x0 - x2) * (py - y2)) / d;
                    if (a >= -0.01 && b >= -0.01 && a + b <= 1.01)
                        mask[y * Grid + x] = true;
                }
            }
        }

        private ImportedMaterial Convert(Entry e)
        {
            if (converted.TryGetValue(e.Material, out var mat))
                return mat;
            mat = new ImportedMaterial
            {
                Name = e.Material.m_Name,
                Diffuse = new Color(0.8f, 0.8f, 0.8f, 1),
                Ambient = new Color(0.2f, 0.2f, 0.2f, 1),
                Emissive = new Color(0, 0, 0, 1),
                Specular = new Color(0.2f, 0.2f, 0.2f, 1),
                Reflection = new Color(0, 0, 0, 1),
                Shininess = 20f,
                Textures = new List<ImportedMaterialTexture>(),
            };
            var path = e.ColorMap != null ? Write(e.ColorMap) : null;
            if (path != null)
                mat.Textures.Add(new ImportedMaterialTexture { Name = path, Dest = 0, Scale = new Vector2(1, 1) });
            converted[e.Material] = mat;
            return mat;
        }

        /// <summary>Write a colour map once (same file name as the texture export) and return its full path.</summary>
        private string Write(Texture2D tex)
        {
            if (written.TryGetValue(tex, out var path))
                return path;
            var fileName = CodmExportService.SafeName(tex.m_Name) + "." + format.ToString().ToLowerInvariant();
            if (!writtenNames.TryGetValue(fileName, out path))
            {
                path = Path.Combine(textureDir, fileName);
                try
                {
                    using (var image = tex.ConvertToImage(true))
                    {
                        if (image == null)
                            path = null;
                        else
                        {
                            Directory.CreateDirectory(textureDir);
                            using (var fs = File.Create(path))
                                image.WriteToStream(fs, format);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[Materials] colour map {tex.m_Name}: {ex.Message}");
                    path = null;
                }
                if (path != null)
                    writtenNames[fileName] = path;
            }
            written[tex] = path;
            return path;
        }

        private static Texture2D ColorMapOf(Material m)
        {
            foreach (var env in m.m_SavedProperties.m_TexEnvs)
                if (env.Key == "_MainTex" && env.Value.m_Texture.TryGet<Texture2D>(out var tex))
                    return tex;
            return null;
        }

        private static List<string> Tokens(string name)
        {
            return (name ?? "").Trim().ToLowerInvariant().Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        /// <summary>Tokens of a mesh name without the role and LOD suffix (_1P, _3P_lod0, _UI_lod0, _3P_BR_lod0).</summary>
        private static List<string> MeshTokens(string name)
        {
            var toks = Tokens(name);
            while (toks.Count > 1 && RoleToken.IsMatch(toks[toks.Count - 1]))
                toks.RemoveAt(toks.Count - 1);
            return toks;
        }

        /// <summary>
        /// Ways to read tokens as part + skin: a leading part token (wea01, bar01, addgrip01, glass) followed
        /// by the skin, and the whole name as the skin of a "wea" part (DLQ33Black, AK_117).
        /// </summary>
        private static IEnumerable<(string part, string skin)> Splits(List<string> toks)
        {
            if (toks.Count == 0)
                yield break;
            var whole = string.Concat(toks);
            if (toks.Count > 1)
            {
                var m = PartToken.Match(toks[0]);
                if (m.Success)
                {
                    var skin = string.Concat(toks.Skip(1));
                    yield return (m.Groups[1].Value, skin);
                    if (skin == whole)
                        yield break;
                }
            }
            yield return ("wea", whole);
        }
    }
}
