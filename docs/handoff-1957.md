# Handoff: CoD Mobile 1.9.57 support (state on 2026-10-24)

Read this first when picking up the 1.9.57 work. It says what exists, how to run it, what was verified,
what is open, and the recommended way to reach the goal: **browse and export the new packs from the GUI
as comfortably as the old loose paks**. Details live in the linked pages; this page is the map.

## 1. Situation

- The tooling was built against CoD Mobile **1.9.55**, which shipped loose UnityFS `.pak` bundles. On
  2026-10-24 the user updated the game to **1.9.57** and archived the old files.
- Two game trees exist on disk and two configs select between them:

| | 1.9.55 (archive) | 1.9.57 (live) |
|---|---|---|
| Game root | `Q:\CODMGameApps_Archive\CN\emulator\Extract\CODM` | `Q:\CODMGameApps\CN\emulator\Extract\CODM` |
| Config | `codm.config.json` (default) | `codm.config.1957.json` |
| Bundles | loose paks in three folders | three QtsVFS containers, read by `AssetStudio.Codm/Vfs` |
| Pak folders in the config | the game folders | `./unpack-1957/{puffer,extract,streaming}`: the output of `codm unpack` |
| Cache / exports / unpack | `cache/`, `exports/` | `cache-1957/`, `exports-1957/`, `unpack-1957/` |

- Switching: CLI `codm --config codm.config.1957.json <command>` (the file name resolves against the
  repository root from any directory); GUI **CODM > Switch config...** (remembered across sessions, the
  menu shows the current file; **Reload config** after unpacking more).
- Rules from the user: the game must never be launched, patched or updated by the tooling; reading or
  patching a copy of `CODM.exe` for static analysis is allowed. The game and `CODMLauncher` were running
  during the 1.9.57 work and rewrote the download pack on 2026-10-24 04:46; the user has closed them.
  Check with `Get-Process CODM, launcher` before touching the live install.

## 2. What exists for 1.9.57

Code (all in `AssetStudio.Codm/Vfs/`, documented in [codm-containers.md](codm-containers.md)):

| File | Role |
|---|---|
| `QtsPack.cs` | download pack (`PersistentData\PufferQts`) and patch pack (`PersistentData\ExtractQts\57`): index records, block tables, page map, LZ4 blocks; `Read(entry)` |
| `QtsfPack.cs` | base pack (`CODM_Data\StreamingAssets\AssetBundle\base`): index, pack order, offset cache (`BuildOffsets` walks the 22 parts once, about 7 minutes; `LoadOffsets`), zstd nodes; `Read(entry)` |
| `CodmVfs.cs` | opens whatever containers exist under `GameRoot` (or `VfsFolders`), one `VfsEntry` list with the file name a loose pak would have had (`PakName` parsed), chunk lookup preferring the bundle's own pack, `ReadBundle(entry, normalize)` |
| `BundleNormalizer.cs` | format-18 bundle -> stock 5.6 bundle: external object payloads from `qtc:/split/<md5>.cpak` inlined, `qtc:/stream/<md5>.cpak` texture chunks embedded as resource nodes, uncompressed UnityFS re-emitted |
| `Lz4Block.cs`, `ZstdFrame.cs` | LZ4 block decoder that stops at the raw size; zstd frame length + ZstdSharp decode |

CLI (`CodmCli/Program.cs`, section "1.9.57 containers"): `codm vfs`, `codm vfs-index [--rebuild]`,
`codm unpack <text...> [--list] [--source puffer|extract|streaming] [--raw] [--chunks] [--max n] [--out d]`.
Config keys (`AssetStudio.Codm/CodmConfig.cs`): `CacheDir`, `VfsFolders` (empty = auto-detect),
`UnpackRoot`; `PakFolders` entries starting with `./` are relative to the config file.
`PakName.Parse` understands the container names (`module!ver!forcedownload!path`).
Core reader: `AssetStudio/SerializedFile.cs` only skips the format-18 string list; everything else
about format 18 is handled by the normalizer, so the rest of the pipeline is unchanged.

Everything else (catalog, dependency resolver, export service, Wwise, browser) works on the unpacked
files exactly as on 1.9.55. Sound banks are read straight from the live install (same Wwise format,
folders renamed to `PufferQts` and `ExtractQts\57`, listed in the 1957 config).

## 3. How to use it today

```
codm --config codm.config.1957.json vfs                                   # packs, counts, offset cache state
codm --config codm.config.1957.json unpack kilo141 --list                 # find the folder names (skin and base)
codm --config codm.config.1957.json unpack mainweapon_2140_kilo141whitecentipede mainweapon_1939_kilo141
codm --config codm.config.1957.json weapon 2140                           # catalog over the unpacked files
codm --config codm.config.1957.json export 2140                           # models, textures, sounds, notetracks
```

- A skin bundle holds no mesh: unpack the **base weapon** too (the catalog's base chain shows it;
  `unpack <weapon name> --list` lists every entry whose decoded name contains the text).
- Weapon animations come from the shared packs; they are unpacked already (`weapons/animations/1p`,
  `br`, `unarmed`, `weapons/animationcontrollers` from all three sources, 770 MB in `unpack-1957`).
- The first base-pack read builds the offset cache (7 minutes) unless `cache-1957/qtsf-AssetBundle_base-offsets.bin`
  exists; it does, keep it. `codm vfs-index --rebuild` recreates it.
- Entries present in both the patch pack (`extract`) and the base pack (`streaming`) are the same file in
  two versions; `SourcePriority` (puffer, extract, streaming) makes the catalog prefer the patch copy.
- GUI: start `AssetStudioGUI.exe`, **CODM > Switch config...**, pick `codm.config.1957.json`; the browser
  lists what has been unpacked. There is no unpack button yet (section 5).

## 4. Verified on 2026-10-24

- `export 2537` (Locus railgun) under the 1957 config equals the 1.9.55 result: 6 models, 36 + 34
  first-person clips with notetracks, 101 textures, 4 dependency bundles, banks 105/105p3, 422 wems.
- 111 entries unpacked without failure: Locus 2537/104, Vector 247/249/013, Kilo 2140/1939, Alias,
  the shared animation packs and controllers from all three containers.
- Base pack walk: 148 592 of 148 592 entries located, 0 size mismatches.
- Qts location rule validated on about 150 random entries of both Qts packs.

## 5. Goal and recommended next step: browse and export without the CLI

The browser should show the whole 1.9.57 catalog (11 122 module bundles, 11 544 base and patch
bundles) and materialize only what gets loaded or exported. Recommended design (on-demand unpack
through the existing `PakEntry` abstraction, no change to the reader):

1. **Virtual pak entries.** In `PakIndex.Build` (`AssetStudio.Codm/PakIndex.cs`), when the config's
   game root holds containers (`CodmVfs.DefaultFolders(cfg).Any(Directory.Exists)` or `VfsFolders`),
   open `CodmVfs` and add one `PakEntry` per non-chunk `VfsEntry`: `Name` from the entry, `Source` from
   `SourceLabel`, `Size`, and `FullPath = <UnpackRoot>/<source>/<FileName>`. Keep a reference to the
   `VfsEntry` on the `PakEntry` (new property) and mark it virtual. Entries already unpacked need no
   special handling, the path simply exists.
2. **Materialize on demand.** Add `PakEntry.EnsureOnDisk(CodmVfs vfs, Action<string> log)`: if
   `FullPath` is missing, `vfs.ReadBundle(entry, normalize: true)` and write it. Call it before every
   place that opens paks by path: `CodmExportService.cs` lines ~138 and ~481 (`am.LoadFiles`),
   `DependencyResolver.cs` lines ~83 and ~132, `AssetStudioGUIForm.Codm.cs` `LoadPaksFromBrowser`
   (line ~123), `ContainerIndex.cs` line ~113 (`new FileReader(pakPath)`), `BundleHeaderReader.cs`
   line ~28. A single helper in `CodmVfs` or a static `PakMaterializer` keeps this to one line per site.
3. **CAB index without materializing everything.** `CabIndex.Build` reads every pak's bundle header
   (`BundleHeaderReader`) to map CAB names to paks; for virtual entries read the header from
   `vfs.ReadRaw(entry)` instead of a file (the first 64 KB of the raw bundle is enough for the
   directory when blocks info is at the start; the Qts reader has no partial read yet, so start with a
   full `ReadRaw` and cache like `cache/cab-index.json`, about as long as the 1.9.55 build of 5 minutes).
   Same for `ContainerIndex` (2 000 attachment bundles).
4. **Session plumbing.** `CodmSession` (`AssetStudioGUI/Codm/CodmSession.cs`) opens the `CodmVfs` once
   next to `PakIndex` and passes it to the export service; `CodmBrowserForm` shows a "virtual" marker
   (size, not yet unpacked) and a log line while materializing. Wwise name recovery already takes
   weapon tokens from the container names (`Program.WeaponTokens`); do the same in `CodmSession.WeaponTokens`.
5. **Keep `codm unpack`** as the batch path and for `--raw` inspection.

Alternative considered and parked: teaching the core reader about format 18 and chunk resolvers
(no files on disk, but touches `SerializedFile`, `ObjectReader` and every load path). The normalizer
approach was chosen so upstream code stays untouched.

## 6. Known gaps and gotchas

- The base-part table at offset 0x1000 (375 u32 per part) is unexplained; the walk does not need it.
- `.cpak` chunk names are stable ids: the patch pack ships newer versions of base chunks under the same
  name with another size. Always resolve a bundle's chunks from its own pack first (done in `CodmVfs`).
- Base pack nodes may carry a link value instead of `-1`; blocks shorter than 16 KB occur mid-entry;
  an entry ends exactly at its index size. The index records whose id starts with the byte `_` must not
  be mistaken for the `QTSF_PACKAGE` marker. All handled; listed so nobody "simplifies" them away.
- `codm weapons` / `weapon` print three container lines on the 1957 config (the VFS opens for weapon
  tokens); cosmetic.
- Characters (`cod_models/avatar/...`) and effects unpack through the same path; only the Alias body was
  checked (`codm scan` shows meshes, Avatar, SkinnedMeshRenderer). `exportchar` on 1.9.57 is untested.
- The `codm.config.1957.json` `PakFolders` are relative (`./unpack-1957/...`); keep the config at the
  repository root.
- Readers open container files with read-write sharing because the running game held them; that stays
  harmless with the game closed.

## 7. Where the knowledge is

- [codm-containers.md](codm-containers.md): byte-level formats of the three containers, format 18, the
  normalizer, the CLI.
- [progress.md](progress.md): dated log of findings, dead ends and decisions (2026-10-24 entries).
- [codm-install-layout.md](codm-install-layout.md): the 1.9.55 layout (still what the default config reads).
- [cli.md](cli.md), [gui.md](gui.md), [README.md](../README.md).
- Git history: `6b0230d` container layer, `683a9e7`, `1799bb5`, `1199edd` fixes, `fd7b202` config lookup.

## 8. Kick-start prompt for the next agent

```
Repository: C:\Users\Dylan\repo\AssetStudio (private AssetStudio fork for Call of Duty Mobile, net472,
build with `dotnet build <proj> -c Release`). Read docs/handoff-1957.md first, then
docs/codm-containers.md and the 2026-10-24 entries of docs/progress.md.

Goal: browse and export Call of Duty Mobile 1.9.57 content from the GUI as comfortably as the
1.9.55 loose paks. Today the 1.9.57 bundles sit in QtsVFS containers; `codm unpack` extracts them as
stock bundles into unpack-1957/ and the unchanged pipeline (catalog, dependency resolver, export,
weapon browser) works on those files. Implement the on-demand design in section 5 of the handoff:
virtual PakEntry rows for every container entry, materialized when loaded or exported, CAB and
container indexes built from the container data, CodmSession/CodmBrowserForm wired up, Wwise weapon
tokens from the container names in the GUI too. Verify with `codm --config codm.config.1957.json
export 2537` (expected: 6 models, 36 + 34 clips, 101 textures, 422 wems) and by loading a weapon
that has not been unpacked yet in the browser after CODM > Switch config to codm.config.1957.json.

Rules: never launch, patch or update the game (check that CODM.exe and CODMLauncher are not running
before reading Q:\CODMGameApps); keep docs/progress.md updated with a dated entry; commit on top of
master with the attribution lines used in the existing commits; .cast is the primary export format.
```
