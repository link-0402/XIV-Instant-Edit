"""XIV Instant Edit bridge for Adobe Substance 3D Painter.

Instant Edit (the FFXIV Dalamud plugin) sends a model and its textures here from its On Screen
browser. This plugin builds the Painter project, and its "Send to game" button exports the
textures in FFXIV's channel layout and hands them back to Instant Edit.
"""

# Kept equal to the Dalamud plugin's version; Instant Edit reports a mismatch otherwise.
PLUGIN_VERSION = "2.1.0"

_plugin = None


def start_plugin():
    global _plugin
    from .plugin import Plugin

    _plugin = Plugin()
    _plugin.start()


def close_plugin():
    global _plugin
    if _plugin is not None:
        _plugin.close()
        _plugin = None
