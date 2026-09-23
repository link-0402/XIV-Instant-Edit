"""Validated display-only FFXIV material previews for XIV Instant Edit imports."""
# Modified for XIV Instant Edit, 2026.

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
import json
import shutil
import tempfile

import bpy
import numpy as np

from .validation import ValidationError, validate_bounded_list, validate_integer, validate_number, validate_string


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
    # MTRL shader-header flags; None when the plugin predates sending them.
    material_flags: int | None = None


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
    try:
        return validate_string(value, f"{label} must be a string of at most {max_length} characters", max_length=max_length)
    except ValidationError as error:
        raise PreviewValidationError(str(error)) from error


def _integer(value, label: str, minimum: int = 0, maximum: int = 0xFFFFFFFF) -> int:
    try:
        return validate_integer(value, f"{label} is outside the supported range", minimum=minimum, maximum=maximum)
    except ValidationError as error:
        raise PreviewValidationError(str(error)) from error


def _bounded_list(value, label: str, maximum: int) -> list:
    try:
        return validate_bounded_list(value, f"{label} must be a list with at most {maximum} entries", maximum=maximum)
    except ValidationError as error:
        raise PreviewValidationError(str(error)) from error


def _number(value, label: str) -> float:
    try:
        return validate_number(value, f"{label} must be a finite number")
    except ValidationError as error:
        raise PreviewValidationError(str(error)) from error


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
        material_flags = raw_material.get("materialFlags")
        if material_flags is not None:
            material_flags = _integer(material_flags, "material flags")

        materials[key] = PreviewMaterial(
            model_material=model_material,
            game_path=_string(raw_material.get("gamePath", ""), "material game path"),
            shader_package=_string(raw_material.get("shaderPackage", ""), "shader package", 256),
            additional_data=additional_data,
            shader_keys=shader_keys,
            shader_constants=shader_constants,
            color_set=color_set,
            textures=textures,
            material_flags=material_flags,
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


def _socket_by_id(sockets, identifier: str):
    # Mix nodes expose several same-named sockets (Float/Vector/Color).
    return next(socket for socket in sockets if socket.identifier == identifier)


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
    # FFXIV textures pack unrelated data into alpha (opacity, colorset index,
    # hair highlights). Straight alpha would black out RGB wherever it is 0.
    image.alpha_mode = "CHANNEL_PACKED"
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


def _linear_to_srgb(values):
    values = np.clip(values, 0.0, 1.0)
    return np.where(values <= 0.0031308, values * 12.92, 1.055 * np.power(values, 1.0 / 2.4) - 0.055)


# MTRL shader-header flags (Meddle ShaderFlags, TexTools EMaterialFlags1).
_FLAG_HIDE_BACKFACES = 0x01
_FLAG_TRANSLUCENT = 0x10

# Shader constant and key ids shared by the character shader family.
# g_AlphaThreshold also matches xivModdingFramework's ConstantId 699138595.
_ALPHA_THRESHOLD_CONSTANT_ID = 0x29AC0223
_EMISSIVE_COLOR_CONSTANT_ID = 0x38A64362
_TEXTURE_MODE_KEY = 0xB616DC5A
_TEXTURE_MODE_COMPATIBILITY = 0x600EF9DF

# Shader packages with character.shpk's colorset, index and normal layout;
# MeddleTools maps all of them onto its character node group.
_CHARACTER_SHADERS = frozenset({
    "character.shpk",
    "characterlegacy.shpk",
    "characterglass.shpk",
    "characterinc.shpk",
    "characterscroll.shpk",
    "characterstockings.shpk",
    "charactertransparency.shpk",
})


def _shader_constant_values(preview: PreviewMaterial, constant_id: int) -> list | None:
    for constant in preview.shader_constants:
        if constant.get("id") == constant_id:
            return constant.get("values")
    return None


def _shader_key_value(preview: PreviewMaterial, key_id: int) -> int | None:
    for key in preview.shader_keys:
        if key.get("category") == key_id:
            return key.get("value")
    return None


def _alpha_threshold(preview: PreviewMaterial) -> float | None:
    values = _shader_constant_values(preview, _ALPHA_THRESHOLD_CONSTANT_ID)
    return float(values[0]) if values else None


def _is_translucent(preview: PreviewMaterial) -> bool | None:
    if preview.material_flags is None:
        return None
    return bool(preview.material_flags & _FLAG_TRANSLUCENT)


def _opacity_source(preview: PreviewMaterial) -> tuple[str, int] | None:
    """Return the texture usage and channel that hold the material's opacity."""
    shader = preview.shader_package.casefold()
    if shader in {"characterstockings.shpk", "iris.shpk"}:
        # Stockings reuse normal B for sheerness; eyes always draw opaque.
        return None
    if shader in _CHARACTER_SHADERS:
        return "normal", 2
    if shader in {"hair.shpk", "charactertattoo.shpk"}:
        return "normal", 3
    return "diffuse", 3


def _can_build_character_base(preview: PreviewMaterial) -> bool:
    return (
        preview.shader_package.casefold() in _CHARACTER_SHADERS
        and preview.color_set is not None
        and _first_texture(preview, "index") is not None
    )


@dataclass
class _ColorsetMaps:
    """Colorset values evaluated at every texel of the index texture."""

    base_color: object
    surface: object
    legacy_gloss: bool
    emissive: object = None
    emission_strength: float = 1.0


def _bake_colorset(preview: PreviewMaterial, index_texture: PreviewTexture) -> _ColorsetMaps:
    """Look up the colorset rows each index texel selects.

    Only index-dependent values are baked, at the index texture's own size.
    Diffuse, mask and normal textures keep their native resolution and are
    combined in the node tree: baking them at the index size decimated a 2K
    suit to the 32x32 placeholder index many modded materials ship.
    """
    row_stride = preview.color_set["width"] * 4
    values = np.asarray(preview.color_set["values"], dtype=np.float32)
    if row_stride not in {16, 32} or values.size < row_stride * 2 or values.size % row_stride:
        raise PreviewValidationError("character colorset does not contain complete rows")
    table = values.reshape((-1, row_stride))
    index_pixels = _read_texture_pixels(index_texture)
    height, width = index_pixels.shape[:2]

    # character.shpk selects a pair of colorset rows through index R and
    # interpolates that pair through inverted index G.
    table_pairs = np.rint((index_pixels[:, :, 0] * 255.0) / 17.0).astype(np.intp)
    previous_rows = np.clip(table_pairs * 2, 0, table.shape[0] - 1)
    next_rows = np.minimum(previous_rows + 1, table.shape[0] - 1)
    blend = (1.0 - index_pixels[:, :, 1])[:, :, None]
    del index_pixels, table_pairs

    def interpolate(*columns):
        rows = table[:, list(columns)]
        return rows[previous_rows] * (1.0 - blend) + rows[next_rows] * blend

    base_color = np.ones((height, width, 4), dtype=np.float32)
    base_color[:, :, :3] = _linear_to_srgb(interpolate(0, 1, 2))

    # Surface channels: R roughness source, G metalness, B specular level.
    surface = np.ones((height, width, 4), dtype=np.float32)
    legacy_gloss = row_stride == 16 or preview.shader_package.casefold() == "characterlegacy.shpk"
    if legacy_gloss:
        # characterlegacy.shpk has no roughness column; its shader derives one
        # from gloss as exp2(-gloss / 15). Dawntrail-width legacy tables store
        # gloss and specular strength in columns 3 and 7, 16-wide ones swapped.
        gloss, specular = (3, 7) if row_stride == 32 else (7, 3)
        surface[:, :, 0] = np.exp2(-np.clip(interpolate(gloss)[:, :, 0], 0.0, 240.0) / 15.0)
        surface[:, :, 1] = 0.0
        surface[:, :, 2] = np.clip(0.5 * interpolate(specular)[:, :, 0], 0.0, 1.0)
    else:
        surface[:, :, 0] = np.clip(interpolate(16)[:, :, 0], 0.0, 1.0)
        surface[:, :, 1] = np.clip(interpolate(18)[:, :, 0], 0.0, 1.0)
        surface[:, :, 2] = 0.5
    maps = _ColorsetMaps(base_color, surface, legacy_gloss)

    emissive = np.maximum(interpolate(8, 9, 10), 0.0)
    emissive_color = _shader_constant_values(preview, _EMISSIVE_COLOR_CONSTANT_ID)
    if emissive_color and len(emissive_color) >= 3:
        emissive *= np.square(np.maximum(np.asarray(emissive_color[:3], dtype=np.float32), 0.0))
    peak = float(emissive.max())
    if peak > 1.0 / 255.0:
        # Colorset emission is HDR: keep the hue in the image and the
        # brightness in the Principled emission strength.
        maps.emission_strength = max(peak, 1.0)
        maps.emissive = np.ones((height, width, 4), dtype=np.float32)
        maps.emissive[:, :, :3] = _linear_to_srgb(emissive / maps.emission_strength)
    return maps


def _normal_preview_pixels(texture: PreviewTexture, opacity_channel: int | None):
    """Rebuild tangent-space Z from a normal map's RG channels.

    FFXIV repurposes normal B and A (opacity, colorset index, hair highlight,
    skin influence), so neither holds a usable Z. When the material's opacity
    lives in this texture, it moves to alpha for the shared alpha wiring.
    """
    pixels = _read_texture_pixels(texture)
    opacity = pixels[:, :, opacity_channel].copy() if opacity_channel is not None else None
    x = pixels[:, :, 0] * 2.0 - 1.0
    y = pixels[:, :, 1] * 2.0 - 1.0
    pixels[:, :, 2] = np.sqrt(np.clip(1.0 - x * x - y * y, 0.0, 1.0)) * 0.5 + 0.5
    pixels[:, :, 3] = 1.0 if opacity is None else opacity
    return pixels


# xivModdingFramework's own un-customized preview defaults (ModelTexture.cs,
# CustomModelColors.HairColor / HairHighlightColor) — the closest thing to a
# "correct" placeholder since the real color is a per-character dye value
# that hair.shpk materials don't carry in the .mtrl itself.
_HAIR_COLOR_DEFAULT = np.array([110, 77, 35], dtype=np.float32) / 255.0
_HAIR_HIGHLIGHT_DEFAULT = np.array([91, 110, 129], dtype=np.float32) / 255.0


def _can_build_hair_preview(preview: PreviewMaterial) -> bool:
    return (
        preview.shader_package.casefold() == "hair.shpk"
        and not any(texture.usage == "diffuse" for texture in preview.textures)
        and _first_texture(preview, "mask") is not None
        and _first_texture(preview, "normal") is not None
    )


def _build_hair_preview(preview: PreviewMaterial):
    """Approximate hair.shpk-style materials (eyebrows, eyelashes, hair cards)
    that ship only a mask + normal texture and no diffuse.

    Channel layout confirmed against xivModdingFramework's ModelTexture.GetShaderMapper
    (EShaderPack.Hair): normal.b is highlight-color influence, normal.a is
    opacity, mask.r/g are specular/roughness, mask.a is a diffuse/occlusion
    multiplier. Opacity is wired from the normal image like every other
    shader, which applies the material's threshold and translucency flag.
    """
    mask_texture = _first_texture(preview, "mask")
    normal_texture = _first_texture(preview, "normal")
    if mask_texture is None or normal_texture is None:
        raise PreviewValidationError("hair preview requires a mask texture and a normal texture")

    normal_pixels = _read_texture_pixels(normal_texture)
    height, width = normal_pixels.shape[:2]
    mask_pixels = _resize_nearest(_read_texture_pixels(mask_texture), width, height)

    highlight_influence = normal_pixels[:, :, 2:3]
    base_color = _HAIR_COLOR_DEFAULT * (1.0 - highlight_influence) + _HAIR_HIGHLIGHT_DEFAULT * highlight_influence
    occlusion = mask_pixels[:, :, 3:4] ** 2

    base_pixels = np.ones((height, width, 4), dtype=np.float32)
    base_pixels[:, :, :3] = base_color * occlusion
    return np.clip(base_pixels, 0.0, 1.0), normal_texture, mask_texture


class _PreviewNodes:
    """Creates one preview material's texture nodes left of its Principled BSDF."""

    def __init__(self, material, package: PreviewPackage, image_label: str):
        self.nodes = material.node_tree.nodes
        self.links = material.node_tree.links
        self.package = package
        self.image_label = image_label
        self._rows = 0

    def image(self, image, uv_set: int, label: str):
        y = 440 - self._rows * 300
        self._rows += 1
        uv = self.nodes.new(type="ShaderNodeUVMap")
        uv.uv_map = f"uv{uv_set}"
        uv.location = (-1320, y)
        node = self.nodes.new(type="ShaderNodeTexImage")
        node.image = image
        node.label = label
        node.location = (-1060, y)
        self.links.new(uv.outputs["UV"], node.inputs["Vector"])
        return node

    def texture(self, texture: PreviewTexture, pixels=None):
        image = _create_image(texture, self.package, self.image_label, pixels=pixels)
        return self.image(image, texture.uv_set, f"{texture.usage.title()} — {Path(texture.game_path).name}")

    def baked(self, pixels, color_space: str, name: str, usage: str, source: PreviewTexture):
        image = _create_pixels_image(f"{self.image_label} {name}", pixels, color_space, self.package)
        image["xiv_texture_game_path"] = source.game_path
        image["xiv_sampler_id"] = f"0x{source.sampler_id:08X}"
        image["instant_edit_preview_usage"] = usage
        return self.image(image, source.uv_set, name)

    def separate(self, image_node):
        node = self.nodes.new(type="ShaderNodeSeparateColor")
        node.location = (image_node.location.x + 300, image_node.location.y)
        self.links.new(image_node.outputs["Color"], node.inputs["Color"])
        return node

    def math(self, operation: str, first, second, location, clamp: bool = False):
        node = self.nodes.new(type="ShaderNodeMath")
        node.operation = operation
        node.use_clamp = clamp
        node.location = location
        for socket, value in zip(node.inputs, (first, second)):
            if isinstance(value, (int, float)):
                socket.default_value = value
            else:
                self.links.new(value, socket)
        return node.outputs["Value"]

    def multiply(self, color, factor, location, label: str):
        node = self.nodes.new(type="ShaderNodeMix")
        node.data_type = "RGBA"
        node.blend_type = "MULTIPLY"
        node.label = label
        node.location = location
        _socket_by_id(node.inputs, "Factor_Float").default_value = 1.0
        self.links.new(color, _socket_by_id(node.inputs, "A_Color"))
        self.links.new(factor, _socket_by_id(node.inputs, "B_Color"))
        return _socket_by_id(node.outputs, "Result_Color")


def _texture_node(tree: _PreviewNodes, texture: PreviewTexture, label: str, warnings: list, pixels=None):
    try:
        return tree.texture(texture, pixels=pixels() if callable(pixels) else pixels)
    except Exception as error:
        warnings.append(f"Could not create {texture.usage} preview for {label}: {error}")
        return None


def _link(tree: _PreviewNodes, output, principled, *names) -> None:
    socket = _socket(principled, *names)
    if socket is not None:
        tree.links.new(output, socket)


def _wire_character(tree, preview, principled, colorset: _ColorsetMaps, label: str, handled: set) -> None:
    """Combine colorset, diffuse and mask the way character.shpk does.

    Channel semantics follow MeddleTools' character node group: diffuse is
    only sampled in GetValuesCompatibility mode, colorset roughness scales
    with mask G, mask R scales specular, and metalness is the colorset's.
    """
    warnings = tree.package.warnings
    index_texture = _first_texture(preview, "index")
    handled.add(id(index_texture))
    base_node = tree.baked(colorset.base_color, "sRGB", "Colorset Base Color", "colorset-base", index_texture)
    base_node.label = "Colorset + Index Base Color"
    color = base_node.outputs["Color"]

    diffuse_texture = _first_texture(preview, "diffuse")
    if (
        diffuse_texture is not None
        and _shader_key_value(preview, _TEXTURE_MODE_KEY) == _TEXTURE_MODE_COMPATIBILITY
    ):
        handled.add(id(diffuse_texture))
        diffuse_node = _texture_node(tree, diffuse_texture, label, warnings)
        if diffuse_node is not None:
            color = tree.multiply(color, diffuse_node.outputs["Color"], (-480, 420), "Diffuse × Colorset")

    mask = None
    mask_texture = _first_texture(preview, "mask")
    if mask_texture is not None:
        handled.add(id(mask_texture))
        mask_node = _texture_node(tree, mask_texture, label, warnings)
        if mask_node is not None:
            mask = tree.separate(mask_node)
            # Mask B is ambient occlusion. xivModdingFramework reads masks as
            # sRGB, so it darkens the linear color by the channel's square.
            occlusion = tree.math("MULTIPLY", mask.outputs["Blue"], mask.outputs["Blue"], (-480, 160))
            color = tree.multiply(color, occlusion, (-260, 420), "Mask Occlusion")
    _link(tree, color, principled, "Base Color")

    surface_node = tree.baked(
        colorset.surface, "Non-Color", "Colorset Roughness Metalness Specular", "colorset-surface", index_texture
    )
    surface = tree.separate(surface_node)
    roughness = surface.outputs["Red"]
    specular = surface.outputs["Blue"]
    if mask is not None:
        # Modern colorset roughness scales by mask G (MeddleTools). Legacy gloss
        # scales by mask G instead, which the baked exp2(-gloss / 15) takes as
        # a power: exp2(-gloss * G / 15).
        roughness = tree.math(
            "POWER" if colorset.legacy_gloss else "MULTIPLY",
            roughness, mask.outputs["Green"], (-260, surface_node.location.y), clamp=True,
        )
        specular = tree.math(
            "MULTIPLY", specular, mask.outputs["Red"], (-260, surface_node.location.y - 180), clamp=True
        )
    _link(tree, roughness, principled, "Roughness")
    _link(tree, surface.outputs["Green"], principled, "Metallic")
    _link(tree, specular, principled, "Specular IOR Level", "Specular")

    if colorset.emissive is not None:
        emissive_node = tree.baked(colorset.emissive, "sRGB", "Colorset Emissive", "colorset-emissive", index_texture)
        _link(tree, emissive_node.outputs["Color"], principled, "Emission Color", "Emission")
        strength = _socket(principled, "Emission Strength")
        if strength is not None:
            strength.default_value = colorset.emission_strength


def _wire_hair(tree, principled, hair_pixels, normal_texture, mask_texture, label: str, handled: set) -> None:
    hair_node = tree.baked(hair_pixels, "sRGB", "Hair Base Color", "hair-base", normal_texture)
    hair_node.label = "Hair Base Color (approximate)"
    _link(tree, hair_node.outputs["Color"], principled, "Base Color")
    handled.add(id(mask_texture))
    mask_node = _texture_node(tree, mask_texture, label, tree.package.warnings)
    if mask_node is not None:
        mask = tree.separate(mask_node)
        _link(tree, mask.outputs["Red"], principled, "Specular IOR Level", "Specular")
        _link(tree, mask.outputs["Green"], principled, "Roughness")


def _link_opacity(tree: _PreviewNodes, principled, alpha, preview: PreviewMaterial) -> bool:
    """Shape sampled opacity like the game's alpha test or alpha blend.

    Opaque materials discard texels below g_AlphaThreshold (default 0).
    Translucent ones blend with opacity divided by the threshold, as TexTools
    and MeddleTools both render it; unknown flags keep that smooth alpha.
    """
    alpha_input = _socket(principled, "Alpha")
    if alpha_input is None or alpha is None:
        return False
    threshold = _alpha_threshold(preview)
    if _is_translucent(preview) is False:
        if threshold is None or threshold <= 0.0:
            return False
        alpha = tree.math("GREATER_THAN", alpha, threshold - 1e-6, (-40, -420))
    elif threshold is not None and threshold > 0.0 and threshold != 1.0:
        alpha = tree.math("DIVIDE", alpha, threshold, (-40, -420), clamp=True)
    tree.links.new(alpha, alpha_input)
    return True


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
    can_build_hair_preview = not can_build_character_base and _can_build_hair_preview(preview)
    if (
        not can_build_character_base
        and not can_build_hair_preview
        and not any(texture.usage in {"diffuse", "normal", "specular"} for texture in preview.textures)
    ):
        package.warnings.append(f"No usable preview textures for {Path(model_material).name or model_material}")
        return None

    label = Path(model_material.replace("\\", "/")).name or "Material"
    suffix = (context_key or "preview")[:8]
    created_image_start = len(package.created_images)
    material = bpy.data.materials.new(f"{label} [{suffix}]")
    package.created_materials.append(material)
    material.use_nodes = True
    material.surface_render_method = "DITHERED"
    material["xiv_mtrl_game_path"] = preview.game_path
    material["xiv_shader_package"] = preview.shader_package
    material["xiv_material_additional_data"] = preview.additional_data
    material["xiv_shader_keys"] = json.dumps(preview.shader_keys, separators=(",", ":"))
    material["xiv_shader_constants"] = json.dumps(preview.shader_constants, separators=(",", ":"))
    if preview.color_set is not None:
        material["xiv_colorset"] = json.dumps(preview.color_set, separators=(",", ":"))
    if preview.material_flags is not None:
        material["xiv_material_flags"] = f"0x{preview.material_flags:08X}"

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

    tree = _PreviewNodes(material, package, f"{label} [{suffix}]")
    handled: set[int] = set()
    colorset = None
    if can_build_character_base:
        try:
            colorset = _bake_colorset(preview, _first_texture(preview, "index"))
            _wire_character(tree, preview, principled, colorset, label, handled)
        except Exception as error:
            colorset = None
            package.warnings.append(f"Could not build colorset preview for {label}: {error}")

    hair_built = False
    if can_build_hair_preview:
        try:
            hair_pixels, normal_texture, mask_texture = _build_hair_preview(preview)
            _wire_hair(tree, principled, hair_pixels, normal_texture, mask_texture, label, handled)
            hair_built = True
            package.warnings.append(
                f"Approximate preview for {label}: hair.shpk has no diffuse texture, using xivModdingFramework's "
                "default hair colors (real color depends on in-game dye data not stored in the material)"
            )
        except Exception as error:
            package.warnings.append(f"Could not build hair preview for {label}: {error}")

    opacity_source = _opacity_source(preview)
    opacity = None
    normal_texture = _first_texture(preview, "normal")
    if normal_texture is not None:
        handled.add(id(normal_texture))
        channel = opacity_source[1] if opacity_source and opacity_source[0] == "normal" else None
        normal_node = _texture_node(
            tree, normal_texture, label, package.warnings,
            pixels=lambda: _normal_preview_pixels(normal_texture, channel),
        )
        if normal_node is not None:
            normal_map = nodes.new(type="ShaderNodeNormalMap")
            normal_map.uv_map = f"uv{normal_texture.uv_set}"
            normal_map.location = (-40, normal_node.location.y)
            links.new(normal_node.outputs["Color"], normal_map.inputs["Color"])
            _link(tree, normal_map.outputs["Normal"], principled, "Normal")
            if channel is not None:
                opacity = normal_node.outputs["Alpha"]

    if colorset is None and not hair_built:
        diffuse_texture = _first_texture(preview, "diffuse")
        if diffuse_texture is not None:
            handled.add(id(diffuse_texture))
            diffuse_node = _texture_node(tree, diffuse_texture, label, package.warnings)
            if diffuse_node is not None:
                _link(tree, diffuse_node.outputs["Color"], principled, "Base Color")
                if opacity_source == ("diffuse", 3):
                    opacity = diffuse_node.outputs["Alpha"]
        specular_texture = _first_texture(preview, "specular")
        if specular_texture is not None:
            handled.add(id(specular_texture))
            specular_node = _texture_node(tree, specular_texture, label, package.warnings)
            if specular_node is not None:
                _link(tree, specular_node.outputs["Color"], principled, "Specular IOR Level", "Specular")
        mask_texture = _first_texture(preview, "mask")
        if mask_texture is not None and preview.shader_package.casefold() == "skin.shpk":
            # skin.shpk: mask R is specular strength and mask G roughness.
            handled.add(id(mask_texture))
            mask_node = _texture_node(tree, mask_texture, label, package.warnings)
            if mask_node is not None:
                mask = tree.separate(mask_node)
                _link(tree, mask.outputs["Red"], principled, "Specular IOR Level", "Specular")
                _link(tree, mask.outputs["Green"], principled, "Roughness")

    # Keep every remaining texture visible in the node tree for inspection.
    for texture in preview.textures:
        if id(texture) not in handled:
            _texture_node(tree, texture, label, package.warnings)

    translucent = _is_translucent(preview)
    alpha_linked = _link_opacity(tree, principled, opacity, preview)
    if translucent and alpha_linked and preview.shader_package.casefold() != "hair.shpk":
        # Translucent gear blends like it does in game; dithering turns sheer
        # fabric into noise. Only each object's nearest layer blends, so one
        # continuous mesh cannot sort its own triangles into the wrong order.
        # Hair cards overlap one another and stay dithered.
        material.surface_render_method = "BLENDED"
        material.use_transparency_overlap = False
    material.use_backface_culling = (
        True if preview.material_flags is None else bool(preview.material_flags & _FLAG_HIDE_BACKFACES)
    )

    if not any(socket.is_linked for socket in principled.inputs):
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
