# AssetStudio, CoD Mobile fork

Private, single-user fork of [Perfare/AssetStudio](https://github.com/Perfare/AssetStudio) that
reads the modified Unity 5.6.4 engine used by **Call of Duty Mobile (CN build)** and exports weapons,
cosmetics, animations, textures and sounds for porting into other games and mods.

This repository is never published. `codm.config.json` points at the **1.9.55 (patch 55)** game files,
since 2026-10-24 archived at `Q:\CODMGameApps_Archive\CN\emulator\Extract\CODM`, which the whole
pipeline reads directly. The live install at `Q:\CODMGameApps` is 1.9.57, whose bundles sit in QtsVFS
containers: with `codm.config.1957.json` every bundle in them is listed, and each one is unpacked as a
stock loose pak the moment something loads or exports it (see [docs/codm-containers.md](docs/codm-containers.md)).
**The game must never be launched, patched or updated from this machine; the tooling only reads files.**

## What it does

| Area | Status |
|---|---|
| Models (skinned meshes, skeleton, bind pose, weights, materials) | works, `.cast` primary, GLB/FBX optional |
| Animations (CoD Mobile quantized dense clips) | works, see [docs/codm-formats.md](docs/codm-formats.md); `_Camera` clips are merged into their animation on a `Camera` bone |
| Textures (DXT1/DXT5/BC7, streamed `.resS`) | works, png by default |
| Pak navigation | obfuscated names are decoded (letters and digits); weapons grouped as base + skins |
| Bundle dependencies | shared animation packs, controllers and avatars are loaded automatically (CAB index) |
| Animation events | `PlayFirstAttackSound`, `PlayRaiseSound`... exported as Cast notetracks plus a per-prefab `notetracks.txt` sheet |
| Sounds (Wwise) | weapon banks found by their event names (`play_wpn_locus_fire_1p`), wems exported per event, class bank from the weapon class map, wav via vgmstream |
| Attachments | magazines, barrels, stocks, grips, sights linked through the asset paths inside the attachment bundles |
| Characters | every skin under `cod_models/avatar` (body, head, hair, first-person arms as separate prefabs), same Cast + textures export |
| Effects | FX bundles are linked by name; their materials' textures (resolved through the shared texture bundles) and an object list are exported |
| Game config tables (`DataTableBytes`) | encrypted, not decoded (see docs) |
| 1.9.57 containers (QtsVFS) | read: download pack, patch pack, base pack; browser, catalog and export list every bundle and unpack on demand, `codm unpack` in batch (see [docs/codm-containers.md](docs/codm-containers.md)); 174 of 91 313 Qts entries (0.2 %, entries with a run table) use a page layout not understood yet |

## Layout

| Project | Purpose |
|---|---|
| `AssetStudio` | core reader (upstream) with the CoD Mobile fixes in `SerializedFile`, `Texture2D`, `Renderer`, `AnimationClip`, `Codm*.cs` |
| `AssetStudioUtility` | converters and exporters: `ModelConverter`, `Cast/`, `GltfExporter`, FBX wrapper |
| `AssetStudio.Codm` | everything CoD Mobile specific: name cipher, pak index, weapon catalog, Wwise, export service, config |
| `AssetStudioGUI` | the WinForms app plus the **CODM** menu and weapon browser |
| `CodmCli` | `codm.exe`, the command line front end over the same library |
| `*Native`, `*Wrapper`, `AssetStudio.PInvoke` | upstream native texture decoder and FBX SDK glue |
| `docs/` | format notes, install layout, CLI and GUI guides, research log |

Everything targets `net472`.

## Build

Visual Studio 2022: open `AssetStudio.sln`, build `AssetStudioGUI` (Debug, x64 natives are copied
from `Texture2DDecoderNative\bin\x64\Debug` and `AssetStudioFBXNative\bin\x64\Debug`).

Command line:

```
dotnet build AssetStudioGUI/AssetStudioGUI.csproj -c Debug
dotnet build CodmCli/CodmCli.csproj -c Debug
```

Outputs: `AssetStudioGUI\bin\Debug\net472\AssetStudioGUI.exe` and `CodmCli\bin\Debug\net472\codm.exe`.
The native DLLs must exist under `bin\x64` next to the exe; if a native project was never built
with the C++ toolset, copy the DLLs from the GUI output folder.

## Configuration

`codm.config.json` at the repository root is found by walking up from the executable directory,
so Debug and Release builds share it:

```json
{
  "GameRoot": "Q:\\CODMGameApps\\CN\\emulator\\Extract\\CODM",
  "PakFolders": [ "CODM_Data\\StreamingAssets\\AssetBundle", "PersistentData\\Puffer\\AssetBundle\\dynamic\\module", "PersistentData\\Extract\\55\\AssetBundle" ],
  "SoundBankFolders": [ "...GeneratedSoundBanks\\Windows", "...\\Windows_HQ" ],
  "VgmstreamPath": "",
  "ExportRoot": "exports",
  "PreferredLanguage": "English(US)",
  "TextureFormat": "png",
  "ScaleFactor": 1.0,
  "SourcePriority": [ "puffer", "extract", "streaming" ],
  "Diagnostics": false,
  "CacheDir": "cache"
}
```

Two configs live at the repository root: `codm.config.json` (the 1.9.55 archive, loose paks) and
`codm.config.1957.json` (the live 1.9.57 install: the containers are found under its `GameRoot`, bundles
are unpacked on demand into `unpack-1957`, which its `PakFolders` also list; plus its own `cache-1957`
and `exports-1957`). The CLI takes `--config <file>`, the
GUI has **CODM > Switch config...** and remembers the choice. Configs for different game versions must
not share `CacheDir`.

Set `VgmstreamPath` to `vgmstream-cli.exe` to get `.wav` next to the `.wem` files. `Diagnostics`
turns the per-field reader logging back on (it is noisy and only useful when a bundle fails to parse).

Two more files live next to the config:

- `weapon-classes.json`: sound class per base weapon (`"locus": "sniper"`). A built-in list covers the
  well-known weapons; this file overrides it. The class picks the `wpn_<class>` bank for the base weapon
  sounds (`codm class <weapon> <class>` or the combo box in the browser).
- `<CacheDir>/cab-index.json`: which pak contains which serialized file (CAB). Built on first use, about five
  minutes for 17 000 paks (1.9.57: 2.5 minutes for 22 666 bundles read from the containers), then reused.
  Needed to load the bundles a weapon references.
- `<CacheDir>/wwise-names.json` and `<CacheDir>/wwise-bank-tokens.json`: Wwise event names recovered by hashing
  candidate names (FNV-1) and the weapon each bank belongs to. Built on first use (about five minutes).
- `<CacheDir>/container-index.json`: asset paths inside the 2 000 attachment bundles, used to link magazines,
  barrels and stocks to weapons. Built in the background by the browser or by `codm components`.

## Workflow

GUI: start `AssetStudioGUI.exe`, open **CODM > Weapon browser** (Ctrl+W, or start with
`--codm [weapon]`). Pick a weapon, check the paks you want, **Load checked** to browse them in the
normal AssetStudio views, or **Export weapon** for a one-click folder with `models/` (Cast per
Animator, animations under `anims/`), `textures/`, `sounds/<bank>/` and `manifest.json`.
See [docs/gui.md](docs/gui.md).

Command line:

```
codm weapons locus                 list the Locus family
codm weapon 2537                   paks, base chain, components, fx and banks of the Locus railgun
codm export 2537                   export it to the configured export root
codm anims 2537                    where each Animator's clips live and whether they resolve
codm class 2537 sniper             set the sound class of the family
codm bank 249                      events and sounds of a skin sound set
codm search cod_effects/particles  find paks by decoded name
codm characters alias              list character skins
codm exportchar c_f_alias          export a character skin (prefabs as cast + textures)
```

See [docs/cli.md](docs/cli.md).

## How a weapon export is assembled

1. All module copies of the weapon's paks plus its base chain are loaded (download modules first).
2. Every bundle they reference is looked up in the CAB index and loaded too (animation packs,
   animator controllers, avatars; shaders and effects are skipped). Three rounds, 60 paks at most.
3. One Cast model per Animator prefab; where the same prefab exists in several bundles the copy whose
   controller resolves the most clips wins. A skin whose own controller bundle is not on disk gets the
   clips of the base weapon prefab with the same role (`_1P`, `_3P`, `_UI`).
4. Textures of the weapon's own bundles, then the sounds: every bank whose event names mention the
   weapon (`play_wpn_locus_fire_1p` lives in `105.bnk`) or that is numbered by its id, for the weapon and
   its base chain, taking the plain bank and its third-person set (`247`, `247p3`); the lettered
   variants (`247a`, `247c`...) are other skins' sets and come only with `--bank-variants`. The class
   bank (`wpn_sniper`, the whole class) is only dumped with `--class-banks`;
   media a weapon bank borrows from it is extracted through the weapon's own events anyway.
   Wems are written per event (`sounds/105/play_wpn_locus_fire_1p/...`, unnamed events by id,
   `events.txt` lists the mapping); `--flat-sounds` dumps a bank into one folder instead.
5. With **FX textures**: each linked effect bundle is loaded with its dependencies, the materials its
   renderers use are resolved, and the textures those materials reference are exported next to an
   `objects.txt` listing particle systems, materials and meshes.

Third-person prefabs ship without a controller (the game assigns `basic_3p` at runtime), so their
animations are the shared packs under `cod_models/weapons/animations/` (load those paks directly).

## Known limitations

- The bundles carry **no type trees** and the game is IL2CPP without `global-metadata.dat` on disk,
  so `MonoBehaviour` fields (weapon definitions, FX and sound references) cannot be read generically.
- The config tables under `PersistentData\Extract\55\DataTableBytes` are **encrypted**. They hold the
  real weapon to sound/FX mapping; weapon relations here are derived from decoded names instead.
  Some skins have no recognisable base weapon name and show up without a base, and weapons missing
  from the class list have no base weapon sounds until a class is set.
- Some skin bundles reference controller/clip bundles that are not on disk (never downloaded); those
  skins fall back to the base weapon's animations.
- Wwise event names are FNV hashes. The recovered names cover the main patterns (`play_wpn_<weapon>_fire_1p`,
  `play_weapon_<class>_raise_3p`); events that use other words stay listed by id, with their wems
  grouped under that id. Switch values that pick a weapon inside the class banks are not named yet.
- Effects cannot be rebuilt as particle systems; the export gives their textures and object names.
- Melee weapons without a sound set of their own export the whole `wpn_melee` bank; the animation
  events say which trigger fires when, but pairing trigger to wem is by ear until the bank's event
  names are recovered.
- Characters: the `_1P` arms prefab exports without a mesh (assigned at runtime from a shared asset
  that has not been located).
- Sound is 22 kHz Vorbis in the shipped banks; only 79 banks have a `Windows_HQ` copy.

## Documentation

- [docs/codm-install-layout.md](docs/codm-install-layout.md): where things live on disk, the name cipher, pak modules, sound banks
- [docs/codm-formats.md](docs/codm-formats.md): engine format differences and the animation decoder
- [docs/handoff-1957.md](docs/handoff-1957.md): **start here for 1.9.57**: state, how to switch versions, how to unpack and export, the recommended next step
- [docs/codm-containers.md](docs/codm-containers.md): the 1.9.57 QtsVFS containers, format 18 and the normalizer
- [docs/cli.md](docs/cli.md), [docs/gui.md](docs/gui.md)
- [docs/progress.md](docs/progress.md): dated log of what was built, what was found, dead ends and open items
- [docs/research-log.md](docs/research-log.md): what the animation reverse-engineering probes tested
