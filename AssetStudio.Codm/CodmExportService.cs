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
    }

    public sealed class WeaponExportReport
    {
        public string Weapon { get; set; }
        public string OutputDir { get; set; }
        public List<string> PaksLoaded { get; } = new List<string>();
        public List<string> Models { get; } = new List<string>();
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

        public CodmExportService(CodmConfig cfg, PakIndex index, WwiseIndex wwise)
        {
            this.cfg = cfg;
            this.index = index;
            this.wwise = wwise;
        }

        /// <summary>The pak files that make up a weapon export, in load order (download modules first).</summary>
        public List<PakEntry> SelectPaks(WeaponEntry w, WeaponExportOptions o)
        {
            var all = new List<PakEntry>(w.Paks);
            if (o.IncludeBaseWeapon)
                all.AddRange(w.BaseChainPaks());
            if (o.IncludeComponents)
                all.AddRange(w.Components);
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
                    if (o.Models)
                        ExportModels(am, Path.Combine(dir, "models"), report, log);
                    if (o.Textures)
                        ExportTextures(am, Path.Combine(dir, "textures"), report, log);
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

        /// <summary>Export every Animator (with the clips its controller references) and every renderer-only root as Cast.</summary>
        public void ExportModels(AssetsManager am, string dir, WeaponExportReport report, Action<string> log)
        {
            var exportedRoots = new HashSet<GameObject>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CastExporter.SkipEmptyAnimations = true;
            foreach (var file in am.assetsFileList)
            {
                foreach (var obj in file.Objects)
                {
                    if (!(obj is Animator animator))
                        continue;
                    if (!animator.m_GameObject.TryGet(out var go))
                        continue;
                    var clips = ClipsForAnimator(animator, file);
                    var name = UniqueName(usedNames, go.m_Name, SourceLabel(file));
                    try
                    {
                        var conv = new ModelConverter(animator, cfg.ImageFormat, clips.Length > 0 ? clips : null);
                        var modelDir = Path.Combine(dir, name);
                        CastExporter.Export(modelDir, name, conv, cfg.ScaleFactor);
                        report.Models.Add(modelDir);
                        exportedRoots.Add(go);
                        log($"[Export] model {name}: {conv.MeshList.Count} mesh(es), {clips.Length} clip(s)");
                    }
                    catch (Exception ex)
                    {
                        report.Warnings.Add($"model {name}: {ex.Message}");
                        log($"[Export] model {name} failed: {ex.Message}");
                    }
                }
            }

            // roots that have renderers but no Animator (static parts, UI/3P variants)
            foreach (var file in am.assetsFileList)
            {
                foreach (var obj in file.Objects)
                {
                    if (!(obj is GameObject go) || go.m_Transform == null)
                        continue;
                    if (go.m_Transform.m_Father != null && go.m_Transform.m_Father.TryGet(out _))
                        continue; // not a root
                    if (exportedRoots.Contains(go) || HasAnimatorInTree(go) || !HasRendererInTree(go))
                        continue;
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
        }

        /// <summary>
        /// Clips that belong to an Animator: from its AnimatorController, or the override clips of its
        /// AnimatorOverrideController. Falls back to every clip in the same serialized file.
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

        public void ExportTextures(AssetsManager am, string dir, WeaponExportReport report, Action<string> log)
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var count = 0;
            foreach (var file in am.assetsFileList)
            {
                foreach (var obj in file.Objects)
                {
                    if (!(obj is Texture2D tex))
                        continue;
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
            }
            log($"[Export] {count} texture(s)");
        }

        public void ExportSounds(WeaponEntry w, string dir, WeaponExportOptions o, WeaponExportReport report, Action<string> log)
        {
            if (w.Banks.Count == 0)
            {
                log("[Export] no sound banks linked to this weapon");
                return;
            }
            foreach (var bankName in w.Banks.Select(b => b.Name).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var bank = wwise.PreferredCopy(bankName);
                if (bank == null)
                    continue;
                var bankDir = Path.Combine(dir, bank.Name);
                var r = WemTools.ExportBank(wwise, bank, bankDir, cfg.VgmstreamPath, o.ConvertWem, log);
                report.Banks.Add(bank.Path);
                report.WemsExtracted += r.Extracted;
                report.WemsConverted += r.Converted;
                if (r.Unresolved.Count > 0)
                    report.Warnings.Add($"bank {bank.Name}: {r.Unresolved.Count} unresolved wem id(s)");
            }
        }

        /// <summary>FX bundles cannot be rebuilt as particle systems; export their textures and list their objects.</summary>
        public void ExportEffects(WeaponEntry w, string dir, WeaponExportReport report, Action<string> log)
        {
            foreach (var fx in PakIndex.OrderForLoading(w.Effects, cfg))
            {
                var fxDir = Path.Combine(dir, SafeName(fx.Name.LastSegment));
                var am = new AssetsManager();
                try
                {
                    am.LoadFiles(fx.FullPath);
                    var sub = new WeaponExportReport();
                    ExportTextures(am, Path.Combine(fxDir, "textures"), sub, log);
                    report.Textures.AddRange(sub.Textures);
                    report.Warnings.AddRange(sub.Warnings);
                    var listing = new List<string>();
                    foreach (var file in am.assetsFileList)
                        foreach (var obj in file.Objects)
                            if (obj is NamedObject no && !(obj is Texture2D))
                                listing.Add($"{obj.type}\t{no.m_Name}");
                            else if (obj is GameObject go)
                                listing.Add($"GameObject\t{go.m_Name}");
                    Directory.CreateDirectory(fxDir);
                    File.WriteAllLines(Path.Combine(fxDir, "objects.txt"), new[] { "# " + fx.Name.Decoded, "# " + fx.FullPath }.Concat(listing.Distinct().OrderBy(x => x)));
                    report.Effects.Add(fxDir);
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

        /// <summary>"streaming", "common", "normal_weapon_cn24.9"... for the bundle a serialized file came from.</summary>
        private string SourceLabel(SerializedFile file)
        {
            var bundlePath = file.originalPath ?? file.fullName;
            var entry = index?.Entries.FirstOrDefault(e => string.Equals(e.FullPath, bundlePath, StringComparison.OrdinalIgnoreCase));
            if (entry != null)
                return entry.Name.Module ?? entry.Source;
            return PakName.Parse(Path.GetFileName(bundlePath ?? "")).Module ?? "bundle";
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
