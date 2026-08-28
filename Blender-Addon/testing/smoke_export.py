"""Headless Blender smoke test for standalone Simple Export."""

import importlib.util
import importlib
import json
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


def assert_mesh_group_conflict_resolution(addon) -> None:
    importer = importlib.import_module(f"{addon.__name__}.io.model.importer")
    visible = []
    hidden = []
    try:
        for name in ("0.0 Visible A", "1.0 Visible B"):
            mesh = bpy.data.meshes.new(f"{name} Data")
            obj = bpy.data.objects.new(name, mesh)
            bpy.context.collection.objects.link(obj)
            visible.append(obj)
        hidden_mesh = bpy.data.meshes.new("9.0 Hidden Data")
        hidden_obj = bpy.data.objects.new("9.0 Hidden", hidden_mesh)
        bpy.context.collection.objects.link(hidden_obj)
        hidden_obj.hide_set(True)
        hidden.append(hidden_obj)

        _visible_groups = importer.visible_mesh_group_ids()
        if _visible_groups != {0, 1}:
            raise AssertionError(f"hidden mesh groups leaked into visibility scan: {_visible_groups}")
        if importer.mesh_group_conflict_offset((0, 1, 2), _visible_groups) != 2:
            raise AssertionError("incoming groups 0,1,2 were not shifted to 2,3,4")
        if importer.mesh_group_conflict_offset((3, 4), _visible_groups) != 0:
            raise AssertionError("non-conflicting incoming groups were shifted")
        print("[PASS] MDL imports resolve visible mesh-group conflicts while ignoring hidden groups")
    finally:
        for obj in visible + hidden:
            mesh = obj.data
            bpy.data.objects.remove(obj, do_unlink=True)
            if mesh.users == 0:
                bpy.data.meshes.remove(mesh)


def run() -> None:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    root = Path(__file__).resolve().parents[1]
    addon = load_addon(root)
    try:
        assert_corner_aware_uv_export(addon)
        assert_mesh_group_conflict_resolution(addon)

        if bpy.context.scene.xiv_ie_settings.create_backfaces:
            raise AssertionError("Create Backfaces should default to disabled")
        if bpy.context.scene.xiv_ie_settings.backup_models_on_export:
            raise AssertionError("Backup models on Export should default to disabled")
        if bpy.context.scene.xiv_ie_settings.keep_shapekeys:
            raise AssertionError("Keep Shape Keys should default to disabled")
        if not bpy.context.scene.xiv_ie_settings.simple_import_set_export_directory:
            raise AssertionError("Set Simple Export Folder on Import should default to enabled")
        if not bpy.context.scene.xiv_ie_settings.resolve_mesh_group_conflicts:
            raise AssertionError("Resolve Mesh Group Name Conflicts should default to enabled")
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
            raise AssertionError("XIV Instant Edit material alias was not kept in sync")
        print("[PASS] Per-group material assignment updates every submesh")

        added_material = materials.assign_material_path(
            [added_group],
            "/mt_c0101e0001_top_added.mtrl",
        )

        initial_instant_props = bpy.context.scene.xiv_ie_instant_edit_props
        initial_settings = bpy.context.scene.xiv_ie_settings
        initial_scope = initial_instant_props.export_scope
        initial_export_directory = initial_settings.export_directory
        initial_export_name = initial_settings.export_name
        try:
            with tempfile.TemporaryDirectory(prefix="xiv-instant-edit-no-context-") as no_context_dir:
                initial_settings.export_directory = no_context_dir
                initial_settings.export_name = "no-context"
                initial_instant_props.export_scope = "CURRENT_COLLECTION"
                try:
                    bpy.ops.xiv_ie.simple_export()
                except RuntimeError as error:
                    if "XIV Instant Edit Collection" not in str(error):
                        raise
                else:
                    raise AssertionError("Simple Export accepted XIV Instant Edit Collection without a Context")
        finally:
            initial_instant_props.export_scope = initial_scope
            initial_settings.export_directory = initial_export_directory
            initial_settings.export_name = initial_export_name
        print("[PASS] Simple Export rejects XIV Instant Edit Collection without a Context")

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
            "resource_manifest_version": 1,
            "resource_manifest_status": "ready",
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
            raise AssertionError("XIV Instant Edit destination context was not preserved")
        visible_groups = materials.visible_material_groups()
        if [group.mesh_index for group in visible_groups] != [0, 1]:
            raise AssertionError("New mesh group was not discovered from object names")
        if len(visible_groups[0].objects) != 2:
            raise AssertionError("New visible mesh part was not grouped with the source mesh")
        if materials.material_paths(visible_groups[1].objects) != [added_material]:
            raise AssertionError("New mesh group material was not retained")
        print("[PASS] XIV Instant Edit discovers new visible parts and groups outside its collection")

        instant_ops = importlib.import_module(f"{addon.__name__}.instant_edit.ops")
        export_module = importlib.import_module(f"{addon.__name__}.mesh.export")

        class FakeResponse:
            status = 200

            def __init__(self, body=b'{"ok":true}'):
                self.body = body

            def __enter__(self):
                return self

            def __exit__(self, exc_type, exc_value, traceback):
                return False

            def getcode(self):
                return self.status

            def read(self, _size=-1):
                return self.body

        original_urlopen = instant_ops.urllib.request.urlopen

        planned_aliases = {
            (context_id, assigned.casefold()): "/mt_c0101e0001_top_a.mtrl",
            (context_id, added_material.casefold()): "/mt_c0101e0001_top_b.mtrl",
        }

        def fake_urlopen(request, timeout=0):
            if request.full_url.endswith("/mashup/plan"):
                payload = json.loads(request.data.decode("utf-8"))
                assignments = []
                incoming_slot = "c"
                for contributor in payload["contributors"]:
                    for material_path in contributor["materials"]:
                        key = (contributor["contextId"], material_path.casefold())
                        alias = planned_aliases.get(key)
                        if alias is None:
                            alias = f"/mt_c0101e0001_top_{incoming_slot}.mtrl"
                            incoming_slot = chr(ord(incoming_slot) + 1)
                        assignments.append({
                            "contextId": contributor["contextId"],
                            "modelMaterial": material_path,
                            "alias": alias,
                            "gamePath": "chara/equipment/e0001/material/v0001" + alias,
                            "slot": alias[-6],
                        })
                body = json.dumps({
                    "ok": True,
                    "planFingerprint": "a" * 64,
                    "assignments": assignments,
                }).encode("utf-8")
                return FakeResponse(body)
            return FakeResponse()

        instant_ops.urllib.request.urlopen = fake_urlopen
        instant_props = bpy.context.scene.xiv_ie_instant_edit_props
        instant_props.export_destination = context_id
        instant_props.variant_target = "NEW_GROUP"
        instant_props.variant_group_name = "Smoke Group"
        instant_props.variant_name = "smoke-quick"

        mashup_context_id = "smoke-mashup-context"
        mashup_collection = context_module.create_collection(bpy.context.scene, {
            "context_id": mashup_context_id,
            "schema": context_module.SCHEMA,
            "version": context_module.VERSION,
            "plugin_instance_id": "smoke-plugin",
            "capability": "smoke-mashup-capability",
            "source_game_path": "chara/equipment/e0002/model/c0101e0002_top.mdl",
            "managed_destination": "C:/Penumbra/OtherSmoke",
            "target_file_path": "C:/Penumbra/OtherSmoke/model.mdl",
            "source_mod_directory": "OtherSmokeMod",
            "source_mod_name": "Other Smoke Mod",
            "resource_manifest_version": 0,
            "resource_manifest_status": "legacy",
            "callback_port": 42428,
        })
        mashup_obj = added_group.copy()
        mashup_obj.data = added_group.data.copy()
        mashup_obj.name = "3.0 Mashup"
        mashup_collection.objects.link(mashup_obj)
        context_module.tag_object(mashup_obj, {
            **imported_metadata,
            "context_id": mashup_context_id,
            "xiv_material": added_material,
            "mesh_index": 2,
        })
        original_scope_for_mashup = instant_props.export_scope
        instant_props.export_scope = "VISIBLE"
        show, enabled, message = instant_ops.mashup_target_state(bpy.context)
        if not show or enabled or "Reload/update" not in message:
            raise AssertionError("Legacy mashup Context did not request a plugin update and re-import")

        context_module._set(mashup_collection, "resource_manifest_status", "capture_failed")
        show, enabled, message = instant_ops.mashup_target_state(bpy.context)
        if not show or enabled or "Dependency capture failed" not in message:
            raise AssertionError("Failed mashup capture was not distinguished from a legacy Context")

        context_module._set(mashup_collection, "resource_manifest_version", 1)
        context_module._set(mashup_collection, "resource_manifest_status", "ready")
        context_module._set(mashup_collection, "source_mod_directory", "SmokeMod")
        if instant_ops.mashup_target_state(bpy.context)[0]:
            raise AssertionError("Same-mod Contexts incorrectly offered Create Mashup")
        context_module._set(mashup_collection, "source_mod_directory", "OtherSmokeMod")
        show, enabled, message = instant_ops.mashup_target_state(bpy.context)
        if not show or not enabled or message:
            raise AssertionError(f"Valid multi-mod Contexts did not enable Create Mashup: {message}")
        instant_props.export_scope = "CURRENT_COLLECTION"
        if instant_ops.mashup_target_state(bpy.context)[0]:
            raise AssertionError("Current-collection Export Parts incorrectly admitted another Context")
        instant_props.export_scope = "VISIBLE"

        untagged_obj = added_group.copy()
        untagged_obj.data = added_group.data.copy()
        untagged_obj.name = "4.0 Untagged"
        bpy.context.scene.collection.objects.link(untagged_obj)
        selection = instant_ops.mashup_export_selection(bpy.context)
        if selection[3][untagged_obj.as_pointer()][0] != context_id:
            raise AssertionError("Untagged mashup mesh did not inherit the active Context")
        bpy.data.objects.remove(untagged_obj, do_unlink=True)

        original_material_properties = {
            item.as_pointer(): (
                item.get("xiv_material"), item.get("instant_edit_xiv_material"))
            for item in (obj, second, added_group, mashup_obj)
        }
        original_finish_job = instant_ops.finish_job
        instant_ops.finish_job = lambda _job: None
        try:
            mashup_target = instant_ops.perform_mashup_export(
                bpy.context, "ACTIVE_MOD", "Smoke Mashup")
        finally:
            instant_ops.finish_job = original_finish_job
        mashup_model = instant_ops.XIVModel.from_file(mashup_target)
        expected_aliases = {
            "/mt_c0101e0001_top_a.mtrl",
            "/mt_c0101e0001_top_b.mtrl",
            "/mt_c0101e0001_top_c.mtrl",
        }
        if set(mashup_model.materials) != expected_aliases:
            raise AssertionError(
                f"Mashup MDL aliases differ: actual={set(mashup_model.materials)} "
                f"expected={expected_aliases}"
            )
        if any(
            (item.get("xiv_material"), item.get("instant_edit_xiv_material")) !=
            original_material_properties[item.as_pointer()]
            for item in (obj, second, added_group, mashup_obj)
        ):
            raise AssertionError("Successful mashup export did not restore temporary aliases")
        importlib.import_module(f"{addon.__name__}.instant_edit.cache").remove_job(
            Path(mashup_target).parent)

        original_mashup_export_result = instant_ops.export_result
        instant_ops.export_result = lambda *_args, **_kwargs: (_ for _ in ()).throw(
            RuntimeError("forced mashup failure"))
        try:
            try:
                instant_ops.perform_mashup_export(bpy.context, "ACTIVE_MOD", "Failed Mashup")
            except RuntimeError as error:
                if str(error) != "forced mashup failure":
                    raise
            else:
                raise AssertionError("Forced mashup export failure did not occur")
        finally:
            instant_ops.export_result = original_mashup_export_result
        if any(
            (item.get("xiv_material"), item.get("instant_edit_xiv_material")) !=
            original_material_properties[item.as_pointer()]
            for item in (obj, second, added_group, mashup_obj)
        ):
            raise AssertionError("Failed mashup export did not restore temporary aliases")
        print("[PASS] Create Mashup eligibility, attribution, aliases, and restoration")

        mashup_obj.hide_set(True)
        if instant_ops.mashup_target_state(bpy.context)[0]:
            raise AssertionError("Hidden second Context did not remove Create Mashup")
        bpy.data.objects.remove(mashup_obj, do_unlink=True)
        bpy.data.collections.remove(mashup_collection)
        instant_props.export_scope = original_scope_for_mashup

        scope_capture = []
        original_simple_export_result = operators.export_result
        mannequin_mesh = bpy.data.meshes.new("SmokeMannequinData")
        mannequin_mesh.from_pydata([(0, 0, 0), (1, 0, 0), (0, 1, 0)], [], [(0, 1, 2)])
        mannequin = bpy.data.objects.new("Mannequin", mannequin_mesh)
        bpy.context.collection.objects.link(mannequin)

        def capture_simple_export(_path, _format, export_objects=None):
            scope_capture.append(tuple(export_objects or ()))

        settings = bpy.context.scene.xiv_ie_settings
        original_model_format = settings.model_format
        original_export_scope = instant_props.export_scope
        operators.export_result = capture_simple_export
        try:
            with tempfile.TemporaryDirectory(prefix="xiv-instant-edit-scope-") as scope_dir:
                settings.export_directory = scope_dir
                settings.export_name = "scope"
                settings.model_format = "FBX"

                instant_props.export_scope = "VISIBLE"
                if bpy.ops.xiv_ie.simple_export() != {"FINISHED"} or set(scope_capture[-1]) != set(operators.visible_meshobj()):
                    raise AssertionError("Simple Export All Visible did not use every visible mesh")

                instant_props.export_scope = "VISIBLE_NO_MANNEQUIN"
                if bpy.ops.xiv_ie.simple_export() != {"FINISHED"} or mannequin in scope_capture[-1]:
                    raise AssertionError("Simple Export did not exclude Mannequin")

                instant_props.export_scope = "CURRENT_COLLECTION"
                if bpy.ops.xiv_ie.simple_export() != {"FINISHED"} or scope_capture[-1] != (obj,):
                    raise AssertionError("Simple Export XIV Instant Edit Collection ignored the selected Context")
        finally:
            operators.export_result = original_simple_export_result
            settings.model_format = original_model_format
            instant_props.export_scope = original_export_scope
            bpy.data.objects.remove(mannequin, do_unlink=True)
        print("[PASS] Simple Export honors every Export Parts mode and requires Context for its collection")

        simple_import_module = importlib.import_module(f"{addon.__name__}.io.model")
        original_simple_import = simple_import_module.ModelImport.from_file
        original_simple_import_model = operators.XIVModel.from_file
        before_simple_import = {item.as_pointer() for item in bpy.data.objects}

        class FakeSimpleImportModel:
            bones = ()

        def fake_simple_import(_file_path, _import_name, **_kwargs):
            mesh = bpy.data.meshes.new("SimpleImportFolderMeshData")
            mesh.from_pydata([(0, 0, 0), (1, 0, 0), (0, 1, 0)], [], [(0, 1, 2)])
            imported = bpy.data.objects.new("SimpleImportFolderMesh", mesh)
            bpy.context.collection.objects.link(imported)
            return (imported,)

        simple_import_module.ModelImport.from_file = staticmethod(fake_simple_import)
        operators.XIVModel.from_file = staticmethod(lambda _path: FakeSimpleImportModel())
        try:
            with tempfile.TemporaryDirectory(prefix="xiv-instant-edit-import-folder-") as import_root:
                source_folder = Path(import_root) / "source"
                source_folder.mkdir()
                source_file = source_folder / "simple-import.mdl"
                source_file.write_bytes(b"placeholder")
                settings.simple_import_set_export_directory = True
                settings.simple_import_use_existing_skeleton = False
                settings.export_directory = str(Path(import_root) / "before-success")
                if bpy.ops.xiv_ie.simple_import(
                    "EXEC_DEFAULT", filepath=str(source_file), import_format="MDL"
                ) != {"FINISHED"}:
                    raise AssertionError("Simple Import folder-setting regression import failed")
                if Path(settings.export_directory).resolve() != source_folder.resolve():
                    raise AssertionError("Simple Import did not set the export folder to its source folder")

                def fail_simple_import(*_args, **_kwargs):
                    raise RuntimeError("forced import failure")

                simple_import_module.ModelImport.from_file = staticmethod(fail_simple_import)
                retained_directory = Path(import_root) / "unchanged-after-failure"
                settings.export_directory = str(retained_directory)
                try:
                    bpy.ops.xiv_ie.simple_import(
                        "EXEC_DEFAULT", filepath=str(source_file), import_format="MDL"
                    )
                except RuntimeError as error:
                    if "forced import failure" not in str(error):
                        raise
                else:
                    raise AssertionError("Forced Simple Import failure did not cancel")
                if Path(settings.export_directory) != retained_directory:
                    raise AssertionError("Failed Simple Import changed the export folder")
        finally:
            simple_import_module.ModelImport.from_file = original_simple_import
            operators.XIVModel.from_file = original_simple_import_model
            for item in tuple(bpy.data.objects):
                if item.as_pointer() not in before_simple_import:
                    bpy.data.objects.remove(item, do_unlink=True)
        print("[PASS] Simple Import updates its export folder only after success")

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
