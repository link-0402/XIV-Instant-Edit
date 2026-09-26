"""Run the Blender regression suites, each in a disposable Blender user profile.

The suites reset Blender with ``bpy.ops.wm.read_factory_settings``, which
refreshes extensions for the factory preferences: it rewrites
``extensions/.cache/compat.dat`` and deletes every wheel in
``extensions/.local`` that no enabled extension needs. Against the real user
profile that removes the wheels of every installed extension, so each suite
gets a fresh temporary profile instead.

Usage: python Blender-Addon/testing/run_blender_suites.py [--blender PATH] [suite ...]
"""

import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

TESTING = Path(__file__).resolve().parent
SUITES = ("bridge_regression", "smoke_export", "correctness_regression", "animation_regression", "skeleton_regression")
# blender_fixtures.addon_session refuses to run unless Blender's user
# directories are inside the directory named by this variable.
PROFILE_VARIABLE = "XIV_IE_TEST_PROFILE"


def isolated_environment(profile: Path) -> dict[str, str]:
    # A BLENDER_USER_CONFIG/SCRIPTS/EXTENSIONS/DATAFILES value inherited from
    # the caller would take precedence over BLENDER_USER_RESOURCES.
    environment = {name: value for name, value in os.environ.items()
                   if not name.startswith("BLENDER_USER_")}
    environment["BLENDER_USER_RESOURCES"] = str(profile)
    environment[PROFILE_VARIABLE] = str(profile)
    return environment


def run_suite(blender: str, script: Path) -> int:
    with tempfile.TemporaryDirectory(prefix="xiv-ie-blender-profile-", ignore_cleanup_errors=True) as profile:
        print(f"Running {script.name} with Blender user profile {profile}", flush=True)
        command = [blender, "--background", "--factory-startup",
                   "--python-exit-code", "1", "--python", str(script)]
        return subprocess.run(command, env=isolated_environment(Path(profile))).returncode


def _script(name: str) -> Path:
    path = Path(name)
    if not path.is_file():
        path = TESTING / (name if name.endswith(".py") else f"{name}.py")
    if not path.is_file():
        raise SystemExit(f"Blender test script not found: {name}")
    return path.resolve()


def main() -> int:
    parser = argparse.ArgumentParser(description="Run Blender test scripts in disposable user profiles.")
    parser.add_argument("--blender", default=shutil.which("blender"),
                        help="Blender executable (default: blender on PATH)")
    parser.add_argument("suites", nargs="*", default=SUITES,
                        help="suite names in Blender-Addon/testing or script paths "
                             f"(default: {' '.join(SUITES)})")
    arguments = parser.parse_args()
    if not arguments.blender:
        parser.error("Blender was not found on PATH; pass --blender")
    scripts = [_script(name) for name in arguments.suites]
    failed = [script.name for script in scripts if run_suite(arguments.blender, script) != 0]
    if failed:
        print(f"Failed Blender suites: {', '.join(failed)}", file=sys.stderr)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
