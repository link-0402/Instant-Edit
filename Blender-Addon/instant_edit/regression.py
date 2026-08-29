"""Blender checks for XIV Instant Edit staging isolation.

Call ``assert_staging_isolated(collection, created_objects, sentinels)`` after a
test import, where ``sentinels`` are user objects captured before the import.
"""
# Modified for XIV Instant Edit, 2026.

import importlib
import importlib.util
import json
import struct
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
    """Assert that an XIV Instant Edit import stayed in its dedicated collection."""
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
        "staging collection has the XIV Instant Edit collection tag",
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
        "every created object has an XIV Instant Edit context tag",
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
    """Run the context-isolation regression in headless Blender."""
    addon_root = Path(__file__).resolve().parents[1]
    addon = None
    temp_path = None
    staging = None
    created_objects = ()
    existing_collection = None
    existing_mesh = None
    explicit_context_collection = None
    explicit_context_mesh = None
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
        props = importlib.import_module(f"{package_name}.instant_edit.props")
        context_module = importlib.import_module(f"{package_name}.instant_edit.context")
        server = importlib.import_module(f"{package_name}.instant_edit.server")
        plugin_http = importlib.import_module(f"{package_name}.instant_edit.plugin_http")
        ui = importlib.import_module(f"{package_name}.ui")
        material_preview = importlib.import_module(
            f"{package_name}.instant_edit.material_preview"
        )
        _require(
            props._export_destination_items(None, bpy.context) == [],
            "an empty scene context list does not expose a placeholder choice",
        )
        export_streams = importlib.import_module(f"{package_name}.io.model.exp.streams")
        model_file = importlib.import_module(f"{package_name}.xivpy.model.file")

        for version, expected in (
            (model_file.XIVModel.V5, "Pre-Dawntrail MDL version"),
            (0xDEADBEEF, "Unsupported MDL version 0xDEADBEEF"),
        ):
            try:
                model_file.XIVModel.from_bytes(struct.pack("<I", version))
            except ValueError as error:
                _require(expected in str(error), f"MDL version 0x{version:08X} is rejected clearly")
            else:
                raise AssertionError(f"unsupported MDL version 0x{version:08X} was accepted")

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

        base_import = {
            "schema": "instant-edit.context",
            "version": 1,
            "pluginInstanceId": "plugin-instance",
            "contextId": "context-id",
            "importId": "import-id",
            "capability": "capability",
            "filePath": r"C:\Temp\instant-edit-import.mdl",
            "sourceGamePath": "chara/equipment/e0001/model/c0101e0001_top.mdl",
            "objectIndex": 0,
            "displayName": "Regression Model",
            "callbackPort": 42428,
            "targetFilePath": r"D:\Penumbra\SourceMod\models\original.mdl",
            "managedDestination": r"D:\Penumbra\SourceMod\models",
            "sourceModDirectory": "SourceModDirectory",
            "sourceModName": "Source Mod",
            "sourceModRootPath": r"D:\Penumbra\SourceMod",
            "targetRelativePath": "Files/models/original.mdl",
            "resourceManifestVersion": 0,
            "resourceManifestStatus": "capture_failed",
        }
        validated = server._ImportHandler._validate_import(base_import)
        try:
            server._ImportHandler._validate_import({**base_import, "schema": "instant-edit.import"})
        except ValueError:
            print("[PASS] transitional import envelopes are rejected")
        else:
            raise AssertionError("a transitional import envelope was accepted")
        _require(
            validated["targetFilePath"] == r"D:\Penumbra\SourceMod\models\original.mdl",
            "the original physical model target is preserved in Blender's import context",
        )
        _require(
            validated["managedDestination"] == r"D:\Penumbra\SourceMod\models",
            "the original target folder is preserved",
        )
        _require(
            validated["targetRelativePath"] == "Files/models/original.mdl",
            "the durable target-relative path survives the import envelope",
        )
        display_ref = SimpleNamespace(
            target_relative_path=validated["targetRelativePath"],
            target_file_path=validated["targetFilePath"],
            source_mod_root_path=r"D:\Penumbra\SourceMod",
        )
        _require(
            ui._export_destination_display(display_ref) == "Files/models/original.mdl",
            "the export display omits the mod root and includes the model filename",
        )
        _require(
            ui._wrap_display_value(
                "model/normal/chara/equipment/e0691/model/c0201e0691_top.mdl",
                42,
            ) == [
                "model/normal/chara/equipment/e0691/model/",
                "c0201e0691_top.mdl",
            ],
            "path info wraps at slash boundaries",
        )
        derived_display_ref = SimpleNamespace(
            target_relative_path="",
            target_file_path=validated["targetFilePath"],
            source_mod_root_path=r"D:\Penumbra\SourceMod",
        )
        _require(
            ui._export_destination_display(derived_display_ref) == "models/original.mdl",
            "the export display derives a mod-relative model path",
        )

        validated_options = server._ImportHandler._validate_import({
            **base_import,
            "contextId": "context-id-options",
            "importId": "import-id-options",
            "importOptions": {
                "armatureMode": "existing",
                "targetObject": "  Skeleton  ",
                "applyTexturesAndMaterials": True,
                "excludeBodyAndGeneralMaterials": True,
            },
            "previewManifestPath": r"C:\Temp\preview\materials.json",
        })
        _require(
            validated_options["importOptions"] == {
                "armatureMode": "existing",
                "targetObject": "Skeleton",
                "applyTexturesAndMaterials": True,
                "excludeBodyAndGeneralMaterials": True,
            },
            "existing-skeleton import options are normalized",
        )
        _require(
            validated_options["previewManifestPath"] == r"C:\Temp\preview\materials.json",
            "the material-preview manifest path is preserved in the import envelope",
        )
        _require(
            server._ImportHandler._validate_import({**base_import})["importOptions"] == {
                "armatureMode": "generated",
                "targetObject": "Skeleton",
                "applyTexturesAndMaterials": False,
                "excludeBodyAndGeneralMaterials": False,
            },
            "material previews default to disabled",
        )
        try:
            server._ImportHandler._validate_import({**base_import, "importOptions": {"armatureMode": "unknown"}})
        except ValueError:
            print("[PASS] invalid import options are rejected")
        else:
            raise AssertionError("invalid import options were accepted")
        try:
            server._ImportHandler._validate_import({
                **base_import,
                "importOptions": {"applyTexturesAndMaterials": "yes"},
            })
        except ValueError:
            print("[PASS] non-boolean material-preview options are rejected")
        else:
            raise AssertionError("a non-boolean material-preview option was accepted")
        try:
            server._ImportHandler._validate_import({
                **base_import,
                "importOptions": {"excludeBodyAndGeneralMaterials": True},
            })
        except ValueError:
            print("[PASS] body/general exclusion requires material previews")
        else:
            raise AssertionError("body/general exclusion was accepted without material previews")

        with tempfile.TemporaryDirectory() as preview_temp:
            import_directory = Path(preview_temp) / ("a" * 32)
            preview_directory = import_directory / "preview"
            preview_directory.mkdir(parents=True)
            model_path = import_directory / "model.mdl"
            model_path.write_bytes(b"synthetic model")
            diffuse_bytes = bytes((255, 0, 0, 255, 0, 255, 0, 128))
            normal_bytes = bytes((128, 128, 255, 255))
            index_bytes = bytes((0, 255, 0, 255, 0, 0, 0, 255))
            (preview_directory / "diffuse.rgba").write_bytes(diffuse_bytes)
            (preview_directory / "normal.rgba").write_bytes(normal_bytes)
            (preview_directory / "index.rgba").write_bytes(index_bytes)
            preview_manifest = {
                "schema": "instant-edit.material-preview",
                "version": 1,
                "warnings": ["One optional sampler was unavailable"],
                "excludedMaterials": ["/mt_c0201b0001_bibo.mtrl"],
                "materials": [{
                    "modelMaterial": "/mt_c0101e0001_top_a.mtrl",
                    "gamePath": "chara/equipment/e0001/material/v0001/mt_c0101e0001_top_a.mtrl",
                    "shaderPackage": "character.shpk",
                    "additionalData": "01020304",
                    "shaderKeys": [{"category": 1, "value": 2}],
                    "shaderConstants": [{"id": 3, "values": [0.5]}],
                    "colorSet": {
                        "width": 1,
                        "height": 1,
                        "values": [1.0, 0.5, 0.25, 1.0],
                    },
                    "textures": [{
                        "usage": "diffuse",
                        "samplerId": 0x115306BE,
                        "samplerFlags": 0,
                        "gamePath": "chara/common/texture/test_d.tex",
                        "file": "diffuse.rgba",
                        "width": 2,
                        "height": 1,
                        "uvSet": 0,
                        "colorSpace": "sRGB",
                    }, {
                        "usage": "normal",
                        "samplerId": 0x0C5EC1F1,
                        "samplerFlags": 0,
                        "gamePath": "chara/common/texture/test_n.tex",
                        "file": "normal.rgba",
                        "width": 1,
                        "height": 1,
                        "uvSet": 1,
                        "colorSpace": "Non-Color",
                    }, {
                        "usage": "mask",
                        "samplerId": 0x8A4E82B6,
                        "samplerFlags": 0,
                        "gamePath": "chara/common/texture/test_m.tex",
                        "file": "normal.rgba",
                        "width": 1,
                        "height": 1,
                        "uvSet": 0,
                        "colorSpace": "Non-Color",
                    }],
                }, {
                    "modelMaterial": "/mt_gear.mtrl",
                    "gamePath": "chara/equipment/e0001/material/v0001/mt_gear.mtrl",
                    "shaderPackage": "character.shpk",
                    "additionalData": "",
                    "shaderKeys": [],
                    "shaderConstants": [],
                    "colorSet": {
                        "width": 8,
                        "height": 2,
                        "values": (
                            [1.0, 0.0, 0.0] + [0.0] * 29
                            + [0.0, 0.0, 1.0] + [0.0] * 29
                        ),
                    },
                    "textures": [{
                        "usage": "index",
                        "samplerId": 0x565F8FD8,
                        "samplerFlags": 0,
                        "gamePath": "chara/equipment/e0001/texture/test_id.tex",
                        "file": "index.rgba",
                        "width": 2,
                        "height": 1,
                        "uvSet": 0,
                        "colorSpace": "Non-Color",
                    }, {
                        "usage": "normal",
                        "samplerId": 0x0C5EC1F1,
                        "samplerFlags": 0,
                        "gamePath": "chara/equipment/e0001/texture/test_n.tex",
                        "file": "normal.rgba",
                        "width": 1,
                        "height": 1,
                        "uvSet": 0,
                        "colorSpace": "Non-Color",
                    }],
                }, {
                    "modelMaterial": "/mt_empty.mtrl",
                    "gamePath": "chara/equipment/e0001/material/v0001/mt_empty.mtrl",
                    "shaderPackage": "character.shpk",
                    "additionalData": "",
                    "shaderKeys": [],
                    "shaderConstants": [],
                    "colorSet": None,
                    "textures": [],
                }],
            }
            manifest_path = preview_directory / "materials.json"
            manifest_path.write_text(json.dumps(preview_manifest), encoding="utf-8")
            preview_package = material_preview.load_preview_manifest(
                str(manifest_path),
                str(model_path),
            )
            _require(
                len(preview_package.materials) == 3,
                "a bounded synthetic material-preview manifest is accepted",
            )
            warning_count = len(preview_package.warnings)
            _require(
                material_preview.create_preview_material(
                    "/mt_c0201b0001_bibo.mtrl",
                    (0.8, 0.1, 0.8, 1.0),
                    preview_package,
                    "excluded-context",
                ) is None and len(preview_package.warnings) == warning_count,
                "intentionally excluded body materials retain placeholders without warnings",
            )
            preview_material_one = material_preview.create_preview_material(
                "/mt_c0101e0001_top_a.mtrl",
                (0.2, 0.3, 0.4, 1.0),
                preview_package,
                "context-one",
            )
            preview_material_two = material_preview.create_preview_material(
                "/mt_c0101e0001_top_a.mtrl",
                (0.2, 0.3, 0.4, 1.0),
                preview_package,
                "context-two",
            )
            _require(
                preview_material_one is not None and preview_material_two is not None,
                "synthetic preview materials are created",
            )
            _require(
                preview_material_one.as_pointer() != preview_material_two.as_pointer(),
                "preview materials remain local to each import context",
            )
            _require(
                preview_material_one["xiv_shader_package"] == "character.shpk"
                and "xiv_colorset" in preview_material_one,
                "shader and colorset metadata are retained on the Blender material",
            )
            principled = next(
                node for node in preview_material_one.node_tree.nodes
                if node.bl_idname == "ShaderNodeBsdfPrincipled"
            )
            _require(
                principled.inputs["Base Color"].is_linked,
                "the diffuse image drives Principled base color",
            )
            _require(
                principled.inputs["Normal"].is_linked,
                "the tangent normal map drives the Principled normal input",
            )
            gear_material = material_preview.create_preview_material(
                "/mt_gear.mtrl",
                (0.8, 0.1, 0.8, 1.0),
                preview_package,
                "gear-context",
            )
            _require(
                gear_material is not None,
                "character gear can build a preview without a diffuse texture",
            )
            gear_principled = next(
                node for node in gear_material.node_tree.nodes
                if node.bl_idname == "ShaderNodeBsdfPrincipled"
            )
            gear_base_node = gear_principled.inputs["Base Color"].links[0].from_node
            _require(
                gear_base_node.image.get("instant_edit_preview_usage") == "colorset-base",
                "gear base color is synthesized from its colorset and index texture",
            )
            gear_pixels = np.asarray(gear_base_node.image.pixels[:], dtype=np.float32).reshape((1, 2, 4))
            _require(
                gear_pixels[0, 0, 0] > 0.99 and gear_pixels[0, 0, 2] < 0.01
                and gear_pixels[0, 1, 2] > 0.99 and gear_pixels[0, 1, 0] < 0.01,
                "colorset row selection and interpolation produce the expected gear colors",
            )
            preview_images = list(preview_package.created_images)
            _require(
                {image.colorspace_settings.name for image in preview_images} == {"sRGB", "Non-Color"},
                "diffuse and data images use the expected Blender color spaces",
            )
            mask_image = next(
                image for image in preview_images
                if image.get("instant_edit_preview_usage") == "mask"
            )
            _require(
                mask_image["xiv_sampler_id"] == "0x8A4E82B6",
                "unsigned FFXIV sampler IDs are retained without overflowing Blender properties",
            )
            _require(
                all(image.packed_file is not None or image.source == "GENERATED" for image in preview_images),
                "all preview images are packed or blend-resident generated images",
            )
            _require(
                material_preview.create_preview_material(
                    "/mt_empty.mtrl",
                    (0.8, 0.1, 0.8, 1.0),
                    preview_package,
                    "context-missing",
                ) is None,
                "a material without usable textures retains the importer placeholder fallback",
            )

            escaped_file = import_directory / "escape.rgba"
            escaped_file.write_bytes(normal_bytes)
            wrong_size_manifest = json.loads(json.dumps(preview_manifest))
            wrong_size_manifest["materials"][0]["textures"][0].update({
                "file": "normal.rgba",
                "width": 2,
                "height": 2,
            })
            wrong_size_manifest["materials"][0]["textures"] = [
                wrong_size_manifest["materials"][0]["textures"][0]
            ]
            manifest_path.write_text(json.dumps(wrong_size_manifest), encoding="utf-8")
            try:
                material_preview.load_preview_manifest(str(manifest_path), str(model_path))
            except material_preview.PreviewValidationError:
                print("[PASS] preview texture byte-count mismatches are rejected")
            else:
                raise AssertionError("a preview texture byte-count mismatch was accepted")

            traversal_manifest = json.loads(json.dumps(preview_manifest))
            traversal_manifest["materials"][0]["textures"][0].update({
                "file": "../escape.rgba",
                "width": 1,
                "height": 1,
            })
            traversal_manifest["materials"][0]["textures"] = [
                traversal_manifest["materials"][0]["textures"][0]
            ]
            manifest_path.write_text(json.dumps(traversal_manifest), encoding="utf-8")
            try:
                material_preview.load_preview_manifest(str(manifest_path), str(model_path))
            except material_preview.PreviewValidationError:
                print("[PASS] preview manifest path traversal is rejected")
            else:
                raise AssertionError("preview manifest path traversal was accepted")
            material_preview.discard_preview_data(preview_package)

        instant_props = bpy.context.scene.xiv_ie_instant_edit_props
        instant_props.last_status = "Full preview warning details for clipboard regression"
        _require(
            bpy.ops.xiv_ie.copy_status() == {"FINISHED"},
            "the complete import-status clipboard action executes",
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
            "redrawMode" not in redraw_payload,
            "export envelope leaves redraw targeting to the Dalamud plugin",
        )

        class ReceiptResponse:
            def __init__(self, status, payload):
                self.status = status
                self.payload = json.dumps(payload).encode("utf-8")

            def __enter__(self):
                return self

            def __exit__(self, exc_type, exc_value, traceback):
                return False

            def getcode(self):
                return self.status

            def read(self, _size=-1):
                return self.payload

        request_counts = {"export": 0, "status": 0}
        original_urlopen = plugin_http.urllib.request.urlopen

        def timed_out_then_receipt(request, timeout=0):
            if request.full_url.endswith("/export"):
                request_counts["export"] += 1
                raise TimeoutError("response was lost")
            if request.full_url.endswith("/export/status"):
                request_counts["status"] += 1
                return ReceiptResponse(200, {
                    "ok": True,
                    "code": "export_applied_with_warnings",
                    "message": "written",
                    "warnings": ["Player-owned redraw warning"],
                    "targetFilePath": r"D:\MovedMod\Files\models\original.mdl",
                })
            raise AssertionError(f"unexpected request: {request.full_url}")

        plugin_http.urllib.request.urlopen = timed_out_then_receipt
        try:
            receipt = ops._send_plugin_export(
                SimpleNamespace(
                    plugin_instance_id="plugin-instance",
                    context_id="context-id",
                    capability="capability",
                    callback_port=42428,
                ),
                redraw_payload,
            )
        finally:
            plugin_http.urllib.request.urlopen = original_urlopen
        _require(
            receipt["warnings"] == ["Player-owned redraw warning"] and
            receipt["targetFilePath"].endswith("original.mdl"),
            "warning receipts preserve the committed target and follow-up warnings",
        )
        _require(
            request_counts == {"export": 1, "status": 1},
            "a lost export response is recovered by status lookup without a second write",
        )

        _require(
            ops.normalise_variant_name("  alternate.mdl ") == "alternate",
            "variant names are normalized without duplicating the .mdl extension",
        )
        try:
            ops.validate_variant_name(
                "chara/equipment/e0001/model/c0101e0001_top.mdl",
                "c0101e0001_top",
            )
        except ValueError:
            pass
        else:
            raise AssertionError("variant name matched the originally imported model")
        print("[PASS] variant names cannot overwrite the imported model")
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

        instant_props.variant_targets.clear()
        variant_group = instant_props.variant_targets.add()
        variant_group.selection_id = "group:11111111-1111-1111-1111-111111111111"
        variant_group.kind = "GROUP"
        variant_group.group_name = "Group A"
        variant_group.expanded = True
        variant_option = instant_props.variant_targets.add()
        variant_option.selection_id = "option:11111111-1111-1111-1111-111111111111:22222222-2222-2222-2222-222222222222"
        variant_option.kind = "OPTION"
        variant_option.group_name = "Group A"
        variant_option.option_name = "Option a"
        variant_option.model_path = "Files/chara/equipment/e0001/model/alternate.mdl"
        instant_props.variant_target = variant_group.selection_id
        _require(
            ops.selected_variant_target(instant_props).group_name == "Group A",
            "a compatible Penumbra group can be selected from the cached tree",
        )
        _require(
            bpy.ops.xiv_ie.toggle_variant_target_group(selection_id=variant_group.selection_id) == {"FINISHED"} and
            not variant_group.expanded,
            "Penumbra target groups can be collapsed independently",
        )
        variant_group.expanded = True
        group_payload = ops.build_export_payload(
            SimpleNamespace(plugin_instance_id="plugin-instance", context_id="context-id", capability="capability"),
            "export-id", Path(tempfile.gettempdir()) / "variant-tree-test.mdl", 1, "0" * 64,
            instant_props, "new-option", "Group A", variant_group,
        )
        _require(
            group_payload["setupInPenumbra"] and group_payload["variantTarget"] == "group" and
            group_payload["variantTargetId"] == variant_group.selection_id,
            "group selection always configures an authenticated Penumbra export target",
        )
        instant_props.variant_targets_context_id = "context-id"
        instant_props.variant_name = "new-option"
        _require(
            ui._export_destination_display(display_ref, instant_props) == "Files/models/new-option.mdl",
            "the export display updates to a new variant sibling for a group target",
        )
        instant_props.variant_target = variant_option.selection_id
        _require(
            ui._export_destination_display(display_ref, instant_props) == variant_option.model_path,
            "the export display updates to the selected option model target",
        )
        option_payload = ops.build_export_payload(
            SimpleNamespace(plugin_instance_id="plugin-instance", context_id="context-id", capability="capability"),
            "export-id", Path(tempfile.gettempdir()) / "variant-tree-test.mdl", 1, "0" * 64,
            instant_props, None, None, variant_option,
        )
        _require(
            option_payload["setupInPenumbra"] and option_payload["variantTarget"] == "option" and
            "variantName" not in option_payload,
            "option selection overwrites its mapped model without creating a sibling name",
        )
        instant_props.variant_target = props.IN_PLACE_TARGET
        instant_props.variant_name = "stale-variant-name"
        in_place_payload = ops.build_export_payload(
            SimpleNamespace(plugin_instance_id="plugin-instance", context_id="context-id", capability="capability"),
            "export-id", Path(tempfile.gettempdir()) / "in-place-test.mdl", 1, "0" * 64,
            instant_props, "stale-variant-name", "stale-group-name", None, setup_in_penumbra=False,
        )
        _require(
            ui._export_destination_display(display_ref, instant_props) == "Files/models/original.mdl",
            "In-place selection displays the imported model path",
        )
        _require(
            not in_place_payload["setupInPenumbra"] and
            all(field not in in_place_payload for field in (
                "variantName", "variantGroupName", "variantTarget", "variantTargetId"
            )),
            "In-place export disables Penumbra setup and omits variant metadata",
        )
        _require(
            ops.SelectVariantTarget.description(
                bpy.context,
                SimpleNamespace(selection_id=props.IN_PLACE_TARGET),
            ) == "Overwrites the imported model at its original path without changing Penumbra option groups.",
            "In-place target hover text explains the original-path overwrite",
        )
        variant_group_id = variant_group.selection_id
        variant_option_id = variant_option.selection_id
        variant_option_path = variant_option.model_path
        props._export_destination_changed(instant_props, None)
        _require(
            instant_props.variant_target == "NEW_GROUP" and not instant_props.variant_targets and
            not instant_props.variant_targets_context_id,
            "changing Context clears the previous context's Penumbra target tree",
        )
        try:
            ops.export_destination_context(bpy.context)
        except context_module.ContextValidationError as error:
            _require("Select a Context" in str(error), "Quick Export requires an explicit Context")
        else:
            raise AssertionError("Quick Export accepted no Context")

        explicit_context_collection = context_module.create_collection(
            bpy.context.scene,
            {
                "context_id": "explicit-context",
                "schema": context_module.SCHEMA,
                "version": context_module.VERSION,
                "plugin_instance_id": "plugin-instance",
                "capability": "capability",
                "source_game_path": "chara/equipment/e0001/model/c0101e0001_top.mdl",
                "managed_destination": r"D:\Penumbra\SourceMod\Files\models",
                "target_file_path": r"D:\Penumbra\SourceMod\Files\models\original.mdl",
                "source_mod_directory": "SourceModDirectory",
                "source_mod_name": "Source Mod",
                "source_mod_root_path": r"D:\Penumbra\SourceMod",
                "target_relative_path": "Files/models/original.mdl",
                "import_id": "explicit-import",
                "callback_port": 42428,
                "import_file_name": "original.mdl",
            },
        )
        explicit_context_mesh_data = bpy.data.meshes.new("ExplicitContextMeshData")
        explicit_context_mesh_data.from_pydata(
            [(0, 0, 0), (1, 0, 0), (0, 1, 0)], [], [(0, 1, 2)]
        )
        explicit_context_mesh = bpy.data.objects.new("0.0 Explicit Context", explicit_context_mesh_data)
        explicit_context_collection.objects.link(explicit_context_mesh)

        _require(
            "ACTIVE" not in {
                item[0] for item in props._export_destination_items(None, bpy.context)
            },
            "Context choices never expose the active-context fallback",
        )
        refresh_contexts = []
        original_variant_request = ops._request_variant_targets
        mapped_option_id = "option:11111111-1111-1111-1111-333333333333"

        def fake_variant_request(ref):
            refresh_contexts.append(ref.context_id)
            return [{
                "id": variant_group_id,
                "name": "Group A",
                "options": [
                    {
                        "id": mapped_option_id,
                        "name": "Original Option",
                        "modelPath": "Files/models/original.mdl",
                    },
                    {
                        "id": variant_option_id,
                        "name": "new-option",
                        "modelPath": variant_option_path,
                    },
                ],
            }]

        ops._request_variant_targets = lambda _ref: []
        empty_target_count = ops.refresh_variant_targets(bpy.context)
        _require(
            empty_target_count == 0 and instant_props.variant_target == props.IN_PLACE_TARGET,
            "a context with no Penumbra groups defaults to the In-place target",
        )
        ops._request_variant_targets = fake_variant_request
        try:
            instant_props.export_destination = props.NO_EXPORT_CONTEXT
            preselected_ref = ops.export_destination_context(bpy.context)
            _require(
                refresh_contexts == ["explicit-context"] and
                preselected_ref.context_id == "explicit-context" and
                instant_props.export_destination == "explicit-context" and
                instant_props.variant_targets_context_id == "explicit-context",
                "a sole Context is preselected and automatically refreshes Penumbra targets",
            )
            _require(
                instant_props.variant_target == mapped_option_id,
                "the imported mod's mapped Penumbra option is automatically selected",
            )
            _require(
                ops.SelectVariantTarget.description(
                    bpy.context,
                    SimpleNamespace(selection_id="NEW_GROUP"),
                ) == "Creates a new Group on Export. Define group and option names below.",
                "New Group hover text explains both name fields",
            )
            _require(
                ops.SelectVariantTarget.description(
                    bpy.context,
                    SimpleNamespace(selection_id=variant_group_id),
                ) == "Creates a new Option in this group. Define the option name below.",
                "existing group hover text explains new option creation",
            )
            _require(
                ops.SelectVariantTarget.description(
                    bpy.context,
                    SimpleNamespace(selection_id=mapped_option_id),
                ) == "Overwrites this mod option within the group.",
                "existing option hover text explains overwrite behavior",
            )
            instant_props.variant_target = "NEW_GROUP"
            _require(
                instant_props.variant_target == "NEW_GROUP",
                "a context refresh can switch from the mapped option to New Group",
            )
            ops.refresh_variant_targets(
                bpy.context,
                select_group_name="Group A",
                select_option_name="new-option",
            )
            _require(
                instant_props.variant_target == variant_option_id,
                "post-export target refresh selects the newly created option",
            )
            selected_ref = ops.export_destination_context(bpy.context)
            _require(
                ops.export_objects_for_scope(selected_ref, "CURRENT_COLLECTION") == [explicit_context_mesh],
                "XIV Instant Edit Collection scope uses the explicitly selected Context",
            )
        finally:
            ops._request_variant_targets = original_variant_request

        class FakeModel:
            bones = ("root",)

        def fake_import(
            file_path,
            import_name,
            collection=None,
            context_metadata=None,
            **kwargs,
        ):
            _require(collection is not None, "XIV Instant Edit supplies a dedicated collection")
            _require(kwargs.get("require_collection") is True, "XIV Instant Edit requires collection containment")

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
                import_name="XIV Instant Edit Regression",
                schema="instant-edit.context",
                version=1,
                plugin_instance_id="plugin-instance",
                context_id="staging-isolation-context",
                import_id="staging-isolation-import",
                capability="capability",
                source_game_path="chara/equipment/e0001/model/c0101e0001_top.mdl",
                object_index=0,
                callback_port=42428,
                managed_destination=r"D:\Penumbra\SourceMod\models",
                target_file_path=r"D:\Penumbra\SourceMod\models\original.mdl",
                source_mod_directory="SourceModDirectory",
                source_mod_name="Source Mod",
                source_mod_root_path=r"D:\Penumbra\SourceMod",
                target_relative_path="Files/models/original.mdl",
                resource_manifest_version=0,
                resource_manifest_status="capture_failed",
            )
        finally:
            ops.ModelImport.from_file = original_import
            ops.XIVModel.from_file = original_model_from_file

        _require(result == {"FINISHED"}, "versioned XIV Instant Edit request completes")

        staging = next(
            collection
            for collection in bpy.data.collections
            if collection.get("instant_edit_collection_kind") == "instant_edit"
            and collection.get("context_id") == "staging-isolation-context"
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
            "existing scene armature is not tagged as an XIV Instant Edit object",
        )

        revocation = importlib.import_module(f"{package_name}.instant_edit.revocation")
        cache = importlib.import_module(f"{package_name}.instant_edit.cache")
        with tempfile.TemporaryDirectory() as revocation_temp:
            cache.configure_cache(revocation_temp, False)
            tombstone_context = {
                "context_id": "revocation-context",
                "import_id": "revocation-import",
                "capability": "revocation-capability",
                "callback_port": 42428,
            }
            _require(
                revocation.queue_context_revocations([tombstone_context]) == 1,
                "Clear Contexts can durably queue an authenticated revocation before metadata removal",
            )
            queued = revocation._load_locked()
            _require(
                queued and queued[0]["contextId"] == "revocation-context" and
                queued[0]["capability"] == "revocation-capability",
                "offline context revocations retain the authority needed for a later retry",
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
        if explicit_context_mesh is not None and any(
            _same_object(item, explicit_context_mesh) for item in bpy.data.objects
        ):
            bpy.data.objects.remove(explicit_context_mesh, do_unlink=True)
        if explicit_context_collection is not None and any(
            _same_object(item, explicit_context_collection) for item in bpy.data.collections
        ):
            bpy.data.collections.remove(explicit_context_collection, do_unlink=True)
        if existing_collection is not None and any(_same_object(item, existing_collection) for item in bpy.data.collections):
            bpy.data.collections.remove(existing_collection, do_unlink=True)
        if staging is not None and any(_same_object(item, staging) for item in bpy.data.collections):
            bpy.data.collections.remove(staging, do_unlink=True)
        if temp_path is not None:
            temp_path.unlink(missing_ok=True)


if __name__ == "__main__":
    run_staging_isolation_regression()
