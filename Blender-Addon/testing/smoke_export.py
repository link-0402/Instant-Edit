"""Headless Blender smoke test for standalone Simple Export."""

import importlib.util
import importlib
import sys
import tempfile
from pathlib import Path

import bpy


def load_addon(root: Path):
    package_name = "_xiv_instant_edit_export_smoke"
    spec = importlib.util.spec_from_file_location(
        package_name,
        root / "__init__.py",
        submodule_search_locations=[str(root)],
    )
    if spec is None or spec.loader is None:
        raise RuntimeError("could not load XIV Instant Edit")
    addon = importlib.util.module_from_spec(spec)
    sys.modules[package_name] = addon
    spec.loader.exec_module(addon)
    addon.register()
    return addon


def run() -> None:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    root = Path(__file__).resolve().parents[1]
    addon = load_addon(root)
    try:
        if bpy.context.scene.xiv_ie_settings.create_backfaces:
            raise AssertionError("Create Backfaces should default to disabled")
        if bpy.context.scene.xiv_ie_instant_edit_props.show_utilities:
            raise AssertionError("Utilities should be collapsed by default")

        armature_data = bpy.data.armatures.new("SmokeSkeletonData")
        armature = bpy.data.objects.new("SmokeSkeleton", armature_data)
        bpy.context.collection.objects.link(armature)
        bpy.context.view_layer.objects.active = armature
        armature.select_set(True)
        bpy.ops.object.mode_set(mode="EDIT")
        bone = armature_data.edit_bones.new("root")
        bone.head = (0, 0, 0)
        bone.tail = (0, 0, 1)
        bpy.ops.object.mode_set(mode="OBJECT")

        mesh = bpy.data.meshes.new("SmokeMeshData")
        mesh.from_pydata([(0, 0, 0), (1, 0, 0), (0, 1, 0)], [], [(0, 1, 2)])
        mesh.update()
        obj = bpy.data.objects.new("0.0 Smoke", mesh)
        bpy.context.collection.objects.link(obj)
        obj.parent = armature
        modifier = obj.modifiers.new(name="Armature", type="ARMATURE")
        modifier.object = armature
        group = obj.vertex_groups.new(name="root")
        group.add([0, 1, 2], 1.0, "REPLACE")
        uv = mesh.uv_layers.new(name="uv0")
        uv.uv.foreach_set("vector", [0, 0, 1, 0, 0, 1])
        colour = mesh.color_attributes.new(name="vc0", type="FLOAT_COLOR", domain="CORNER")
        colour.data.foreach_set("color", [1, 1, 1, 1] * 3)
        material = bpy.data.materials.new("SmokeMaterial")
        mesh.materials.append(material)
        obj["xiv_material"] = "/mt_c0101e0001_top_a.mtrl"
        obj["instant_edit_xiv_material"] = obj["xiv_material"]

        second = obj.copy()
        second.data = obj.data.copy()
        second.name = "0.1 Smoke"
        bpy.context.collection.objects.link(second)

        added_group = obj.copy()
        added_group.data = obj.data.copy()
        added_group.name = "1.0 Added Group"
        for key in tuple(added_group.keys()):
            del added_group[key]
        bpy.context.collection.objects.link(added_group)

        materials = importlib.import_module(f"{addon.__name__}.materials")
        groups = materials.group_mesh_objects([obj, second])
        if len(groups) != 1 or groups[0].mesh_index != 0 or len(groups[0].objects) != 2:
            raise AssertionError("Mesh material grouping did not collect both submeshes")
        assigned = materials.assign_material_path(groups[0].objects, "Bibo")
        if assigned != "/mt_c0101b0001_bibo.mtrl":
            raise AssertionError(f"Material preset was not normalized: {assigned}")
        if any(item["xiv_material"] != assigned for item in groups[0].objects):
            raise AssertionError("Material path was not assigned to every submesh")
        if obj["instant_edit_xiv_material"] != assigned:
            raise AssertionError("Instant Edit material alias was not kept in sync")
        print("[PASS] Per-group material assignment updates every submesh")

        added_material = materials.assign_material_path(
            [added_group],
            "/mt_c0101e0001_top_added.mtrl",
        )

        context_module = importlib.import_module(f"{addon.__name__}.instant_edit.context")
        context_id = "smoke-context"
        context_collection = context_module.create_collection(bpy.context.scene, {
            "context_id": context_id,
            "schema": context_module.SCHEMA,
            "version": context_module.VERSION,
            "plugin_instance_id": "smoke-plugin",
            "capability": "smoke-capability",
            "source_game_path": "chara/equipment/e0001/model/c0101e0001_top.mdl",
            "managed_destination": "C:/Penumbra/Smoke",
            "target_file_path": "C:/Penumbra/Smoke/model.mdl",
            "source_mod_directory": "SmokeMod",
            "source_mod_name": "Smoke Mod",
            "callback_port": 42428,
        })
        context_collection.objects.link(obj)

        imported_metadata = {
            "context_id": context_id,
            "schema": context_module.SCHEMA,
            "version": context_module.VERSION,
            "xiv_material": assigned,
            "original_material": "SmokeMaterial",
            "material_index": 0,
            "mesh_index": 0,
            "submesh_index": 0,
        }
        context_module.tag_object(obj, imported_metadata)
        context_module.tag_object(second, imported_metadata)
        context_module.tag_object(added_group, {
            **imported_metadata,
            "xiv_material": added_material,
        })
        bpy.context.scene.xiv_ie_instant_edit_props.context_id = context_id

        for selected in bpy.context.selected_objects:
            selected.select_set(False)
        added_group.select_set(True)
        bpy.context.view_layer.objects.active = added_group
        ref = context_module.active_context(bpy.context)
        if len(ref.mesh_objects) != 1:
            raise AssertionError("Instant Edit destination context was not preserved")
        visible_groups = materials.visible_material_groups()
        if [group.mesh_index for group in visible_groups] != [0, 1]:
            raise AssertionError("New mesh group was not discovered from object names")
        if len(visible_groups[0].objects) != 2:
            raise AssertionError("New visible mesh part was not grouped with the source mesh")
        if materials.material_paths(visible_groups[1].objects) != [added_material]:
            raise AssertionError("New mesh group material was not retained")
        print("[PASS] Instant Edit discovers new visible parts and groups outside its collection")

        instant_ops = importlib.import_module(f"{addon.__name__}.instant_edit.ops")

        class FakeResponse:
            status = 200

            def __enter__(self):
                return self

            def __exit__(self, exc_type, exc_value, traceback):
                return False

            def getcode(self):
                return self.status

            def read(self, _size=-1):
                return b'{"ok":true}'

        original_urlopen = instant_ops.urllib.request.urlopen
        instant_ops.urllib.request.urlopen = lambda request, timeout=0: FakeResponse()
        try:
            quick_target = instant_ops.perform_instant_export(bpy.context)
        finally:
            instant_ops.urllib.request.urlopen = original_urlopen

        model_module = importlib.import_module(f"{addon.__name__}.xivpy.model")
        quick_model = model_module.XIVModel.from_file(quick_target)
        if quick_model.lods[0].mesh_count != 2:
            raise AssertionError("Quick Export did not include the added mesh group")
        if [mesh.submesh_count for mesh in quick_model.meshes[:2]] != [2, 1]:
            raise AssertionError("Quick Export did not include the added mesh parts")
        if added_material not in quick_model.materials:
            raise AssertionError("Quick Export did not include the added material")
        if "0.(0,1); 1.(0)" not in bpy.context.scene.xiv_ie_instant_edit_props.last_status:
            raise AssertionError("Quick Export status did not report the exported group layout")
        print("[PASS] Actual Quick Export contains all visible parts, groups, and materials")

        for selected in bpy.context.selected_objects:
            selected.select_set(False)
        obj.select_set(True)
        bpy.context.view_layer.objects.active = obj

        with tempfile.TemporaryDirectory(prefix="xiv-instant-edit-smoke-") as temp_dir:
            settings = bpy.context.scene.xiv_ie_settings
            settings.export_directory = temp_dir
            settings.export_name = "smoke"
            settings.model_format = "MDL"
            settings.create_backfaces = False
            result = bpy.ops.xiv_ie.simple_export()
            target = Path(temp_dir) / "smoke.mdl"
            if result != {"FINISHED"} or not target.is_file() or target.stat().st_size == 0:
                raise AssertionError(f"Simple Export failed: result={result}, target={target}")
            exported_model = model_module.XIVModel.from_file(target)
            if exported_model.lods[0].mesh_count != 2:
                raise AssertionError("Export did not contain the newly added mesh group")
            if [mesh.submesh_count for mesh in exported_model.meshes[:2]] != [2, 1]:
                raise AssertionError("Export did not contain all new mesh parts")
            if added_material not in exported_model.materials:
                raise AssertionError("Export did not contain the new mesh group's material")
            print("[PASS] Exported MDL contains added parts, groups, and material")
            print(f"[PASS] Simple Export produced {target.stat().st_size} bytes")

            obj.parent = None
            settings.export_name = "smoke_modifier_only"
            result = bpy.ops.xiv_ie.simple_export()
            modifier_target = Path(temp_dir) / "smoke_modifier_only.mdl"
            if result != {"FINISHED"} or not modifier_target.is_file() or modifier_target.stat().st_size == 0:
                raise AssertionError(
                    f"Modifier-only export failed: result={result}, target={modifier_target}"
                )
            print(f"[PASS] Modifier-only skeleton export produced {modifier_target.stat().st_size} bytes")

        renamed = materials.rename_mesh_part([obj, second, added_group], 0, 1, "Renamed Part")
        if renamed != "Renamed Part" or second.name != "0.1 Renamed Part":
            raise AssertionError("Mass part rename did not preserve the mesh ID")
        materials.set_mesh_part_tags([obj, second, added_group], 0, 1, "body,  Body, armor")
        if second.get("instant_edit_tags") != "body, armor":
            raise AssertionError("Part tags were not normalized and stored")
        attribute = materials.set_mesh_part_attribute(
            [obj, second, added_group], 0, 1, "atr_nek", True
        )
        if attribute != "atr_nek" or not second.get(attribute):
            raise AssertionError("Mesh Studio attribute was not applied to the mesh part")
        if materials.attribute_display_name(attribute) != "Neck":
            raise AssertionError("Mesh Studio attribute label was not resolved")
        if materials.ensure_flow_data([obj, second]) != 2:
            raise AssertionError("Mesh Studio flow data was not created")
        materials.set_mesh_flow_enabled([obj, second], True)
        if not materials.mesh_flow_enabled([obj, second]):
            raise AssertionError("Mesh Studio flow export was not enabled")
        if bpy.ops.xiv_ie.mesh_attribute(
            mesh_group=0, mesh_part=1, attribute="atr_nek"
        ) != {"FINISHED"} or second.get("atr_nek"):
            raise AssertionError("Mesh Studio attribute operator did not remove the attribute")
        if bpy.ops.xiv_ie.mesh_attribute(
            mesh_group=0, mesh_part=1, attribute="NEW", selection="atr_nek"
        ) != {"FINISHED"} or not second.get("atr_nek"):
            raise AssertionError("Mesh Studio attribute operator did not add the attribute")
        if bpy.ops.xiv_ie.mesh_flow(mesh_group=0, action="TOGGLE") != {"FINISHED"}:
            raise AssertionError("Mesh Studio flow operator failed")
        if materials.mesh_flow_enabled([obj, second]):
            raise AssertionError("Mesh Studio flow operator did not disable export")
        bpy.ops.xiv_ie.mesh_flow(mesh_group=0, action="TOGGLE")
        materials.swap_mesh_groups([obj, second, added_group], 0, 1)
        if not obj.name.startswith("1.0 ") or not added_group.name.startswith("0.0 "):
            raise AssertionError("Mesh group priority swap did not preserve part IDs")
        print("[PASS] Mesh Studio rename, attributes, flow, and priority controls")
    finally:
        addon.unregister()


if __name__ == "__main__":
    run()
