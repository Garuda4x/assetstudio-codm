# Handoff: CoD Mobile 1.9.57 support (state on 2026-10-24, evening)

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
| Pak folders in the config | the game folders | `./unpack-1957/{puffer,extract,streaming}`; every container bundle is listed anyway and unpacked there on first use |
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
| `QtsPack.cs` | download pack (`PersistentData\PufferQts`) and patch pack (`PersistentData\ExtractQts\57`): index records, block tables (page count, run-table flag), page maps walked across part boundaries or one contiguous run, LZ4 blocks with a block-end check; `Read(entry[, maxBytes])` |
| `QtsfPack.cs` | base pack (`CODM_Data\StreamingAssets\AssetBundle\base`): index, pack order, offset cache (`BuildOffsets` walks the 22 parts once, about 7 minutes; `LoadOffsets`), zstd nodes; `Read(entry)` |
| `CodmVfs.cs` | opens whatever containers exist under `GameRoot` (or `VfsFolders`), one `VfsEntry` list with the file name a loose pak would have had (`PakName` parsed), chunk lookup preferring the bundle's own pack, `ReadBundle(entry, normalize)` |
| `BundleNormalizer.cs` | format-18 bundle -> stock 5.6 bundle: external object payloads from `qtc:/split/<md5>.cpak` inlined, `qtc:/stream/<md5>.cpak` texture chunks embedded as resource nodes, uncompressed UnityFS re-emitted |
| `Lz4Block.cs`, `ZstdFrame.cs` | LZ4 block decoder that stops at the raw size; zstd frame length + ZstdSharp decode |

On-demand layer (evening of 2026-10-24): `PakIndex.Build(cfg, log, vfs)` lists every container bundle as
a `PakEntry` with `Container` set and `FullPath` = `<UnpackRoot>/<source>/<file>`; `PakEntry.EnsureOnDisk`
unpacks before each load; `CabIndex` and `ContainerIndex` read packed bundles straight from the
containers (header prefix / in-memory normalization, storage order). `CodmVfs.UnpackPath`,
`ReadPrefix`, `ReadBundleHeader`, `InStorageOrder`, `Materialize`, `HasContainers`. See
[codm-containers.md](codm-containers.md#on-demand-loading).

CLI (`CodmCli/Program.cs`, section "1.9.57 containers"): `codm vfs`, `codm vfs-index [--rebuild]`,
`codm vfs-check [--source s] [--bundles] [--max n]`, `codm vfs-check --unpacked`,
`codm unpack <text...> [--list] [--source puffer|extract|streaming] [--raw] [--chunks] [--max n] [--out d]`.
Config keys (`AssetStudio.Codm/CodmConfig.cs`): `CacheDir`, `VfsFolders` (empty = auto-detect),
`UnpackRoot`; `PakFolders` entries starting with `./` are relative to the config file.
`PakName.Parse` understands the container names (`module!ver!forcedownload!path`).
Core reader: `AssetStudio/SerializedFile.cs` only skips the format-18 string list; everything else
about format 18 is handled by the normalizer, so the rest of the pipeline is unchanged.

Everything else (catalog, dependency resolver, export service, Wwise, browser) works on the unpacked
files exactly as on 1.9.55; the only change is that they are unpacked when first needed. Sound banks
are read straight from the live install (same Wwise format, folders renamed to `PufferQts` and
`ExtractQts\57`, listed in the 1957 config).

## 3. How to use it today

GUI: start `AssetStudioGUI.exe`, **CODM > Switch config...**, pick `codm.config.1957.json`. The browser
lists the whole 1.9.57 catalog; grey rows are still in the containers. Select a weapon and use
**Load checked**, **Preview mesh** or **Export weapon...** as on 1.9.55: the paks involved (and the
dependencies the resolver picks) are unpacked to `unpack-1957/` first and stay there. **Unpack checked**
writes rows without loading them. The CAB index (dependencies) and the attachment index build in the
background after the browser opens; on 1.9.57 the first build reads all bundle headers from the
containers (2.5 minutes), cached in `cache-1957/` after.

```
codm --config codm.config.1957.json weapon 2140                    # catalog: every bundle, packed ones marked (packed)
codm --config codm.config.1957.json export 2140                    # unpacks what it loads, then models, textures, sounds
codm --config codm.config.1957.json vfs-check --source puffer      # read every entry of a pack, list the failures
codm --config codm.config.1957.json vfs-check --unpacked           # unpacked files still equal to the containers?
codm --config codm.config.1957.json unpack kilo141 --list          # batch unpack / inspection, as before
```

- A skin bundle holds no mesh; exports include the base chain by default, as on 1.9.55.
- The first base-pack read builds the offset cache (7 minutes) unless `cache-1957/qtsf-AssetBundle_base-offsets.bin`
  exists; it does, keep it. `codm vfs-index --rebuild` recreates it.
- Entries present in both the patch pack (`extract`) and the base pack (`streaming`) are the same file in
  two versions; `SourcePriority` (puffer, extract, streaming) makes the catalog prefer the patch copy.
- Unpacked files are trusted while they exist. After the game patches its containers, run
  `vfs-check --unpacked` and delete what differs (or all of `unpack-1957`).

## 4. Verified on 2026-10-24

- `export 2537` (Locus railgun) under the 1957 config: 6 models, 36 + 34 first-person clips with
  notetracks, 101 textures, banks 105/105p3, 422 wems, 0 warnings, 0 unresolved references. With the
  full CAB index the resolver loads 60 dependency paks (the limit), as 1.9.55 exports of comparable
  weapons did; the earlier "4 dependency bundles" only reflected a CAB index of the 114 unpacked files.
  45 dependencies were unpacked on the fly.
- CAB index over all 22 666 bundles: 34 566 serialized files, 1 unreadable (`HARDWARE.PAK` in the patch
  pack is a 32-byte placeholder text, not a bundle), 145 s.
- `vfs-check`: patch pack 2 846 of 2 849 entries decode, download pack 88 293 of 88 464 entries decode
  (66.7 GB in 25 minutes); the 171 failures (169 chunks and 2 bundles, `component/mag/39.1` and
  `assiweapon_45030_mw11antihero`) all carry a run table.
- GUI: the Release build, driven through its real menu and dialogs (Win32 messages and MSAA from a
  script; the managed UI Automation client only sees panes on this machine), started on the 1.9.55 config
  (17 674 paks, 1 051 weapons); CODM > Switch config... to `codm.config.1957.json` re-indexed to 22 666
  paks, 2 733 weapons and 5 233 bank files in under 3 minutes. AK117 Bengal (`mainweapon_173`, 4 skin and
  41 base paks, none unpacked before) with **Load checked (replace)** and dependencies: 75 s, the 45 paks
  and 29 dependencies unpacked on the fly, among them the patch pack's `camouflage_character` (702
  chunks, 270 MB, unreadable before the page-layout fix); one dependency copy (`personalshow@4` in the
  download pack, a run-table entry) was skipped with a log line. Second run, AK117 SD (`mainweapon_207`,
  base already unpacked by the first): the 3 skin paks unpacked, load 30 s, 37 412 nodes in the main
  window's scene hierarchy; the Release build then remembers the 1957 config. `codm vfs-check
  --unpacked`: all 250 files in `unpack-1957` (the 114 written by the old reader included) equal a fresh
  read of their container entry. Final `export 2537` with the final build: same numbers and the same file
  list.
- Base pack walk: 148 592 of 148 592 entries located, 0 size mismatches.

## 5. Goal reached; what is next

Browsing and exporting 1.9.57 from the GUI works without the CLI (design: on-demand unpack through
`PakEntry`, see [codm-containers.md](codm-containers.md#on-demand-loading)). Next candidates:

1. **Run tables of the Qts packs.** Some entries with a run table fit neither known page layout: 3 of
   2 849 in the patch pack, 171 of 88 464 in the download pack (`vfs-check` lists them). The format
   notes are in codm-containers.md ("Run table"); the dead ends are in progress.md. A bundle referencing
   such a chunk fails to unpack and is skipped (logged); in the case seen (`personalshow@4`) the base
   pack's copy of the same path loaded instead.
2. **Characters and effects on 1.9.57**: same path, untested (`exportchar` on an avatar, FX export).
3. **Stale unpacks**: `vfs-check --unpacked` finds them; an automatic check (record the container stamp
   per unpacked file) would make it hands-free.
4. Optional: unpack to a temporary folder when the user does not want `unpack-1957` to grow (today it
   keeps everything loaded once: a weapon with its dependencies is a few hundred MB, mostly shared).

Alternative considered and parked: teaching the core reader about format 18 and chunk resolvers
(no files on disk, but touches `SerializedFile`, `ObjectReader` and every load path). The normalizer
approach was chosen so upstream code stays untouched.

## 6. Known gaps and gotchas

- Qts pages: an entry's page `p` is logical page `Q + p` through its part's page map, possibly in the
  next part; entries with a run table are contiguous in the part file instead (with exceptions, see 5.1).
  The reader checks every block end against the table and tries the other layout before failing.
  Part files end mid-page. Do not "simplify" any of this back to one contiguous read.
- The base-part table at offset 0x1000 (375 u32 per part) is unexplained; the walk does not need it.
- `.cpak` chunk names are stable ids: the patch pack ships newer versions of base chunks under the same
  name with another size. Always resolve a bundle's chunks from its own pack first (done in `CodmVfs`).
- Base pack nodes may carry a link value instead of `-1`; blocks shorter than 16 KB occur mid-entry;
  an entry ends exactly at its index size. The index records whose id starts with the byte `_` must not
  be mistaken for the `QTSF_PACKAGE` marker. All handled; listed so nobody "simplifies" them away.
- The containers sit on a hard disk (`Q:`): reads of many entries go through `CodmVfs.InStorageOrder`.
- Every command that builds the pak index opens the containers on the 1957 config (three log lines, 3 s).
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
- Git history: `6b0230d` container layer, `683a9e7`, `1799bb5`, `1199edd` fixes, `fd7b202` config lookup,
  then the on-demand commit (virtual pak entries, Qts page layout fix, `vfs-check`).

## 8. Kick-start prompt for the next agent

```
Repository: C:\Users\Dylan\repo\AssetStudio (private AssetStudio fork for Call of Duty Mobile, net472,
build with `dotnet build <proj> -c Release`). Read docs/handoff-1957.md first, then
docs/codm-containers.md and the 2026-10-24 entries of docs/progress.md.

State: 1.9.57 is browsed and exported from the GUI and CLI with on-demand unpacking from the QtsVFS
containers (codm.config.1957.json). Pick the next item from section 5 of the handoff with the user,
for example the Qts run tables (`codm --config codm.config.1957.json vfs-check --source extract` lists
the entries that still fail) or character exports on 1.9.57.

Rules: never launch, patch or update the game (check that CODM.exe and CODMLauncher are not running
before reading Q:\CODMGameApps); keep docs/progress.md updated with a dated entry; commit on top of
master with the attribution lines used in the existing commits; .cast is the primary export format.
```
