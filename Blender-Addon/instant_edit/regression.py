"""Blender checks for Instant Edit staging isolation.

Call ``assert_staging_isolated(collection, created_objects, sentinels)`` after a
test import, where ``sentinels`` are user objects captured before the import.
"""
# Modified for XIV Instant Edit, 2026. See MODIFICATIONS.md.

import importlib
import importlib.util
import sys
import tempfile
from pathlib import Path
from types import SimpleNamespace

import bpy
import numpy as np


def _same_object(left, right) -> bool:
    """Compare Blender IDs without bpy_prop_collection object containment."""
    return left is right or (
        left is not None
        and right is not None
        and left.name == right.name
        and left.as_pointer() == right.as_pointer()
    )


def _require(condition: bool, message: str) -> None:
    if condition:
        print(f"[PASS] {message}")
    else:
        print(f"[FAIL] {message}")
        raise AssertionError(message)


def assert_staging_isolated(collection, created_objects, sentinels=()) -> None:
    """Assert that an Instant Edit import stayed in its dedicated collection."""
    _require(
        not _same_object(collection, bpy.context.collection),
        "staging collection is not the ambient context collection",
    )
    _require(
        any(_same_object(item, collection) for item in bpy.data.collections),
        "staging collection is present in bpy.data.collections",
    )
    _require(
        collection.get("instant_edit_collection_kind") == "instant_edit",
        "staging collection has the Instant Edit collection tag",
    )

    created_objects = tuple(created_objects)
    _require(
        all(
            any(_same_object(item, obj) for item in collection.objects)
            for obj in created_objects
        ),
        "every created object is linked to the staging collection",
    )
    _require(
        not any(
            _same_object(created, sentinel)
            for created in created_objects
            for sentinel in sentinels
        ),
        "created objects do not include user sentinel objects",
    )
    _require(
        all(obj.get("instant_edit_context_id") for obj in created_objects),
        "every created object has an Instant Edit context tag",
    )


def _load_addon(addon_root: Path):
    """Load the hyphenated extension directory under a test-only package name."""
    package_name = "_xiv_instant_edit_regression_addon"
    spec = importlib.util.spec_from_file_location(
        package_name,
        addon_root / "__init__.py",
        submodule_search_locations=[str(addon_root)],
    )
    if spec is None or spec.loader is None:
        raise RuntimeError("could not create addon import specification")
    addon = importlib.util.module_from_spec(spec)
    sys.modules[package_name] = addon
    spec.loader.exec_module(addon)
    return addon, package_name


def _register_for_test(addon, package_name: str) -> None:
    """Register the standalone add-on for a Blender regression run."""
    addon.register()


def _unregister_for_test(addon, package_name: str) -> None:
    addon.unregister()


def run_staging_isolation_regression() -> None:
    """Run the legacy-request containment regression in headless Blender."""
    addon_root = Path(__file__).resolve().parents[1]
    addon = None
    temp_path = None
    staging = None
    created_objects = ()
    existing_collection = None
    existing_mesh = None
    skeleton = None
    sentinel = None

    try:
        bpy.ops.wm.read_factory_settings(use_empty=True)
        ambient = bpy.context.collection

        sentinel_mesh = bpy.data.meshes.new("InstantEditRegressionSentinelMesh")
        sentinel = bpy.data.objects.new("InstantEditRegressionSentinel", sentinel_mesh)
        ambient.objects.link(sentinel)

        bpy.ops.object.select_all(action="DESELECT")
        sentinel.select_set(True)
        bpy.context.view_layer.objects.active = sentinel
        before_selected = tuple(bpy.context.selected_objects)
        before_active = bpy.context.view_layer.objects.active

        with tempfile.NamedTemporaryFile(suffix=".mdl", delete=False) as mdl_file:
            temp_path = Path(mdl_file.name)
            mdl_file.write(b"regression placeholder")

        addon, package_name = _load_addon(addon_root)
        _register_for_test(addon, package_name)
        ops = importlib.import_module(f"{package_name}.instant_edit.ops")
        server = importlib.import_module(f"{package_name}.instant_edit.server")
        export_streams = importlib.import_module(f"{package_name}.io.model.exp.streams")

        stream_dtype = np.dtype([
            ("uv0", np.float32, (4,)),
            ("colour0", np.uint8, (4,)),
            ("colour1", np.uint8, (4,)),
            ("flow", np.uint8, (4,)),
        ])
        texture_stream = np.zeros(2, dtype=stream_dtype)
        texture_stream["uv0"][:, :2] = ((0.25, 0.75), (0.5, 0.125))
        texture_stream["colour0"][:] = 12
        texture_stream["colour1"][:] = 34
        texture_stream["flow"][:] = 56
        export_streams.apply_mesh_options({1: texture_stream}, {
            "copy_uv1_to_uv2": True,
            "clear_vertex_color1": True,
            "clear_vertex_color2": True,
            "clear_flow_data": True,
        })
        _require(
            np.array_equal(texture_stream["uv0"][:, :2], texture_stream["uv0"][:, 2:4]),
            "UV1 can be copied into UV2 during export",
        )
        _require(np.all(texture_stream["colour0"] == 255), "vertex color 1 can be cleared")
        _require(
            np.all(texture_stream["colour1"][:, :3] == 0) and np.all(texture_stream["colour1"][:, 3] == 255),
            "vertex color 2 can be cleared",
        )
        _require(
            np.all(texture_stream["flow"][:, :2] == 0) and np.all(texture_stream["flow"][:, 2:] == 255),
            "flow data can be reset to neutral",
        )
        export_streams.apply_mesh_options({1: texture_stream}, {"clear_uv2": True})
        _require(np.all(texture_stream["uv0"][:, 2:4] == 0), "UV2 can be cleared during export")

        validated = server._ImportHandler._validate_import({
            "schema": "instant-edit.import",
            "version": 1,
            "filePath": r"C:\Temp\instant-edit-import.mdl",
            "name": "Regression Model",
            "context": {
                "schema": "instant-edit.context",
                "version": 1,
                "pluginInstanceId": "plugin-instance",
                "contextId": "context-id",
                "importId": "import-id",
                "capability": "capability",
                "gamePath": "chara/equipment/e0001/model/c0101e0001_top.mdl",
                "objectIndex": 0,
                "modName": "SourceModDirectory",
                "targetFilePath": r"D:\Penumbra\SourceMod\models\original.mdl",
                "managedDestination": r"D:\Penumbra\SourceMod\models",
                "sourceModDirectory": "SourceModDirectory",
                "sourceModName": "Source Mod",
                "callbackPort": 42428,
            },
        })
        _require(
            validated["targetFilePath"] == r"D:\Penumbra\SourceMod\models\original.mdl",
            "the original physical model target is preserved in Blender's import context",
        )
        _require(
            validated["managedDestination"] == r"D:\Penumbra\SourceMod\models",
            "the original target folder is preserved for Blender's UI",
        )

        validated_options = server._ImportHandler._validate_import({
            "schema": "instant-edit.import",
            "version": 1,
            "filePath": r"C:\Temp\instant-edit-import.mdl",
            "name": "Regression Model",
            "importOptions": {
                "armatureMode": "existing",
                "targetObject": "  Skeleton  ",
            },
            "context": {
                "schema": "instant-edit.context",
                "version": 1,
                "pluginInstanceId": "plugin-instance",
                "contextId": "context-id-options",
                "importId": "import-id-options",
                "capability": "capability",
                "gamePath": "chara/equipment/e0001/model/c0101e0001_top.mdl",
                "objectIndex": 0,
                "modName": "SourceModDirectory",
                "targetFilePath": r"D:\Penumbra\SourceMod\models\original.mdl",
                "managedDestination": r"D:\Penumbra\SourceMod\models",
                "sourceModDirectory": "SourceModDirectory",
                "sourceModName": "Source Mod",
                "callbackPort": 42428,
            },
        })
        _require(
            validated_options["importOptions"] == {
                "armatureMode": "existing",
                "targetObject": "Skeleton",
            },
            "existing-skeleton import options are normalized",
        )
        try:
            server._ImportHandler._validate_import({"importOptions": {"armatureMode": "unknown"}})
        except ValueError:
            print("[PASS] invalid import options are rejected")
        else:
            raise AssertionError("invalid import options were accepted")

        instant_props = bpy.context.scene.xiv_ie_instant_edit_props
        _require(
            instant_props.redraw_mode == "GLAM",
            "Glamourer refresh is the default redraw mode",
        )
        for redraw_mode in ("SELF", "ALL", "GLAM"):
            instant_props.redraw_mode = redraw_mode
            _require(
                instant_props.redraw_mode == redraw_mode,
                f"{redraw_mode} redraw mode can be selected",
            )
        redraw_payload = ops.build_export_payload(
            SimpleNamespace(
                plugin_instance_id="plugin-instance",
                context_id="context-id",
                capability="capability",
            ),
            "export-id",
            Path(tempfile.gettempdir()) / "redraw-test.mdl",
            1,
            "0" * 64,
            instant_props,
            None,
        )
        _require(
            redraw_payload["redrawMode"] == "GLAM",
            "selected redraw mode is included in the secure export envelope",
        )
        instant_props.save_as_variant = True
        instant_props.auto_setup_penumbra = True
        instant_props.save_as_variant = False
        _require(
            not instant_props.auto_setup_penumbra,
            "Penumbra setup is cleared when Save as Variant is disabled",
        )

        _require(
            ops.normalise_variant_name("  alternate.mdl ") == "alternate",
            "variant names are normalized without duplicating the .mdl extension",
        )
        _require(
            ops.variant_game_path(
                "chara/equipment/e0001/model/c0101e0001_top.mdl",
                "alternate",
            ) == "chara/equipment/e0001/model/alternate.mdl",
            "variant export stays beside the authorized source model",
        )
        try:
            ops.normalise_variant_name("../outside")
        except ValueError:
            pass
        else:
            raise AssertionError("variant name accepted a path traversal")
        print("[PASS] variant names cannot select another directory")

        class FakeModel:
            bones = ("root",)

        def fake_import(
            file_path,
            import_name,
            collection=None,
            context_metadata=None,
            **kwargs,
        ):
            _require(collection is not None, "Instant Edit supplies a dedicated collection")
            _require(kwargs.get("require_collection") is True, "Instant Edit requires collection containment")

            mesh = bpy.data.meshes.new("InstantEditRegressionImportedMesh")
            obj = bpy.data.objects.new("InstantEditRegressionImported", mesh)
            collection.objects.link(obj)
            created_objects_arg = kwargs.get("created_objects")
            if created_objects_arg is not None:
                created_objects_arg.append(obj)
            return (obj,)

        original_import = ops.ModelImport.from_file
        original_model_from_file = ops.XIVModel.from_file
        ops.ModelImport.from_file = staticmethod(fake_import)
        ops.XIVModel.from_file = staticmethod(lambda file_path: FakeModel())

        try:
            result = bpy.ops.xiv_ie.instant_import(
                "EXEC_DEFAULT",
                file_path=str(temp_path),
                import_name="Instant Edit Regression",
                schema="",
                version=0,
            )
        finally:
            ops.ModelImport.from_file = original_import
            ops.XIVModel.from_file = original_model_from_file

        _require(result == {"FINISHED"}, "legacy Instant Edit request completes")

        staging = next(
            collection
            for collection in bpy.data.collections
            if collection.get("instant_edit_collection_kind") == "instant_edit"
            and collection.get("legacy")
        )
        created_objects = tuple(staging.objects)
        assert_staging_isolated(staging, created_objects, (sentinel,))

        _require(
            any(_same_object(item, sentinel) for item in ambient.objects),
            "user sentinel remains in the ambient collection",
        )
        _require(sentinel.parent is None, "user sentinel parentage is unchanged")
        _require(len(sentinel.modifiers) == 0, "user sentinel modifiers are unchanged")
        _require(
            not any(
                any(_same_object(item, created) for item in ambient.objects)
                for created in created_objects
            ),
            "no staging object is linked to the ambient collection",
        )
        _require(
            tuple(bpy.context.selected_objects)
            and all(
                any(_same_object(item, selected) for item in bpy.context.selected_objects)
                for selected in before_selected
            ),
            "user selection is restored",
        )
        _require(
            _same_object(bpy.context.view_layer.objects.active, before_active),
            "user active object is restored",
        )

        mesh_objects = [obj for obj in created_objects if obj.type == "MESH"]
        armatures = [obj for obj in created_objects if obj.type == "ARMATURE"]
        _require(len(mesh_objects) == 1, "exactly one imported mesh is staged")
        _require(len(armatures) == 1, "exactly one imported armature is staged")
        _require(
            _same_object(mesh_objects[0].parent, armatures[0]),
            "only the returned imported mesh is parented to the armature",
        )
        _require(
            any(
                modifier.type == "ARMATURE"
                and _same_object(modifier.object, armatures[0])
                for modifier in mesh_objects[0].modifiers
            ),
            "the returned imported mesh receives the armature modifier",
        )

        skeleton_data = bpy.data.armatures.new("InstantEditRegressionSkeletonData")
        skeleton = bpy.data.objects.new("Skeleton", skeleton_data)
        ambient.objects.link(skeleton)
        existing_collection = bpy.data.collections.new("InstantEditRegressionExisting")
        bpy.context.scene.collection.children.link(existing_collection)
        existing_mesh_data = bpy.data.meshes.new("InstantEditRegressionExistingMeshData")
        existing_mesh = bpy.data.objects.new("InstantEditRegressionExistingMesh", existing_mesh_data)
        existing_collection.objects.link(existing_mesh)
        ops.InstantImport._bind_existing_armature(
            None,
            bpy.context,
            (existing_mesh,),
            existing_collection,
            "Skeleton",
            [existing_mesh],
        )
        _require(existing_mesh.parent is None, "existing-skeleton binding does not parent imported meshes")
        _require(
            len([modifier for modifier in existing_mesh.modifiers if modifier.type == "ARMATURE"]) == 1 and
            _same_object(next(modifier for modifier in existing_mesh.modifiers if modifier.type == "ARMATURE").object, skeleton),
            "existing-skeleton binding targets the named scene armature",
        )
        _require(
            not skeleton.get("instant_edit_context_id"),
            "existing scene armature is not tagged as an Instant Edit object",
        )

        print("[RESULT] staging-isolation regression PASSED")
    finally:
        if addon is not None:
            try:
                _unregister_for_test(addon, package_name)
            except Exception as error:
                print(f"[WARN] addon cleanup failed: {error}")

        for obj in reversed(tuple(created_objects)):
            if any(_same_object(item, obj) for item in bpy.data.objects):
                bpy.data.objects.remove(obj, do_unlink=True)
        if sentinel is not None and any(_same_object(item, sentinel) for item in bpy.data.objects):
            bpy.data.objects.remove(sentinel, do_unlink=True)
        if existing_mesh is not None and any(_same_object(item, existing_mesh) for item in bpy.data.objects):
            bpy.data.objects.remove(existing_mesh, do_unlink=True)
        if skeleton is not None and any(_same_object(item, skeleton) for item in bpy.data.objects):
            bpy.data.objects.remove(skeleton, do_unlink=True)
        if existing_collection is not None and any(_same_object(item, existing_collection) for item in bpy.data.collections):
            bpy.data.collections.remove(existing_collection, do_unlink=True)
        if staging is not None and any(_same_object(item, staging) for item in bpy.data.collections):
            bpy.data.collections.remove(staging, do_unlink=True)
        if temp_path is not None:
            temp_path.unlink(missing_ok=True)


if __name__ == "__main__":
    run_staging_isolation_regression()
