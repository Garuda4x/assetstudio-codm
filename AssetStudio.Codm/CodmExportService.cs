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
        /// <summary>
        /// Also dump the class banks (wpn_sniper...) and the banks the weapon's banks borrow media from.
        /// Off by default: those hold the sounds of a whole weapon class, and media a weapon bank borrows
        /// is extracted through its own events anyway.
        /// </summary>
        public bool ClassBanks { get; set; } = false;
        /// <summary>
        /// Also dump the lettered variants of a numeric bank (247a, 247c...). Off by default: only the
        /// plain bank and its third-person set (247, 247p3) are taken; the variants are most likely the
        /// sets of other skins sharing the base.
        /// </summary>
        public bool BankVariants { get; set; } = false;
        /// <summary>Load the bundles the weapon paks reference (shared animations, controllers, avatars).</summary>
        public bool ResolveDependencies { get; set; } = true;
        public int MaxDependencyPaks { get; set; } = 60;
        /// <summary>Export every copy of a prefab that appears in several bundles instead of the first one.</summary>
        public bool AllPrefabCopies { get; set; } = false;
        /// <summary>Give the models their materials (matched by name) with the colour map written to textures/.</summary>
        public bool Materials { get; set; } = true;
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
        /// <summary>Wwise event ids found in the animation clips' event data.</summary>
        public List<uint> ClipWwiseEvents { get; } = new List<uint>();
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
            currentBanks = w.Banks.Concat(w.Ancestors().SelectMany(a => a.Banks)).Concat(w.SharedBanks).Distinct().ToList();
            ExportBundleSet(paks, dir, o, report, log, o.IncludeComponents ? ComponentScope(w) : null);
            currentBanks = null;

            if (o.Sounds && wwise != null)
            {
                ExportSounds(w, Path.Combine(dir, "sounds"), o, report, log);
                ExportClipEventSounds(report, Path.Combine(dir, "sounds", "_from_animation_events"), o, log);
            }

            if (o.Effects && w.Effects.Count > 0)
                ExportEffects(w, Path.Combine(dir, "fx"), report, log);

            File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonConvert.SerializeObject(report, Formatting.Indented));
            log($"[Export] {w.Folder}: done -> {dir}");
            return report;
        }

        /// <summary>A character skin: every prefab (body, head, hair, first-person arms) as Cast plus textures. No sounds.</summary>
        public WeaponExportReport ExportCharacter(CharacterEntry c, string outRoot, WeaponExportOptions o, Action<string> log = null)
        {
            log = log ?? (_ => { });
            var dir = Path.Combine(outRoot, SafeName(c.DisplayName.Replace('/', '_')));
            Directory.CreateDirectory(dir);
            var report = new WeaponExportReport { Weapon = c.Folder, OutputDir = dir };
            var paks = PakIndex.OrderForLoading(c.Paks, cfg);
            ExportBundleSet(paks, dir, o, report, log);
            File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonConvert.SerializeObject(report, Formatting.Indented));
            log($"[Export] {c.Folder}: done -> {dir}");
            return report;
        }

        /// <summary>
        /// Component paks are version buckets (component/bar/37.2) holding the attachments of many weapons.
        /// Per pak linked through its container paths: the paths that tie it to this weapon or its bases.
        /// Paks linked by name only (sights named after the base weapon) are not listed and export whole.
        /// </summary>
        private static Dictionary<string, HashSet<string>> ComponentScope(WeaponEntry w)
        {
            var scope = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pak in w.AllComponents())
            {
                var paths = w.ComponentPaths(pak);
                if (paths.Count > 0)
                    scope[pak.FullPath] = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            }
            return scope;
        }

        /// <summary>
        /// Objects of the scoped bundles that belong to the matching container paths: each path's asset and
        /// everything in its preload range (the prefab hierarchy, meshes, materials, textures).
        /// </summary>
        private static HashSet<Object> ScopedObjects(AssetsManager am, Dictionary<string, HashSet<string>> scope)
        {
            var allowed = new HashSet<Object>();
            foreach (var file in am.assetsFileList)
            {
                if (!scope.TryGetValue(file.originalPath ?? file.fullName ?? "", out var paths))
                    continue;
                foreach (var ab in file.Objects.OfType<AssetBundle>())
                {
                    foreach (var c in ab.m_Container)
                    {
                        if (!paths.Contains(c.Key))
                            continue;
                        if (c.Value.asset.TryGet(out var asset))
                            allowed.Add(asset);
                        var end = Math.Min(c.Value.preloadIndex + c.Value.preloadSize, ab.m_PreloadTable.Length);
                        for (var i = Math.Max(c.Value.preloadIndex, 0); i < end; i++)
                            if (ab.m_PreloadTable[i].TryGet(out var dep))
                                allowed.Add(dep);
                    }
                }
            }
            return allowed;
        }

        /// <summary>Load a set of paks (plus their dependencies) and export their models and textures.</summary>
        private void ExportBundleSet(List<PakEntry> paks, string dir, WeaponExportOptions o, WeaponExportReport report, Action<string> log,
            Dictionary<string, HashSet<string>> componentScope = null)
        {
            if (o.Models || o.Textures)
            {
                var am = new AssetsManager();
                try
                {
                    log($"[Export] loading {paks.Count} pak(s)");
                    am.LoadFiles(PakEntry.EnsureOnDisk(paks, log));
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
                    Func<SerializedFile, Object, bool> inScope = null;
                    if (componentScope != null && componentScope.Count > 0)
                    {
                        var allowed = ScopedObjects(am, componentScope);
                        inScope = (f, obj) => !componentScope.ContainsKey(f.originalPath ?? f.fullName ?? "") || allowed.Contains(obj);
                        log($"[Export] {componentScope.Count} component pak(s) shared with other weapons: only this weapon's attachments are exported from them");
                    }
                    if (o.Models)
                        ExportModels(am, Path.Combine(dir, "models"), report, log, weaponFiles, o.AllPrefabCopies, inScope,
                            o.Materials ? Path.Combine(dir, "textures") : null);
                    if (o.Textures)
                        ExportTextures(am, Path.Combine(dir, "textures"), report, log, weaponFiles, inScope);
                }
                finally
                {
                    am.Clear();
                }
            }
        }

        /// <summary>
        /// Export every Animator (with the clips its controller references) and every renderer-only root
        /// as Cast. Only objects from the weapon's own paks are exported (dependency paks just resolve
        /// references). A prefab that exists in several bundles is exported once unless requested.
        /// </summary>
        /// <summary>Banks of the weapon being exported, for the notetrack sheet (set by ExportWeapon).</summary>
        private List<Wwise.WwiseBankInfo> currentBanks;

        public void ExportModels(AssetsManager am, string dir, WeaponExportReport report, Action<string> log, HashSet<string> ownBundles = null, bool allPrefabCopies = false,
            Func<SerializedFile, Object, bool> inScope = null, string materialTextureDir = null)
        {
            var exportedRoots = new HashSet<GameObject>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CastExporter.SkipEmptyAnimations = true;
            var materials = materialTextureDir != null ? new MaterialMatcher(am, materialTextureDir, cfg.ImageFormat) : null;
            Func<Mesh, int, ImportedMaterial> materialFallback = materials != null ? materials.Resolve : (Func<Mesh, int, ImportedMaterial>)null;

            bool IsOwn(SerializedFile f) => ownBundles == null || ownBundles.Contains(f.originalPath ?? f.fullName ?? "");
            bool InScope(SerializedFile f, Object obj) => inScope == null || inScope(f, obj);

            // Every module copy of a prefab is a candidate; the copy whose controller resolves the most
            // clips wins (a stripped copy with no controller often sits next to the real one).
            var candidates = new List<(Animator animator, GameObject go, SerializedFile file, AnimationClip[] clips)>();
            foreach (var file in am.assetsFileList.Where(IsOwn))
            {
                foreach (var obj in file.Objects)
                {
                    if (!(obj is Animator animator) || !animator.m_GameObject.TryGet(out var go) || !InScope(file, go))
                        continue;
                    candidates.Add((animator, go, file, ClipsForAnimator(animator, file)));
                }
            }
            var ordered = allPrefabCopies
                ? candidates
                : candidates.GroupBy(c => SafeName(c.go.m_Name), StringComparer.OrdinalIgnoreCase)
                    .Select(g =>
                    {
                        var best = g.OrderByDescending(c => c.clips.Length).ThenBy(c => cfg.SourceRank(SourceOf(c.file))).First();
                        if (HasMeshInTree(best.go))
                            return best;
                        // the prefab with the controller can leave its renderer's mesh empty (filled in at
                        // runtime) while the imported model next to it carries the mesh: take the rig and mesh
                        // of that copy and the clips of the prefab
                        var withMesh = g.Where(c => HasMeshInTree(c.go))
                            .OrderByDescending(c => c.clips.Length).ThenBy(c => cfg.SourceRank(SourceOf(c.file))).FirstOrDefault();
                        if (withMesh.go == null)
                            return best;
                        log($"[Export] {best.go.m_Name}: the copy with {best.clips.Length} clip(s) has no mesh, using the mesh of the {SourceLabel(withMesh.file)} copy");
                        return (animator: withMesh.animator, go: withMesh.go, file: withMesh.file,
                            clips: best.clips.Length > withMesh.clips.Length ? best.clips : withMesh.clips);
                    })
                    .ToList();
            var chosen = new HashSet<GameObject>(ordered.Select(c => c.go));
            foreach (var c in candidates)
                if (!chosen.Contains(c.go))
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
                        var conv = new ModelConverter(animator, cfg.ImageFormat, clips.Length > 0 ? clips : null, materialFallback);
                        CameraTrackMerger.Merge(conv, log);
                        var modelDir = Path.Combine(dir, name);
                        CastExporter.Export(modelDir, name, conv, cfg.ScaleFactor);
                        report.Models.Add(modelDir);
                        exportedRoots.Add(go);
                        var animDir = Path.Combine(modelDir, "anims");
                        var written = Directory.Exists(animDir) ? Directory.GetFiles(animDir, "*.cast").Length : 0;
                        log($"[Export] model {name}: {conv.MeshList.Count} mesh(es), {clips.Length} clip(s) found, {written} written");
                        if (clips.Length > 0 && Directory.Exists(animDir))
                        {
                            var sheet = AnimationSoundMap.Collect(clips, wwise, currentBanks);
                            if (sheet.Count > 0)
                            {
                                AnimationSoundMap.WriteSheet(Path.Combine(animDir, "notetracks.txt"), sheet);
                                foreach (var id in sheet.SelectMany(e => e.WwiseEvents)) report.ClipWwiseEvents.Add(id);
                                log($"[Export] {name}: {sheet.Count} animation event(s) -> anims/notetracks.txt");
                            }
                        }
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
                    if (exportedRoots.Contains(go) || HasAnimatorInTree(go) || !HasRendererInTree(go) || !InScope(file, go))
                        continue;
                    if (!allPrefabCopies && usedNames.Contains(SafeName(go.m_Name)))
                    {
                        report.SkippedPrefabCopies.Add($"{go.m_Name} ({SourceLabel(file)})");
                        continue;
                    }
                    var name = UniqueName(usedNames, go.m_Name, SourceLabel(file));
                    try
                    {
                        var conv = new ModelConverter(go, cfg.ImageFormat, null, materialFallback);
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
            if (materials != null)
            {
                log($"[Materials] {materials.WrittenTextures.Count()} colour map(s) referenced by the models");
                if (materials.LayoutChoices.Count > 0)
                    log($"[Materials] sheet by UV layout: {string.Join(", ", materials.LayoutChoices.Select(c => c.Key + " -> " + c.Value))}");
                if (materials.Unmatched.Count > 0)
                    log($"[Materials] no material found for {materials.Unmatched.Count} mesh(es): {string.Join(", ", materials.Unmatched.Take(12))}{(materials.Unmatched.Count > 12 ? ", ..." : "")}");
            }
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

        private static bool HasMeshInTree(GameObject go)
        {
            if (go.m_SkinnedMeshRenderer != null && go.m_SkinnedMeshRenderer.m_Mesh.TryGet(out _))
                return true;
            if (go.m_MeshRenderer != null && go.m_MeshFilter != null && go.m_MeshFilter.m_Mesh.TryGet(out _))
                return true;
            if (go.m_Transform == null)
                return false;
            foreach (var c in go.m_Transform.m_Children)
            {
                if (c.TryGet(out var t) && t.m_GameObject.TryGet(out var child) && HasMeshInTree(child))
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

        public void ExportTextures(AssetsManager am, string dir, WeaponExportReport report, Action<string> log, HashSet<string> ownBundles = null,
            Func<SerializedFile, Object, bool> inScope = null)
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var count = 0;
            foreach (var file in am.assetsFileList)
            {
                if (ownBundles != null && !ownBundles.Contains(file.originalPath ?? file.fullName ?? ""))
                    continue;
                foreach (var obj in file.Objects)
                {
                    if (!(obj is Texture2D tex) || (inScope != null && !inScope(file, tex)))
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
            // the weapon's own banks and those of its base chain (a skin without custom sounds uses the base's)
            var own = w.Banks.Concat(w.Ancestors().SelectMany(a => a.Banks)).Where(b => b.HasHirc).ToList(); // 32-byte header-only banks carry nothing
            if (!o.BankVariants)
                own = own.Where(b => !b.IsNumeric || System.Text.RegularExpressions.Regex.IsMatch(b.Name, @"^\d+(p\d)?$")).ToList();
            var banks = own.Select(b => b.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (o.ClassBanks)
                banks.AddRange(w.SharedBanks.Select(b => b.Name).Where(n => !banks.Contains(n, StringComparer.OrdinalIgnoreCase)));
            if (banks.Count == 0)
            {
                log(w.SharedBanks.Count > 0
                    ? $"[Export] no weapon-specific sound bank; its class banks ({string.Join(", ", w.SharedBanks.Select(b => b.Name))}) hold the whole class, enable class banks to dump them"
                    : "[Export] no sound banks linked to this weapon (set its sound class to get the wpn_<class> bank)");
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
                    am.LoadFiles(fx.EnsureOnDisk(log));
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
            var entry = index?.ByFullPath(file.originalPath ?? file.fullName);
            return entry?.Source ?? "streaming";
        }

        /// <summary>
        /// Wems of the Wwise events the animation clips name directly (PlayEventSound data), wherever
        /// their bank lives (footsteps, generic handling): one folder per event.
        /// </summary>
        private void ExportClipEventSounds(WeaponExportReport report, string dir, WeaponExportOptions o, Action<string> log)
        {
            var ids = report.ClipWwiseEvents.Distinct().ToList();
            if (ids.Count == 0) return;
            var hirc = wwise.HircObjects();
            var byBank = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ids)
            {
                if (!hirc.TryGetValue(id, out var r) || r.Type != 4) continue;
                if (!byBank.TryGetValue(r.Bank, out var list)) byBank[r.Bank] = list = new List<uint>();
                list.Add(id);
            }
            foreach (var kv in byBank)
            {
                var info = wwise.PreferredCopy(kv.Key);
                if (info == null) continue;
                var wanted = new HashSet<uint>(kv.Value);
                var r = WemTools.ExportBank(wwise, info, Path.Combine(dir, info.Name), cfg.VgmstreamPath, o.ConvertWem, log, true, id => wanted.Contains(id));
                report.WemsExtracted += r.Extracted;
                report.WemsConverted += r.Converted;
            }
            log($"[Export] {ids.Count} Wwise event(s) referenced by animation events exported under sounds/_from_animation_events");
        }

        /// <summary>Short tag for the bundle a serialized file came from: "streaming", "common", "cn13.0"...</summary>
        private string SourceLabel(SerializedFile file)
        {
            var bundlePath = file.originalPath ?? file.fullName;
            var entry = index?.ByFullPath(bundlePath);
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
