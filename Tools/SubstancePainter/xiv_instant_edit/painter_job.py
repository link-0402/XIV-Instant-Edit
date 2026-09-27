"""Painter-side work for a job: project setup, metadata, and texture exports.

Everything here calls Painter's API and must run on Painter's main thread.
"""

import copy
import os
from dataclasses import dataclass, field

import substance_painter.colormanagement as colormanagement
import substance_painter.export as export
import substance_painter.layerstack as layerstack
import substance_painter.project as project
import substance_painter.resource as resource
import substance_painter.textureset as textureset

from . import manifest as manifest_module
from .manifest import ManifestError, TargetSpec

METADATA_CONTEXT = "xiv_instant_edit"
METADATA_KEY = "job"
BASE_LAYER_NAME = "XIV original"
RESOURCE_GROUP = "XIV Instant Edit"


class SetupError(RuntimeError):
    """The project could not be prepared for the job."""


@dataclass
class JobState:
    """What a project needs to send textures back; stored in the project's metadata."""

    job_id: str
    capability: str
    callback_port: int
    display_name: str
    job_dir: str
    export: dict = field(repr=False)
    targets: tuple = ()
    base_layers: dict = field(default_factory=dict)

    def to_metadata(self) -> dict:
        return {
            "version": 1,
            "jobId": self.job_id,
            "capability": self.capability,
            "callbackPort": self.callback_port,
            "displayName": self.display_name,
            "jobDir": self.job_dir,
            "export": self.export,
            "targets": [{"key": t.key, "textureSet": t.texture_set, "label": t.label} for t in self.targets],
            "baseLayers": {name: int(uid) for name, uid in self.base_layers.items()},
        }

    @staticmethod
    def from_metadata(data) -> "JobState":
        if not isinstance(data, dict) or data.get("version") != 1:
            raise ManifestError("The project's Instant Edit data is from an incompatible version.")
        targets = tuple(TargetSpec(str(t["key"]), str(t["textureSet"]), str(t.get("label", t["key"])))
                        for t in data.get("targets", []) if isinstance(t, dict) and "key" in t and "textureSet" in t)
        layers = data.get("baseLayers") if isinstance(data.get("baseLayers"), dict) else {}
        state = JobState(str(data.get("jobId", "")), str(data.get("capability", "")), int(data.get("callbackPort", 0)),
                         str(data.get("displayName", "")), str(data.get("jobDir", "")),
                         data.get("export") if isinstance(data.get("export"), dict) else {}, targets,
                         {str(k): int(v) for k, v in layers.items()})
        if len(state.job_id) != 32 or not state.capability or not state.targets or not state.export:
            raise ManifestError("The project's Instant Edit data is incomplete.")
        return state

    @staticmethod
    def from_manifest(job: manifest_module.JobManifest, base_layers: dict) -> "JobState":
        return JobState(job.job_id, job.capability, job.callback_port, job.display_name, job.job_dir,
                        job.export, job.targets, dict(base_layers))


def _name(texture_set) -> str:
    # A property in current Painter versions and a method in older ones; calling the property warns.
    name = texture_set.name
    return str(name) if isinstance(name, str) else name()


def find_texture_set(name: str):
    matches = [ts for ts in textureset.all_texture_sets() if _name(ts) == name]
    if not matches:
        original = [ts for ts in textureset.all_texture_sets() if getattr(ts, "original_name", None) == name]
        matches = original
    if len(matches) != 1:
        raise SetupError(f"Painter did not create the texture set {name} from the mesh.")
    return matches[0]


def _channel_type(name: str):
    return getattr(textureset.ChannelType, name)


def _channel_format(name: str):
    return getattr(textureset.ChannelFormat, name)


_COLOR_SPACE_PREFERENCES = {
    "color": (("LegacyColorSpace", "sRGB"), ("GenericColorSpace", "sRGB")),
    "data": (("DataColorSpace", "Data"), ("GenericColorSpace", "Raw"), ("LegacyColorSpace", "Linear")),
    # OpenGL tangent space is Painter's right-handed normal format.
    "normal": (("NormalColorSpace", "NormalXYZRight"), ("GenericColorSpace", "Raw")),
}


def apply_color_space(source, kind: str) -> None:
    """Pins a seed's color space so Painter reads its values unchanged."""
    try:
        available = source.list_available_color_spaces()
    except Exception:
        return
    for enum_name, member in _COLOR_SPACE_PREFERENCES.get(kind, ()):
        enum = getattr(colormanagement, enum_name, None)
        value = getattr(enum, member, None) if enum is not None else None
        if value is not None and value in available:
            source.set_color_space(value)
            return


def create_project(job: manifest_module.JobManifest) -> None:
    if project.is_open():
        raise SetupError("Another project is open in Painter. Save and close it, then send again.")
    largest = max(max(ts.width, ts.height) for ts in job.texture_sets)
    settings = project.Settings(
        normal_map_format=project.NormalMapFormat.OpenGL,
        default_texture_resolution=min(largest, 4096),
    )
    project.create(mesh_file_path=job.mesh_path, settings=settings)


def _tile_offset(scale: int) -> float:
    """Painter scales a fill's UVs around the tile center and offsets them first; this offset puts
    the repeated seed's corner on the UV origin, so one copy fills the texture's corner exactly."""
    return 0.5 - 0.5 / scale


def _bottom_position(stack):
    roots = layerstack.get_root_layer_nodes(stack)
    if roots:
        return layerstack.InsertPosition.below_node(roots[-1])
    return layerstack.InsertPosition.from_textureset_stack(stack)


def setup_texture_sets(job: manifest_module.JobManifest) -> dict:
    """Sizes each texture set, sets its channels and seeds a bottom fill layer. Returns {set: layer uid}."""
    layers = {}
    for spec in job.texture_sets:
        texture_set = find_texture_set(spec.name)
        texture_set.set_resolution(textureset.Resolution(spec.width, spec.height))
        stack = texture_set.get_stack()
        for name in spec.remove_channels:
            channel = _channel_type(name)
            if stack.has_channel(channel):
                stack.remove_channel(channel)
        for channel in spec.channels:
            kind, fmt = _channel_type(channel.type), _channel_format(channel.format)
            if stack.has_channel(kind):
                stack.edit_channel(kind, fmt, channel.label or None)
            else:
                stack.add_channel(kind, fmt, channel.label or None)
        if not spec.seeds:
            continue
        imported = []
        for seed in spec.seeds:
            if not os.path.isfile(seed.path):
                raise SetupError(f"A texture file for {spec.name} is missing: {os.path.basename(seed.path)}")
            imported.append((seed, resource.import_project_resource(seed.path, resource.Usage.TEXTURE,
                                                                   group=RESOURCE_GROUP)))
        with layerstack.ScopedModification("XIV Instant Edit setup"):
            fill = layerstack.insert_fill(_bottom_position(stack))
            fill.set_name(BASE_LAYER_NAME)
            fill.active_channels = {_channel_type(seed.channel) for seed in spec.seeds}
            fill.set_projection_parameters(layerstack.UVProjectionParams(
                filtering_mode=layerstack.FilteringMode.Nearest,
                uv_wrapping_mode=layerstack.UVWrapMode.Repeat,
                uv_transformation=layerstack.UVTransformationParams(
                    scale_mode=layerstack.ScaleMode.Factors, scale=[float(s) for s in spec.uv_scale],
                    rotation=0.0, offset=[_tile_offset(s) for s in spec.uv_scale]),
            ))
            for seed, res in imported:
                apply_color_space(fill.set_source(_channel_type(seed.channel), res.identifier()), seed.color_space)
        layers[spec.name] = fill.uid()
    return layers


_SHADER = "asm-metal-rough"


def _instance_name(display: manifest_module.DisplaySpec) -> str:
    parts = {"blend": ["XIV alpha blend"], "test": [f"XIV alpha test {display.threshold:.2f}"]}.get(display.alpha, ["XIV opaque"])
    if display.double_sided:
        parts.append("two-sided")
    return ", ".join(parts)


def apply_display(job: manifest_module.JobManifest) -> str:
    """Gives texture sets shader instances that draw them like the game: see-through where its
    opacity says so, and from both sides where it has no back-face culling. Painter's Adobe
    Standard Material shader does both through parameters. Returns a warning, or "" when done.
    """
    wanted = {spec.name: spec.display for spec in job.texture_sets if spec.display != manifest_module.DisplaySpec()}
    if not wanted:
        return ""
    import json

    import substance_painter.js as js

    data = js.evaluate("alg.shaders.shaderInstancesToObject()")
    shaders = data.get("shaders") if isinstance(data, dict) else None
    if not isinstance(shaders, dict) or not shaders or not isinstance(data.get("texturesets"), dict):
        return "Painter did not report its shaders, so transparency isn't shown."
    base = next(iter(shaders.values()))
    if base.get("shader") != _SHADER:
        # Other shaders name their parameters differently; start the instances from the defaults.
        base = {"shader": _SHADER, "parameters": {}}
    for name, display in wanted.items():
        instance_name = _instance_name(display)
        if instance_name not in shaders:
            instance = copy.deepcopy(base)
            instance["shaderInstance"] = instance_name
            parameters = instance.setdefault("parameters", {})
            parameters.setdefault("Geometry", {})["doubleSided"] = display.double_sided
            opacity = parameters.setdefault("Geometry/Opacity", {})
            opacity["alphaBlendEnabled"] = display.alpha == "blend"
            opacity["alpha_test_enabled"] = display.alpha == "test"
            opacity["alpha_test_threshold"] = display.threshold
            shaders[instance_name] = instance
        data["texturesets"][name] = {"shader": instance_name}
    js.evaluate("alg.shaders.shaderInstancesFromObject(" + json.dumps(data) + ")")
    return ""


def store(job: JobState) -> None:
    project.Metadata(METADATA_CONTEXT).set(METADATA_KEY, job.to_metadata())


def load() -> "JobState | None":
    """The job linked to the open project, or None when the project isn't an Instant Edit one."""
    if not project.is_open():
        return None
    metadata = project.Metadata(METADATA_CONTEXT)
    if METADATA_KEY not in metadata.list():
        return None
    return JobState.from_metadata(metadata.get(METADATA_KEY))


def missing_base_layers(job: JobState) -> list:
    """Texture sets whose "XIV original" layer was deleted; exporting them would drop the original texture."""
    missing = []
    for name, uid in job.base_layers.items():
        try:
            nodes = layerstack.get_node_by_uid(uid)
        except Exception:
            nodes = None
        if not nodes:
            missing.append(name)
    return sorted(missing)


def run_export(job, folder: str) -> list:
    """Exports every target into folder; returns [(key, path)]."""
    os.makedirs(folder, exist_ok=True)
    result = export.export_project_textures(manifest_module.export_config(job, folder))
    if result.status == export.ExportStatus.Cancelled:
        raise SetupError("The export was cancelled.")
    if result.status not in (export.ExportStatus.Success, export.ExportStatus.Warning):
        raise SetupError(result.message or "The export failed.")
    files = manifest_module.match_exported_files(job, result.textures)
    missing = sorted({t.key for t in job.targets} - {key for key, _ in files})
    if missing:
        raise SetupError("Painter did not export: " + ", ".join(missing))
    return files
