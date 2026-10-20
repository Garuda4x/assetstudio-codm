# GUI guide

`AssetStudioGUI.exe` is upstream AssetStudio with a **CODM** menu. Start it with `--codm` to open the
weapon browser immediately, or `--codm 1648` to open it on a weapon.

## CODM menu

| Item | Action |
|---|---|
| Weapon browser... (Ctrl+W) | opens the browser; the pak and bank index is built in the background the first time (about 15 s) |
| Reload codm.config.json | re-reads the config and rebuilds the index |
| Open codm.config.json | opens the config in the default editor (creates it with defaults when missing) |
| Open export folder | opens `ExportRoot` |
| Reader diagnostics in log | toggles the verbose per-field reader logging |

## Weapon browser

**Weapons tab**

- Left: weapons grouped by kind. Bold entries are base weapons (they carry the mesh); their children are
  skins, nested when a skin is itself the base of further skins. The filter matches id, name or folder.
  Ids are the real game ids (`2537` is the Locus railgun), the same numbers the sound banks use.
- Right: every file the catalog links to the selected weapon, grouped: weapon paks, one group per base
  in the chain, components (sights), effects, own sound banks, class banks. Weapon and base paks are
  checked by default.
- **Load checked (replace)** / **Add checked**: push the checked paks into the main window (all module
  copies of a path are loaded; duplicate CABs are skipped). With **Resolve dependencies** ticked the
  bundles they reference (shared animation packs, controllers, avatars) are loaded as well, so
  Animator + AnimationClip exports from the main window find their clips. Double-click a row to load
  that pak alone.
- **Sound class of this family**: the class that picks the `wpn_<class>` bank; shown as map / seed /
  kind. Change it and **Save class** to write `weapon-classes.json`.
- **Export weapon...**: choose a folder; `CodmExportService` writes `<folder>/<weapon>/` with the
  options ticked above the list (models as Cast, textures, sounds with wav conversion, FX textures,
  base weapon paks, component paks). Progress and warnings show in the log at the bottom.
- **Show pak in Explorer**, **Copy names** (decoded name + path of the checked rows).

**All paks tab**: search any decoded path (`cod_effects/particles`, `avatar/`, `ui_icon`, a skin name).
Load or add checked results, or double-click one.

**Sound banks tab**: filter the 2 187 bank names, see a bank's objects, events, where each sound's wem
lives, and extract all its wems to `ExportRoot/sounds/<bank>/`.

## Export Options (Options > Export options)

- **Export Cast** (default on): `.cast` model plus `anims/*.cast` for Animator exports and for skinned
  meshes exported through "Export selected assets".
- **Export glTF Binary**: debugging format, off by default.
- **Export FBX**: the stock AssetStudio FBX path (plus a Blender import script for single meshes), off by default.
- Scale factor applies to Cast and GLB.

The stock workflows are unchanged: load, pick an Animator plus AnimationClips, Export > Animator with
selected AnimationClips. The orphan/camera clip export remains an explicit exception path.
