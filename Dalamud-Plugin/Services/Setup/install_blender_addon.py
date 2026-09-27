"""Sets up the XIV Instant Edit add-on in the Blender that runs this script.

Instant Edit's setup runs it as
    blender --background --online-mode --python install_blender_addon.py -- RESULT_JSON [--replace]
against the user's own preferences. It adds the GitHub extension repository (or reuses one
with the same URL), turns on its update check at startup and Blender's online access, installs
and enables the add-on from it, saves the preferences, and writes what happened to RESULT_JSON.

A copy of the add-on installed from another repository (a downloaded ZIP, say) would clash
with the new one, so it is only replaced with --replace; without it the script reports the
copy and installs nothing. Linked copies (a developer's checkout) are never removed.
"""

import json
import os
import sys
import traceback

import bpy

PACKAGE = "xiv_instant_edit"
REPOSITORY_URL = "https://raw.githubusercontent.com/link-0402/XIV-Instant-Edit/main/Blender-Addon/blender_repo/index.json"
# URLs the add-on has been published under; a repository with any of them counts as ours.
KNOWN_URLS = (
    REPOSITORY_URL,
    "https://link-0402.github.io/XIV-Instant-Edit/index.json",
)
REPOSITORY_NAME = "XIV Instant Edit"
REPOSITORY_MODULE = "xiv_instant_edit_github"


def _normalized(url):
    return url.strip().rstrip("/").lower()


def _manifest_version(folder):
    try:
        with open(os.path.join(folder, "blender_manifest.toml"), encoding="utf-8") as manifest:
            for line in manifest:
                key, _, value = line.partition("=")
                if key.strip() == "version":
                    return value.strip().strip("\"'")
    except OSError:
        pass
    return ""


def _is_link(path):
    isjunction = getattr(os.path, "isjunction", None)
    return os.path.islink(path) or (isjunction is not None and isjunction(path))


def _find_repository(repos):
    known = {_normalized(url) for url in KNOWN_URLS}
    for repo in repos:
        if repo.use_remote_url and _normalized(repo.remote_url) in known:
            return repo
    return None


def _other_copies(repos, ours):
    copies = []
    for repo in repos:
        if repo == ours or not repo.directory:
            continue
        folder = os.path.join(repo.directory, PACKAGE)
        if os.path.isdir(folder):
            copies.append({
                "repository": repo.name,
                "module": repo.module,
                "directory": repo.directory,
                "version": _manifest_version(folder),
                "linked": _is_link(folder),
            })
    return copies


def _succeeded(result):
    return "FINISHED" in result


def run(result, replace):
    prefs = bpy.context.preferences
    repos = prefs.extensions.repos

    repo = _find_repository(repos)
    if repo is None:
        repo = repos.new(name=REPOSITORY_NAME, module=REPOSITORY_MODULE, remote_url=REPOSITORY_URL)
        result["repositoryCreated"] = True
    repo.enabled = True
    repo.use_sync_on_startup = True
    # The update check needs online access; the setup asked the user before running this.
    prefs.system.use_online_access = True
    if hasattr(prefs.extensions, "use_online_access_handled"):
        prefs.extensions.use_online_access_handled = True
    result["repository"] = repo.name
    result["repositoryUrl"] = repo.remote_url

    if not _succeeded(bpy.ops.extensions.repo_sync(repo_directory=repo.directory)):
        raise RuntimeError("Blender could not download the extension list from GitHub.")

    copies = _other_copies(repos, repo)
    result["otherCopies"] = copies
    if copies:
        linked = [copy for copy in copies if copy["linked"]]
        if not replace or linked:
            bpy.ops.wm.save_userpref()
            result["preferencesSaved"] = True
            result["error"] = (
                "Another copy of the add-on is installed from \"{}\"; it is linked to a folder, "
                "so remove it in Blender yourself.".format(linked[0]["repository"])
                if linked and replace else
                "Another copy of the add-on is installed from \"{}\".".format(copies[0]["repository"]))
            return
        for copy in copies:
            if not _succeeded(bpy.ops.extensions.package_uninstall(repo_directory=copy["directory"], pkg_id=PACKAGE)):
                raise RuntimeError("Blender could not remove the add-on installed from \"{}\".".format(copy["repository"]))
        result["replaced"] = [copy["repository"] for copy in copies]

    if not _succeeded(bpy.ops.extensions.package_install(
            repo_directory=repo.directory, pkg_id=PACKAGE, enable_on_install=False)):
        raise RuntimeError("Blender could not install the add-on from GitHub.")
    folder = os.path.join(repo.directory, PACKAGE)
    if not os.path.isdir(folder):
        raise RuntimeError("Blender reported the install, but the add-on folder is missing.")
    result["installedVersion"] = _manifest_version(folder)

    # Enabled here rather than by the install operator, whose context has no scene. When
    # registering fails in this windowless session (another Blender holding the add-on's
    # port, say), the add-on is still listed as enabled, so the next start loads it.
    import addon_utils
    module = "bl_ext.{}.{}".format(repo.module, PACKAGE)
    errors = []
    if module not in prefs.addons:
        addon_utils.enable(module, default_set=True, handle_error=lambda error: errors.append(str(error)))
    if module not in prefs.addons:
        prefs.addons.new().module = module
        result["enableWarning"] = errors[0] if errors else "The add-on loads the next time Blender starts."
    result["enabled"] = module in prefs.addons

    if not _succeeded(bpy.ops.wm.save_userpref()):
        raise RuntimeError("Blender could not save its preferences.")
    result["preferencesSaved"] = True
    result["ok"] = result["enabled"]


def main():
    arguments = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    if not arguments:
        print("usage: blender --background --online-mode --python install_blender_addon.py -- RESULT_JSON [--replace]")
        sys.exit(2)
    result = {
        "ok": False,
        "blenderVersion": bpy.app.version_string,
        "userConfig": bpy.utils.user_resource("CONFIG"),
    }
    try:
        if not bpy.app.online_access:
            raise RuntimeError("Blender is not allowed to go online; start it without --offline-mode.")
        run(result, "--replace" in arguments[1:])
    except Exception as error:
        result["error"] = str(error) or type(error).__name__
        result["traceback"] = traceback.format_exc()
    with open(arguments[0], "w", encoding="utf-8") as output:
        json.dump(result, output, indent=2)


main()
