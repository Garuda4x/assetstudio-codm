using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetStudio;
using AssetStudio.Codm;
using AssetStudio.Codm.Wwise;

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

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Logger.Default = new ConsoleLogger();
            if (args.Length == 0 || args[0] == "-h" || args[0] == "--help" || args[0] == "help")
            {
                PrintUsage();
                return 0;
            }

            var opts = new Options(args.Skip(1));
            cfg = CodmConfig.Load(opts.Get("config"));
            if (opts.Has("diag"))
                cfg.Diagnostics = true;
            CodmDiagnostics.Enabled = cfg.Diagnostics;

            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "config": return CmdConfig(opts);
                    case "decode": return CmdCipher(opts, decode: true);
                    case "encode": return CmdCipher(opts, decode: false);
                    case "index": return CmdIndex(opts);
                    case "search": return CmdSearch(opts);
                    case "weapons": return CmdWeapons(opts);
                    case "weapon": return CmdWeapon(opts);
                    case "export": return CmdExport(opts);
                    case "banks": return CmdBanks(opts);
                    case "bank": return CmdBank(opts);
                    case "wems": return CmdWems(opts);
                    case "scan": return CmdScan(opts);
                    case "mono": return CmdMono(opts);
                    case "typetree": return CmdTypeTree(opts);
                    default:
                        Console.Error.WriteLine($"Unknown command '{args[0]}'.");
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
            Console.WriteLine("  export <id|folder> [--out d]   export models (cast), textures, sounds [--fx] [--no-sounds] [--no-models] [--no-textures] [--no-base] [--components]");
            Console.WriteLine("  banks [filter]                 list sound banks");
            Console.WriteLine("  bank <name|path>               show a bank: events, actions, sounds and where their wems live");
            Console.WriteLine("  wems <bank> [--out d] [--wav]  extract every wem a bank uses (converted with vgmstream when --wav)");
            Console.WriteLine("  scan <pak|dir> [-v] [--csv f]  bundle metadata scan (types, names, container paths)");
            Console.WriteLine("  mono <pak>                     list MonoScript / MonoBehaviour and embedded strings");
            Console.WriteLine("  typetree <pak>                 report whether the bundle carries type trees");
            Console.WriteLine();
            Console.WriteLine("Global: --config <file>  --diag");
        }

        // ---------------------------------------------------------------- helpers

        private static void EnsureIndex()
        {
            if (pakIndex != null) return;
            pakIndex = PakIndex.Build(cfg, Console.Error.WriteLine);
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
            catalog = WeaponCatalog.Build(pakIndex, withBanks ? wwiseIndex : null);
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
            Console.WriteLine($"{pakIndex.Entries.Count} paks");
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
                Console.WriteLine($"{e.Source,-9} {Size(e.Size),9}  {e.Name.Decoded}");
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
            Console.WriteLine($"{w.Folder}  ({w.Kind}, id {w.Id}{(w.IsBase ? ", BASE weapon" : "")})");
            if (w.Base != null)
                Console.WriteLine($"  base chain  : {string.Join(" -> ", w.Ancestors().Select(a => a.Folder))}");
            if (w.Skins.Count > 0)
                Console.WriteLine($"  skins       : {w.Skins.Count}  ({string.Join(", ", w.Skins.Take(8).Select(s => s.Folder))}{(w.Skins.Count > 8 ? ", ..." : "")})");
            Console.WriteLine($"  paks ({w.Paks.Count}):");
            foreach (var p in w.Paks.OrderBy(p => p.Name.Decoded))
                Console.WriteLine($"    {p.Source,-9} {Size(p.Size),9}  {p.Name.Decoded}");
            foreach (var a in w.Ancestors())
            {
                Console.WriteLine($"  base paks of {a.Folder} ({a.Paks.Count}):");
                foreach (var p in a.Paks.OrderBy(p => p.Name.Decoded))
                    Console.WriteLine($"    {p.Source,-9} {Size(p.Size),9}  {p.Name.Decoded}");
            }
            if (w.Components.Count > 0)
            {
                Console.WriteLine($"  components ({w.Components.Count}):");
                foreach (var p in w.Components.OrderBy(p => p.Name.Decoded))
                    Console.WriteLine($"    {p.Source,-9} {Size(p.Size),9}  {p.Name.Decoded}");
            }
            if (w.Effects.Count > 0)
            {
                Console.WriteLine($"  effects ({w.Effects.Count}):");
                foreach (var p in w.Effects.OrderBy(p => p.Name.Decoded))
                    Console.WriteLine($"    {p.Source,-9} {Size(p.Size),9}  {p.Name.Decoded}");
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
            };
            if (string.IsNullOrEmpty(cfg.VgmstreamPath))
                cfg.VgmstreamPath = WemTools.FindVgmstream() ?? "";
            var svc = new CodmExportService(cfg, pakIndex, wwiseIndex);
            var report = svc.ExportWeapon(w, outRoot, options, Console.WriteLine);
            Console.WriteLine($"models={report.Models.Count} textures={report.Textures.Count} banks={report.Banks.Count} wems={report.WemsExtracted} (wav {report.WemsConverted}) warnings={report.Warnings.Count}");
            foreach (var warn in report.Warnings)
                Console.WriteLine("  ! " + warn);
            Console.WriteLine(report.OutputDir);
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
            Console.WriteLine($"events ({bank.Events.Count}):");
            foreach (var ev in bank.Events)
            {
                var acts = ev.ActionIds.Select(id => actionsById.TryGetValue(id, out var a) ? $"{a.ActionTypeName}->{a.TargetId}" : id.ToString());
                Console.WriteLine($"  {ev.Id,-11} {string.Join("  ", acts)}");
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
            var r = WemTools.ExportBank(wwiseIndex, info, outDir, cfg.VgmstreamPath, o.Has("wav"), Console.WriteLine);
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
            "config", "tsv", "out", "csv", "kind", "max", "top",
        };

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
