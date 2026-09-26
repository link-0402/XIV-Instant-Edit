"""Reads and checks the job.json Instant Edit writes for each Painter project.

Nothing here touches Painter, so it runs in plain Python tests as well.
"""

import json
import os
import re
from dataclasses import dataclass, field

SCHEMA = "instant-edit.painter-job"
VERSION = 1
MAX_MANIFEST_BYTES = 4 * 1024 * 1024

_JOB_ID = re.compile(r"^[0-9a-f]{32}$")
_NAME = re.compile(r"^[A-Za-z0-9_\-]{1,64}$")
_CHANNELS = {
    "BaseColor", "Height", "Specular", "Opacity", "Emissive", "Roughness", "Glossiness", "Metallic",
    "Normal", "AO", "Specularlevel", "Scattering",
    "User0", "User1", "User2", "User3", "User4", "User5", "User6", "User7",
}
_FORMATS = {"sRGB8", "L8", "RGB8", "L16", "RGB16", "L16F", "RGB16F", "L32F", "RGB32F"}
_COLOR_SPACES = {"color", "data", "normal"}
_SIZES = {128, 256, 512, 1024, 2048, 4096, 8192}


class ManifestError(ValueError):
    """The job file is missing, malformed, or points outside its job folder."""


@dataclass(frozen=True)
class ChannelSpec:
    type: str
    format: str
    label: str


@dataclass(frozen=True)
class SeedSpec:
    channel: str
    path: str
    color_space: str


@dataclass(frozen=True)
class TextureSetSpec:
    name: str
    label: str
    width: int
    height: int
    remove_channels: tuple
    channels: tuple
    seeds: tuple


@dataclass(frozen=True)
class TargetSpec:
    key: str
    texture_set: str
    label: str


@dataclass(frozen=True)
class JobManifest:
    job_id: str
    capability: str
    callback_port: int
    plugin_version: str
    display_name: str
    job_dir: str
    mesh_path: str
    texture_sets: tuple
    targets: tuple
    export: dict = field(hash=False, compare=False)

    def target_keys(self) -> set:
        return {target.key for target in self.targets}


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise ManifestError(message)


def _text(value, name: str, limit: int = 260) -> str:
    _require(isinstance(value, str) and 0 < len(value) <= limit and "\x00" not in value, f"{name} is invalid.")
    return value


def _inside(job_dir: str, relative: str, name: str) -> str:
    """Resolves a job-relative path and refuses anything that leaves the job folder."""
    _text(relative, name)
    _require(not os.path.isabs(relative) and ":" not in relative, f"{name} must be relative to the job folder.")
    full = os.path.normcase(os.path.abspath(os.path.join(job_dir, relative)))
    root = os.path.normcase(os.path.abspath(job_dir))
    _require(full.startswith(root + os.sep), f"{name} points outside the job folder.")
    return os.path.abspath(os.path.join(job_dir, relative))


def load(manifest_path: str) -> JobManifest:
    _text(manifest_path, "Manifest path", 1024)
    _require(os.path.basename(manifest_path).lower() == "job.json", "The manifest must be a job.json file.")
    try:
        size = os.path.getsize(manifest_path)
        _require(size <= MAX_MANIFEST_BYTES, "The job file is too large.")
        with open(manifest_path, "r", encoding="utf-8") as handle:
            data = json.load(handle)
    except OSError as error:
        raise ManifestError(f"The job file could not be read: {error}") from error
    except ValueError as error:
        raise ManifestError(f"The job file is not valid JSON: {error}") from error
    return parse(data, os.path.dirname(os.path.abspath(manifest_path)))


def parse(data, job_dir: str) -> JobManifest:
    _require(isinstance(data, dict), "The job file must contain an object.")
    _require(data.get("schema") == SCHEMA and data.get("version") == VERSION,
             "This job was written by an incompatible Instant Edit version.")
    job_id = data.get("jobId")
    _require(isinstance(job_id, str) and _JOB_ID.match(job_id) is not None, "The job id is invalid.")
    _require(os.path.basename(os.path.normpath(job_dir)).lower() == job_id,
             "The job folder does not match the job id.")
    capability = _text(data.get("capability"), "Capability", 128)
    port = data.get("callbackPort")
    _require(isinstance(port, int) and not isinstance(port, bool) and 1 <= port <= 65535, "The callback port is invalid.")
    plugin_version = _text(data.get("pluginVersion"), "Plugin version", 32)
    display_name = _text(data.get("displayName"), "Display name", 200)
    mesh = _inside(job_dir, data.get("mesh"), "Mesh path")
    _require(mesh.lower().endswith(".obj"), "The mesh must be an OBJ file.")

    sets = data.get("textureSets")
    _require(isinstance(sets, list) and 0 < len(sets) <= 64, "The job has no texture sets.")
    texture_sets = []
    names = set()
    for raw in sets:
        _require(isinstance(raw, dict), "A texture set is invalid.")
        name = raw.get("name")
        _require(isinstance(name, str) and _NAME.match(name) is not None, "A texture set name is invalid.")
        _require(name not in names, f"Texture set {name} is listed twice.")
        names.add(name)
        width, height = raw.get("width"), raw.get("height")
        _require(width in _SIZES and height in _SIZES, f"Texture set {name} has an unsupported resolution.")
        removed = raw.get("removeChannels", [])
        _require(isinstance(removed, list) and all(c in _CHANNELS for c in removed),
                 f"Texture set {name} removes an unknown channel.")
        channels = []
        for channel in raw.get("channels", []):
            _require(isinstance(channel, dict) and channel.get("type") in _CHANNELS and channel.get("format") in _FORMATS,
                     f"Texture set {name} adds an invalid channel.")
            label = channel.get("label", "")
            _require(isinstance(label, str) and len(label) <= 64, f"Texture set {name} has an invalid channel label.")
            channels.append(ChannelSpec(channel["type"], channel["format"], label))
        seeds = []
        for seed in raw.get("seeds", []):
            _require(isinstance(seed, dict) and seed.get("channel") in _CHANNELS and seed.get("colorSpace") in _COLOR_SPACES,
                     f"Texture set {name} has an invalid seed.")
            path = _inside(job_dir, seed.get("file"), "Seed path")
            _require(path.lower().endswith(".tga"), "Seeds must be TGA files.")
            seeds.append(SeedSpec(seed["channel"], path, seed["colorSpace"]))
        _require(len({seed.channel for seed in seeds}) == len(seeds), f"Texture set {name} seeds a channel twice.")
        label = raw.get("label", name)
        texture_sets.append(TextureSetSpec(name, label if isinstance(label, str) else name, width, height,
                                           tuple(removed), tuple(channels), tuple(seeds)))

    targets = []
    keys = set()
    for raw in data.get("targets", []):
        _require(isinstance(raw, dict), "A target is invalid.")
        key = raw.get("key")
        _require(isinstance(key, str) and _NAME.match(key) is not None and key.lower() not in keys, "A target key is invalid.")
        keys.add(key.lower())
        texture_set = raw.get("textureSet")
        _require(texture_set in names, f"Target {key} names an unknown texture set.")
        label = raw.get("label", key)
        targets.append(TargetSpec(key, texture_set, label if isinstance(label, str) else key))

    export = data.get("export")
    _require(isinstance(export, dict) and isinstance(export.get("exportPresets"), list)
             and isinstance(export.get("exportList"), list), "The export configuration is missing.")
    _require("exportPath" not in export, "The export configuration must not set an export path.")
    return JobManifest(job_id, capability, port, plugin_version, display_name, os.path.abspath(job_dir), mesh,
                       tuple(texture_sets), tuple(targets), export)


def export_config(manifest: JobManifest, export_path: str) -> dict:
    """The job's export configuration aimed at one output folder."""
    config = json.loads(json.dumps(manifest.export))
    config["exportPath"] = export_path.replace("\\", "/")
    return config


def match_exported_files(manifest: JobManifest, textures: dict) -> list:
    """Pairs Painter's export result {(texture set, stack): [files]} with the job's target keys.

    Returns [(key, path)]. A file that is not one of the job's targets raises, since the export
    configuration only produces target files.
    """
    by_key = {target.key.lower(): target for target in manifest.targets}
    matched = {}
    for (texture_set, _stack), files in textures.items():
        for path in files:
            stem = os.path.splitext(os.path.basename(path))[0].lower()
            target = by_key.get(stem)
            _require(target is not None, f"Painter exported an unexpected file: {os.path.basename(path)}")
            _require(target.texture_set == texture_set,
                     f"{os.path.basename(path)} came from texture set {texture_set}, not {target.texture_set}.")
            _require(target.key not in matched, f"Painter exported {target.key} twice.")
            matched[target.key] = os.path.abspath(path)
    return sorted(matched.items())
