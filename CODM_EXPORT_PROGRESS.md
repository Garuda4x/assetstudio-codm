# CoD Mobile Asset Extraction — Progress Summary

## Objective
Export 3D character models (skinned meshes with skeleton, materials, and textures) from **Call of Duty Mobile** `.pak` files using a modified [AssetStudio](https://github.com/Perfare/AssetStudio) fork. The game uses **Unity 5.6.4p4** with a **modified/custom engine**.

---

## What Works ?

### 1. Mesh + Skeleton Export to GLB
- **Single skinned mesh** exports correctly to `.glb` via `Exporter.ExportSkinnedMeshGlb()`
- Uses the `ModelConverter(Mesh, Avatar, ImageFormat, Material[])` constructor
- Skeleton is built from the `Avatar` object's `m_AvatarSkeleton` + `m_DefaultPose`
- Bone paths resolved via `Avatar.FindBonePath()` + CRC32 hash matching
- Bind poses come from `Mesh.m_BindPose`
- Bone weights/indices come from `Mesh.m_Skin`
- **Armature is correct** in Blender
- **Weights are correct** and binding is intact
- **UVMap is present** on the mesh

### 2. Materials on GLB Export
- `ModelConverter.ConvertMeshDirect()` now accepts optional `Material[]`
- `Exporter.FindMaterialsForMesh()` locates materials by finding `SkinnedMeshRenderer` objects that reference the mesh, then resolving their `m_Materials` PPtrs
- When real materials are found, `ConvertMaterial()` extracts colors (diffuse, ambient, emissive, specular) and texture references
- When no materials found, named placeholder `ImportedMaterial` objects are created so GLB still has material slots
- `GltfExporter` builds PBR materials with base color and can embed textures (diffuse + normal maps)

### 3. Skeleton Pruning (no more empties)
- `GltfExporter` now collects all bone paths from meshes, builds a `requiredPaths` set (bones + ancestors), and only emits glTF nodes for those paths
- This removes the LOD mesh references (Head_LOD0, Body_LOD1, Hair_LOD2, etc.) that previously showed as empty objects in Blender

### 4. SkinnedMeshRenderer Deserialization
- CoD Mobile's modified Unity 5.6 engine has quirks; the `SkinnedMeshRenderer` and `Renderer` classes have extensive diagnostic logging
- The `Renderer` class comment notes: CoD Mobile modified engine does NOT include `m_SortingLayer` (SInt16) in version 5.6+ — the standard AssetStudio code has a comment about this
- Deserialization is wrapped in try/catch in `AssetsManager.ReadAssets()` so failures don't crash loading

### 5. Version Parsing Hardening
- `SerializedFile.SetVersion()` sanitizes control characters, truncates versions with >4 parts
- `AssetsManager.CheckStrippedVersion()` warns about obfuscated versions

---

## Texture2D — FIXED ?

### Root Cause: Alignment + Extra Fields
CoD Mobile's modified Unity 5.6 engine has TWO differences from standard Unity in Texture2D:
1. **No `AlignStream()` padding** after `m_IsReadable` boolean — standard Unity pads to 4 bytes, CoD Mobile doesn't
2. **Two extra Int32 fields** after alignment position (before `m_ImageCount`) — one appears to be a constant (9), the other a per-texture hash

**Fix**: Heuristic detection in `Texture2D.cs` — peeks at what `m_ImageCount` and `m_TextureDimension` would be at the current position. If they look invalid, tries skipping 8 bytes (the extra fields). Valid `m_ImageCount` is 1-6, valid `m_TextureDimension` is -1 to 6.

**Result**: Textures now preview correctly in GUI and export individually via Convert. All formats confirmed working: DXT1, DXT5, BC7.

**Fix applied**: `Texture2D.cs` now uses a heuristic — saves position before alignment, peeks at what `m_ImageCount` would be after alignment, and if the value is unreasonable (negative or >256), skips alignment.

#### Diagnostic Logging Added (not yet run)
Four logging points were added to diagnose the root cause:

| Location | Tag | What it logs |
|----------|-----|-------------|
| `Texture2D` constructor | `[Tex2D]` | Dimensions, format, `image_data_size`, `m_StreamData.path/offset/size` |
| `Texture2DConverter.DecodeTexture2D()` | `[Tex2DDecode]` | Whether skipped (size=0) or decoding, catches resource FileNotFoundException |
| `ResourceReader.GetReader()` | `[ResourceReader]` | When resource file not found — lists all known resource file names |
| `Exporter.ExportTexture2D()` | `[TexExport]` | Texture details and when ConvertToImage returns null |

#### Most Likely Root Cause
For Unity 5.6, when `image_data_size == 0`, the texture data is **streamed** in a separate `.resS` file. The `StreamingInfo.path` points to this file, but `ResourceReader.GetReader()` likely can't find it because:
1. The `.resS` file wasn't included in the bundle that was loaded
2. The `.resS` file isn't on disk next to the loaded file
3. Path naming mismatch between `StreamingInfo.path` and actual resource file names

**Next step**: Run with diagnostics, check `[Tex2D]` logs for `inlineSize` vs `streamPath` values. If `inlineSize=0` and `streamPath` is non-empty but the resource isn't found, need to ensure the companion `.resS` file is loaded alongside the main asset bundle.

---

## Files Modified (from upstream AssetStudio master)

### Core Asset Reading
| File | Changes |
|------|---------|
| `AssetStudio\SerializedFile.cs` | Version sanitization, truncation to 4 parts, debug logging |
| `AssetStudio\AssetsManager.cs` | try/catch around `SkinnedMeshRenderer` + `AnimationClip` deserialization, hex dump logging for SMR, version debug logging in `ReadAssets()` |
| `AssetStudio\Classes\Renderer.cs` | Extensive position logging at each deserialization step; note about CoD Mobile missing `m_SortingLayer` field |
| `AssetStudio\Classes\SkinnedMeshRenderer.cs` | Position logging at each field read |
| `AssetStudio\Classes\Texture2D.cs` | Added `[Tex2D]` diagnostic log after reading all fields |
| `AssetStudio\ResourceReader.cs` | Added `[ResourceReader]` warning log when resource file not found |
| `AssetStudioGUI\GUILogger.cs` | Added `Debug.WriteLine()` to always output to VS Output window |

### Export Pipeline
| File | Changes |
|------|---------|
| `AssetStudioUtility\ModelConverter.cs` | New `ModelConverter(Mesh, Avatar, ImageFormat, Material[])` constructor; extracted `BuildSkeletonFromAvatar()` method; `ConvertMeshDirect()` now accepts `Material[]` and creates real `ImportedMaterial` entries |
| `AssetStudioUtility\GltfExporter.cs` | **New file** — full glTF Binary exporter using SharpGLTF Schema2 API. Supports skinning, skeleton, materials with PBR, texture embedding, skeleton pruning via `requiredPaths` |
| `AssetStudioGUI\Exporter.cs` | `ExportSkinnedMeshGlb()` method; `ExportSkinnedMeshFbx()` passes materials; `FindAvatarForMesh()`, `FindMaterialsForMesh()`, `GenerateBlenderScript()` helpers; `ExportConvertFile` routes skinned meshes through GLB+OBJ+FBX pipeline; `[TexExport]` diagnostic logging |
| `AssetStudioUtility\Texture2DConverter.cs` | Added `[Tex2DDecode]` logging and try/catch around resource data read |

### Dependencies Added
- **SharpGLTF** NuGet package (for `GltfExporter.cs`)

---

## Architecture Notes

### Export Flow for Skinned Meshes
```
User selects Mesh in Asset List ? Export selected assets (Convert)
  ? ExportConvertFile() detects skinned mesh (has m_Skin + m_BindPose)
    ? ExportSkinnedMeshGlb()     ? ModelConverter(Mesh, Avatar, ..., Materials)  ? GltfExporter.ExportGlb()  ? .glb
    ? ExportMesh()               ? standard OBJ export                                                       ? .obj
    ? ExportSkinnedMeshFbx()     ? ModelConverter(Mesh, Avatar, ..., Materials)  ? FBX + Blender .py script  ? .fbx + .py
```

### Export Flow for Textures (broken)
```
User selects Texture2D ? Export
  ? ExportConvertFile() ? ExportTexture2D()
    ? m_Texture2D.ConvertToImage(true)
      ? Texture2DConverter.DecodeTexture2D()
        ? reader.Size == 0 ? returns false (likely: streamed data, .resS not found)
    ? ConvertToImage returns null ? export returns false, nothing written
```

### Key Classes
- `ModelConverter` implements `IImported` — the bridge between Unity objects and the export format
- `ImportedMesh`, `ImportedBone`, `ImportedMaterial`, `ImportedTexture` — intermediate representation
- `GltfExporter.ExportGlb()` — takes `IImported`, produces `.glb` via SharpGLTF
- `ResourceReader` — lazy reader that resolves streamed data from `.resS` files or inline data

---

## Test Asset
- Character: `C_F_Alias_Player_Co_Creation` (female character)
- Parts: Body_LOD0, Head_LOD0, Hair_LOD0, Eyelash_LOD0 (+ LOD1/2/3 variants)
- Container paths: `assets/cod_models/avatar/34v1_28v1/c_f_alias_player_co_creation/...`
- Textures: `_bottom_a.tga`, `_head_a.tga`, `_body_m.tga`, `_head_n.tga` etc.

---

## Merge Export (Removed/Parked)
A multi-mesh merged export was implemented and then reverted because it added complexity. The code existed as:
- `ModelConverter(Mesh[], Avatar, ImageFormat, Material[][])` constructor
- `Exporter.ExportSkinnedMeshMergedGlb()` with `FindRelatedSkinnedMeshes()` and `GetCharacterBaseName()`
- Node counter in GltfExporter for multiple meshes on same node

This was removed to reduce convolution. Can be re-added later when single-mesh export is fully stable.

---

## How to Export (Current Best Practice)
1. Load `.pak` file in AssetStudio GUI
2. Go to **Asset List** tab
3. Filter by **Type = Mesh**
4. Select a skinned mesh (e.g., `C_F_Alias_Player_Co_Creation_Body_LOD0`)
5. Right-click ? **Export selected assets** (or menu: Export ? Selected assets ? Convert)
6. Output folder will contain `.glb` (primary), `.obj`, `.fbx`, `.py`
7. Open the `.glb` in Blender — armature + skinned mesh + materials

---

## Immediate Next Steps

### GLB Export Fixes (Latest)
Two issues fixed in `GltfExporter.cs` during "Export All":

1. **`FileNotFoundException` for textures** — `model.UseImage(imgName)` was being interpreted as a file path by SharpGLTF. Fixed by using `model.UseImage(new SharpGLTF.Memory.MemoryImage(texData.Data))` to create the image from in-memory bytes directly. Also added null/length checks and try/catch to gracefully handle missing texture data.

2. **`Node instances with a Skin must not contain spatial transformations`** — glTF spec requires skinned mesh nodes to have identity transforms. Fixed by creating a child node with identity transform for the mesh+skin attachment, keeping the parent node's spatial transform intact for the skeleton.

### Action Items
1. **Test Export All** — stop debugger, rebuild, re-launch, load pak, Export All
2. **Verify GLB in Blender** — check that textures are embedded, skeleton is correct, mesh is properly skinned
3. **Clean up diagnostic logging** — once everything is confirmed working, remove the verbose `[Tex2D]`, `[Tex2DDecode]`, `[TexExport]` etc. log lines
