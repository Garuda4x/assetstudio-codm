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

## Open items

- Name the switch values inside the class banks (per-weapon split of `wpn_*`), and widen event
  name recovery (90 of several thousand ids named).
- Source of the characters' first-person arms mesh, and of the Beam Saber's (`meleeweapon_59823`).
- Dependency resolution with components included follows the other weapons' attachments in the
  shared buckets too (60-pak limit reached for the DL Q33); scope it to the exported objects.
- Decrypt `DataTableBytes` by static analysis of `CODM.exe` (the real manifest: `WeaponAsset`,
  `WeaponSoundConfigs`, `WeaponPartCompose`, `LegendaryWeaponEffect`). Reading or patching a copy
  of the exe is allowed; running the game is not.
- Optional: embedded textured mesh preview in the browser; standalone GUI.
- 1.9.57: the Qts run tables (174 of 91 313 entries fit no known page layout, `codm vfs-check`);
  characters and effects through the on-demand path (untested); an automatic stale-unpack check after
  game patches (today `vfs-check --unpacked`); the 375-entry table in base parts; the game must be
  closed while reading the live install.
