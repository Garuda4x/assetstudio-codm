# AssetStudio, CoD Mobile fork

Private, single-user fork of [Perfare/AssetStudio](https://github.com/Perfare/AssetStudio) that
reads the modified Unity 5.6.4 engine used by **Call of Duty Mobile (CN build)** and exports weapons,
cosmetics, animations, textures and sounds for porting into other games and mods.

This repository is never published. It is pinned to the game install at
`Q:\CODMGameApps\CN\emulator\Extract\CODM` (patch 55). **The game must never be launched, patched
or updated from this machine; the tooling only reads files.**

## What it does

| Area | Status |
|---|---|
| Models (skinned meshes, skeleton, bind pose, weights, materials) | works, `.cast` primary, GLB/FBX optional |
| Animations (CoD Mobile quantized dense clips) | works, see [docs/codm-formats.md](docs/codm-formats.md) |
| Textures (DXT1/DXT5/BC7, streamed `.resS`) | works, png by default |
| Pak navigation | obfuscated names are decoded; weapons grouped as base + skins |
| Sounds (Wwise) | banks parsed, wems resolved across banks, converted with vgmstream |
| Effects | FX bundles are linked by name; textures and an object list are exported |
| Game config tables (`DataTableBytes`) | encrypted, not decoded (see docs) |

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
  "Diagnostics": false
}
```

Set `VgmstreamPath` to `vgmstream-cli.exe` to get `.wav` next to the `.wem` files. `Diagnostics`
turns the per-field reader logging back on (it is noisy and only useful when a bundle fails to parse).

## Workflow

GUI: start `AssetStudioGUI.exe`, open **CODM > Weapon browser** (Ctrl+W, or start with
`--codm [weapon]`). Pick a weapon, check the paks you want, **Load checked** to browse them in the
normal AssetStudio views, or **Export weapon** for a one-click folder with `models/` (Cast per
Animator, animations under `anims/`), `textures/`, `sounds/<bank>/` and `manifest.json`.
See [docs/gui.md](docs/gui.md).

Command line:

```
codm weapons locus                 list the Locus family
codm weapon 1648                   paks, base chain, components, fx and banks of the Locus railgun
codm export 1648                   export it to the configured export root
codm bank 130                      events and sounds of a skin sound set
codm search cod_effects/particles  find paks by decoded name
```

See [docs/cli.md](docs/cli.md).

## Known limitations

- The bundles carry **no type trees** and the game is IL2CPP without `global-metadata.dat` on disk,
  so `MonoBehaviour` fields (weapon definitions, FX and sound references) cannot be read generically.
- The config tables under `PersistentData\Extract\55\DataTableBytes` are **encrypted**. They hold the
  real weapon to sound/FX mapping; weapon relations here are derived from decoded names instead.
  Some skins have no recognisable base weapon name and show up without a base.
- Wwise event names are FNV hashes; events are listed by id. Containers inside banks are not walked,
  so sound extraction is per bank, not per event.
- Effects cannot be rebuilt as particle systems; the export gives their textures and object names.

## Documentation

- [docs/codm-install-layout.md](docs/codm-install-layout.md): where things live on disk, the name cipher, pak modules, sound banks
- [docs/codm-formats.md](docs/codm-formats.md): engine format differences and the animation decoder
- [docs/cli.md](docs/cli.md), [docs/gui.md](docs/gui.md)
- [docs/research-log.md](docs/research-log.md): what the animation reverse-engineering probes tested
