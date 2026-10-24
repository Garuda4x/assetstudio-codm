# GUI guide

`AssetStudioGUI.exe` is upstream AssetStudio with a **CODM** menu. Start it with `--codm` to open the
weapon browser immediately, or `--codm 1648` to open it on a weapon.

## CODM menu

| Item | Action |
|---|---|
| Weapon browser... (Ctrl+W) | opens the browser; the pak and bank index is built in the background the first time (about 15 s) |
| Switch config... | picks another config file (`codm.config.1957.json` for the live install); remembered across sessions, the menu shows the current file |
| Reload config | re-reads the config and rebuilds the index |
| Open config file | opens the config in the default editor (creates it with defaults when missing) |
| Open export folder | opens `ExportRoot` |
| Reader diagnostics in log | toggles the verbose per-field reader logging |

## Weapon browser

On a 1.9.57 config the browser lists every bundle of the game containers, not only what has been
unpacked: the catalog, base chains, components and banks look as they did for 1.9.55. Rows in **grey**
are still packed; loading, previewing or exporting unpacks them to `UnpackRoot` first (the log shows each
one, a few seconds for a weapon) and they stay there for next time. **Unpack checked** writes them
without loading. After the index is ready, the CAB index (needed to resolve dependencies) and then the
attachment index are built in the background: the first time on 1.9.57 that reads the headers of all
22 552 bundles from the containers, about 2.5 minutes, cached after.

**Weapons tab**

- Left: weapons grouped by kind. Bold entries are base weapons (they carry the mesh); their children are
  skins, nested when a skin is itself the base of further skins. The filter matches id, name or folder.
  Ids are the real game ids (`2537` is the Locus railgun), the same numbers the sound banks use.
- Right: every file the catalog links to the selected weapon, grouped: weapon paks, one group per base
  in the chain, components (sights by name; magazines, barrels, stocks, grips once the attachment index
  has been built in the background, each row showing the matching asset folder and the full paths as a
  tooltip), effects, own sound banks (found by their event names), class banks. Weapon and base paks are
  checked by default.
- **Load checked (replace)** / **Add checked**: push the checked paks into the main window (all module
  copies of a path are loaded; duplicate CABs are skipped). With **Resolve dependencies** ticked the
  bundles they reference (shared animation packs, controllers, avatars) are loaded as well, so
  Animator + AnimationClip exports from the main window find their clips. Double-click a row to load
  that pak alone.
- **Sound class of this family**: the class that picks the `wpn_<class>` bank; shown as map / seed /
  kind. Change it and **Save class** to write `weapon-classes.json`.
- **Export weapon...**: choose a folder; `CodmExportService` writes `<folder>/<weapon>/` with the
  options ticked above the list:
  - **Models (cast)**: one Cast per prefab with its animations; `_Camera` clips are merged onto a
    `Camera` bone of the first-person rig; animation events become Cast notetracks and
    `anims/notetracks.txt`. Each mesh gets its material (found by name, attachments by UV layout)
    with the colour map, written to `textures/` and referenced from the Cast file, so the model
    imports textured (Blender Cast addon). Normal and metal/roughness maps are not linked.
  - **Textures**, **Sounds**, **wem -> wav** (needs `VgmstreamPath`).
  - **Class banks too**: also dump `wpn_<class>` and the banks the weapon's banks borrow from (a whole
    weapon class, hundreds of wems). Off by default.
  - **Bank variants**: also dump the lettered banks (`247a`, `247c`...), most likely other skins'
    sets. Off by default; `<id>` and `<id>p3` are always taken.
  - **FX textures**, **Include base weapon**, **Include components**, **Resolve dependencies**.
    Component paks are shared buckets (`component/bar/37.2` holds the barrels of many weapons); once
    the attachment index is built, only the attachments linked to this weapon and its bases are
    exported from them.

  Progress and warnings show in the log at the bottom. HQ bank copies (`Windows_HQ`) are used when the
  game shipped one; everything else is 22 kHz source material.
- **Preview mesh**: loads the checked paks (with dependencies when ticked) into the main window and
  selects the most interesting mesh (first-person prefab first, then the biggest LOD0) in the Asset List,
  so the Preview tab shows it.
- **Unpack checked**: write the checked grey rows to the unpack folder without loading them.
- **Show pak in Explorer** (unpacks a grey row first), **Copy names** (decoded name + path of the checked
  rows).
- A **Characters** root at the bottom of the tree lists every skin under `cod_models/avatar`
  (bot and event variants under their sub folder). Selecting one shows its paks; Load, Preview and
  Export work the same way, the export going to `<export root>/characters/<name>/`.

**All paks tab**: search any decoded path (`cod_effects/particles`, `avatar/`, `ui_icon`, a skin name).
Load, add or unpack checked results, or double-click one. Grey rows are still in the game containers.

**Sound banks tab**: filter the 2 187 bank names, see a bank's events (with recovered names where
known and the number of wems each one reaches), where each sound's wem lives, and extract all its wems
to `ExportRoot/sounds/<bank>/<event>/`.

## Export Options (Options > Export options)

- **Export Cast** (default on): `.cast` model plus `anims/*.cast` for Animator exports and for skinned
  meshes exported through "Export selected assets".
- **Export glTF Binary**: debugging format, off by default.
- **Export FBX**: the stock AssetStudio FBX path (plus a Blender import script for single meshes), off by default.
- Scale factor applies to Cast and GLB.

The stock workflows are unchanged: load, pick an Animator plus AnimationClips, Export > Animator with
selected AnimationClips. The orphan/camera clip export remains an explicit exception path.
