"""Ports and version, from the config.json Instant Edit writes when it installs this plugin."""

import json
import os
from dataclasses import dataclass

from . import PLUGIN_VERSION

DEFAULT_PAINTER_PORT = 42426
DEFAULT_PLUGIN_PORT = 42428
CONFIG_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "config.json")


@dataclass(frozen=True)
class Settings:
    painter_port: int = DEFAULT_PAINTER_PORT
    plugin_port: int = DEFAULT_PLUGIN_PORT
    version: str = PLUGIN_VERSION


def _port(value, fallback: int) -> int:
    return value if isinstance(value, int) and not isinstance(value, bool) and 1 <= value <= 65535 else fallback


def load(path: str = CONFIG_FILE) -> Settings:
    """Reads the config; a missing or damaged file falls back to the defaults."""
    try:
        with open(path, "r", encoding="utf-8") as handle:
            data = json.load(handle)
    except (OSError, ValueError):
        return Settings()
    if not isinstance(data, dict):
        return Settings()
    version = data.get("version")
    return Settings(
        painter_port=_port(data.get("painterPort"), DEFAULT_PAINTER_PORT),
        plugin_port=_port(data.get("pluginPort"), DEFAULT_PLUGIN_PORT),
        version=version if isinstance(version, str) and 0 < len(version) <= 32 else PLUGIN_VERSION,
    )
