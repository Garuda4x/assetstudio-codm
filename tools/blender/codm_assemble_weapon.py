"""Assemble a CoD Mobile weapon in Blender: merge the attachment armatures into the main weapon rig.

Run it in a scene where the weapon (MainWeapon_..._1P) and its attachments were imported from the
AssetStudio .cast exports, one armature each. Text Editor > Open > Run Script, or
    blender file.blend -P codm_assemble_weapon.py
How CoD Mobile puts a weapon together (and what the script does):
  - Skinned parts (magazine, grip, rail with its blades) carry a copy of the weapon's bone chain and are
    weighted to bones the main rig has too (Bone_DanJia, Bone_RightHand, daopian_00...): their vertex
    groups are kept and the mesh is bound to the main rig.
  - Static parts (barrel, muzzle, stock, sight, bipod) hang at an attach point of the main rig: the
    part's root sits exactly on Bar_point, Sto_point, Sight_point, Bip_point... (the muzzle on the
    barrel's own Muzzle_point). The attach point is found by rest position, not by name, and the part
    is weighted 100 % to it.
  - Sockets of the parts that the main rig lacks (Muzzle_point, MuzzleFlashSocket, Center_point, ...)
    are added to the main rig under their attach point, so they move with the weapon.
  - The emptied part armatures are deleted; meshes, materials and modifiers stay.
The main rig is the armature named MainWeapon_... with Bone_RightHand (else the one with the most
bones). Animations from MainWeapon_..._1P/anims then play on the whole weapon.
"""
import bpy

TOL = 1e-4


def world_head(arm, bone):
    return arm.matrix_world @ bone.head_local


def find_main():
    cands = [o for o in bpy.data.objects if o.type == 'ARMATURE' and 'Bone_RightHand' in o.data.bones]
    if not cands:
        raise RuntimeError('no armature with Bone_RightHand: import the MainWeapon_..._1P cast first')
    return max(cands, key=lambda o: (o.name.lower().startswith('mainweapon'), len(o.data.bones)))


def part_meshes(arm):
    return [o for o in bpy.data.objects if o.type == 'MESH' and (
        o.parent == arm or any(m.type == 'ARMATURE' and m.object == arm for m in o.modifiers))]


def weighted_groups(obj):
    names = {g.index: g.name for g in obj.vertex_groups}
    used = set()
    for v in obj.data.vertices:
        for g in v.groups:
            if g.weight > 0:
                used.add(names.get(g.group))
    used.discard(None)
    return used


def prefix(name):
    letters = ''
    for ch in name.lower():
        if not ch.isalpha():
            break
        letters += ch
    return letters[:3]


def attach_bone(main, arm, root):
    """The main-rig bone at the part root's rest position: Bar_point over Bar_pointqiangtou_root."""
    pos = world_head(arm, root)
    pre = prefix(root.name)
    cands = [b for b in main.data.bones if (world_head(main, b) - pos).length < TOL]
    if not cands:
        return None
    cands.sort(key=lambda b: (not b.name.lower().startswith(pre), not b.name.endswith('_point'), len(b.name)))
    return cands[0].name


def graft(main, arm, bone, parent_name):
    """Add a part bone to the main rig with the same rest transform, under parent_name."""
    bpy.ops.object.mode_set(mode='OBJECT')
    bpy.context.view_layer.objects.active = main
    bpy.ops.object.mode_set(mode='EDIT')
    eb = main.data.edit_bones.new(bone.name)
    eb.matrix = main.matrix_world.inverted() @ arm.matrix_world @ bone.matrix_local
    eb.length = max(bone.length, 0.001)
    eb.parent = main.data.edit_bones[parent_name]
    bpy.ops.object.mode_set(mode='OBJECT')


def rebind(obj, main, mapping, default_bone):
    """Rename/merge vertex groups through mapping, bind unweighted meshes to default_bone, retarget to main."""
    used = weighted_groups(obj)
    if not used:
        g = obj.vertex_groups.get(default_bone) or obj.vertex_groups.new(name=default_bone)
        g.add(range(len(obj.data.vertices)), 1.0, 'REPLACE')
        used = {default_bone}
    for name in sorted(used):
        target = mapping.get(name, name)
        if target == name:
            continue
        src = obj.vertex_groups[name]
        dst = obj.vertex_groups.get(target)
        if dst is None:
            src.name = target
            continue
        for v in obj.data.vertices:  # merge into the existing group
            for g in v.groups:
                if g.group == src.index and g.weight > 0:
                    dst.add([v.index], g.weight, 'ADD')
        obj.vertex_groups.remove(src)
    for m in obj.modifiers:
        if m.type == 'ARMATURE':
            m.object = main
    if not any(m.type == 'ARMATURE' for m in obj.modifiers):
        obj.modifiers.new('Armature', 'ARMATURE').object = main
    mw = obj.matrix_world.copy()
    obj.parent = main
    obj.parent_type = 'OBJECT'
    obj.matrix_world = mw


def assemble():
    if bpy.context.object is not None and bpy.context.object.mode != 'OBJECT':
        bpy.ops.object.mode_set(mode='OBJECT')
    main = find_main()
    parts = [o for o in bpy.data.objects if o.type == 'ARMATURE' and o != main]
    print('main rig:', main.name, '(%d bones), parts: %d' % (len(main.data.bones), len(parts)))
    pending = list(parts)
    # a part can hang on a socket another part brings (muzzle on the barrel's Muzzle_point): repeat
    while pending:
        progress = False
        for arm in list(pending):
            meshes = part_meshes(arm)
            used = set().union(*(weighted_groups(o) for o in meshes)) if meshes else set()
            roots = [b for b in arm.data.bones if b.parent is None]
            mapping = {}
            # static: unweighted, or weighted to its own bones (root, mesh node) that the main rig lacks
            static = not used or any(n not in main.data.bones for n in used)
            static_root = None
            if static:
                root = roots[0]
                at = attach_bone(main, arm, root)
                if at is None:
                    continue  # its socket comes with another part
                mapping[root.name] = at
                static_root = root
                # sockets and node bones the main rig lacks, top-down
                for b in root.children_recursive:
                    if b.name in main.data.bones:
                        mapping[b.name] = b.name
                        continue
                    parent = mapping.get(b.parent.name, at)
                    graft(main, arm, b, parent)
                    mapping[b.name] = b.name
            for o in meshes:
                rebind(o, main, mapping, mapping.get(static_root.name) if static_root else 'Bone_RightHand')
            kind = 'static on %s' % mapping[static_root.name] if static_root else 'skinned by name'
            print('  %-34s %-26s meshes %d' % (arm.name, kind, len(meshes)))
            if not part_meshes(arm):
                bpy.data.objects.remove(arm, do_unlink=True)
            pending.remove(arm)
            progress = True
        if not progress:
            for arm in pending:
                print('  %-34s no attach point found at its root position, left as is' % arm.name)
            break
    bpy.context.view_layer.objects.active = main
    main.select_set(True)
    print('done: %d bones on %s' % (len(main.data.bones), main.name))


assemble()
