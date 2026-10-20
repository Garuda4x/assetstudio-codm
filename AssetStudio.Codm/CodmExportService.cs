using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetStudio.Codm.Wwise;
using Newtonsoft.Json;

namespace AssetStudio.Codm
{
    public sealed class WeaponExportOptions
    {
        public bool Models { get; set; } = true;
        public bool Textures { get; set; } = true;
        public bool Sounds { get; set; } = true;
        public bool Effects { get; set; } = false;
        public bool IncludeBaseWeapon { get; set; } = true;
        public bool IncludeComponents { get; set; } = false;
        public bool ConvertWem { get; set; } = true;
        /// <summary>Group wems by the event that plays them (folders named after the resolved event name).</summary>
        public bool SoundsByEvent { get; set; } = true;
        /// <summary>Load the bundles the weapon paks reference (shared animations, controllers, avatars).</summary>
        public bool ResolveDependencies { get; set; } = true;
        public int MaxDependencyPaks { get; set; } = 60;
        /// <summary>Export every copy of a prefab that appears in several bundles instead of the first one.</summary>
        public bool AllPrefabCopies { get; set; } = false;
    }

    public sealed class WeaponExportReport
    {
        public string Weapon { get; set; }
        public string OutputDir { get; set; }
        public List<string> PaksLoaded { get; } = new List<string>();
        public List<string> DependencyPaks { get; } = new List<string>();
        public List<string> UnresolvedReferences { get; } = new List<string>();
        public List<string> Models { get; } = new List<string>();
        public List<string> SkippedPrefabCopies { get; } = new List<string>();
        public List<string> Textures { get; } = new List<string>();
        public List<string> Banks { get; } = new List<string>();
        public List<string> Effects { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public int WemsExtracted { get; set; }
        public int WemsConverted { get; set; }
    }

    /// <summary>
    /// Headless export of everything the catalog knows about a weapon: models (Cast), textures,
    /// sounds and optionally FX textures. Shared by the CLI and the GUI browser.
    /// </summary>
    public sealed class CodmExportService
    {
        private readonly CodmConfig cfg;
        private readonly PakIndex index;
        private readonly WwiseIndex wwise;
        private readonly Func<CabIndex> cabIndexProvider;

        public CodmExportService(CodmConfig cfg, PakIndex index, WwiseIndex wwise, Func<CabIndex> cabIndexProvider = null)
        {
            this.cfg = cfg;
            this.index = index;
            this.wwise = wwise;
            this.cabIndexProvider = cabIndexProvider;
        }

        /// <summary>The pak files that make up a weapon export, in load order (download modules first).</summary>
        public List<PakEntry> SelectPaks(WeaponEntry w, WeaponExportOptions o)
        {
            var all = new List<PakEntry>(w.Paks);
            if (o.IncludeBaseWeapon)
                all.AddRange(w.BaseChainPaks());
            if (o.IncludeComponents)
                all.AddRange(w.AllComponents());
            return PakIndex.OrderForLoading(all, cfg);
        }

        public WeaponExportReport ExportWeapon(WeaponEntry w, string outRoot, WeaponExportOptions o, Action<string> log = null)
        {
            log = log ?? (_ => { });
            var dir = Path.Combine(outRoot, w.Folder);
            Directory.CreateDirectory(dir);
            var report = new WeaponExportReport { Weapon = w.Folder, OutputDir = dir };

            var paks = SelectPaks(w, o);
            if (o.Models || o.Textures)
            {
                var am = new AssetsManager();
                try
                {
                    log($"[Export] {w.Folder}: loading {paks.Count} pak(s)");
                    am.LoadFiles(paks.Select(p => p.FullPath).ToArray());
                    report.PaksLoaded.AddRange(paks.Select(p => p.FullPath));

                    if (o.ResolveDependencies && o.Models)
                    {
                        var cabs = cabIndexProvider?.Invoke();
                        if (cabs == null)
                        {
                            log("[Deps] no CAB index available, dependencies not resolved");
                        }
                        else
                        {
                            var deps = DependencyResolver.Resolve(am, cabs, cfg, log, maxPaks: o.MaxDependencyPaks);
                            report.DependencyPaks.AddRange(deps.Loaded.Select(p => p.FullPath));
                            report.UnresolvedReferences.AddRange(deps.Unresolved);
                        }
                    }

                    var weaponFiles = new HashSet<string>(report.PaksLoaded, StringComparer.OrdinalIgnoreCase);
                    if (o.Models)
                        ExportModels(am, Path.Combine(dir, "models"), report, log, weaponFiles, o.AllPrefabCopies);
                    if (o.Textures)
                        ExportTextures(am, Path.Combine(dir, "textures"), report, log, weaponFiles);
                }
                finally
                {
                    am.Clear();
                }
            }

            if (o.Sounds && wwise != null)
                ExportSounds(w, Path.Combine(dir, "sounds"), o, report, log);

            if (o.Effects && w.Effects.Count > 0)
                ExportEffects(w, Path.Combine(dir, "fx"), report, log);

            File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonConvert.SerializeObject(report, Formatting.Indented));
            log($"[Export] {w.Folder}: done -> {dir}");
            return report;
        }

        /// <summary>
        /// Export every Animator (with the clips its controller references) and every renderer-only root
        /// as Cast. Only objects from the weapon's own paks are exported (dependency paks just resolve
        /// references). A prefab that exists in several bundles is exported once unless requested.
        /// </summary>
        public void ExportModels(AssetsManager am, string dir, WeaponExportReport report, Action<string> log, HashSet<string> ownBundles = null, bool allPrefabCopies = false)
        {
            var exportedRoots = new HashSet<GameObject>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CastExporter.SkipEmptyAnimations = true;

            bool IsOwn(SerializedFile f) => ownBundles == null || ownBundles.Contains(f.originalPath ?? f.fullName ?? "");

            // Every module copy of a prefab is a candidate; the copy whose controller resolves the most
            // clips wins (a stripped copy with no controller often sits next to the real one).
            var candidates = new List<(Animator animator, GameObject go, SerializedFile file, AnimationClip[] clips)>();
            foreach (var file in am.assetsFileList.Where(IsOwn))
            {
                foreach (var obj in file.Objects)
                {
                    if (!(obj is Animator animator) || !animator.m_GameObject.TryGet(out var go))
                        continue;
                    candidates.Add((animator, go, file, ClipsForAnimator(animator, file)));
                }
            }
            var ordered = allPrefabCopies
                ? candidates
                : candidates.GroupBy(c => SafeName(c.go.m_Name), StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(c => c.clips.Length).ThenBy(c => cfg.SourceRank(SourceOf(c.file))).First())
                    .ToList();
            foreach (var c in candidates)
                if (!ordered.Contains(c))
                    report.SkippedPrefabCopies.Add($"{c.go.m_Name} ({SourceLabel(c.file)}, {c.clips.Length} clips)");

            foreach (var cand in ordered)
            {
                var animator = cand.animator;
                var go = cand.go;
                var file = cand.file;
                var clips = cand.clips;
                if (clips.Length == 0)
                {
                    // a skin whose own controller bundle is missing: reuse the clips of the base weapon's
                    // prefab with the same role suffix (_1P, _3P, _UI)
                    var suffix = RoleSuffix(go.m_Name);
                    var donor = ordered.FirstOrDefault(o => o.go != go && o.clips.Length > 0 && RoleSuffix(o.go.m_Name) == suffix);
                    if (donor.go != null)
                    {
                        clips = donor.clips;
                        log($"[Export] {go.m_Name}: no clips of its own, using the {clips.Length} clip(s) of {donor.go.m_Name}");
                    }
                }
                {
                    var name = UniqueName(usedNames, go.m_Name, SourceLabel(file));
                    try
                    {
                        var conv = new ModelConverter(animator, cfg.ImageFormat, clips.Length > 0 ? clips : null);
                        var modelDir = Path.Combine(dir, name);
                        CastExporter.Export(modelDir, name, conv, cfg.ScaleFactor);
                        report.Models.Add(modelDir);
                        exportedRoots.Add(go);
                        var animDir = Path.Combine(modelDir, "anims");
                        var written = Directory.Exists(animDir) ? Directory.GetFiles(animDir, "*.cast").Length : 0;
                        log($"[Export] model {name}: {conv.MeshList.Count} mesh(es), {clips.Length} clip(s) found, {written} written");
                    }
                    catch (Exception ex)
                    {
                        report.Warnings.Add($"model {name}: {ex.Message}");
                        log($"[Export] model {name} failed: {ex.Message}");
                    }
                }
            }

            // roots that have renderers but no Animator (static parts, UI/3P variants)
            foreach (var file in am.assetsFileList.Where(IsOwn))
            {
                foreach (var obj in file.Objects)
                {
                    if (!(obj is GameObject go) || go.m_Transform == null)
                        continue;
                    if (go.m_Transform.m_Father != null && go.m_Transform.m_Father.TryGet(out _))
                        continue; // not a root
                    if (exportedRoots.Contains(go) || HasAnimatorInTree(go) || !HasRendererInTree(go))
                        continue;
                    if (!allPrefabCopies && usedNames.Contains(SafeName(go.m_Name)))
                    {
                        report.SkippedPrefabCopies.Add($"{go.m_Name} ({SourceLabel(file)})");
                        continue;
                    }
                    var name = UniqueName(usedNames, go.m_Name, SourceLabel(file));
                    try
                    {
                        var conv = new ModelConverter(go, cfg.ImageFormat);
                        var modelDir = Path.Combine(dir, name);
                        CastExporter.Export(modelDir, name, conv, cfg.ScaleFactor);
                        report.Models.Add(modelDir);
                        log($"[Export] static {name}: {conv.MeshList.Count} mesh(es)");
                    }
                    catch (Exception ex)
                    {
                        report.Warnings.Add($"static {name}: {ex.Message}");
                        log($"[Export] static {name} failed: {ex.Message}");
                    }
                }
            }
            if (report.SkippedPrefabCopies.Count > 0)
                log($"[Export] {report.SkippedPrefabCopies.Count} duplicate prefab copies skipped (other module copies of the same prefab)");
        }

        /// <summary>
        /// Clips that belong to an Animator: from its AnimatorController, or the override clips of its
        /// AnimatorOverrideController (plus the controller's own clips when nothing is overridden).
        /// Falls back to every clip in the same serialized file.
        /// </summary>
        public static AnimationClip[] ClipsForAnimator(Animator animator, SerializedFile file)
        {
            var clips = new List<AnimationClip>();
            if (animator.m_Controller.TryGet(out var rac))
            {
                if (rac is AnimatorOverrideController aoc)
                {
                    foreach (var pair in aoc.m_Clips)
                    {
                        if (pair.m_OverrideClip.TryGet(out var clip))
                            clips.Add(clip);
                    }
                    if (clips.Count == 0 && aoc.m_Controller.TryGet(out var baseRac) && baseRac is AnimatorController baseAc)
                        AddControllerClips(baseAc, clips);
                }
                else if (rac is AnimatorController ac)
                {
                    AddControllerClips(ac, clips);
                }
            }
            if (clips.Count == 0)
            {
                foreach (var obj in file.Objects)
                    if (obj is AnimationClip clip)
                        clips.Add(clip);
            }
            return clips.Distinct().ToArray();
        }

        private static void AddControllerClips(AnimatorController ac, List<AnimationClip> clips)
        {
            if (ac.m_AnimationClips == null)
                return;
            foreach (var p in ac.m_AnimationClips)
                if (p.TryGet(out var clip))
                    clips.Add(clip);
        }

        private static bool HasAnimatorInTree(GameObject go)
        {
            if (go.m_Animator != null)
                return true;
            if (go.m_Transform == null)
                return false;
            foreach (var c in go.m_Transform.m_Children)
            {
                if (c.TryGet(out var t) && t.m_GameObject.TryGet(out var child) && HasAnimatorInTree(child))
                    return true;
            }
            return false;
        }

        private static bool HasRendererInTree(GameObject go)
        {
            if (go.m_MeshRenderer != null || go.m_SkinnedMeshRenderer != null)
                return true;
            if (go.m_Transform == null)
                return false;
            foreach (var c in go.m_Transform.m_Children)
            {
                if (c.TryGet(out var t) && t.m_GameObject.TryGet(out var child) && HasRendererInTree(child))
                    return true;
            }
            return false;
        }

        public void ExportTextureSet(IEnumerable<Texture2D> textures, string dir, WeaponExportReport report, Action<string> log)
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var count = 0;
            foreach (var tex in textures)
            {
                var name = UniqueName(usedNames, tex.m_Name);
                try
                {
                    var image = tex.ConvertToImage(true);
                    if (image == null)
                    {
                        report.Warnings.Add($"texture {name}: could not decode ({tex.m_TextureFormat})");
                        continue;
                    }
                    Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, name + "." + cfg.ImageFormat.ToString().ToLowerInvariant());
                    using (image)
                    using (var fs = File.Create(path))
                    {
                        image.WriteToStream(fs, cfg.ImageFormat);
                    }
                    report.Textures.Add(path);
                    count++;
                }
                catch (Exception ex)
                {
                    report.Warnings.Add($"texture {name}: {ex.Message}");
                }
            }
            log($"[Export] {count} texture(s)");
        }

        public void ExportTextures(AssetsManager am, string dir, WeaponExportReport report, Action<string> log, HashSet<string> ownBundles = null)
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var count = 0;
            foreach (var file in am.assetsFileList)
            {
                if (ownBundles != null && !ownBundles.Contains(file.originalPath ?? file.fullName ?? ""))
                    continue;
                foreach (var obj in file.Objects)
                {
                    if (!(obj is Texture2D tex))
                        continue;
                    var name = UniqueName(usedNames, tex.m_Name, SourceLabel(file));
                    try
                    {
                        var image = tex.ConvertToImage(true);
                        if (image == null)
                        {
                            report.Warnings.Add($"texture {name}: could not decode ({tex.m_TextureFormat})");
                            continue;
                        }
                        Directory.CreateDirectory(dir);
                        var path = Path.Combine(dir, name + "." + cfg.ImageFormat.ToString().ToLowerInvariant());
                        using (image)
                        using (var fs = File.Create(path))
                        {
                            image.WriteToStream(fs, cfg.ImageFormat);
                        }
                        report.Textures.Add(path);
                        count++;
                    }
                    catch (Exception ex)
                    {
                        report.Warnings.Add($"texture {name}: {ex.Message}");
                    }
                }
            }
            log($"[Export] {count} texture(s)");
        }

        public void ExportSounds(WeaponEntry w, string dir, WeaponExportOptions o, WeaponExportReport report, Action<string> log)
        {
            var banks = w.Banks.Concat(w.SharedBanks).Select(b => b.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (banks.Count == 0)
            {
                log("[Export] no sound banks linked to this weapon (set its sound class to get the wpn_<class> bank)");
                return;
            }
            foreach (var bankName in banks)
            {
                var bank = wwise.PreferredCopy(bankName);
                if (bank == null)
                    continue;
                var bankDir = Path.Combine(dir, bank.Name);
                var r = WemTools.ExportBank(wwise, bank, bankDir, cfg.VgmstreamPath, o.ConvertWem, log, o.SoundsByEvent);
                report.Banks.Add(bank.Path);
                report.WemsExtracted += r.Extracted;
                report.WemsConverted += r.Converted;
                if (r.Unresolved.Count > 0)
                    report.Warnings.Add($"bank {bank.Name}: {r.Unresolved.Count} unresolved wem id(s)");
            }
        }

        /// <summary>
        /// FX bundles cannot be rebuilt as particle systems. Their textures live in shared "#tex" bundles,
        /// so the bundle's dependencies are loaded and the textures its materials reference are exported,
        /// together with a listing of its particle systems, materials and meshes.
        /// </summary>
        public void ExportEffects(WeaponEntry w, string dir, WeaponExportReport report, Action<string> log)
        {
            var fxSkip = new[] { "_codm_shader", "scenes/", "_s_", "textures/ui", "cod_prefabs/ngui", "cod_models/avatar" };
            foreach (var fx in PakIndex.OrderForLoading(w.Effects, cfg))
            {
                var fxDir = Path.Combine(dir, SafeName(fx.Name.LastSegment));
                var am = new AssetsManager();
                try
                {
                    am.LoadFiles(fx.FullPath);
                    var cabs = cabIndexProvider?.Invoke();
                    if (cabs != null)
                        DependencyResolver.Resolve(am, cabs, cfg, log, maxRounds: 1, maxPaks: 80, skipPrefixes: fxSkip);
                    var own = am.assetsFileList.Where(f => string.Equals(f.originalPath ?? f.fullName, fx.FullPath, StringComparison.OrdinalIgnoreCase)).ToList();

                    // materials referenced by the effect's renderers (they usually live in shared bundles),
                    // then the textures those materials reference
                    var textures = new List<Texture2D>();
                    var listing = new List<string>();
                    var materials = new List<Material>();
                    foreach (var file in own)
                    {
                        foreach (var obj in file.Objects)
                        {
                            if (obj is Material m0 && !materials.Contains(m0))
                                materials.Add(m0);
                            if (obj is Renderer r && r.m_Materials != null)
                                foreach (var mp in r.m_Materials)
                                    if (mp.TryGet(out var m1) && !materials.Contains(m1))
                                        materials.Add(m1);
                        }
                    }
                    // the materials' textures sit one bundle further: load exactly those bundles
                    if (cabs != null)
                    {
                        var need = new List<string>();
                        foreach (var mat in materials)
                            foreach (var kv in mat.m_SavedProperties.m_TexEnvs)
                            {
                                var ptr = kv.Value.m_Texture;
                                if (ptr.m_PathID == 0 || ptr.m_FileID <= 0 || ptr.TryGet(out _)) continue;
                                if (ptr.m_FileID - 1 < mat.assetsFile.m_Externals.Count)
                                    need.Add(mat.assetsFile.m_Externals[ptr.m_FileID - 1].fileName);
                            }
                        DependencyResolver.LoadCabs(am, cabs, cfg, need, log);
                    }
                    foreach (var file in own)
                    {
                        foreach (var obj in file.Objects)
                        {
                            if (obj is Material) continue;
                            if (obj is Texture2D tex2 && !textures.Contains(tex2))
                                textures.Add(tex2);
                            else if (obj is GameObject go)
                                listing.Add($"GameObject\t{go.m_Name}");
                            else if (obj is NamedObject no && !(obj is Texture2D))
                                listing.Add($"{obj.type}\t{no.m_Name}");
                        }
                    }
                    foreach (var mat in materials)
                    {
                        {
                            {
                                var texNames = new List<string>();
                                foreach (var kv in mat.m_SavedProperties.m_TexEnvs)
                                {
                                    if (kv.Value.m_Texture.TryGet(out var tex) && tex is Texture2D t2)
                                    {
                                        if (!textures.Contains(t2)) textures.Add(t2);
                                        texNames.Add($"{kv.Key}={t2.m_Name}");
                                    }
                                    else if (kv.Value.m_Texture.m_PathID != 0)
                                    {
                                        texNames.Add($"{kv.Key}=(unresolved)");
                                    }
                                }
                                var shaderName = mat.m_Shader.TryGet(out var sh) ? sh.m_Name : "?";
                                listing.Add($"Material\t{mat.m_Name}\tshader={shaderName}\t{string.Join(" ", texNames)}");
                            }
                        }
                    }
                    var sub = new WeaponExportReport();
                    ExportTextureSet(textures, Path.Combine(fxDir, "textures"), sub, log);
                    report.Textures.AddRange(sub.Textures);
                    report.Warnings.AddRange(sub.Warnings);
                    Directory.CreateDirectory(fxDir);
                    File.WriteAllLines(Path.Combine(fxDir, "objects.txt"), new[] { "# " + fx.Name.Decoded, "# " + fx.FullPath, $"# {textures.Count} texture(s) referenced by materials" }.Concat(listing.Distinct().OrderBy(x => x)));
                    report.Effects.Add(fxDir);
                    log($"[Export] fx {fx.Name.LastSegment}: {textures.Count} texture(s), {listing.Count(l => l.StartsWith("Material"))} material(s)");
                }
                catch (Exception ex)
                {
                    report.Warnings.Add($"fx {fx.Name.Decoded}: {ex.Message}");
                }
                finally
                {
                    am.Clear();
                }
            }
        }

        /// <summary>"_1p", "_3p", "_ui", "_3p_br"... the role part of a weapon prefab name, lower case.</summary>
        private static string RoleSuffix(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
                return "";
            var n = prefabName.ToLowerInvariant();
            foreach (var tag in new[] { "_1p", "_3p", "_ui" })
            {
                var i = n.IndexOf(tag, StringComparison.Ordinal);
                if (i >= 0)
                    return n.Substring(i);
            }
            return "";
        }

        private string SourceOf(SerializedFile file)
        {
            var bundlePath = file.originalPath ?? file.fullName;
            var entry = index?.Entries.FirstOrDefault(e => string.Equals(e.FullPath, bundlePath, StringComparison.OrdinalIgnoreCase));
            return entry?.Source ?? "streaming";
        }

        /// <summary>Short tag for the bundle a serialized file came from: "streaming", "common", "cn13.0"...</summary>
        private string SourceLabel(SerializedFile file)
        {
            var bundlePath = file.originalPath ?? file.fullName;
            var entry = index?.Entries.FirstOrDefault(e => string.Equals(e.FullPath, bundlePath, StringComparison.OrdinalIgnoreCase));
            var module = entry != null ? entry.Name.Module : PakName.Parse(Path.GetFileName(bundlePath ?? "")).Module;
            if (module == null)
                return entry?.Source ?? "bundle";
            var us = module.LastIndexOf('_');
            return us >= 0 && us < module.Length - 1 ? module.Substring(us + 1) : module;
        }

        private static string UniqueName(HashSet<string> used, string name, string sourceLabel = null)
        {
            var baseName = SafeName(string.IsNullOrWhiteSpace(name) ? "unnamed" : name);
            if (used.Add(baseName))
                return baseName;
            if (!string.IsNullOrEmpty(sourceLabel))
            {
                var tagged = SafeName(baseName + " (" + sourceLabel + ")");
                if (used.Add(tagged))
                    return tagged;
                baseName = tagged;
            }
            var i = 2;
            var candidate = baseName + "_" + i;
            while (!used.Add(candidate))
                candidate = baseName + "_" + ++i;
            return candidate;
        }

        public static string SafeName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
            return new string(chars).Trim();
        }
    }
}
