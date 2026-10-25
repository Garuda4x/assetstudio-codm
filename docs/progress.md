# Progress log

Dated record of what was built, what was found out about the game data, what was tried and did not
work, and what is still open. The git history has the code-level detail; this is the narrative.
Older history (the animation-format reverse engineering, July to September 2026) is summarised in
[research-log.md](research-log.md).

## 2026-10-20: repository, navigation, sounds, FX

**Repository.** Upstream Perfare remote dropped, history kept; the uncommitted work of the previous
months committed in five logical commits (core reader, animation decoder, Cast/glTF export, GUI,
PakScanner). All projects pinned to `net472`. Probe scripts moved out to
`C:\Users\Dylan\repo\AssetStudio_research_archive`. Reader diagnostics gated behind one switch
(`CodmDiagnostics`, `"Diagnostics"` in the config). Config moved to `codm.config.json`.

**Finding: the pak names are a cipher.** A fixed 13-pair letter swap with the case flipped, `$` for
`/`, `!` for `.`: `LUJ_EUJMCF$XMPAUVF$...` is `cod_models/weapons/...`. Later the same day the digits
turned out to be swapped too (0-9, 1-2, 3-4, 5-6, 7-8); the ids in decoded names are the real game ids
(Locus railgun is 2537, Locus 104), which the prefab names inside the bundles confirmed.

**Finding: weapons are split across bundles.** A skin folder (`mainweapon_2537_locusrailgun`) holds
textures and overrides; the mesh sits in the base weapon folder (`mainweapon_104_locus`, shipped by
`normal_baseweapon_*` modules); skins start with the base name. Every download module copy of a path
is a different bundle holding a slice. Weapon bundles contain no animation clips: the override
controller sits in a small controller bundle and the clips in shared packs
(`cod_models/weapons/animations/1p`, `br`, `unarmed`...), referenced by CAB name.

**Built.** `AssetStudio.Codm` library (name cipher, pak index, weapon catalog with base chains,
Wwise v135 bank parser and index, export service), `codm.exe` CLI, CODM menu and weapon browser in
the GUI, CAB index (bundle headers only, cached) plus dependency resolver, weapon class map for the
`wpn_<class>` banks, per-event wem export, attachment linking through the asset paths inside the
attachment bundles (container index), FX export through the materials' texture bundles,
`ParticleSystemRenderer` parsed for its materials, CoD Mobile's 2017-style vertex formats accepted.
Cast became the default export; GLB and FBX opt-in.

**Finding: Wwise.** Bank version 135, no names. 192 000 embedded media ids across 3 819 bank files;
skin banks borrow media from other banks, a global media index resolves 99.95 % of sources. Event ids
are FNV-1 of names and the weapon events follow patterns (`play_wpn_locus_fire_1p` in `105.bnk`,
`play_weapon_sniper_raise_3p` in `wpn_sniper`); candidate hashing recovers the names that fit the
patterns and identifies which weapon a numeric bank belongs to. Bank numbers are not the pak ids of
the same kind (`105.bnk` is the Locus, `throwweapon_105_cannon_turret` is unrelated), so the name
match is the primary link and the numeric match only a fallback.

**Dead ends.**
- Bank media sharing as a class signal: skin banks borrow handling sounds from several `wpn_*`
  banks, so inferring the weapon class from them gave wrong classes (Vector -> pistol). Replaced
  by a seeded class list plus `weapon-classes.json`.
- `MonoBehaviour` blobs as a per-weapon manifest: `AssetRef` only lists the prefab's LOD meshes,
  `CameraAnimation` its camera clips, `WeaponControllerAsset` two config ids; no Wwise ids anywhere.
- The config tables (`DataTableBytes`) are not a repeating XOR: per-position byte statistics and a
  brute force over 4-byte keys found nothing; the high 16 bits of each dword are near-constant per
  file, so it is a short-key stream cipher of some kind. Decrypting them means static analysis of
  `CODM.exe`.
- `CODM.exe` plain strings: 96 000 strings in 290 MB and no Wwise names, so the IL2CPP metadata (and
  with it every C# string literal and class layout) is encrypted in the binary.
- Switch values inside the class banks (the per-weapon selector) did not match any generated name.

**Characters.** `cod_models/avatar/<skin>` (573 folders, bot and event variants under `pvebot`,
`vfw`); `cod_models/cly` and `cod_models/br` are pickups and boxes, not characters. Character
catalog, CLI and browser root added; body, UI and arms prefabs export as Cast. The `_1P` arms prefab
resolves no mesh (assigned at runtime from a shared asset, source not found yet). "Preview mesh"
button added to the browser.

**Sound export trimmed.** Weapon exports take the weapon's own banks and its base chain's; class
banks (`wpn_sniper`, 337 wems) and the lettered bank variants (`247a`, `247c`...) are opt-in.

## 2026-10-21: camera clips

**Finding.** Every first-person animation has a sibling `<name>_Camera` clip animating the Animator
root (path hash 0). The archive showed they had only ever been exported alone (an 8.8 KB skeleton-only
dud next to each weapon animation, plus the "orphan camera" exception path); combining them with the
weapon animation had been a manual Blender step.

**Built.** `CameraTrackMerger`: a `Camera` bone under the rig root receives the camera clip's root
curves inside the partner animation, for every Cast/FBX/GLB write. Only rigs the partner animation
drives (the first-person arms) get the bone; where several clips share a short name the one keying
the most bones wins. On the knife 7 of 8 camera clips merge; `ProneCrawlBwd_Camera` carries no
decoded curves.

## 2026-10-22: sound triggers on the animations

**Finding.** The clips carry Unity `AnimationEvent`s and the game keys its weapon audio on them:
semantic triggers (`PlayFirstAttackSound`, `PlayStabSound`, `PlayRaiseSound`, `PlayPutDownSound`,
`PlayInspectionSound1/2`, `PlayClipInSound`, `PlayClipOutSound`, `PlayBoltBackAndReleaseSound`,
`FiringEvent`) with empty data, and `PlayEventSound` with ids in the data. A plain number is a Wwise
event id (`347825172` in `MP_GEN.bnk`); the 64-bit pairs (`562950372539117~1125900325960429|8`) are
config-table ids (tag in the high bits, same low 32 bits in both halves), not Wwise hashes. The Cast
exporter had been writing the events as notification tracks all along.

**Built.** `anims/notetracks.txt` per prefab (time, function, decoded Wwise ids with bank, guessed
bank events by function name), wems of clip-referenced Wwise events under
`sounds/_from_animation_events/`, `codm events`. `Windows_HQ` bank copies preferred where they exist
(79 banks; the rest is 22 kHz Vorbis). The event vocabulary was widened with the function-name words;
total recovered names 90, the `wpn_melee` events still unnamed, so the melee trigger-to-wem pairing
stays by ear.

## 2026-10-24: the game was updated to 1.9.57 (first look)

The 1.9.55 install the tooling was built against (client 2026-04-21, exe 290 MB) was moved to
`Q:\CODMGameApps_Archive`; `Q:\CODMGameApps` now holds 1.9.57.0 (client 2026-09-03, patch 1.9.57.8 of
2026-10-21, exe 306 MB). The engine is still Unity 5.6.4p4 (`globalgamemanagers`, bundle headers). The
browser finds 0 weapons because none of the configured pak folders exist or hold bundles any more.

**Layout.** `PersistentData\Puffer` became `PersistentData\PufferQts`, `PersistentData\Extract\55`
became `PersistentData\ExtractQts\57`. The 17 674 individual `.pak` bundles are gone. Everything sits
in three packed stores of Tencent's **QtsVFS / Qtsf** system (class names in the exe: `QtsfSys`,
`QtsfPacker`, `QtsfPatcher`, `QtsfDiffer`, `QtsfMerger`, `QtsfStorage`, `QtsVFSManager`, `QtskDB`):

| Store | Files | Content |
|---|---|---|
| base pack `CODM_Data\StreamingAssets\AssetBundle\base\` | `AssetBundle_base.pak` (21 MB index), `AssetBundle_base_0..21.pak` (2 000 000 000 bytes each, 42 GB), `AssetBundle_base_packorder.bin` | 148 592 entries: 10 988 ciphered pak names (the former StreamingAssets bundles: `_s_br` 4 357, textures 1 807, `cod_models` 1 612, `_s_cly`, scenes, prefabs, effects) + 137 604 `<md5>.cpak` chunks |
| Qts pack `PersistentData\PufferQts\` | `AssetBundle.pak` (30 MB index), `AssetBundle_0..50.pak` (250-300 MB each, 29 GB), `puffer_res.eifs` | 10 108 module entries, i.e. all former Puffer module bundles including the weapons (`normal_weapon_cn13.0.7.forcedownload.cod_models/weapons/mainweapon/...`, Locus 104/2537, Vector 247/249, `avatar/c_f_alias`) + 77 343 `dynamic/split/<md5>.cpak` chunks |
| Qts patch `PersistentData\ExtractQts\57\` | `AssetBundle.pak` = `[QTCVFSDB]` (1.17 MB index), `AssetBundle_0..2.pak` (1.3 GB), `DynamicBaseDB.pak` (33 MB) | 556 `split/*.cpak` chunks; `DynamicBaseDB` lists 76 097 chunk paths plus 2 969 wem, 1 509 bnk and 206 mp4 paths in plain text |

**Base pack format (verified by rebuilding one bundle).** Index: header `01 00 00 01 01 02 03 04`,
page size 4096, `QTSF_PACKAGE` marker, then one node per entry: node header `[self offset][-1][node
size][u64 hash][8][payload size][0x20000]`, payload `QTSF`, 8-byte entry id, 8-byte kind hash (one
value for chunks, one for named files), u32 name length, name (4-byte aligned), u32 uncompressed size,
u32 part number, 20-byte hash, entry id again. `packorder.bin` ("QPKO") lists the 148 592 ids in
storage order. Data parts: 4 KB header, a 375-entry u32 table at 0x1000 (meaning open), then nodes
from 0x1a000: node header `[self offset][-1][size][u64 hash]`, block header `[16][compressed
size][method 2][raw size, 16384 or the remainder]`, a zstd frame (single segment) plus 8 trailing bytes,
8-byte aligned. Walking 97 consecutive nodes from the first `UnityFS` literal in part 0 produced
`cod_prefabs/uitemporaryresources/34.1+30.1` with exactly the 1 581 346 bytes its index record states
(part 0). The record carries no offset: entries follow the pack order, so locating one means walking
the nodes (or finding the table that does it). The `.cpak` names are not md5/sha1/sha256 of any of 25
spellings of the old pak names; the chunk sizes (median 88 KB, up to 134 MB, 68 GB in total against
10 GB for the named entries) say they are pieces of large files for delta patching.

**Qts pack format (partly verified).** Data parts: header `1c 00 00 00, 1001, 20201112, <build
time>, 4096, 128000, 32, 128000` (4 KB pages, 128 000 pages per part). An entry is a u32 page list
(one per 4 KB of raw data) followed by LZ4 blocks of 16 KB raw each, back to back with no block
headers (the first two blocks of a 1 074 254-byte bundle decode cleanly). Index files share the magic
family `xx xx 34 01` (`ed ff 34 01` for the two AssetBundle indexes, `33 89 34 01` for
`DynamicBaseDB.pak`, `c0 61 34 01` for `puffer_res.eifs`) and store plain lowercase paths; the module
entries are the old cipher in lower case under `dynamic/`. No `UnityFS` literal was found in sampled
regions of the PufferQts parts, so their block layout still has to be confirmed.

**Inside the bundles.** UnityFS 6 / 5.6.4p4 as before, blocks info LZ4, but the data block is now
stored uncompressed (the container compresses). The serialized file format went from **17 to 18** with
one inserted field after `enableTypeTree`: a u32 count and that many NUL-terminated strings of the
form `qtc:/split/<md5>.cpak` (three in the probe), followed by the usual type list (126 types in the
probe, standard layout including MonoBehaviour script ids). Our reader dies there (it takes the
string bytes as the type count). Deeper changes are unknown until that is fixed.

**Unchanged.** Wwise banks: version 135, same names (`105.bnk`, `wpn_sniper.bnk`, `MP_GEN.bnk`), now
under `PufferQts\Audio\GeneratedSoundBanks\Windows\dynamic\` (4 445 banks, 6 637 wems; language
subfolders) and `Windows_HQ\dynamic\` (82 banks, no loose wems any more), plus
`StreamingAssets\Audio\GeneratedSoundBanks\Windows` (372 banks, 1 749 wems) and a new
`ExtractQts\57\Audio\GeneratedSoundBanks\Windows` (333 banks). The name cipher is unchanged (`codm
decode` of names from the new indexes gives the right paths). `0db.bytes` keeps its format (197 MB ->
47 MB, 68 192 entries). Config tables still ciphered names, still encrypted: `DataTableBytes` 172
files (was 106), `DataTableDiffBytes` 70, `DataAssetBytes` 25, `DataAssetDiffBytes` 50,
`DataPropertyBytes` 8, `LanguageBytes` 21, new `PVSBytes`, `AudioBytes`, `TextJson`.

**Consequence.** Nothing of the pak pipeline works on 1.9.57 until a container layer exists:
a QTSF index + node walker + zstd (ZstdSharp) for the base pack; a Qts DB index + page/LZ4 reader for
PufferQts (where the weapons live); the version-18 string list in `SerializedFile`; new folders in the
config; and the chunk-to-file mapping for the `.cpak` entries. Sounds need only the new folders.
Pointing `GameRoot` at `Q:\CODMGameApps_Archive\CN\emulator\Extract\CODM` restores the 1.9.55 workflow
meanwhile.

## 2026-10-24, later: config switch, Qts index decoded, format 18 decoded

**Built.** `CacheDir` in the config and `CodmConfig.CachePath` (caches of different game versions no
longer collide); `codm.config.json` now points at the 1.9.55 archive, `codm.config.1957.json` at the
live install (sound banks only, `cache-1957`, `exports-1957`); the CLI accepts `--config` before the
command; the GUI has **CODM > Switch config...** (remembered in the user settings) and shows the current
file in the menu. `SerializedFile` skips the version-18 string list.

**Qts pack (PufferQts / ExtractQts), verified on ~150 entries.** Index file (`AssetBundle.pak`, magic
`ed ff 34 01`): header `[magic][stamp][4096][128000][QTSF section offset][...][0x34][table count][part
count][part descriptor offset][...]`; a directory of `(offset, size)` pairs from 0x34, one **block
table** per entry; 16-byte part descriptors `98 3e 34 01, stamp, 4096, 128000` (96 for PufferQts, 3 for
the patch pack, in part-file order, stamps equal the part files' header stamps); then a QTSF node section
(same node layout as the base pack index) with one record per entry: `u32 name length, u32 20, u32
uncompressed size, u32 record ordinal, u32 timestamp, u32 0x01001389, u32 x, name`. Block table of
entry `i` (same ordinal): `u16 block count, u16 last block raw size, u32 0, u32 end offsets of blocks
0..n-2, then (u32 x re-encoded, u32 P << 16, u32 Q), optional extra values (some entries carry a second
tuple and flag bytes), 16-byte hash`. **Location rule:** `part = Q / 128000`, `logical = Q % 128000`,
`physical = pageMap[logical]`, data at `32 + 128000 + 512000 + physical * 4096` in `AssetBundle_<part>.pak`
(part file = 32-byte header, 128 000 flag bytes, 128 000 x u32 page map, 128 000 data pages). An
entry is its LZ4 blocks back to back, 16 KB raw each, compressed sizes from the block table. The
record's timestamp and `x` are not needed. Named module bundles (the weapons) are stored whole: the
Locus railgun entries decode to `UnityFS` headers. The `.cpak` chunk entries use the same scheme.
`AssetBundle_Merge.pak` is a copy of the index; `QtcLocalFile.pak` and `DynamicBaseDB.pak` list audio
and video paths in plain text.

**Format 18.** See [codm-formats.md](codm-formats.md#serialized-file-format-18-1957): string list of
`qtc:/split/<md5>.cpak` chunk references, variable-length object entries with a flag that moves the
payload into a chunk at an offset, stock script types and externals. The three chunks of the probe
bundle sit in the base pack (parts 7, 16, 17) with exactly the sizes the object table implies.

**Base pack location, still open.** Index records give size and part but no offset; data node hashes
are per block (not the entry id); the 375-entry table at 0x1000 of each part matches neither node nor
entry starts in any unit tried (16 to 4096 bytes). Entry boundaries are visible while walking nodes
(a block shorter than 16 KB ends an entry; entries follow `packorder.bin`), so a one-time sequential
walk per part builds the offset cache.

**Dead ends today.** The record timestamp as part selector (it is a build time); `x` as page, byte or
16-byte offset; the second tuple in oversized block tables as location (the first tuple is right); md5 /
sha1 / sha256 of 25 spellings of the old pak names as `.cpak` names (chunks are content pieces).

## 2026-10-24, night: container layer, first 1.9.57 bundles through the pipeline

**Built.** `AssetStudio.Codm/Vfs`: `QtsPack` (download and patch packs), `QtsfPack` (base pack with the
offset walk cache), `CodmVfs` (all stores, chunk lookup), `BundleNormalizer` (format 18 -> stock 17
bundle), `Lz4Block`, `ZstdFrame` (ZstdSharp.Port from NuGet). CLI: `codm vfs`, `codm vfs-index`,
`codm unpack`. Config: `VfsFolders`, `UnpackRoot`, `./`-relative `PakFolders`; `codm.config.1957.json`
reads the `unpack-1957` folders. `PakName` understands the `module!ver!forcedownload!path` prefix.
Reference: [codm-containers.md](codm-containers.md).

**Verified.** `codm --config codm.config.1957.json export 2537` on the unpacked bundles (Locus railgun
and base, shared animation packs and controllers from the base and patch packs, 102 entries, 750 MB)
gives the same result as on 1.9.55: 6 models, 36 + 34 first-person clips with notetracks, 101 textures
(none failing), 4 dependency bundles, banks 105 and 105p3 with 422 wems converted to wav. The base
pack walk located all 148 592 entries with no size mismatch in about 7 minutes.

**Found.** Entries in a Qts pack are found from the block table's `Q` (global logical page) through the
part's page map; the record timestamp is not the part. Base pack nodes carry a link field that is not
always `-1` (chains of identical blocks), and blocks shorter than 16 KB occur in the middle of an entry,
so an entry ends exactly at its index size. Module bundles are stored whole; their chunks live in the
same pack. Most texture pixel data now sits in `qtc:/stream/<md5>.cpak` chunks instead of the bundle's
`.resS`; the normalizer embeds those chunks as resource nodes and the reader finds them by file name.
The base index records whose id starts with the byte `_` must not be mistaken for the `QTSF_PACKAGE`
marker. Wwise name recovery needs the weapon tokens from the container names, since the unpacked
folder only holds a few weapons.

**Observed.** While this was built the game itself (`CODM`, started 02:14) and `CODMLauncher` were
running and rewrote all 96 download-pack parts at 04:46; they also hold the parts open, so the readers
open files with read-write sharing. Work on the live install should happen with both closed.

## 2026-10-24, handoff

Kilo 141 White Centipede (2140) and its base 1939 unpacked on request; the CLI now resolves a bare
`--config` name against the repository root from any directory and refuses a config that resolves
nowhere (it used to fall back to built-in defaults silently, which re-walked the base pack into a
cache next to the exe). [handoff-1957.md](handoff-1957.md) is the entry point for the next session,
with the recommended on-demand design for browsing the containers from the GUI.

## 2026-10-24, evening: 1.9.57 in the browser without unpacking first

**Built** (the on-demand design of [handoff-1957.md](handoff-1957.md) section 5). On a config whose game
root holds containers, `PakIndex.Build` lists all 22 666 container bundles as `PakEntry` rows
(`Container` = the `VfsEntry`, `FullPath` = `<UnpackRoot>/<source>/<file>`); the 114 files unpacked
earlier are matched to their entries. `PakEntry.EnsureOnDisk` normalizes and writes a missing file
(temporary file + move) right before `AssetsManager.LoadFiles`, at every load site: export service
(weapon set, FX), dependency resolver, browser load/preview, CLI `anims`/`events`/`mbscan`. A pak that
fails to unpack is logged and skipped. CAB index: header-only reads (first 16 KB block) of the packed
bundles in storage order, 22 552 in 145 s from the hard disk (`Q:` is a 7200 rpm disk, random reads
would have been several times slower), cache identity = container size + hash stamp. Attachment index:
component bundles normalized in memory without texture streams. GUI: `CodmSession` opens the containers
next to the index, one lock per index (the attachment build no longer blocks dependency loading), CAB
index then attachment index built in the background after the browser loads; grey rows for packed
entries, **Unpack checked**, **Show in Explorer** unpacks first. Wwise weapon tokens come from the index
in CLI and GUI alike (`WeaponCatalog.WeaponTokens`; the index now holds every container name). New CLI:
`codm vfs-check [--source s] [--bundles]` and `vfs-check --unpacked`.

**Found: Qts page layout.** The location rule only held for entries whose logical pages map to
consecutive physical pages inside one part. The low 16 bits of the block table's `x` word are the page
count (all 91 313 entries). 95 entries run past the last page of a part into the next one, 586 carry a
run table (`w & 0xFFFF == 1`). Entries without a run table are read logical page by logical page through
the page maps (into the next part when needed); entries with one are, for almost all of them, contiguous
in the part file from their first physical page. Part files end mid-page (the last page is short). The
reader now checks that every block but the last ends exactly at its table offset, so a wrong layout fails
loudly instead of producing garbage, and tries the other layout before giving up. Before: the patch
pack's `camouflage_character` (a dependency of the Locus export) failed on one chunk; `vfs-check` found
48 failing patch-pack entries with the page-map walk alone, 3 with the final rules. Download pack: 88 293
of 88 464 entries decode (66.7 GB in 25 minutes); the 171 failures (169 chunks and 2 bundles,
`component/mag/39.1` and `assiweapon_45030_mw11antihero`) all carry a run table. The few left carry run
tables that fit neither layout; their trailing values are not ordinals of the same pack (open).

**Verified.** `codm --config codm.config.1957.json export 2537`: 6 models (36 + 34 first-person clips),
101 textures, banks 105/105p3, 422 wems, 0 warnings, 0 unresolved references (124 before: the old CAB
index only knew the unpacked files). Dependency resolution now sees all 22 666 bundles and loads 60
dependency paks (the limit), as the 1.9.55 exports of comparable weapons did (`mainweapon_487`,
`mainweapon_200`: 60 each); 45 of them were unpacked on the fly.

**GUI**: the Release build, driven through its real menu and dialogs (Win32 messages and MSAA from a
script; the managed UI Automation client only sees panes on this machine), started on the 1.9.55 config
(17 674 paks, 1 051 weapons); CODM > Switch config... to `codm.config.1957.json` re-indexed to 22 666
paks, 2 733 weapons and 5 233 bank files in under 3 minutes. AK117 Bengal (`mainweapon_173`, 4 skin and
41 base paks, none unpacked before) with **Load checked (replace)** and dependencies: 75 s, the 45 paks
and 29 dependencies unpacked on the fly, among them the patch pack's `camouflage_character` (702 chunks,
270 MB, unreadable before the page-layout fix); one dependency copy (`personalshow@4` in the download
pack, a run-table entry) was skipped with a log line. Second run, AK117 SD (`mainweapon_207`, base
already unpacked by the first): the 3 skin paks unpacked, load 30 s, 37 412 nodes in the main window's
scene hierarchy; the Release build then remembers the 1957 config. `codm vfs-check --unpacked`: all 250
files in `unpack-1957` (the 114 written by the old reader included) equal a fresh read of their container
entry. Final `export 2537` with the final build: same numbers and the same file list.

**Dead ends.** The block-table hash is not the md5 of the decoded entry. The run table's trailing
values are neither ordinals in the same pack's directory nor logical start pages of the runs (global
or local to the entry's part): reading run `r` from logical page `V[r-1]` breaks exactly where run 1
begins. All failing download-pack entries carry a run table; plain entries (including the 92 that
cross a part boundary) all decode.

## 2026-10-24, late: skin without its first-person mesh, components export whole buckets

First real use of the on-demand GUI: DL Q33 Black Noir (`mainweapon_72022_dlq33blacknoirblp`, new in
1.9.57) exported with base, dependencies and **Include components**. Two faults, both in the export.

**The 1P model was a bare skeleton.** The skin bundle carries two copies of the first-person rig: the
`.prefab`, whose Animator has the override controller but whose SkinnedMeshRenderer has an empty mesh
reference (filled in at runtime), and the imported `.fbx` with the 4 856-vertex mesh. The export keeps
the copy that resolves the most clips; alone the `.fbx` copy wins (27 clips from the bundle against 26
overrides), but with the base chain loaded the override controller resolves 31 (35 with dependencies)
and the prefab won.
Now, when the copy with the most clips has no mesh, the export takes the rig and mesh of a copy that has
one and the clips of the other (logged). Curves of the clips are identical to before (427 per clip).

**1 770 models.** Component paks are version buckets (`component/bar/37.2` holds the barrels of about
twenty weapons); a pak linked through one matching container path was exported whole. With components
included, only the objects of the container paths that tie a pak to the weapon or its bases (the
asset and its preload range) are exported, models and textures alike; paks linked by name only
(sights named after the base) still export whole. Result: 141 models and 79 textures, all DL Q33 parts
(own, base chain, the other DL Q33 gunsmith barrels, magazines, rails), no models without a mesh.

**Not this bug.** The 1.9.55 Beam Saber (`meleeweapon_59823`) 1P model has no mesh either, but there
no copy has one: its renderer's mesh reference (path id `501 << 32`) resolves to nothing.

## 2026-10-24, late: materials and colour maps on the models

No exported Cast had ever carried a material: CoD Mobile renderers hold no usable material reference
(empty, or path id 0 / `1 << 32` of an animation pack), because the game assigns skin and camo materials
at runtime; the base DL Q33 bundle holds one material per camo. No MonoBehaviour names them either
(the component bundles only carry `AssetRef`, which lists meshes). Materials are matched by name
instead, part + skin (`Wea01_DLQ33BlackNoirBLP_1P` -> `Wea01_DLQ33BlackNoirBLP_mtl`, `DLQ33Black_1P` ->
`wea_DLQ33Black_mtl`, `Wea01_DL_Q33_UI_lod0` -> `wea_DLQ33_mtl`, second submesh -> `..._mtl_SightPupil`).

**Finding: attachments share the skin's sheets.** The Black Noir ships `Wea01`, `Rai01`, `Sig01`, `Sto01`
and `Glass` sheets but has barrel, magazine, muzzle, grip and bipod meshes too. Rendered against every
sheet, the magazine is only coherent on `Rai01` (fins, copper round) and is a patchwork on `Wea01`. Parts
sharing a sheet are packed into separate regions, so the sheet whose own mesh covers none of the part's
UV area is the one: magazine `Rai01` (0 % overlap, `Wea01` 48 %), barrel, muzzle and grip `Wea01` (0 to
2 %). Where no sheet is clearly free (over 10 %, the Black Noir bipod), the skin's `wea` sheet.

**Built.** `MaterialMatcher` (name match, then UV layout, then `wea`); `ModelConverter` takes a material
fallback for renderers whose reference does not resolve; Cast materials reference a colour map already
on disk relatively (`../../textures/<name>.png`), written once per texture. Only the colour map
(`_MainTex`, `_A`) is linked; the normal map is packed (`_BumpMapPakced`) and metal/roughness is `_M`.
`--no-materials` in the CLI.

**Verified** in Blender 4.1 with the Cast addon (headless import, unlit render of the colour map): the
Black Noir 1P/UI body, magazine, grip, the base DL Q33 rail, DL Q33 Black stock, scope and body, and the
1.9.55 Locus railgun all import textured with the details in place. Full DL Q33 Black Noir export: 141
models, 11 colour maps, no material for the `_H` holo reticle meshes, `SightPupilCircle_Mesh`, a mesh
named `Rai03_DLQ33_UIi_lod0` and three loose props.

**Texture alpha (checked on request).** PNG export keeps alpha: the DXT5 textures (`_M_trans`, glass,
camo masks) come out 0-255 with the colour under alpha 0 intact. The Black Noir set is BC7 and opaque in
the game data itself (254-255); Pillow's BC7 decoder on the raw data gives the same pixels bit for bit.

## 2026-10-24, night: one skeleton for a weapon and its attachments (DL Q33 Black Noir, IW4x port)

The user rebuilt the Black Noir in Blender from the per-part casts (nine armatures) and found barrel,
stock, sight, muzzle and bipod without weights, with no way to tell how the rigs combine.

**Finding: attach points.** The 1P rig carries `<Type>_point` bones under `Bone_RightHand`; every part's
root sits exactly on one (barrel 0.1264 forward = `Bar_point`, muzzle 0.7201 = barrel + its own
`Muzzle_point`, stock `Sto_point`, sight `Sight_point`, bipod `Bip_point`, grip `Grip_point`, rail
`Rai_point`, magazine mesh `Mag_point`). Skinned parts (magazine, grip, rail) carry a copy of the
weapon's chain and are weighted to main-rig bone names (`Bone_DanJia`, `Bone_RightHand`, `daopian_*`);
the rail copy matches the main rig exactly, the magazine's and grip's `Bone_RightHand` is zeroed (0.374
off), irrelevant once rebound. Static parts are MeshRenderers: the Cast exporter wrote them without any
weights. The ChangeClip curves move `Bip01`, `Bone_RightHand`, `Bone_shuan`/`Bone_Bolt`, `Bone_DanJia`
(1.4 travel) and the blades; the attach points only hold constant keys.

**Built.** Cast export binds a static mesh 100 % to the bone of its own node (3P/UI parts gain it too).
`tools/blender/codm_assemble_weapon.py`: main rig = `MainWeapon_..._1P` armature; skinned parts rebound
by name; static parts weighted to the main-rig bone at their root's rest position (prefix and `_point`
break ties: `Bar_point` over `Bar_pointqiangtou_root`), parts that hang on another part's socket
(muzzle) wait for it; sockets the rig lacks (`Muzzle_point`, `MuzzleFlashSocket`, `Center_point`, the
holo reticle node) are grafted; emptied part armatures removed, materials kept.

**Verified** on a copy of the user's file (Blender 4.0, Cast addon 1.9.4): 82 + 4 bones, all 15 meshes
on the main rig; ChangeClip imported onto it: every part keeps a constant offset to the bone it should
follow over the clip (drift 0.00000; rail 0.001 as it is weighted across blades), the magazine leaves and
returns with `Bone_DanJia`; renders with the camera on `Bone_RightHand` show it. Saved for the user as
`dql_noirshadow_assembled.blend` next to their file (original untouched).

## 2026-10-24, night: IW4 port pilot, gun xmodel

The DL Q33 Black Noir is the pilot for porting CODM weapons to IW4x. The IW4-side tooling lives with the
user's earlier CODM ports in `Q:\Modding\iw4x\iw4xTools\CODM\CODMConverter` (viewhands retarget
pipeline, calibration: CODM rig at scale 143.037, Z +90 deg); new: `Weapon\codm_weapon_to_iw4_xmodel.py`
and `Weapon\README.md`. From the assembled .blend it builds the gun skeleton on `j_gun` (= CODM
`Bone_RightHand`, which hangs off `Bip01` like IW4 `tag_weapon` off `tag_torso`), keeps the 16 bones
the clips move or the engine needs (`j_mag1`, `j_bolt`, `j_blade_*`, `tag_flash`, `tag_brass`) and
collapses the static attach points, renames materials per colour map and writes XMODEL_EXPORT (36 550
verts, 4 materials, 64 in long) plus a material converter config. Retarget plan: the gun FK 1:1 in world
space (same rig, renamed and scaled), arms by the existing IK pipeline. BetterBlenderCOD fails on rigs with
several armature-bound meshes (it frees each evaluated mesh when toggling the next one's modifier): the
builder exports a joined copy.

## 2026-10-24, night: camera clips of skins, camera rest at neutral

The DL Q33 Black Noir export had no camera at all: the 1P rig takes its clips from the override
controller, and camera clips are played by the prefab's `CameraAnimation` component, never listed there.
The skin's camera clips sit in the base bundle (`mainweapon_200_dl_q33`: AimingBolt, ChangeClip,
ChangeClip_E, FireBolt and Inspection `_Camera`); only the skin's own `Inspection_camera` was reachable,
through the same-file fallback, and only in a skin-only export. The export now adds, per action, the
matching camera clip of the weapon's own bundles (rig's bundle first). Result: `Camera` bone on the 1P
rig, camera curves on 5 clips (Inspection up to 13 deg, FireBolt 0.4, AimingBolt 0.3; the reload cameras
are constant, no quantized blocks). The merger log is quiet for rigs nothing merges into.

**Finding: the camera's neutral is 180 deg about Y.** Every keyed camera clip starts there while the
synthetic bone rested at identity, so clips without camera rotation keys (reloads, Idle) sat 180 deg
away from the rest: a retarget onto IW4 `tag_camera` would flip on every reload. The bone now rests at
the first rotation key of the merged clips. `retarget_codm_to_iw4.py` (iw4xTools) assumed rest + 180 deg
as the neutral, which double-counts with new exports.

## 2026-10-24, late night: IW4 pilot retarget set up

Re-exported the DL Q33 Black Noir with the camera (CLI, into the user's `export` folder; every export
root is now git-ignored by `export*/`). In the iw4xTools CODM converter, `Weapon\codm_weapon_retarget_setup.py`
writes the anim_utils pair file (54 World pairs: torso, gun controller and gun bones, IK controls,
twists, fingers, camera) and a retarget scene at a built sync pose; the old pipeline detects which
Camera rest convention a rig uses. Lesson: CODM exports each weapon's arms in its own default pose
(DL Q33: 61 cm and up to 160 deg from the calibration weapon's rest), so a sync captured with both rigs
at rest keeps that offset; solving the CODM rest once with the calibrated deltas fixes it. Headless bake
through anim_utils (Idle, ChangeClip, FireBolt, Inspection): gun within 0.0001, wrists within 0.003 of
the CODM hands on every frame.

## 2026-10-25: IW4 pilot materials and xmodel

**Finding: weapon skin sheets pack their channels differently from the character textures** the
iw4xTools converter was written for: `_M` R = AO, G = metallic mask, B = camo mask; `_N` B = gloss
(details and evidence in the format notes). The converter (`blender_convert\ShaderConverter\convert_materials.py`)
takes the `_M` layout from an optional config entry now (`material_channels`, default unchanged). The
DL Q33's four materials converted to `_col`/`_spc`/`_cos`/`_nml` TGA folders; the gun xmodel was
re-exported from the user's edited gun blend with their material names (`codm_export_gun_xmodel.py`,
bone rests checked equal to the animated rig's within 0.00001). The retarget setup saves its sync pose
as actions on both rigs and no longer replaces an existing output file.

## 2026-10-25: which sounds the animation triggers play

**Finding: the triggers resolve to the skin's own bank, else the class bank.** The DL Q33 Black Noir's
clips carry only semantic triggers (`PlayClipOutSound`...). Hashing candidate names against all
401 145 event ids of the install (5 233 bank files) found its skin bank, `200g` / `200gp3`
(`play_dlq33blacknoir_clipout_1p`, `_boltpull_1p`, `_fire_sup_first_1p`...: 29 of their 43 events
named), and the rest in `wpn_sniper` (`play_weapon_sniper_aim_up_1p`, `_reload_end_1p`, `_down_1p`).
Every trigger of the 1P clips now has an event; the table, the naming rules, the fire layers and the
surface switch are in the format notes. The weapon export had missed `200g` because the bank linking
does not strip the skin's `BLP` suffix; extracted by hand with `codm wems` into the user's export
(`sounds/200g`, `200gp3`, `wpn_sniper`), and the recovered names went into the local name cache so
the folders carry them. In the iw4xTools CODM converter, `Weapon\codm_weapon_notetracks.py` copies the
triggers onto the baked IW4 actions as notetracks named after the event (`play_` -> `weap_`, `_1p` ->
`_plr`, words split: `weap_dlq33blacknoir_bolt_pull_plr`); a test XANIM export of the empty reload
carries all seven at the CODM frames. The `_e` clip events play the same sounds as the plain ones
(by ear), so they share a notetrack.

**Scope reticle.** Sniper skin reticles live in the shared `component/sight/crosshair` bundle, not in the
weapon's paks (format notes, "Scope reticles"), so weapon exports miss them. The Black Noir's is a
quarter-ring atlas with sprites; rebuilt into a full IW4 scope overlay (the ring mirrored, chevrons,
cross and fine lines placed as the lens texture and the prefab mesh show them) in the pilot's work
folder.

**First raise.** The Black Noir bank has a 2.13 s `raisefirst_1p` that no Black Noir clip triggers.
`codm anims --clips` (new) lists the override pairs, and a scan of the shared controllers' string
tables (CRC32-checked) gave their state names: the DL Q33 runs on `Basic_1P`, which has no first-raise
state (equip clips `Equip`, `Equip_W`, `Equip_B`); newer weapons (Bal-27, Static-HV, Cronen Squall)
run on `Basic_1P_Advanced`, whose `Equip_First` state plays the weapon's `First_Equip` clip with a
`PlayFirstRaiseSound` event. So no clip was missed. The DL Q33's first-raise sound is never played in
CODM; it isn't a BR or 3P sound either. The IW4 notetrack script now maps `PlayFirstRaiseSound` to
`raise_first` for weapons that have the clip.

## 2026-10-25: IW4 pilot world model (3P LODs)

The user cleaned up the pilot work folder (now `source\weapons\dlq33\dlq33blacknoir`, every name
`dlq33blacknoir`) and asked for the world model, LOD0 to LOD2, in a Blender file with the viewmodel's
skeleton, scale and materials (placement, export and xmodel build are theirs).

**Finding: CODM's 3P LODs merge parts.** The 3P parts are the 1P ones scaled by 10/7; only body, rail,
magazine, sight and bipod have LOD meshes, and the body's and rail's absorb the stock, grip, barrel and
muzzle, one sheet per submesh (format notes, "Third-person models and LODs"). Scaled by 0.7 x 143.037
cm, the 3P parts land on the viewmodel's within 0.002 cm.

**Built** (scratch script, not in a repo): `xmodel\w_dlq33blacknoir_xmodel.blend`, a new file next to the
viewmodel's: the 36 3P meshes on `w_dlq_amt`, a copy of the viewmodel skeleton (16 bones, same names,
hierarchy and rests), with the viewmodel's five materials appended from its blend; magazine on
`j_mag1`, the rest on `j_gun`; collections `W::DLQ::LOD0..2::<part>`, the user's w_cheytac LOD0 as an
excluded reference.

**Verified** headless: bones equal to the viewmodel's within 2e-6 cm; the materials of 30 meshes
re-derived from the viewmodel by position and UV, no mismatch; the other 6 (3P-only scope mount, lens
caps) by the sheet's free UV area and the lens's full-square UVs; textured renders of the three LODs
show every part coherent.

## 2026-10-25: DL Q33 Lotus Flames, first look (second IW4 port)

The user asked for the whole port pipeline on `mainweapon_16856_dlq33lotusflames`, a skin directly on
the base DL Q33 (no Black intermediate).

**The 1P body was missing.** Both copies of the 1P prefab have the body renderer's `m_Mesh` at 0 and
no bones (the UI's `Enter01_Wea` points at `1 << 40`), so the fallback to the `.fbx` copy that fixed
the Black Noir had nothing to take. The mesh is in the bundle (`Wea01_DLQ33LotusFlames_1P`, 6 375
vertices, 4 submeshes) under the renderer's GameObject name. `ModelConverter` now takes a bundle mesh
of that name when the reference does not resolve and the name is unique; the bones come from the mesh's
name hashes as before. The 1P model has the body and `Enter01_Wea` now; a Black Noir re-export is byte
identical (141 of 141 casts).

**What the skin is made of.** Own 1P clips (58 overrides; a second override controller, `..HW_1P`,
swaps the five aim clips), a body, an `Enter01_Wea` piece, three `Huanwen01..03` overlays (own sheets
`HW01..03`), and part sets: barrel, extra barrel, extra side rail, rail, magazine and muzzle 01-03,
stock 01-04, bipod 01-02, grip and sight 01. The assembler puts every part on its attach point
(`ExBar_point`, `ExSra_point`, `Sra_point_R` are new). Emission maps (`_E`) on most sheets, five glow
levels for the rail and the `HW03` emission (`00..08`, orange to red; the rail material has no colour
map of its own), a flow map without data. Open: the matcher puts the `Huanwen` meshes on the body's
sheet (their `HW0n` names are not derivable from the part name), and the skin's sound bank is one of
`200a..200f`, whose older event names are not recovered.

## 2026-10-25: DL Q33 Lotus Flames through the IW4 pipeline

The user picked overlay 3 with the default part set, Lotus notetrack names, and asked for the hand
steps of the pilot to be done headless too. Everything was built in a new work folder
(`source\weapons\dlq33\dlq33lotusflames`, the pilot's layout); the user's files were only read.

**Findings.** No renderer of this skin names a material and its sheets are painted edge to edge, so
every submesh's sheet came from UV overlap between meshes and textured renders (format notes, renderer
section; the export's name match misses the `Ball`, `HW01Glass` and `Sto` submeshes). The attach points
and ornament bones are animated in this skin's clips, and clips key bone scale for effects (format
notes, "Weapon attach points"); the bolt never moves. Its 3P parts are the 1P ones x 8/7, where the
Black Noir's are x 10/7 (format notes, "Third-person models and LODs").

**Built** (iw4xTools CODM converter): a script that swaps a new gun into a finished weapon's animated
viewhands (`Weapon\codm_weapon_merge_viewhands.py`; bones and meshes equal to the gun blend within
2e-5), so no hand merge or IK setup is needed for later weapons. With the existing steps: 36-bone gun
xmodel (39 385 vertices, 9 materials), retarget at an exact sync pose (wrists 0.003, gun 0.0000), nine
anims baked through anim_utils as in its panel (gun bones on CODM within 0.0002, wrists 0.003), the
magazine moved out of view where CODM scales it to 0, notetracks, XANIMs as the pilot's (deform bones
without `tag_ads`), ten materials through the user's converter and its chosen variant, and the world
model (3P LOD0 to LOD2 on a copy of the viewmodel skeleton, bones equal within 2e-7).

## 2026-10-25, evening: the port pipeline written down for other agents

The user asked for the whole CODM -> IW4x weapon port as a guide other agents can follow. It lives with
the tools: `Q:\Modding\iw4x\iw4xTools\CODM\CODMConverter\Weapon\PIPELINE.md` (the user's rules, tools and
places, sixteen steps with their commands, what went wrong before, the Lotus Flames as worked example).
The one-off scripts of the Lotus port became tools there, driven by arguments or a per-weapon spec
(`look_*.json`, `world_*.json`, `materials_*.json` in the Lotus work folder): look assembly, sheet
check, headless bake, XANIM export, viewmodel blend, world model fit/build, materials to IW4x, sound bank
search, sound aliases. Rerun against the Lotus results: assembly, viewmodel blend and world model match
(same meshes, sheets, faces), the materials chain reproduces all 40 delivered files byte for byte, and the
XANIM export matches the user's own BetterBlenderCOD exports. The IW4x material jsons and IWIs (normal
maps in image.exe's default encoding, as MW2's own and the pilot's) went into the mod.

## Open items

- Weapon sound banks: link a skin's letter bank by its name without the `BLP` suffix; teach
  `WwiseNames` the skin actions (`boltpull`, `boltrelease`, `reloadstart`, `raisefirst`, `fire_first`,
  `fire_sup`, `fire_sup_first`, `clipin_e`, `clipout_e`, `armsettle`, `kill_sfx`) and the class ones
  (`aim_up`, `aim_down`, `down`, `reload_end`); resolve each trigger in `notetracks.txt` (skin event,
  else class event). Four `200g` events still unnamed.
- Export a sniper skin's reticle with the weapon (`component/sight/crosshair`, by the skin's name).
- Name the switch values inside the class banks (per-weapon split of `wpn_*`), and widen event
  name recovery (90 of several thousand ids named).
- Source of the characters' first-person arms mesh, and of the Beam Saber's (`meleeweapon_59823`).
- Dependency resolution with components included follows the other weapons' attachments in the
  shared buckets too (60-pak limit reached for the DL Q33); scope it to the exported objects.
- Materials: normal (`_BumpMapPakced`, channel layout unknown) and metal/roughness (`_M`) maps; camo
  materials as alternatives; characters (untested with the matcher); per-submesh sheets: the name
  match puts the Black Noir body's second submesh on `_mtl_SightPupil` and the stock's first on
  `Sto01`, and the bipod falls back to `Wea01`, where the user's viewmodel (and the 3P match against
  it) has `Rai01`, `Rai01` and `Sto01` / `Rai01`. Lotus Flames: `Huanwen0n` -> `HW0n`, the stock on
  `Sto` (not `Sto01`), body submeshes on `Ball` and `HW01Glass`, the rail's levelled colour maps.
- Decrypt `DataTableBytes` by static analysis of `CODM.exe` (the real manifest: `WeaponAsset`,
  `WeaponSoundConfigs`, `WeaponPartCompose`, `LegendaryWeaponEffect`). Reading or patching a copy
  of the exe is allowed; running the game is not.
- Optional: embedded textured mesh preview in the browser; standalone GUI.
- 1.9.57: the Qts run tables (174 of 91 313 entries fit no known page layout, `codm vfs-check`);
  characters and effects through the on-demand path (untested); an automatic stale-unpack check after
  game patches (today `vfs-check --unpacked`); the 375-entry table in base parts; the game must be
  closed while reading the live install.
