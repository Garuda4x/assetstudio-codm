using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetStudio;
using AssetStudio.Codm;
using AssetStudio.Codm.Wwise;
using AssetStudio.Codm.Vfs;

namespace CodmCli
{
    /// <summary>
    /// Command line front end for the CoD Mobile tooling. The GUI browser uses the same library,
    /// so anything available here is available there and vice versa.
    /// </summary>
    internal static class Program
    {
        private static CodmConfig cfg;
        private static PakIndex pakIndex;
        private static WwiseIndex wwiseIndex;
        private static WeaponCatalog catalog;
        private static CabIndex cabIndex;
        private static ContainerIndex containerIndex;
        private static CharacterCatalog characters;
        private static CodmVfs vfs;

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Logger.Default = new ConsoleLogger();
            // global options (--config <file>, --diag) may precede the command
            var cmdIndex = Options.FindCommandIndex(args);
            if (cmdIndex < 0 || args[cmdIndex] == "-h" || args[cmdIndex] == "--help" || args[cmdIndex] == "help")
            {
                PrintUsage();
                return 0;
            }
            var command = args[cmdIndex];

            var opts = new Options(args.Where((a, i) => i != cmdIndex));
            try
            {
                cfg = CodmConfig.Load(opts.Get("config"));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }
            if (opts.Has("diag"))
                cfg.Diagnostics = true;
            CodmDiagnostics.Enabled = cfg.Diagnostics;

            try
            {
                switch (command.ToLowerInvariant())
                {
                    case "config": return CmdConfig(opts);
                    case "decode": return CmdCipher(opts, decode: true);
                    case "encode": return CmdCipher(opts, decode: false);
                    case "index": return CmdIndex(opts);
                    case "search": return CmdSearch(opts);
                    case "weapons": return CmdWeapons(opts);
                    case "weapon": return CmdWeapon(opts);
                    case "export": return CmdExport(opts);
                    case "cabindex": return CmdCabIndex(opts);
                    case "anims": return CmdAnims(opts);
                    case "mbscan": return CmdMbScan(opts);
                    case "events": return CmdEvents(opts);
                    case "wwnames": return CmdWwNames(opts);
                    case "characters": return CmdCharacters(opts);
                    case "character": return CmdCharacter(opts);
                    case "exportchar": return CmdExportCharacter(opts);
                    case "components": return CmdComponents(opts);
                    case "deps": return CmdDeps(opts);
                    case "classes": return CmdClasses(opts);
                    case "class": return CmdClass(opts);
                    case "banks": return CmdBanks(opts);
                    case "bank": return CmdBank(opts);
                    case "wems": return CmdWems(opts);
                    case "scan": return CmdScan(opts);
                    case "mono": return CmdMono(opts);
                    case "typetree": return CmdTypeTree(opts);
                    case "vfs": return CmdVfs(opts);
                    case "vfs-index": return CmdVfsIndex(opts);
                    case "vfs-check": return CmdVfsCheck(opts);
                    case "unpack": return CmdUnpack(opts);
                    default:
                        Console.Error.WriteLine($"Unknown command '{command}'.");
                        PrintUsage();
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
                if (cfg.Diagnostics)
                    Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("codm - Call of Duty Mobile asset tooling (AssetStudio fork)");
            Console.WriteLine();
            Console.WriteLine("  config [--write]               show the resolved configuration (codm.config.json)");
            Console.WriteLine("  decode <name...>               decode obfuscated pak / table names");
            Console.WriteLine("  encode <name...>               inverse of decode");
            Console.WriteLine("  index [--tsv <file>]           list pak folders and counts per top folder; optionally dump all names");
            Console.WriteLine("  search <text>                  find paks by decoded name");
            Console.WriteLine("  weapons [filter] [--kind k]    list weapons (filter matches folder; --kind main|melee|throw|assi|special; --base)");
            Console.WriteLine("  weapon <id|folder|name>        show the paks, base weapon, skins, components, fx and banks of a weapon");
            Console.WriteLine("  export <id|folder> [--out d]   export models (cast), textures, sounds [--fx] [--no-sounds] [--class-banks] [--bank-variants] [--no-models] [--no-textures] [--no-base] [--components] [--no-deps] [--max-deps n] [--all-copies]");
            Console.WriteLine("  cabindex [--rebuild]           build/refresh the CAB -> pak index used to load bundle dependencies");
            Console.WriteLine("  anims <weapon> [--no-deps]     per Animator: controller, where its clips live and whether they resolve");
            Console.WriteLine("  mbscan <weapon|pak> [--deps]   scan MonoBehaviour blobs for PPtrs (objects / other bundles) and Wwise object ids (--hex dumps small blobs)");
            Console.WriteLine("  events <weapon> [--all]        animation events (notetracks) of the weapon's clips, cross-checked against Wwise ids");
            Console.WriteLine("  wwnames [--rebuild]            guess Wwise event/switch names (FNV) for all weapon banks; links banks to weapons by name");
            Console.WriteLine("  components [--rebuild]         index attachment bundles by their container paths and link them to weapons");
            Console.WriteLine("  deps <pak...>                  load bundle(s) and show which other paks they reference");
            Console.WriteLine("  classes [--unknown]            sound class per weapon family (wpn_<class> bank)");
            Console.WriteLine("  class <weapon> [<class>|-]     show or set the sound class of a weapon family (ar smg lmg sniper shotgun pistol launcher melee)");
            Console.WriteLine("  characters [filter]            list character skins (cod_models/avatar)");
            Console.WriteLine("  character <name>               paks of a character skin");
            Console.WriteLine("  exportchar <name> [--out d]    export a character: every prefab as cast (body, head, arms) + textures [--no-deps] [--all-copies]");
            Console.WriteLine("  banks [filter]                 list sound banks");
            Console.WriteLine("  bank <name|path>               show a bank: events, actions, sounds and where their wems live");
            Console.WriteLine("  wems <bank> [--out d] [--wav]  extract every wem a bank uses, one folder per event (--flat for one folder; vgmstream with --wav)");
            Console.WriteLine("  scan <pak|dir> [-v] [--csv f]  bundle metadata scan (types, names, container paths)");
            Console.WriteLine("  mono <pak>                     list MonoScript / MonoBehaviour and embedded strings");
            Console.WriteLine("  typetree <pak>                 report whether the bundle carries type trees");
            Console.WriteLine("  vfs                            1.9.57 containers found for the config: packs, entries, chunks, base offset cache");
            Console.WriteLine("  vfs-index [--rebuild]          walk the base pack once and cache where each entry starts (minutes)");
            Console.WriteLine("  vfs-check [--source s]         read and decompress every container entry, report failures [--bundles] [--max n]; --unpacked: compare the unpack folder with the containers");
            Console.WriteLine("  unpack <text...> [--out d]     extract container entries whose decoded name contains the text as loose paks (format 18 rewritten to stock) [--list] [--raw] [--chunks] [--max n] [--source puffer|extract|streaming]");
            Console.WriteLine();
            Console.WriteLine("Global (before or after the command): --config <file>  --diag");
        }

        // ---------------------------------------------------------------- 1.9.57 containers

        private static CodmVfs EnsureVfs()
        {
            if (vfs != null) return vfs;
            vfs = CodmVfs.Open(cfg, Console.Error.WriteLine);
            return vfs;
        }

        private static int CmdVfs(Options o)
        {
            var v = EnsureVfs();
            foreach (var p in v.QtsPacks)
                Console.WriteLine($"{p.Label,-10} {p.Entries.Count,8} entries  {p.Entries.Count(e => e.IsChunk),8} chunks  {p.PartsOnDisk}/{p.PartCount} parts  {p.Directory}");
            if (v.BasePack != null)
            {
                var b = v.BasePack;
                var cache = v.BaseOffsetCachePath(cfg);
                var cached = b.HasOffsets || (File.Exists(cache) && b.LoadOffsets(cache));
                Console.WriteLine($"{b.Label,-10} {b.Entries.Count,8} entries  {b.Entries.Count(e => e.IsChunk),8} chunks  {b.PartCount} parts  {b.Directory}");
                Console.WriteLine($"           offset cache: {(cached ? $"{b.EntriesWithOffsets}/{b.Entries.Count} entries ({cache})" : "not built (codm vfs-index)")}");
            }
            if (v.QtsPacks.Count == 0 && v.BasePack == null)
                Console.WriteLine("no containers found (is GameRoot a 1.9.57 install?)");
            Console.WriteLine($"total: {v.Entries.Count} entries, {v.ChunkCount} distinct chunks, {v.Entries.Count(e => e.IsBundle)} bundles");
            return 0;
        }

        private static int CmdVfsIndex(Options o)
        {
            var v = EnsureVfs();
            if (v.BasePack == null)
            {
                Console.Error.WriteLine("no base pack found");
                return 1;
            }
            v.EnsureBaseOffsets(cfg, Console.Error.WriteLine, o.Has("rebuild"));
            Console.WriteLine($"{v.BasePack.EntriesWithOffsets}/{v.BasePack.Entries.Count} entries located; cache {v.BaseOffsetCachePath(cfg)}");
            return 0;
        }

        /// <summary>
        /// Read and decompress every container entry (in storage order) and report the ones that fail; with
        /// --unpacked, compare the files in the unpack folder with a fresh read of their container entry instead
        /// (finds files unpacked before a game patch).
        /// </summary>
        private static int CmdVfsCheck(Options o)
        {
            if (o.Has("unpacked"))
                return CheckUnpacked();
            var v = EnsureVfs();
            var source = o.Get("source");
            var entries = v.Entries
                .Where(e => source == null || string.Equals(e.SourceLabel, source, StringComparison.OrdinalIgnoreCase))
                .Where(e => !o.Has("bundles") || e.IsBundle)
                .Take(o.GetInt("max", int.MaxValue))
                .ToList();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long bytes = 0;
            var failed = 0;
            var done = 0;
            foreach (var e in v.InStorageOrder(entries, x => x))
            {
                try
                {
                    var data = v.ReadRaw(e);
                    if (data.Length != e.Size)
                        throw new InvalidDataException($"read {data.Length} bytes, the index says {e.Size}");
                    bytes += data.Length;
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"FAILED {e.SourceLabel} {e.Decoded}: {ex.Message}");
                }
                if (++done % 5000 == 0)
                    Console.Error.WriteLine($"{done}/{entries.Count} entries, {bytes / (1024 * 1024):N0} MB, {failed} failed, {sw.Elapsed.TotalSeconds:F0} s");
            }
            Console.WriteLine($"{entries.Count - failed}/{entries.Count} entries read ({bytes:N0} bytes) in {sw.Elapsed.TotalSeconds:F0} s, {failed} failed");
            return failed == 0 ? 0 : 1;
        }

        private static int CheckUnpacked()
        {
            EnsureIndex();
            if (pakIndex.Vfs == null)
            {
                Console.Error.WriteLine("no containers for this config");
                return 1;
            }
            var unpacked = pakIndex.Entries.Where(e => e.IsVirtual && File.Exists(e.FullPath)).ToList();
            var differ = 0;
            var failed = 0;
            foreach (var e in pakIndex.Vfs.InStorageOrder(unpacked, x => x.Container))
            {
                try
                {
                    var fresh = pakIndex.Vfs.ReadBundle(e.Container, true);
                    var disk = File.ReadAllBytes(e.FullPath);
                    if (disk.Length != fresh.Length || !disk.SequenceEqual(fresh))
                    {
                        differ++;
                        Console.WriteLine($"DIFFERS {e.Source} {e.Name.Decoded}: {disk.Length:N0} bytes on disk, {fresh.Length:N0} from the containers  {e.FullPath}");
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"FAILED {e.Source} {e.Name.Decoded}: {ex.Message}");
                }
            }
            Console.WriteLine($"{unpacked.Count - differ - failed}/{unpacked.Count} unpacked paks match the containers, {differ} differ (delete them to unpack again), {failed} unreadable");
            return differ + failed == 0 ? 0 : 1;
        }

        private static int CmdUnpack(Options o)
        {
            if (o.Positional.Count == 0)
            {
                Console.Error.WriteLine("usage: codm unpack <text...> [--out dir] [--list] [--raw] [--chunks] [--max n] [--source puffer|extract|streaming]");
                return 1;
            }
            var v = EnsureVfs();
            var includeChunks = o.Has("chunks");
            var source = o.Get("source");
            var matches = o.Positional.SelectMany(q => v.Find(q, includeChunks)).Distinct()
                .Where(e => source == null || string.Equals(e.SourceLabel, source, StringComparison.OrdinalIgnoreCase)).ToList();
            var max = o.GetInt("max", int.MaxValue);
            if (matches.Count > max) matches = matches.Take(max).ToList();
            if (o.Has("list") || matches.Count == 0)
            {
                foreach (var e in matches)
                    Console.WriteLine($"{e.SourceLabel,-9} {e.Size,12:N0}  {e.Decoded}");
                Console.WriteLine($"{matches.Count} entries");
                return 0;
            }
            if (matches.Any(e => e.Source == VfsSource.Streaming))
                v.EnsureBaseOffsets(cfg, Console.Error.WriteLine);
            var outRoot = o.Get("out") ?? cfg.UnpackRootPath;
            var normalize = !o.Has("raw");
            var written = 0;
            long bytes = 0;
            foreach (var e in matches)
            {
                var dir = Path.Combine(outRoot, e.IsChunk ? "chunks" : e.SourceLabel);
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, e.FileName);
                try
                {
                    var stats = new BundleNormalizer.Stats();
                    var data = e.IsBundle ? v.ReadBundle(e, normalize, stats, Console.Error.WriteLine) : v.ReadRaw(e);
                    File.WriteAllBytes(path, data);
                    written++;
                    bytes += data.Length;
                    var note = stats.RewrittenFiles > 0 || stats.StreamChunks.Count > 0
                        ? $" (format 18 -> 17: {stats.ExternalObjects}/{stats.Objects} objects from {stats.Chunks.Count} chunks, {stats.StreamChunks.Count} stream chunks embedded)"
                        : "";
                    Console.WriteLine($"{e.SourceLabel,-9} {data.Length,12:N0}  {e.Decoded}{note}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"FAILED {e.Decoded}: {ex.Message}");
                }
            }
            Console.WriteLine($"{written}/{matches.Count} entries written to {outRoot} ({bytes:N0} bytes)");
            return 0;
        }

        // ---------------------------------------------------------------- helpers

        private static void EnsureIndex()
        {
            if (pakIndex != null) return;
            // a 1.9.57 install: every bundle in the containers is listed, unpacked on first use
            pakIndex = PakIndex.Build(cfg, Console.Error.WriteLine, CodmVfs.HasContainers(cfg) ? EnsureVfs() : null);
        }

        private static void EnsureWwise()
        {
            if (wwiseIndex != null) return;
            wwiseIndex = WwiseIndex.Build(cfg, Console.Error.WriteLine);
        }

        private static void EnsureCatalog(bool withBanks = true)
        {
            if (catalog != null) return;
            EnsureIndex();
            if (withBanks) EnsureWwise();
            if (withBanks)
                wwiseIndex.LoadNames(cfg, WeaponCatalog.WeaponTokens(pakIndex), Console.Error.WriteLine);
            catalog = WeaponCatalog.Build(pakIndex, withBanks ? wwiseIndex : null, WeaponClassMap.Load(cfg));
        }

        private static ContainerIndex EnsureContainerIndex(bool rebuild = false)
        {
            if (containerIndex != null && !rebuild) return containerIndex;
            EnsureCatalog();
            containerIndex = ContainerIndex.Build(catalog.ComponentPaks, cfg, Console.Error.WriteLine, rebuild);
            var linked = catalog.LinkComponentsByContainers(containerIndex);
            Console.Error.WriteLine($"components: {linked} attachment links from container paths");
            return containerIndex;
        }

        private static CabIndex EnsureCabIndex(bool rebuild = false)
        {
            if (cabIndex != null && !rebuild) return cabIndex;
            EnsureIndex();
            cabIndex = CabIndex.Build(pakIndex, cfg, Console.Error.WriteLine, rebuild);
            return cabIndex;
        }

        private static WeaponEntry ResolveWeapon(string query)
        {
            var found = catalog.Find(query);
            if (found.Count == 1)
                return found[0];
            if (found.Count == 0)
            {
                Console.Error.WriteLine($"No weapon matches '{query}'.");
                return null;
            }
            Console.Error.WriteLine($"'{query}' is ambiguous ({found.Count} matches):");
            foreach (var w in found.Take(30))
                Console.Error.WriteLine($"  {w.Folder}");
            return null;
        }

        /// <summary>Marker for a pak that is still in the 1.9.57 containers (unpacked when something loads it).</summary>
        private static string Packed(PakEntry p) => p.IsPacked ? "  (packed)" : "";

        private static string Size(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }

        // ---------------------------------------------------------------- commands

        private static int CmdConfig(Options o)
        {
            Console.WriteLine($"config file : {(File.Exists(cfg.ConfigPath) ? cfg.ConfigPath : "(defaults, no file found)")}");
            Console.WriteLine($"game root   : {cfg.GameRoot} {(Directory.Exists(cfg.GameRoot) ? "" : "(MISSING)")}");
            foreach (var p in cfg.PakFolderPaths)
                Console.WriteLine($"pak folder  : {p} {(Directory.Exists(p) ? "" : "(MISSING)")}");
            foreach (var p in cfg.SoundBankFolderPaths)
                Console.WriteLine($"bank folder : {p} {(Directory.Exists(p) ? "" : "(MISSING)")}");
            var vgm = string.IsNullOrEmpty(cfg.VgmstreamPath) ? WemTools.FindVgmstream() : cfg.VgmstreamPath;
            Console.WriteLine($"vgmstream   : {(vgm ?? "(not configured, wem files will not be converted)")}");
            Console.WriteLine($"export root : {cfg.ExportRootPath}");
            Console.WriteLine($"cache dir   : {cfg.CachePath("")}");
            Console.WriteLine($"textures    : {cfg.ImageFormat}, scale {cfg.ScaleFactor}");
            if (o.Has("write"))
            {
                cfg.Save();
                Console.WriteLine($"written {cfg.ConfigPath}");
            }
            return 0;
        }

        private static int CmdCipher(Options o, bool decode)
        {
            foreach (var name in o.Positional)
            {
                if (decode)
                {
                    var pn = PakName.Parse(name);
                    Console.WriteLine(name);
                    Console.WriteLine($"  -> {pn.Decoded}");
                    if (pn.HasModule)
                        Console.WriteLine($"     module={pn.Module} version={pn.ModuleVersion} path={pn.Path}");
                }
                else
                {
                    Console.WriteLine($"{name} -> {CodmNameCipher.Encode(name)}");
                }
            }
            return 0;
        }

        private static int CmdIndex(Options o)
        {
            EnsureIndex();
            var packed = pakIndex.Vfs != null ? pakIndex.Entries.Count(e => e.IsPacked) : 0;
            Console.WriteLine($"{pakIndex.Entries.Count} paks" + (pakIndex.Vfs != null ? $" ({packed} still in the game containers, unpacked on first use to {cfg.UnpackRootPath})" : ""));
            foreach (var kv in pakIndex.CountByTopFolder(2).OrderByDescending(kv => kv.Value).Take(o.GetInt("top", 40)))
                Console.WriteLine($"{kv.Value,7}  {kv.Key}");
            var tsv = o.Get("tsv");
            if (tsv != null)
            {
                pakIndex.WriteTsv(tsv);
                Console.WriteLine($"written {tsv}");
            }
            return 0;
        }

        private static int CmdSearch(Options o)
        {
            if (o.Positional.Count == 0) { Console.Error.WriteLine("search <text>"); return 2; }
            EnsureIndex();
            var hits = pakIndex.Search(string.Join(" ", o.Positional)).ToList();
            foreach (var e in hits.Take(o.GetInt("max", 200)))
                Console.WriteLine($"{e.Source,-9} {Size(e.Size),9}  {e.Name.Decoded}{Packed(e)}");
            Console.WriteLine($"{hits.Count} match(es)");
            return 0;
        }

        private static int CmdWeapons(Options o)
        {
            EnsureCatalog(withBanks: !o.Has("no-banks"));
            IEnumerable<WeaponEntry> list = catalog.Weapons;
            var kind = o.Get("kind");
            if (kind != null)
                list = list.Where(w => w.KindFolder.StartsWith(kind, StringComparison.OrdinalIgnoreCase));
            if (o.Has("base"))
                list = list.Where(w => w.IsBase);
            if (o.Positional.Count > 0)
            {
                var f = o.Positional[0];
                list = list.Where(w => w.Folder.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            var rows = list.ToList();
            Console.WriteLine($"{"folder",-52} {"paks",4} {"base",-22} {"skins",5} {"fx",3} {"banks",5} {"class",5}");
            foreach (var w in rows)
                Console.WriteLine($"{w.Folder,-52} {w.Paks.Count,4} {(w.IsBase ? "*" : w.Base?.Name ?? "?"),-22} {w.Skins.Count,5} {w.Effects.Count,3} {w.Banks.Count,5} {w.SharedBanks.Count,5}");
            Console.WriteLine($"{rows.Count} weapon(s), {rows.Count(w => w.IsBase)} base, {rows.Count(w => !w.IsBase && w.Base == null)} without a base match");
            return 0;
        }

        private static int CmdWeapon(Options o)
        {
            if (o.Positional.Count == 0) { Console.Error.WriteLine("weapon <id|folder|name>"); return 2; }
            EnsureCatalog();
            var w = ResolveWeapon(o.Positional[0]);
            if (w == null) return 1;
            PrintWeapon(w);
            return 0;
        }

        private static void PrintWeapon(WeaponEntry w)
        {
            Console.WriteLine($"{w.Folder}  ({w.Kind}, id {w.Id}{(w.IsBase ? ", BASE weapon" : "")}, sound class {w.WeaponClass ?? "?"}{(w.WeaponClassSource != null ? " [" + w.WeaponClassSource + "]" : "")})");
            if (w.Base != null)
                Console.WriteLine($"  base chain  : {string.Join(" -> ", w.Ancestors().Select(a => a.Folder))}");
            if (w.Skins.Count > 0)
                Console.WriteLine($"  skins       : {w.Skins.Count}  ({string.Join(", ", w.Skins.Take(8).Select(s => s.Folder))}{(w.Skins.Count > 8 ? ", ..." : "")})");
            Console.WriteLine($"  paks ({w.Paks.Count}):");
            foreach (var p in w.Paks.OrderBy(p => p.Name.Decoded))
                Console.WriteLine($"    {p.Source,-9} {Size(p.Size),9}  {p.Name.Decoded}{Packed(p)}");
            foreach (var a in w.Ancestors())
            {
                Console.WriteLine($"  base paks of {a.Folder} ({a.Paks.Count}):");
                foreach (var p in a.Paks.OrderBy(p => p.Name.Decoded))
                    Console.WriteLine($"    {p.Source,-9} {Size(p.Size),9}  {p.Name.Decoded}{Packed(p)}");
            }
            var comps = w.AllComponents();
            if (comps.Count > 0)
            {
                Console.WriteLine($"  components ({comps.Count}, run 'codm components' once to link magazines/barrels/stocks by container path):");
                foreach (var p in comps.OrderBy(p => p.Name.Decoded))
                    Console.WriteLine($"    {p.Source,-9} {Size(p.Size),9}  {p.Name.Decoded}{Packed(p)}");
            }
            if (w.Effects.Count > 0)
            {
                Console.WriteLine($"  effects ({w.Effects.Count}):");
                foreach (var p in w.Effects.OrderBy(p => p.Name.Decoded))
                    Console.WriteLine($"    {p.Source,-9} {Size(p.Size),9}  {p.Name.Decoded}{Packed(p)}");
            }
            if (w.Banks.Count > 0)
            {
                Console.WriteLine($"  sound banks ({w.Banks.Count}):");
                foreach (var b in w.Banks.OrderBy(b => b.Name))
                    Console.WriteLine($"    {b.Name,-30} {Size(b.Size),9}  embedded={b.EmbeddedCount,-4} {b.Path}");
            }
            if (w.SharedBanks.Count > 0)
            {
                Console.WriteLine($"  class banks ({w.SharedBanks.Count}, media borrowed by the family's skin banks):");
                foreach (var b in w.SharedBanks.OrderBy(b => b.Name))
                    Console.WriteLine($"    {b.Name,-30} {Size(b.Size),9}  embedded={b.EmbeddedCount,-4} {b.Path}");
            }
        }

        private static int CmdExport(Options o)
        {
            if (o.Positional.Count == 0) { Console.Error.WriteLine("export <id|folder> [--out dir]"); return 2; }
            EnsureCatalog();
            var w = ResolveWeapon(o.Positional[0]);
            if (w == null) return 1;
            var outRoot = o.Get("out") ?? cfg.ExportRootPath;
            var options = new WeaponExportOptions
            {
                Models = !o.Has("no-models"),
                Textures = !o.Has("no-textures"),
                Sounds = !o.Has("no-sounds"),
                Effects = o.Has("fx"),
                IncludeBaseWeapon = !o.Has("no-base"),
                IncludeComponents = o.Has("components"),
                ConvertWem = !o.Has("no-wav"),
                ResolveDependencies = !o.Has("no-deps"),
                SoundsByEvent = !o.Has("flat-sounds"),
                ClassBanks = o.Has("class-banks"),
                BankVariants = o.Has("bank-variants"),
                MaxDependencyPaks = o.GetInt("max-deps", 60),
                AllPrefabCopies = o.Has("all-copies"),
            };
            if (string.IsNullOrEmpty(cfg.VgmstreamPath))
                cfg.VgmstreamPath = WemTools.FindVgmstream() ?? "";
            if (options.IncludeComponents)
                EnsureContainerIndex();
            var svc = new CodmExportService(cfg, pakIndex, wwiseIndex, () => EnsureCabIndex());
            var report = svc.ExportWeapon(w, outRoot, options, Console.WriteLine);
            Console.WriteLine($"models={report.Models.Count} (+{report.SkippedPrefabCopies.Count} duplicate copies skipped) textures={report.Textures.Count} deps={report.DependencyPaks.Count} banks={report.Banks.Count} wems={report.WemsExtracted} (wav {report.WemsConverted}) warnings={report.Warnings.Count}");
            foreach (var warn in report.Warnings)
                Console.WriteLine("  ! " + warn);
            Console.WriteLine(report.OutputDir);
            return 0;
        }

        private static int CmdAnims(Options o)
        {
            if (o.Positional.Count == 0) { Console.Error.WriteLine("anims <weapon>"); return 2; }
            EnsureCatalog();
            var w = ResolveWeapon(o.Positional[0]);
            if (w == null) return 1;
            var svc = new CodmExportService(cfg, pakIndex, wwiseIndex, () => EnsureCabIndex());
            var paks = svc.SelectPaks(w, new WeaponExportOptions { IncludeBaseWeapon = !o.Has("no-base") });
            var am = new AssetsManager();
            try
            {
                am.LoadFiles(PakEntry.EnsureOnDisk(paks, Console.Error.WriteLine));
                if (!o.Has("no-deps"))
                    DependencyResolver.Resolve(am, EnsureCabIndex(), cfg, Console.WriteLine, maxPaks: o.GetInt("max-deps", 60));
                var own = new HashSet<string>(paks.Select(p => p.FullPath), StringComparer.OrdinalIgnoreCase);
                var loadedNames = new HashSet<string>(am.assetsFileList.Select(f => f.fileName), StringComparer.OrdinalIgnoreCase);
                foreach (var file in am.assetsFileList)
                {
                    if (!own.Contains(file.originalPath ?? file.fullName ?? "")) continue;
                    var localClips = file.Objects.Count(x => x is AnimationClip);
                    foreach (var obj in file.Objects)
                    {
                        if (!(obj is Animator animator)) continue;
                        animator.m_GameObject.TryGet(out var go);
                        Console.WriteLine($"Animator '{go?.m_Name}'  file={file.fileName} ({PakName.Parse(Path.GetFileName(file.originalPath ?? "")).Decoded})  clips in same file={localClips}");
                        var ctrlPtr = animator.m_Controller;
                        var ctrlFile = ctrlPtr.m_FileID == 0 ? file.fileName : (ctrlPtr.m_FileID - 1 < file.m_Externals.Count ? file.m_Externals[ctrlPtr.m_FileID - 1].fileName : "?");
                        if (!ctrlPtr.TryGet(out var rac))
                        {
                            Console.WriteLine($"  controller: UNRESOLVED (pathID {ctrlPtr.m_PathID} in {ctrlFile}{(loadedNames.Contains(ctrlFile) ? ", file loaded but object missing" : ", file not loaded")})");
                            continue;
                        }
                        Console.WriteLine($"  controller: {rac.GetType().Name} '{rac.m_Name}' in {ctrlFile}");
                        if (rac is AnimatorOverrideController aoc)
                        {
                            var resolved = 0; var missing = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                            foreach (var pair in aoc.m_Clips)
                            {
                                if (pair.m_OverrideClip.TryGet(out _)) { resolved++; continue; }
                                var ext = pair.m_OverrideClip.m_FileID == 0 ? aoc.assetsFile.fileName : (pair.m_OverrideClip.m_FileID - 1 < aoc.assetsFile.m_Externals.Count ? aoc.assetsFile.m_Externals[pair.m_OverrideClip.m_FileID - 1].fileName : "?");
                                missing[ext] = missing.TryGetValue(ext, out var n) ? n + 1 : 1;
                            }
                            Console.WriteLine($"  override clips: {aoc.m_Clips.Length} total, {resolved} resolved");
                            foreach (var kv in missing.OrderByDescending(kv => kv.Value))
                            {
                                var paksFor = EnsureCabIndex().PaksFor(kv.Key);
                                Console.WriteLine($"    {kv.Value,4} missing in {kv.Key}  ->  {(paksFor.Count == 0 ? "NOT IN ANY PAK" : string.Join(" | ", paksFor.Take(2).Select(p => "[" + p.Source + "] " + p.Name.Decoded)))}{(loadedNames.Contains(kv.Key) ? "  (loaded, object missing)" : "")}");
                            }
                            if (aoc.m_Controller.TryGet(out var baseRac))
                                Console.WriteLine($"  base controller: {baseRac.GetType().Name} '{baseRac.m_Name}' clips={(baseRac as AnimatorController)?.m_AnimationClips?.Length ?? 0}");
                            else
                                Console.WriteLine($"  base controller: UNRESOLVED");
                        }
                        else if (rac is AnimatorController ac)
                        {
                            var resolved = ac.m_AnimationClips?.Count(c => c.TryGet(out _)) ?? 0;
                            Console.WriteLine($"  controller clips: {ac.m_AnimationClips?.Length ?? 0} total, {resolved} resolved");
                        }
                    }
                }
            }
            finally
            {
                am.Clear();
            }
            return 0;
        }

        private static int CmdEvents(Options o)
        {
            if (o.Positional.Count == 0) { Console.Error.WriteLine("events <weapon> [--all]"); return 2; }
            EnsureCatalog();
            var w = ResolveWeapon(o.Positional[0]);
            if (w == null) return 1;
            var cabs = EnsureCabIndex();
            var svc = new CodmExportService(cfg, pakIndex, wwiseIndex, () => cabs);
            var paks = svc.SelectPaks(w, new WeaponExportOptions());
            var hirc = wwiseIndex.HircObjects(Console.Error.WriteLine);
            var names = wwiseIndex.Names ?? WwiseNames.Load(cfg);
            var am = new AssetsManager();
            try
            {
                am.LoadFiles(PakEntry.EnsureOnDisk(paks, Console.Error.WriteLine));
                if (!o.Has("no-deps"))
                    DependencyResolver.Resolve(am, cabs, cfg, Console.Error.WriteLine);
                var own = new HashSet<string>(paks.Select(p => p.FullPath), StringComparer.OrdinalIgnoreCase);
                var clips = new List<AnimationClip>();
                foreach (var file in am.assetsFileList)
                {
                    var isOwn = own.Contains(file.originalPath ?? file.fullName ?? "");
                    foreach (var obj in file.Objects)
                    {
                        if (obj is Animator animator && isOwn)
                            clips.AddRange(CodmExportService.ClipsForAnimator(animator, file));
                        else if (obj is AnimationClip clip && (isOwn || o.Has("all")))
                            clips.Add(clip);
                    }
                }
                clips = clips.Distinct().OrderBy(c => c.m_Name).ToList();
                var withEvents = 0; var total = 0;
                var fnCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var clip in clips)
                {
                    if (clip.m_Events == null || clip.m_Events.Length == 0) continue;
                    withEvents++;
                    Console.WriteLine($"{clip.m_Name}  ({clip.m_Events.Length} event(s), {clip.m_MuscleClip?.m_StopTime ?? 0:F2}s)");
                    foreach (var ev in clip.m_Events.OrderBy(e => e.time))
                    {
                        total++;
                        fnCounts[ev.functionName ?? ""] = fnCounts.TryGetValue(ev.functionName ?? "", out var n) ? n + 1 : 1;
                        var extra = new List<string>();
                        if (!string.IsNullOrEmpty(ev.data))
                        {
                            var h = WwiseNames.Fnv1(ev.data);
                            if (hirc.TryGetValue(h, out var r)) extra.Add($"fnv(data)=Wwise {HircTypeName(r.Type)} in {r.Bank}");
                            foreach (var id in AnimationSoundMap.IdsInData(ev.data))
                                if (hirc.TryGetValue(id, out var r2)) extra.Add($"id {id}=Wwise {HircTypeName(r2.Type)} {names.GetOrId(id)} in {r2.Bank}");
                        }
                        if (ev.intParameter != 0 && hirc.TryGetValue((uint)ev.intParameter, out var r3)) extra.Add($"int=Wwise {HircTypeName(r3.Type)} {names.GetOrId((uint)ev.intParameter)} in {r3.Bank}");
                        if (ev.floatParameter != 0 && Math.Abs(ev.floatParameter - Math.Round(ev.floatParameter)) < 1e-6 && ev.floatParameter > 1000 && hirc.TryGetValue((uint)ev.floatParameter, out var r4)) extra.Add($"float=Wwise {HircTypeName(r4.Type)} in {r4.Bank}");
                        var objRef = "";
                        if (ev.objectReferenceParameter != null && ev.objectReferenceParameter.m_PathID != 0)
                            objRef = ev.objectReferenceParameter.TryGet(out var refObj) ? $" obj={refObj.type}:{(refObj as NamedObject)?.m_Name ?? (refObj as GameObject)?.m_Name}" : $" obj=unresolved(file {ev.objectReferenceParameter.m_FileID}, path {ev.objectReferenceParameter.m_PathID})";
                        Console.WriteLine($"    {ev.time,7:F3}s  {ev.functionName,-32} data='{ev.data}' int={ev.intParameter} float={ev.floatParameter:G6}{objRef}{(extra.Count > 0 ? "   <- " + string.Join("; ", extra) : "")}");
                    }
                }
                Console.WriteLine($"{clips.Count} clip(s), {withEvents} with events, {total} event(s). Function names: {string.Join(", ", fnCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}x{kv.Value}"))}");
            }
            finally
            {
                am.Clear();
            }
            return 0;
        }

        private static int CmdMbScan(Options o)
        {
            if (o.Positional.Count == 0) { Console.Error.WriteLine("mbscan <weapon|pak> [--deps]"); return 2; }
            EnsureCatalog();
            var cabs = EnsureCabIndex();
            string[] files;
            HashSet<string> own;
            if (File.Exists(o.Positional[0]))
            {
                files = new[] { Path.GetFullPath(o.Positional[0]) };
            }
            else
            {
                var w = ResolveWeapon(o.Positional[0]);
                if (w == null) return 1;
                var svc = new CodmExportService(cfg, pakIndex, wwiseIndex, () => cabs);
                files = PakEntry.EnsureOnDisk(svc.SelectPaks(w, new WeaponExportOptions()), Console.Error.WriteLine);
            }
            own = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            var hirc = wwiseIndex.HircObjects(Console.Error.WriteLine);
            var am = new AssetsManager();
            try
            {
                am.LoadFiles(files);
                if (o.Has("deps"))
                    DependencyResolver.Resolve(am, cabs, cfg, Console.Error.WriteLine);
                var loaded = new HashSet<string>(am.assetsFileList.Select(f => f.fileName), StringComparer.OrdinalIgnoreCase);
                foreach (var file in am.assetsFileList)
                {
                    if (!own.Contains(file.originalPath ?? file.fullName ?? "")) continue;
                    foreach (var objInfo in file.m_Objects)
                    {
                        if ((ClassIDType)objInfo.classID != ClassIDType.MonoBehaviour) continue;
                        var reader = new ObjectReader(file.reader, file, objInfo);
                        MonoBehaviour mb;
                        try { mb = new MonoBehaviour(reader); } catch (Exception) { continue; }
                        var className = mb.m_Script.TryGet(out MonoScript ms) ? ms.m_ClassName : "?";
                        var goName = mb.m_GameObject.TryGet(out GameObject go) ? go.m_Name : "";
                        var bodyStart = reader.Position;
                        var body = reader.ReadBytes((int)Math.Min(reader.Remaining, 1 << 20));
                        Console.WriteLine($"MonoBehaviour {className} name='{mb.m_Name}' go='{goName}' pathID={objInfo.m_PathID} body={body.Length} B  ({PakName.Parse(Path.GetFileName(file.originalPath ?? "")).Decoded})");
                        if (o.Has("hex") && body.Length <= 256)
                        {
                            for (var off = 0; off < body.Length; off += 16)
                            {
                                var chunk = body.Skip(off).Take(16).ToArray();
                                var u32s = Enumerable.Range(0, chunk.Length / 4).Select(k => BitConverter.ToUInt32(chunk, k * 4).ToString()).ToArray();
                                Console.WriteLine($"    {off:X4}: {BitConverter.ToString(chunk).Replace("-", " "),-48}  u32: {string.Join(" ", u32s)}");
                            }
                        }

                        // PPtr pattern: int32 fileID (0..externals) + int64 pathID (non-zero), 4-byte aligned
                        var pptrHits = 0;
                        for (var off = 0; off + 12 <= body.Length; off += 4)
                        {
                            var fileId = BitConverter.ToInt32(body, off);
                            if (fileId < 0 || fileId > file.m_Externals.Count) continue;
                            var pathId = BitConverter.ToInt64(body, off + 4);
                            if (pathId == 0 || pathId < -1000000000000L) continue;
                            SerializedFile target = null;
                            string targetFile;
                            if (fileId == 0) { target = file; targetFile = file.fileName; }
                            else
                            {
                                targetFile = file.m_Externals[fileId - 1].fileName;
                                target = am.assetsFileList.FirstOrDefault(f => f.fileName.Equals(targetFile, StringComparison.OrdinalIgnoreCase));
                            }
                            if (target != null)
                            {
                                if (!target.ObjectsDic.TryGetValue(pathId, out var obj)) continue;
                                var nm = (obj as NamedObject)?.m_Name ?? (obj as GameObject)?.m_Name ?? "";
                                if (obj is MonoBehaviour mb2 && mb2.m_Script.TryGet(out MonoScript ms2)) nm = $"{ms2.m_ClassName} '{mb2.m_Name}'";
                                Console.WriteLine($"    +0x{off:X4} PPtr -> {obj.type} '{nm}' (pathID {pathId}{(fileId > 0 ? ", in " + targetFile : "")})");
                                pptrHits++;
                            }
                            else if (fileId > 0)
                            {
                                // external not loaded: only report when the pathID looks like a real Unity 5 pathID (small positive or hash-like)
                                var paks = cabs.PaksFor(targetFile);
                                if (paks.Count == 0 && !loaded.Contains(targetFile)) continue;
                                if (pathId < 0 || pathId > 1L << 40) continue;
                                Console.WriteLine($"    +0x{off:X4} PPtr -> external {targetFile} pathID {pathId}  [{(paks.Count > 0 ? paks[0].Name.Decoded : "not in any pak")}]");
                                pptrHits++;
                            }
                        }

                        // Wwise ids: any aligned u32 that is a HIRC object id in some bank
                        var wwiseHits = 0;
                        for (var off = 0; off + 4 <= body.Length; off += 4)
                        {
                            var v = BitConverter.ToUInt32(body, off);
                            if (v < 1000 || !hirc.TryGetValue(v, out var r)) continue;
                            Console.WriteLine($"    +0x{off:X4} Wwise {HircTypeName(r.Type)} {v} in {r.Bank}.bnk{(r.Copies > 1 ? $" (+{r.Copies - 1} more)" : "")}");
                            wwiseHits++;
                        }
                        if (pptrHits == 0 && wwiseHits == 0)
                            Console.WriteLine("    (no PPtr or Wwise id hits)");
                    }
                }
            }
            finally
            {
                am.Clear();
            }
            return 0;
        }

        private static CharacterCatalog EnsureCharacters()
        {
            if (characters != null) return characters;
            EnsureIndex();
            characters = CharacterCatalog.Build(pakIndex);
            return characters;
        }

        private static CharacterEntry ResolveCharacter(string query)
        {
            var found = EnsureCharacters().Find(query);
            if (found.Count == 1) return found[0];
            if (found.Count == 0) { Console.Error.WriteLine($"No character matches '{query}'."); return null; }
            Console.Error.WriteLine($"'{query}' is ambiguous ({found.Count} matches):");
            foreach (var c in found.Take(30)) Console.Error.WriteLine($"  {c.DisplayName}");
            return null;
        }

        private static int CmdCharacters(Options o)
        {
            var cat = EnsureCharacters();
            var filter = o.Positional.Count > 0 ? o.Positional[0] : null;
            var rows = cat.Characters.Where(c => filter == null || c.DisplayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            foreach (var c in rows)
                Console.WriteLine($"{c.DisplayName,-52} paks={c.Paks.Count,-3} {Size(c.Paks.Sum(p => p.Size)),9}");
            Console.WriteLine($"{rows.Count} character skin(s)");
            return 0;
        }

        private static int CmdCharacter(Options o)
        {
            if (o.Positional.Count == 0) { Console.Error.WriteLine("character <name>"); return 2; }
            var c = ResolveCharacter(o.Positional[0]);
            if (c == null) return 1;
            Console.WriteLine($"{c.Folder}  ({c.Paks.Count} pak(s))");
            foreach (var p in c.Paks.OrderBy(p => p.Name.Decoded))
                Console.WriteLine($"    {p.Source,-9} {Size(p.Size),9}  {p.Name.Decoded}{Packed(p)}");
            return 0;
        }

        private static int CmdExportCharacter(Options o)
        {
            if (o.Positional.Count == 0) { Console.Error.WriteLine("exportchar <name> [--out dir]"); return 2; }
            var c = ResolveCharacter(o.Positional[0]);
            if (c == null) return 1;
            var outRoot = o.Get("out") ?? Path.Combine(cfg.ExportRootPath, "characters");
            var options = new WeaponExportOptions
            {
                Models = !o.Has("no-models"),
                Textures = !o.Has("no-textures"),
                Sounds = false,
                ResolveDependencies = !o.Has("no-deps"),
                MaxDependencyPaks = o.GetInt("max-deps", 60),
                AllPrefabCopies = o.Has("all-copies"),
            };
            var svc = new CodmExportService(cfg, pakIndex, null, () => EnsureCabIndex());
            var report = svc.ExportCharacter(c, outRoot, options, Console.WriteLine);
            Console.WriteLine($"models={report.Models.Count} (+{report.SkippedPrefabCopies.Count} duplicate copies skipped) textures={report.Textures.Count} deps={report.DependencyPaks.Count} warnings={report.Warnings.Count}");
            foreach (var warn in report.Warnings) Console.WriteLine("  ! " + warn);
            Console.WriteLine(report.OutputDir);
            return 0;
        }

        private static int CmdWwNames(Options o)
        {
            EnsureIndex();
            EnsureWwise();
            wwiseIndex.LoadNames(cfg, WeaponCatalog.WeaponTokens(pakIndex), Console.Error.WriteLine, o.Has("rebuild"));
            var identified = wwiseIndex.BankTokens.Where(kv => kv.Value.Count > 0).ToList();
            Console.WriteLine($"{wwiseIndex.Names.Count} names resolved; {identified.Count} of {wwiseIndex.BankTokens.Count} weapon banks identified by name; cache {wwiseIndex.Names.Path}");
            foreach (var kv in identified.OrderBy(kv => kv.Key).Take(o.GetInt("max", 60)))
                Console.WriteLine($"  {kv.Key,-16} {string.Join(", ", kv.Value)}");
            return 0;
        }

        private static int CmdComponents(Options o)
        {
            EnsureContainerIndex(o.Has("rebuild"));
            var withComponents = catalog.Weapons.Count(w => w.Components.Count > 0);
            Console.WriteLine($"{catalog.ComponentPaks.Count} component paks, {withComponents} weapons with linked attachments");
            if (o.Positional.Count > 0)
            {
                var w = ResolveWeapon(o.Positional[0]);
                if (w == null) return 1;
                foreach (var c in w.AllComponents())
                    Console.WriteLine($"  {c.Source,-9} {Size(c.Size),9}  {c.Name.Decoded}  <- {string.Join(" | ", w.ComponentPaths(c).Take(2).Select(x => x.Substring(x.LastIndexOf('/', Math.Max(0, x.LastIndexOf('/') - 1)) + 1)))}");
            }
            return 0;
        }

        private static int CmdCabIndex(Options o)
        {
            var cabs = EnsureCabIndex(o.Has("rebuild"));
            Console.WriteLine($"{cabs.CabCount} serialized files in {cabs.PakCount} paks, {cabs.Failed} unreadable; cache: {CabIndex.DefaultCachePath(cfg)}");
            return 0;
        }

        private static int CmdDeps(Options o)
        {
            if (o.Positional.Count == 0) { Console.Error.WriteLine("deps <pak...>"); return 2; }
            var cabs = EnsureCabIndex();
            var am = new AssetsManager();
            try
            {
                am.LoadFiles(o.Positional.ToArray());
                foreach (var f in am.assetsFileList)
                {
                    Console.WriteLine($"{f.fileName}  ({Path.GetFileName(f.originalPath ?? f.fullName)})");
                    foreach (var ext in f.m_Externals)
                    {
                        var paks = cabs.PaksFor(ext.fileName);
                        var where = paks.Count == 0 ? "(not in any pak)" : string.Join(" | ", paks.Take(3).Select(p => $"[{p.Source}] {p.Name.Decoded}"));
                        Console.WriteLine($"  -> {ext.fileName,-40} {where}");
                    }
                }
                var report = DependencyResolver.Resolve(am, cabs, cfg, Console.WriteLine);
                Console.WriteLine($"closure: {report.Loaded.Count} extra pak(s) in {report.Rounds} round(s), {report.Unresolved.Count} unresolved, {am.assetsFileList.Count} files loaded");
            }
            finally
            {
                am.Clear();
            }
            return 0;
        }

        private static int CmdClasses(Options o)
        {
            EnsureCatalog();
            var families = catalog.Weapons.GroupBy(w => w.RootBase).OrderBy(g => g.Key.Kind).ThenBy(g => g.Key.Name).ToList();
            var shown = 0;
            foreach (var g in families)
            {
                var root = g.Key;
                if (o.Has("unknown") && root.WeaponClass != null) continue;
                Console.WriteLine($"{root.Folder,-48} {root.WeaponClass ?? "?",-8} {root.WeaponClassSource ?? "",-9} skins={g.Count(w => w != root),-3} banks={g.Sum(w => w.Banks.Count)}");
                shown++;
            }
            Console.WriteLine($"{shown} families shown, {families.Count(f => f.Key.WeaponClass == null)} without a class; map file: {catalog.ClassMap?.Path}");
            return 0;
        }

        private static int CmdClass(Options o)
        {
            if (o.Positional.Count == 0) { Console.Error.WriteLine("class <weapon> [<class>|-]"); return 2; }
            EnsureCatalog();
            var w = ResolveWeapon(o.Positional[0]);
            if (w == null) return 1;
            if (o.Positional.Count > 1)
            {
                var cls = o.Positional[1] == "-" ? null : o.Positional[1].ToLowerInvariant();
                if (cls != null && Array.IndexOf(WeaponClassMap.KnownClasses, cls) < 0)
                {
                    Console.Error.WriteLine($"unknown class '{cls}'. Known: {string.Join(", ", WeaponClassMap.KnownClasses)}");
                    return 2;
                }
                catalog.SetClass(w, cls);
                Console.WriteLine($"{w.RootBase.Folder}: class {(cls ?? "(cleared)")} -> {catalog.ClassMap.Path}");
            }
            Console.WriteLine($"{w.Folder}: family {w.RootBase.Folder}, class {w.WeaponClass ?? "?"} ({w.WeaponClassSource ?? "none"}), class banks: {string.Join(", ", w.SharedBanks.Select(b => b.Name))}");
            return 0;
        }

        private static int CmdBanks(Options o)
        {
            EnsureWwise();
            var filter = o.Positional.Count > 0 ? o.Positional[0] : null;
            var groups = wwiseIndex.Banks
                .Where(b => filter == null || b.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var g in groups)
            {
                var first = g.First();
                var langs = string.Join(",", g.Select(b => (b.Language ?? "-") + (b.HighQuality ? "/HQ" : "")).Distinct());
                Console.WriteLine($"{g.Key,-36} copies={g.Count(),-2} embedded={first.EmbeddedCount,-4} {Size(first.Size),9}  [{langs}]");
            }
            Console.WriteLine($"{groups.Count} bank name(s), {wwiseIndex.Banks.Count} files, {wwiseIndex.Media.Count} embedded media ids, {wwiseIndex.LooseWems.Count} loose wems");
            return 0;
        }

        private static int CmdBank(Options o)
        {
            if (o.Positional.Count == 0) { Console.Error.WriteLine("bank <name|path>"); return 2; }
            EnsureWwise();
            var arg = o.Positional[0];
            var path = File.Exists(arg) ? arg : wwiseIndex.PreferredCopy(Path.GetFileNameWithoutExtension(arg))?.Path;
            if (path == null) { Console.Error.WriteLine($"bank '{arg}' not found"); return 1; }
            var bank = WwiseBank.Parse(path);
            Console.WriteLine($"{bank.Name}  version={bank.Version} id={bank.BankId} size={Size(bank.FileSize)} sections={string.Join(",", bank.Sections)}");
            Console.WriteLine($"objects: {string.Join(", ", bank.ObjectTypeCounts.OrderBy(kv => kv.Key).Select(kv => $"{HircTypeName(kv.Key)}={kv.Value}"))}");
            Console.WriteLine($"embedded media: {bank.Embedded.Count}");
            var actionsById = bank.Actions.ToDictionary(a => a.Id);
            var nm = wwiseIndex.Names ?? WwiseNames.Load(cfg);
            Console.WriteLine($"events ({bank.Events.Count}):");
            foreach (var ev in bank.Events)
            {
                var acts = ev.ActionIds.Select(id => actionsById.TryGetValue(id, out var a) ? $"{a.ActionTypeName}->{nm.GetOrId(a.TargetId)}" : id.ToString());
                var srcs = bank.EventSources(ev.Id, out var ext);
                Console.WriteLine($"  {nm.GetOrId(ev.Id),-36} {srcs.Count,3} wem(s){(ext.Count > 0 ? $" +{ext.Count} ext" : "")}  {string.Join("  ", acts)}");
            }
            if (bank.Containers.Count > 0)
            {
                var sw = bank.Containers.Values.Where(c => c.Type == 6).ToList();
                if (sw.Count > 0)
                    Console.WriteLine($"switch containers: {sw.Count}; groups: {string.Join(", ", sw.Select(c => nm.GetOrId(c.SwitchGroupId)).Distinct())}; values: {string.Join(", ", sw.SelectMany(c => c.SwitchMap.Keys).Distinct().Select(nm.GetOrId).Take(40))}");
            }
            Console.WriteLine($"sounds ({bank.Sounds.Count}):");
            var embeddedIds = new HashSet<uint>(bank.Embedded.Select(m => m.Id));
            foreach (var s in bank.Sounds.OrderBy(s => s.SourceId))
            {
                string where;
                if (embeddedIds.Contains(s.SourceId)) where = "this bank";
                else if (wwiseIndex.TryResolve(s.SourceId, out var media, out var loose)) where = media != null ? Path.GetFileName(media.BankPath) : "loose " + Path.GetFileName(Path.GetDirectoryName(loose));
                else where = "UNRESOLVED";
                Console.WriteLine($"  sound {s.Id,-11} wem {s.SourceId,-11} {s.Codec,-7} stream={s.StreamType} mem={Size(s.InMemorySize),9}  {where}");
            }
            return 0;
        }

        private static string HircTypeName(byte t)
        {
            switch (t)
            {
                case 1: return "Settings";
                case 2: return "Sound";
                case 3: return "Action";
                case 4: return "Event";
                case 5: return "RandomSeq";
                case 6: return "Switch";
                case 7: return "ActorMixer";
                case 8: return "Bus";
                case 9: return "Layer";
                case 10: return "MusicSegment";
                case 11: return "MusicTrack";
                case 12: return "MusicSwitch";
                case 13: return "MusicRanSeq";
                case 14: return "Attenuation";
                case 15: return "DialogueEvent";
                case 16: return "FxShareSet";
                case 17: return "FxCustom";
                case 18: return "AuxBus";
                case 19: return "LFO";
                case 20: return "Envelope";
                case 21: return "AudioDevice";
                case 22: return "TimeMod";
                default: return "type" + t;
            }
        }

        private static int CmdWems(Options o)
        {
            if (o.Positional.Count == 0) { Console.Error.WriteLine("wems <bank> [--out dir] [--wav]"); return 2; }
            EnsureWwise();
            var arg = o.Positional[0];
            var info = File.Exists(arg)
                ? wwiseIndex.Banks.FirstOrDefault(b => string.Equals(b.Path, Path.GetFullPath(arg), StringComparison.OrdinalIgnoreCase))
                : wwiseIndex.PreferredCopy(Path.GetFileNameWithoutExtension(arg));
            if (info == null) { Console.Error.WriteLine($"bank '{arg}' not found in the index"); return 1; }
            var outDir = o.Get("out") ?? Path.Combine(cfg.ExportRootPath, "sounds", info.Name);
            if (o.Has("wav") && string.IsNullOrEmpty(cfg.VgmstreamPath))
                cfg.VgmstreamPath = WemTools.FindVgmstream() ?? "";
            EnsureCatalog();
            var r = WemTools.ExportBank(wwiseIndex, info, outDir, cfg.VgmstreamPath, o.Has("wav"), Console.WriteLine, !o.Has("flat"));
            Console.WriteLine($"{r.Extracted}/{r.SourcesTotal} extracted, {r.Converted} converted -> {outDir}");
            if (r.Unresolved.Count > 0)
                Console.WriteLine("unresolved: " + string.Join(", ", r.Unresolved));
            return 0;
        }

        private static int CmdScan(Options o)
        {
            if (o.Positional.Count == 0) { Console.Error.WriteLine("scan <pak|dir>"); return 2; }
            var path = o.Positional[0];
            var verbose = o.Has("v") || o.Has("verbose");
            if (File.Exists(path))
            {
                LegacyScan.PrintPakInfo(Scanner.ScanFile(path), verbose);
                return 0;
            }
            if (Directory.Exists(path))
            {
                var pattern = o.Positional.Count > 1 ? o.Positional[1] : "*.pak";
                LegacyScan.ScanDirectoryToConsole(path, pattern, verbose, o.Get("csv"));
                return 0;
            }
            Console.Error.WriteLine($"not found: {path}");
            return 1;
        }

        private static int CmdMono(Options o)
        {
            if (o.Positional.Count == 0) { Console.Error.WriteLine("mono <pak>"); return 2; }
            MonoDumper.DumpFile(o.Positional[0], o.Has("strings"));
            return 0;
        }

        private static int CmdTypeTree(Options o)
        {
            if (o.Positional.Count == 0) { Console.Error.WriteLine("typetree <pak>"); return 2; }
            var am = new AssetsManager();
            try
            {
                am.LoadFiles(o.Positional[0]);
                foreach (var f in am.assetsFileList)
                {
                    var withTree = f.m_Types.Count(t => t.m_Type != null && t.m_Type.m_Nodes != null && t.m_Type.m_Nodes.Count > 0);
                    Console.WriteLine($"{f.fileName}: format={f.header.m_Version} unity={f.unityVersion} types={f.m_Types.Count} withTypeTree={withTree}");
                    foreach (var t in f.m_Types.Take(o.GetInt("max", 60)))
                    {
                        var nodes = t.m_Type?.m_Nodes?.Count ?? 0;
                        var root = nodes > 0 ? t.m_Type.m_Nodes[0].m_Type : "-";
                        Console.WriteLine($"  classID={t.classID,-5} scriptIndex={t.m_ScriptTypeIndex,-3} nodes={nodes,-4} {root}");
                    }
                }
            }
            finally
            {
                am.Clear();
            }
            return 0;
        }
    }

    /// <summary>Tiny argument parser: positional values plus --key value / --flag switches.</summary>
    internal sealed class Options
    {
        public List<string> Positional { get; } = new List<string>();
        private readonly Dictionary<string, string> named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> ValueKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "config", "tsv", "out", "csv", "kind", "max", "top", "source",
        };

        /// <summary>Index of the command word: the first argument that is neither an option nor the value of a value-taking option.</summary>
        public static int FindCommandIndex(string[] args)
        {
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith("--") || (a.StartsWith("-") && a.Length == 2))
                {
                    var key = a.TrimStart('-');
                    if (key == "h" || key == "help")
                        return i;
                    if (key.IndexOf('=') < 0 && ValueKeys.Contains(key))
                        i++;
                    continue;
                }
                return i;
            }
            return -1;
        }

        public Options(IEnumerable<string> args)
        {
            var list = args.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                var a = list[i];
                if (a.StartsWith("--") || (a.StartsWith("-") && a.Length == 2))
                {
                    var key = a.TrimStart('-');
                    var eq = key.IndexOf('=');
                    if (eq >= 0)
                    {
                        named[key.Substring(0, eq)] = key.Substring(eq + 1);
                    }
                    else if (ValueKeys.Contains(key) && i + 1 < list.Count)
                    {
                        named[key] = list[++i];
                    }
                    else
                    {
                        named[key] = "true";
                    }
                }
                else
                {
                    Positional.Add(a);
                }
            }
        }

        public bool Has(string key) => named.ContainsKey(key);
        public string Get(string key) => named.TryGetValue(key, out var v) ? v : null;
        public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var v) ? v : fallback;
    }

    internal sealed class ConsoleLogger : ILogger
    {
        public void Log(LoggerEvent loggerEvent, string message)
        {
            if (loggerEvent == LoggerEvent.Verbose || loggerEvent == LoggerEvent.Debug)
                return;
            if (loggerEvent == LoggerEvent.Info && !CodmDiagnostics.Enabled)
                return;
            Console.Error.WriteLine($"[{loggerEvent}] {message}");
        }
    }
}
