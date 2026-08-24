"""Validated display-only FFXIV material previews for Instant Edit imports."""
# Modified for XIV Instant Edit, 2026. See MODIFICATIONS.md.

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
import json
import math
import shutil
import tempfile

import bpy
import numpy as np


SCHEMA = "instant-edit.material-preview"
VERSION = 1
MAX_MANIFEST_SIZE = 1024 * 1024
MAX_MATERIALS = 256
MAX_TEXTURES = 1024
MAX_DIMENSION = 8192
MAX_DECODED_BYTES = 512 * 1024 * 1024


class PreviewValidationError(ValueError):
    """Raised when a preview manifest cannot safely be consumed."""


@dataclass
class PreviewTexture:
    usage: str
    sampler_id: int
    sampler_flags: int
    game_path: str
    file_path: Path
    width: int
    height: int
    uv_set: int
    color_space: str


@dataclass
class PreviewMaterial:
    model_material: str
    game_path: str
    shader_package: str
    additional_data: str
    shader_keys: list
    shader_constants: list
    color_set: object
    textures: list[PreviewTexture]


@dataclass
class PreviewPackage:
    manifest_path: Path
    import_directory: Path
    materials: dict[str, PreviewMaterial]
    excluded_materials: set[str] = field(default_factory=set)
    warnings: list[str] = field(default_factory=list)
    created_materials: list = field(default_factory=list)
    created_images: list = field(default_factory=list)

    def material_for(self, model_material: str) -> PreviewMaterial | None:
        return self.materials.get(_material_key(model_material))

    def is_excluded(self, model_material: str) -> bool:
        return _material_key(model_material) in self.excluded_materials


def _material_key(value: str) -> str:
    return (value or "").replace("\\", "/").strip().casefold()


def _string(value, label: str, max_length: int = 4096) -> str:
    if not isinstance(value, str) or len(value) > max_length:
        raise PreviewValidationError(f"{label} must be a string of at most {max_length} characters")
    return value


def _integer(value, label: str, minimum: int = 0, maximum: int = 0xFFFFFFFF) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or not minimum <= value <= maximum:
        raise PreviewValidationError(f"{label} is outside the supported range")
    return value


def _bounded_list(value, label: str, maximum: int) -> list:
    if not isinstance(value, list) or len(value) > maximum:
        raise PreviewValidationError(f"{label} must be a list with at most {maximum} entries")
    return value


def _number(value, label: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise PreviewValidationError(f"{label} must be a finite number")
    return float(value)


def _contained_file(root: Path, relative: str) -> Path:
    relative_path = Path(_string(relative, "texture file"))
    if relative_path.is_absolute() or not relative_path.parts or any(part in {"", ".", ".."} for part in relative_path.parts):
        raise PreviewValidationError("texture file must be a safe relative path")
    resolved = (root / relative_path).resolve()
    try:
        resolved.relative_to(root)
    except ValueError as error:
        raise PreviewValidationError("texture file escapes the preview bundle") from error
    if resolved.suffix.casefold() != ".rgba" or not resolved.is_file():
        raise PreviewValidationError("texture file is missing or has an invalid extension")
    return resolved


def load_preview_manifest(manifest_path: str, model_file_path: str) -> PreviewPackage:
    """Validate one manifest and return only safe, bounded paths and values."""
    manifest = Path(manifest_path).resolve()
    model_file = Path(model_file_path).resolve()
    if not manifest.is_file() or manifest.stat().st_size > MAX_MANIFEST_SIZE:
        raise PreviewValidationError("preview manifest is missing or too large")

    preview_root = manifest.parent.resolve()
    import_directory = preview_root.parent.resolve()
    if manifest.name != "materials.json" or preview_root.name != "preview" or import_directory != model_file.parent:
        raise PreviewValidationError("preview manifest is not contained beside the imported model")

    try:
        data = json.loads(manifest.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise PreviewValidationError(f"preview manifest could not be read: {error}") from error
    if not isinstance(data, dict) or data.get("schema") != SCHEMA or data.get("version") != VERSION:
        raise PreviewValidationError("preview manifest has an unsupported schema or version")

    warnings = []
    for warning in _bounded_list(data.get("warnings", []), "warnings", 256):
        if isinstance(warning, str) and warning:
            warnings.append(warning[:512])

    excluded_materials = set()
    for excluded_material in _bounded_list(
        data.get("excludedMaterials", []), "excluded materials", MAX_MATERIALS
    ):
        excluded_material = _string(excluded_material, "excluded material")
        if not excluded_material:
            raise PreviewValidationError("excluded material must not be empty")
        key = _material_key(excluded_material)
        if key in excluded_materials:
            raise PreviewValidationError("preview manifest contains duplicate excluded materials")
        excluded_materials.add(key)

    materials: dict[str, PreviewMaterial] = {}
    texture_count = 0
    decoded_bytes = 0
    for raw_material in _bounded_list(data.get("materials", []), "materials", MAX_MATERIALS):
        if not isinstance(raw_material, dict):
            raise PreviewValidationError("material entries must be objects")
        model_material = _string(raw_material.get("modelMaterial", ""), "model material")
        if not model_material:
            raise PreviewValidationError("model material must not be empty")
        key = _material_key(model_material)
        if key in materials or key in excluded_materials:
            raise PreviewValidationError("preview manifest contains duplicate model materials")

        textures = []
        for raw_texture in _bounded_list(raw_material.get("textures", []), "textures", MAX_TEXTURES):
            texture_count += 1
            if texture_count > MAX_TEXTURES:
                raise PreviewValidationError("preview manifest contains too many textures")
            if not isinstance(raw_texture, dict):
                raise PreviewValidationError("texture entries must be objects")
            width = _integer(raw_texture.get("width"), "texture width", 1, MAX_DIMENSION)
            height = _integer(raw_texture.get("height"), "texture height", 1, MAX_DIMENSION)
            expected_size = width * height * 4
            decoded_bytes += expected_size
            if decoded_bytes > MAX_DECODED_BYTES:
                raise PreviewValidationError("preview texture data exceeds 512 MiB")
            file_path = _contained_file(preview_root, raw_texture.get("file", ""))
            if file_path.stat().st_size != expected_size:
                raise PreviewValidationError("texture byte count does not match its dimensions")
            color_space = _string(raw_texture.get("colorSpace", "Non-Color"), "texture color space", 32)
            if color_space not in {"sRGB", "Non-Color"}:
                raise PreviewValidationError("texture color space is invalid")
            usage = _string(raw_texture.get("usage", "other"), "texture usage", 32).casefold()
            if usage not in {"diffuse", "normal", "mask", "index", "specular", "occlusion", "flow", "decal", "other"}:
                usage = "other"
            textures.append(PreviewTexture(
                usage=usage,
                sampler_id=_integer(raw_texture.get("samplerId", 0), "sampler id"),
                sampler_flags=_integer(raw_texture.get("samplerFlags", 0), "sampler flags"),
                game_path=_string(raw_texture.get("gamePath", ""), "texture game path"),
                file_path=file_path,
                width=width,
                height=height,
                uv_set=_integer(raw_texture.get("uvSet", 0), "texture UV set", 0, 1),
                color_space=color_space,
            ))

        shader_keys = []
        for raw_key in _bounded_list(raw_material.get("shaderKeys", []), "shader keys", 256):
            if not isinstance(raw_key, dict):
                raise PreviewValidationError("shader key entries must be objects")
            shader_keys.append({
                "category": _integer(raw_key.get("category"), "shader key category"),
                "value": _integer(raw_key.get("value"), "shader key value"),
            })
        shader_constants = []
        for raw_constant in _bounded_list(raw_material.get("shaderConstants", []), "shader constants", 256):
            if not isinstance(raw_constant, dict):
                raise PreviewValidationError("shader constant entries must be objects")
            values = [
                _number(item, "shader constant value")
                for item in _bounded_list(raw_constant.get("values", []), "shader constant values", 256)
            ]
            shader_constants.append({
                "id": _integer(raw_constant.get("id"), "shader constant id"),
                "values": values,
            })
        color_set = raw_material.get("colorSet")
        if color_set is not None:
            if not isinstance(color_set, dict):
                raise PreviewValidationError("colorset metadata must be an object")
            color_width = _integer(color_set.get("width"), "colorset width", 1, 8)
            color_height = _integer(color_set.get("height"), "colorset height", 1, 32)
            values = _bounded_list(color_set.get("values", []), "colorset values", 1024)
            if len(values) != color_width * color_height * 4:
                raise PreviewValidationError("colorset values do not match its dimensions")
            color_set = {
                "width": color_width,
                "height": color_height,
                "values": [_number(item, "colorset value") for item in values],
            }

        additional_data = _string(raw_material.get("additionalData", ""), "additional material data", 256)
        if len(additional_data) % 2 or any(character not in "0123456789abcdefABCDEF" for character in additional_data):
            raise PreviewValidationError("additional material data must be hexadecimal")

        materials[key] = PreviewMaterial(
            model_material=model_material,
            game_path=_string(raw_material.get("gamePath", ""), "material game path"),
            shader_package=_string(raw_material.get("shaderPackage", ""), "shader package", 256),
            additional_data=additional_data,
            shader_keys=shader_keys,
            shader_constants=shader_constants,
            color_set=color_set,
            textures=textures,
        )

    return PreviewPackage(
        manifest_path=manifest,
        import_directory=import_directory,
        materials=materials,
        excluded_materials=excluded_materials,
        warnings=warnings,
    )


def _socket(node, *names):
    for name in names:
        socket = node.inputs.get(name)
        if socket is not None:
            return socket
    return None


def _read_texture_pixels(texture: PreviewTexture):
    expected_size = texture.width * texture.height * 4
    if texture.file_path.stat().st_size != expected_size:
        raise PreviewValidationError("texture byte count changed after manifest validation")
    pixels = np.fromfile(texture.file_path, dtype=np.uint8, count=expected_size)
    if pixels.size != expected_size:
        raise PreviewValidationError("texture data ended before its declared byte count")
    return pixels.reshape((texture.height, texture.width, 4)).astype(np.float32) / 255.0


def _create_pixels_image(name: str, pixels, color_space: str, package: PreviewPackage):
    height, width, channels = pixels.shape
    if channels != 4 or width < 1 or height < 1:
        raise PreviewValidationError("generated preview pixels have invalid dimensions")
    image = bpy.data.images.new(
        name=name,
        width=width,
        height=height,
        alpha=True,
        float_buffer=False,
    )
    package.created_images.append(image)
    image.colorspace_settings.name = color_space
    image.pixels.foreach_set(np.asarray(pixels[::-1], dtype=np.float32).ravel())
    image.update()
    try:
        image.pack()
    except RuntimeError:
        # Generated images are already stored in the blend; this marker lets the
        # regression distinguish them from an unresolved external image.
        image["instant_edit_generated_image"] = True
    return image


def _create_image(texture: PreviewTexture, package: PreviewPackage, label: str, pixels=None):
    image = _create_pixels_image(
        f"{label} {texture.usage.title()}",
        _read_texture_pixels(texture) if pixels is None else pixels,
        texture.color_space,
        package,
    )
    image["xiv_texture_game_path"] = texture.game_path
    # Blender integer ID properties are signed 32-bit values. FFXIV sampler
    # IDs are unsigned hashes, so values such as 0x8A4E82B6 must be retained
    # as hexadecimal strings rather than overflowing Blender's C API.
    image["xiv_sampler_id"] = f"0x{texture.sampler_id:08X}"
    image["instant_edit_preview_usage"] = texture.usage
    return image


def _first_texture(preview: PreviewMaterial, usage: str) -> PreviewTexture | None:
    return next((texture for texture in preview.textures if texture.usage == usage), None)


def _resize_nearest(pixels, width: int, height: int):
    if pixels.shape[1] == width and pixels.shape[0] == height:
        return pixels
    source_height, source_width = pixels.shape[:2]
    rows = np.minimum((np.arange(height) * source_height / height).astype(np.intp), source_height - 1)
    columns = np.minimum((np.arange(width) * source_width / width).astype(np.intp), source_width - 1)
    return pixels[rows[:, None], columns[None, :]]


def _can_build_character_base(preview: PreviewMaterial) -> bool:
    return (
        preview.shader_package.casefold() in {"character.shpk", "characterlegacy.shpk", "characterglass.shpk"}
        and preview.color_set is not None
        and _first_texture(preview, "index") is not None
    )


def _build_character_base(preview: PreviewMaterial):
    """Bake the practical character colorset/index path used by ordinary gear."""
    index_texture = _first_texture(preview, "index")
    if index_texture is None or preview.color_set is None:
        raise PreviewValidationError("character preview requires an index texture and colorset")

    row_stride = preview.color_set["width"] * 4
    table_values = np.asarray(preview.color_set["values"], dtype=np.float32)
    if row_stride not in {16, 32} or table_values.size < row_stride * 2 or table_values.size % row_stride:
        raise PreviewValidationError("character colorset does not contain complete rows")
    table = table_values.reshape((-1, row_stride))
    index_pixels = _read_texture_pixels(index_texture)
    height, width = index_pixels.shape[:2]

    # character.shpk selects a pair of colorset rows through index R and
    # interpolates that pair through inverted index G.
    table_pairs = np.rint((index_pixels[:, :, 0] * 255.0) / 17.0).astype(np.intp)
    previous_rows = np.clip(table_pairs * 2, 0, table.shape[0] - 1)
    next_rows = np.minimum(previous_rows + 1, table.shape[0] - 1)
    blend = (1.0 - index_pixels[:, :, 1])[:, :, None]
    linear_diffuse = table[previous_rows, :3] * (1.0 - blend) + table[next_rows, :3] * blend
    base_pixels = np.ones((height, width, 4), dtype=np.float32)
    base_pixels[:, :, :3] = np.clip(np.sqrt(np.maximum(linear_diffuse, 0.0)), 0.0, 1.0)

    normal_texture = _first_texture(preview, "normal")
    if normal_texture is not None:
        normal_pixels = _resize_nearest(_read_texture_pixels(normal_texture), width, height)
        base_pixels[:, :, 3] *= normal_pixels[:, :, 2]

    diffuse_texture = _first_texture(preview, "diffuse")
    if diffuse_texture is not None:
        diffuse_pixels = _resize_nearest(_read_texture_pixels(diffuse_texture), width, height)
        base_pixels *= diffuse_pixels

    mask_texture = _first_texture(preview, "mask")
    if mask_texture is not None:
        mask_pixels = _resize_nearest(_read_texture_pixels(mask_texture), width, height)
        base_pixels[:, :, :3] *= mask_pixels[:, :, 2:3]

    return np.clip(base_pixels, 0.0, 1.0), index_texture


def create_preview_material(
    model_material: str,
    fallback_color,
    package: PreviewPackage,
    context_key: str,
):
    """Create one import-local Principled material, falling back per texture."""
    if package.is_excluded(model_material):
        return None
    preview = package.material_for(model_material)
    if preview is None:
        package.warnings.append(f"No preview data for {Path(model_material).name or model_material}")
        return None
    can_build_character_base = _can_build_character_base(preview)
    if not can_build_character_base and not any(texture.usage in {"diffuse", "normal", "specular"} for texture in preview.textures):
        package.warnings.append(f"No usable preview textures for {Path(model_material).name or model_material}")
        return None

    label = Path(model_material.replace("\\", "/")).name or "Material"
    suffix = (context_key or "preview")[:8]
    created_image_start = len(package.created_images)
    material = bpy.data.materials.new(f"{label} [{suffix}]")
    package.created_materials.append(material)
    material.use_nodes = True
    material.surface_render_method = "DITHERED"
    material.use_backface_culling = True
    material["xiv_mtrl_game_path"] = preview.game_path
    material["xiv_shader_package"] = preview.shader_package
    material["xiv_material_additional_data"] = preview.additional_data
    material["xiv_shader_keys"] = json.dumps(preview.shader_keys, separators=(",", ":"))
    material["xiv_shader_constants"] = json.dumps(preview.shader_constants, separators=(",", ":"))
    if preview.color_set is not None:
        material["xiv_colorset"] = json.dumps(preview.color_set, separators=(",", ":"))

    nodes = material.node_tree.nodes
    links = material.node_tree.links
    nodes.clear()
    principled = nodes.new(type="ShaderNodeBsdfPrincipled")
    principled.location = (300, 0)
    output = nodes.new(type="ShaderNodeOutputMaterial")
    output.location = (620, 0)
    links.new(principled.outputs["BSDF"], output.inputs["Surface"])
    base_color = _socket(principled, "Base Color")
    if base_color is not None:
        base_color.default_value = fallback_color
    roughness = _socket(principled, "Roughness")
    if roughness is not None:
        roughness.default_value = 0.6
    metallic = _socket(principled, "Metallic")
    if metallic is not None:
        metallic.default_value = 0.0

    role_y = {"diffuse": 300, "normal": 20, "specular": -260, "mask": -500, "index": -720, "other": -940}
    connected = set()
    character_base_built = False
    if can_build_character_base:
        try:
            character_pixels, index_texture = _build_character_base(preview)
            character_image = _create_pixels_image(
                f"{label} [{suffix}] Colorset Base Color",
                character_pixels,
                "sRGB",
                package,
            )
            character_image["xiv_texture_game_path"] = index_texture.game_path
            character_image["xiv_sampler_id"] = f"0x{index_texture.sampler_id:08X}"
            character_image["instant_edit_preview_usage"] = "colorset-base"
            uv = nodes.new(type="ShaderNodeUVMap")
            uv.uv_map = f"uv{index_texture.uv_set}"
            uv.location = (-900, 300)
            image_node = nodes.new(type="ShaderNodeTexImage")
            image_node.image = character_image
            image_node.label = "Colorset + Index Base Color"
            image_node.location = (-620, 300)
            links.new(uv.outputs["UV"], image_node.inputs["Vector"])
            if base_color is not None:
                links.new(image_node.outputs["Color"], base_color)
            alpha = _socket(principled, "Alpha")
            if alpha is not None:
                links.new(image_node.outputs["Alpha"], alpha)
            connected.add("diffuse")
            character_base_built = True
        except Exception as error:
            package.warnings.append(f"Could not build colorset preview for {label}: {error}")

    for index, texture in enumerate(preview.textures):
        if character_base_built and texture.usage in {"diffuse", "index", "mask"}:
            continue
        try:
            pixels = None
            if character_base_built and texture.usage == "normal":
                pixels = _read_texture_pixels(texture).copy()
                pixels[:, :, 2:] = 1.0
            image = _create_image(texture, package, f"{label} [{suffix}]", pixels=pixels)
            uv = nodes.new(type="ShaderNodeUVMap")
            uv.uv_map = f"uv{texture.uv_set}"
            uv.location = (-900, role_y.get(texture.usage, -940) - index * 35)
            image_node = nodes.new(type="ShaderNodeTexImage")
            image_node.image = image
            image_node.label = f"{texture.usage.title()} — {Path(texture.game_path).name}"
            image_node.location = (-620, uv.location.y)
            links.new(uv.outputs["UV"], image_node.inputs["Vector"])

            if texture.usage == "diffuse" and "diffuse" not in connected:
                if base_color is not None:
                    links.new(image_node.outputs["Color"], base_color)
                alpha = _socket(principled, "Alpha")
                if alpha is not None:
                    links.new(image_node.outputs["Alpha"], alpha)
                connected.add("diffuse")
            elif texture.usage == "normal" and "normal" not in connected:
                normal_map = nodes.new(type="ShaderNodeNormalMap")
                normal_map.location = (-40, image_node.location.y)
                links.new(image_node.outputs["Color"], normal_map.inputs["Color"])
                normal = _socket(principled, "Normal")
                if normal is not None:
                    links.new(normal_map.outputs["Normal"], normal)
                connected.add("normal")
            elif texture.usage == "specular" and "specular" not in connected:
                specular = _socket(principled, "Specular IOR Level", "Specular")
                if specular is not None:
                    links.new(image_node.outputs["Color"], specular)
                connected.add("specular")
        except Exception as error:
            package.warnings.append(f"Could not create {texture.usage} preview for {label}: {error}")

    if not connected:
        package.warnings.append(f"No usable preview could be built for {label}")
        package.created_materials.remove(material)
        bpy.data.materials.remove(material)
        for image in reversed(package.created_images[created_image_start:]):
            if image.name in bpy.data.images and image.users == 0:
                bpy.data.images.remove(image)
        del package.created_images[created_image_start:]
        return None

    return material


def discard_preview_data(package: PreviewPackage | None) -> None:
    """Remove datablocks created by a failed staged import."""
    if package is None:
        return
    for material in reversed(package.created_materials):
        if material.name in bpy.data.materials and material.users == 0:
            bpy.data.materials.remove(material)
    for image in reversed(package.created_images):
        if image.name in bpy.data.images and image.users == 0:
            bpy.data.images.remove(image)


def cleanup_preview_bundle(package: PreviewPackage | None) -> None:
    """Delete only a nonce directory created below the system InstantEdit temp root."""
    if package is None:
        return
    import_directory = package.import_directory.resolve()
    expected_root = (Path(tempfile.gettempdir()) / "InstantEdit").resolve()
    if import_directory.parent != expected_root or len(import_directory.name) != 32:
        return
    try:
        int(import_directory.name, 16)
    except ValueError:
        return
    shutil.rmtree(import_directory, ignore_errors=True)
