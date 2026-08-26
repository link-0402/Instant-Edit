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


def assert_corner_aware_uv_export(addon) -> None:
    mesh = bpy.data.meshes.new("UVSeamRegressionData")
    mesh.from_pydata(
        [(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)],
        [],
        [(0, 1, 2), (0, 2, 3)],
    )
    mesh.update()
    obj = bpy.data.objects.new("UV Seam Regression", mesh)
    bpy.context.collection.objects.link(obj)

    # A non-export layer before uv0 used to make physical-layer slicing select
    # the wrong data. uv0 also has a discontinuity at shared vertex 0.
    ignored = mesh.uv_layers.new(name="Lightmap")
    ignored.uv.foreach_set("vector", [0.5, 0.5] * 6)
    uv0_values = [
        0.0, 0.0,
        1.0, 0.0,
        1.0, 1.0,
        0.75, 0.75,
        0.25, 0.75,
        0.0, 1.0,
    ]
    uv1_values = [
        0.1, 0.1,
        0.9, 0.1,
        0.9, 0.9,
        0.2, 0.8,
        0.8, 0.2,
        0.1, 0.9,
    ]
    uv0 = mesh.uv_layers.new(name="uv0")
    uv1 = mesh.uv_layers.new(name="uv1")
    uv0.uv.foreach_set("vector", uv0_values)
    uv1.uv.foreach_set("vector", uv1_values)

    constructor = importlib.import_module(f"{addon.__name__}.io.model.exp.constructor")
    streams_module = importlib.import_module(f"{addon.__name__}.io.model.exp.streams")
    declaration = constructor.decl_from_blend_mesh([obj])
    indices, streams, _shapes, source_vertices = streams_module.get_submesh_streams(
        obj, declaration, False
    )

    if len(source_vertices) <= len(mesh.vertices):
        raise AssertionError("A UV seam did not expand the shared Blender vertex")

    packed_uvs = streams[1]["uv0"]
    loop_vertices = [loop.vertex_index for loop in mesh.loops]
    for loop_idx, source_vertex in enumerate(loop_vertices):
        export_vertex = int(indices[loop_idx])
        if int(source_vertices[export_vertex]) != source_vertex:
            raise AssertionError("Corner mapping changed a loop's source vertex")

        expected = (
            uv0_values[loop_idx * 2],
            1.0 - uv0_values[loop_idx * 2 + 1],
            uv1_values[loop_idx * 2],
            1.0 - uv1_values[loop_idx * 2 + 1],
        )
        actual = tuple(float(value) for value in packed_uvs[export_vertex])
        if any(abs(got - want) > 1e-6 for got, want in zip(actual, expected)):
            raise AssertionError(
                f"Loop {loop_idx} UVs were reassociated: actual={actual}, expected={expected}"
            )

    print("[PASS] Corner-aware export preserves UV seams and logical layer order")
    bpy.data.objects.remove(obj, do_unlink=True)
    bpy.data.meshes.remove(mesh)


def run() -> None:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    root = Path(__file__).resolve().parents[1]
    addon = load_addon(root)
    try:
        assert_corner_aware_uv_export(addon)

        if bpy.context.scene.xiv_ie_settings.create_backfaces:
            raise AssertionError("Create Backfaces should default to disabled")
        if bpy.context.scene.xiv_ie_settings.backup_models_on_export:
            raise AssertionError("Backup models on Export should default to disabled")
        if bpy.context.scene.xiv_ie_settings.keep_shapekeys:
            raise AssertionError("Keep Shape Keys should default to disabled")
        bpy.context.scene.xiv_ie_settings.keep_shapekeys = True
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
        mesh.from_pydata(
            [(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0)],
            [],
            [(0, 1, 2), (0, 2, 3)],
        )
        mesh.update()
        obj = bpy.data.objects.new("0.0 Smoke", mesh)
        bpy.context.collection.objects.link(obj)
        obj.parent = armature
        modifier = obj.modifiers.new(name="Armature", type="ARMATURE")
        modifier.object = armature
        group = obj.vertex_groups.new(name="root")
        group.add([0, 1, 2, 3], 1.0, "REPLACE")
        uv = mesh.uv_layers.new(name="uv0")
        uv.uv.foreach_set(
            "vector",
            [0, 0, 1, 0, 1, 1, 0.75, 0.75, 0.25, 0.75, 0, 1],
        )
        colour = mesh.color_attributes.new(name="vc0", type="FLOAT_COLOR", domain="CORNER")
        colour.data.foreach_set("color", [1, 1, 1, 1] * 6)
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
        operators = importlib.import_module(f"{addon.__name__}.operators")
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
        export_module = importlib.import_module(f"{addon.__name__}.mesh.export")

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
        cache_module = importlib.import_module(f"{addon.__name__}.instant_edit.cache")
        original_auto_cleanup = cache_module.automatic_cleanup_enabled()
        cache_module.configure_cache(cache_module.cache_root().parent, False)
        armature.data.pose_position = "POSE"
        pose_bone = armature.pose.bones["root"]
        pose_bone.rotation_mode = "XYZ"
        pose_bone.rotation_euler = (0.2, -0.1, 0.3)
        original_pose = pose_bone.matrix_basis.copy()
        armature.scale = (2.0, 2.0, 2.0)
        obj.scale = (0.5, 0.75, 1.25)
        second.scale = (1.25, 0.75, 0.5)
        added_group.scale = (0.8, 1.1, 1.4)
        original_armature_scale = armature.scale.copy()
        original_mesh_scales = {
            mesh_obj: mesh_obj.scale.copy()
            for mesh_obj in (obj, second, added_group)
        }
        if obj.data.shape_keys is None:
            obj.shape_key_add(name="Basis")
        smoke_shape = obj.shape_key_add(name="shp_smoke")
        smoke_shape.data[0].co.z += 0.25
        smoke_shape.value = 0.65
        expected_shape_delta = tuple(
            smoke_shape.data[0].co[index] - obj.data.shape_keys.key_blocks[0].data[0].co[index]
            for index in range(3)
        )
        if export_module._armature_for_object(obj) is not armature:
            raise AssertionError("Smoke mesh is not associated with the expected armature")
        with export_module._clean_export_state([obj, second, added_group]):
            if tuple(armature.scale) != (1.0, 1.0, 1.0):
                raise AssertionError(f"Export guard did not neutralize armature scale: {tuple(armature.scale)}")
        if tuple(armature.scale) != (2.0, 2.0, 2.0):
            raise AssertionError(f"Export guard did not restore direct state: {tuple(armature.scale)}")
        if any(
            abs(actual - expected) > 1e-5
            for actual_row, expected_row in zip(pose_bone.matrix_basis, original_pose)
            for actual, expected in zip(actual_row, expected_row)
        ):
            raise AssertionError(
                f"Export guard did not restore direct pose state: "
                f"actual={tuple(tuple(row) for row in pose_bone.matrix_basis)} "
                f"expected={tuple(tuple(row) for row in original_pose)}"
            )
        try:
            quick_target = instant_ops.perform_instant_export(bpy.context)
        finally:
            instant_ops.urllib.request.urlopen = original_urlopen

        if tuple(armature.scale) != tuple(original_armature_scale):
            raise AssertionError(
                f"Quick Export did not restore armature scale: "
                f"actual={tuple(armature.scale)} expected={tuple(original_armature_scale)}"
            )
        if any(tuple(mesh_obj.scale) != tuple(scale) for mesh_obj, scale in original_mesh_scales.items()):
            raise AssertionError("Quick Export did not restore mesh scales")
        if armature.data.pose_position != "POSE" or any(
            abs(actual - expected) > 1e-5
            for actual_row, expected_row in zip(pose_bone.matrix_basis, original_pose)
            for actual, expected in zip(actual_row, expected_row)
        ):
            raise AssertionError("Quick Export did not restore armature pose")
        if abs(smoke_shape.value - 0.65) > 1e-6:
            raise AssertionError(f"Quick Export did not restore shape-key value: {smoke_shape.value}")
        print("[PASS] Quick Export temporarily resets and restores pose, scale, and shape keys")

        model_module = importlib.import_module(f"{addon.__name__}.xivpy.model")
        quick_model = model_module.XIVModel.from_file(quick_target)
        exported_shapes = {shape.name for shape in quick_model.shapes}
        if "shp_smoke" not in exported_shapes or quick_model.mesh_header.shape_value_count == 0:
            raise AssertionError("Quick Export did not serialize the shape key")
        importer_module = importlib.import_module(f"{addon.__name__}.io.model.importer")
        roundtrip_objects = []
        importer_module.ModelImport.from_file(
            str(quick_target),
            "shape-roundtrip",
            select_objects=False,
            created_objects=roundtrip_objects,
        )
        roundtrip_shape = next(
            (
                obj.data.shape_keys.key_blocks.get("shp_smoke")
                for obj in roundtrip_objects
                if obj.data.shape_keys and obj.data.shape_keys.key_blocks.get("shp_smoke")
            ),
            None,
        )
        if roundtrip_shape is None:
            raise AssertionError("Quick Export shape key was not reconstructed on import")
        if abs(roundtrip_shape.value) > 1e-6:
            raise AssertionError(f"Reimported shape key was not initialized to zero: {roundtrip_shape.value}")
        roundtrip_basis = roundtrip_shape.relative_key.data[0].co
        roundtrip_delta = tuple(roundtrip_shape.data[0].co[index] - roundtrip_basis[index] for index in range(3))
        if any(abs(actual - expected) > 1e-5 for actual, expected in zip(roundtrip_delta, expected_shape_delta)):
            raise AssertionError(
                f"Shape-key delta changed during export/import: actual={roundtrip_delta} expected={expected_shape_delta}"
            )
        for roundtrip_obj in roundtrip_objects:
            bpy.data.objects.remove(roundtrip_obj, do_unlink=True)
        print("[PASS] Quick Export round-trips shape-key geometry")
        if quick_model.lods[0].mesh_count != 2:
            raise AssertionError("Quick Export did not include the added mesh group")
        if [mesh.submesh_count for mesh in quick_model.meshes[:2]] != [2, 1]:
            raise AssertionError("Quick Export did not include the added mesh parts")
        if added_material not in quick_model.materials:
            raise AssertionError("Quick Export did not include the added material")
        if "0.(0,1); 1.(0)" not in bpy.context.scene.xiv_ie_instant_edit_props.last_status:
            raise AssertionError("Quick Export status did not report the exported group layout")
        print("[PASS] Actual Quick Export contains all visible parts, groups, and materials")
        cache_module.remove_job(Path(quick_target).parent)
        cache_module.configure_cache(cache_module.cache_root().parent, original_auto_cleanup)

        original_export_scene = export_module.ModelExport.__dict__["export_scene"]
        def fail_after_scene_preparation(_cls, *_args, **_kwargs):
            raise RuntimeError("forced exporter failure")
        export_module.ModelExport.export_scene = classmethod(fail_after_scene_preparation)
        try:
            with tempfile.TemporaryDirectory(prefix="xiv-instant-edit-failure-") as failure_dir:
                try:
                    export_module.export_result(
                        Path(failure_dir) / "forced_failure",
                        "MDL",
                        export_objects=[obj, second, added_group],
                    )
                except RuntimeError as error:
                    if str(error) != "forced exporter failure":
                        raise
                else:
                    raise AssertionError("Forced exporter failure did not occur")
        finally:
            export_module.ModelExport.export_scene = original_export_scene
        if abs(smoke_shape.value - 0.65) > 1e-6:
            raise AssertionError("Shape-key value was not restored after a failed export")
        print("[PASS] Failed export restores shape-key values")

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
            settings.backup_models_on_export = True
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

            result = bpy.ops.xiv_ie.simple_export()
            backup_module = importlib.import_module(f"{addon.__name__}.backups")
            backups = backup_module.list_backups(Path(temp_dir))
            if result != {"FINISHED"} or len(backups) != 1:
                raise AssertionError("Replacing an MDL did not create one timestamped backup")
            result = bpy.ops.xiv_ie.simple_export()
            backups = backup_module.list_backups(Path(temp_dir))
            if result != {"FINISHED"} or len(backups) != 2 or backups[0].created < backups[1].created:
                raise AssertionError("Repeated MDL exports did not retain backups newest first")
            print("[PASS] Simple Export retains timestamped backups in newest-first order")
            selected_backup = backups[-1]
            backup_module.restore_local(Path(temp_dir), selected_backup)
            if target.read_bytes() != selected_backup.path.read_bytes():
                raise AssertionError("Restoring an MDL backup did not replace the target model")
            if backup_module.clear_backups(Path(temp_dir)) < 3 or backup_module.list_backups(Path(temp_dir)):
                raise AssertionError("Clearing backups did not remove the recognized model backups")
            print("[PASS] Backup restore preserves the selected history and clear removes backups")
            settings.backup_models_on_export = False

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
        moved_part = operators._move_mesh_part_once(0, 1, "UP")
        if moved_part != 0 or not second.name.startswith("0.0 "):
            raise AssertionError("Mesh Studio part drag did not move the part upward")
        if operators._move_mesh_part_once(0, 0, "DOWN") != 1:
            raise AssertionError("Mesh Studio part drag did not restore the part order")
        moved_group = operators._move_mesh_group_once(0, "DOWN")
        if moved_group != 1:
            raise AssertionError("Mesh Studio group drag did not move the group downward")
        if not obj.name.startswith("1.0 ") or not added_group.name.startswith("0.0 "):
            raise AssertionError("Mesh Studio group drag did not preserve part IDs")
        print("[PASS] Mesh Studio rename, attributes, flow, and drag reordering")
    finally:
        addon.unregister()


if __name__ == "__main__":
    run()
