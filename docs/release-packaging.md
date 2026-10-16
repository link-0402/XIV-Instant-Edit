# Release packaging

Release ZIPs are build output. They are built into the ignored `dist/` folder and shipped as
GitHub release assets, never committed. Before this change both ZIPs were committed on every
release: `Dalamud-Plugin/InstantEdit/latest.zip` (67 revisions, 61 MB packed) and
`Blender-Addon/blender_repo/XIV-Instant-Edit.zip` (60 revisions, 5 MB packed).

## How users get each package

| Channel | URL the user added (unchanged) | Package URL before | Package URL after |
| --- | --- | --- | --- |
| Dalamud, shared repo | `https://raw.githubusercontent.com/link-0402/DalamudPlugins/main/repo.json` | `https://github.com/link-0402/XIV-Instant-Edit/releases/latest/download/XIV-Instant-Edit.zip` | unchanged |
| Dalamud, in-repo `Dalamud-Plugin/repo.json` | `https://raw.githubusercontent.com/link-0402/XIV-Instant-Edit/main/Dalamud-Plugin/repo.json` | `https://raw.githubusercontent.com/link-0402/XIV-Instant-Edit/main/Dalamud-Plugin/InstantEdit/latest.zip` | `https://github.com/link-0402/XIV-Instant-Edit/releases/download/<ver>/XIV-Instant-Edit.zip`, pinned to its `AssemblyVersion` |
| Blender, raw index (setup wizard, README) | `https://raw.githubusercontent.com/link-0402/XIV-Instant-Edit/main/Blender-Addon/blender_repo/index.json` | `./XIV-Instant-Edit.zip`, the committed ZIP | `https://github.com/link-0402/XIV-Instant-Edit/releases/download/<tag>/XIV-Instant-Edit-Blender.zip` |
| Blender, Pages index | `https://link-0402.github.io/XIV-Instant-Edit/index.json` | `./XIV-Instant-Edit.zip`, rebuilt by CI from every push to main | same `index.json` as the raw index |

The URLs users added stay the same, so installed clients keep finding updates. This matters most
for Blender, which names the local extension folder after the repository URL.

- **Plugin asset:** `XIV-Instant-Edit.zip`. The name stays because the DalamudPlugins links use
  `releases/latest/download/XIV-Instant-Edit.zip`.
- **Blender asset:** `XIV-Instant-Edit-Blender.zip`. The index points at a pinned tag, not
  `latest`, because it also holds the file's size and hash. A plugin-only hotfix release has no
  Blender asset and leaves the index pointing at the last add-on release. Never delete a release
  whose asset the index points at.
- **Absolute `archive_url`:** Blender 4.5 and 5.2 accept one, follow GitHub's download redirect
  and check the hash. This was tested with each version's `blender_ext.py install` against the
  2.1.0 release asset.
- **Pages:** serves the tracked `index.json` instead of building from main, so it no longer
  publishes unreleased add-on code. Blender's `index.html` listing page is no longer published.

## Scripts

| Script | Output |
| --- | --- |
| `scripts/prepare-release.ps1` | Refuses to run with uncommitted add-on, plugin or `Tools` changes (`-AllowDirty` overrides). Builds the plugin with `-p:OutputPath=dist/plugin-build/`. Empties `dist/plugin-build/` first. Copies both assets to `dist/release/`, points the in-repo `repo.json` download links and the tracked `index.json` at release `-Tag` (default: the plugin version), and refuses a tag whose release already holds that asset. `repo.json`'s `AssemblyVersion` must match the csproj. `-SkipPlugin` and `-SkipBlender` work as before. |
| `scripts/generate-blender-repository.ps1` | `dist/blender_repo/` holds the package, `index.json` and `index.html`. `-ArchiveUrl` swaps the relative URL for an absolute one. |
| `scripts/package-blender-addon.ps1` | `dist/blender_repo/XIV-Instant-Edit-Blender.zip` |
| `scripts/verify-blender-repository.ps1` | Checks size and hash against a local `./` file, `-ArchivePath`, or by downloading an `https` `archive_url`. |
| `InstantEdit.csproj` (Release) | Copies `latest.zip` to `dist/InstantEdit/latest.zip`, no longer `Dalamud-Plugin/InstantEdit/`. |

`prepare-release.ps1` never builds into `Dalamud-Plugin\bin\Release`. That folder is a Dalamud
dev-plugin location with automatic reloading, so building there would hot-reload a running game.

### Equivalence with the committed ZIPs

- **Blender package:** functionally equivalent. It has the same 78 entries, each byte-identical
  to the git blob at HEAD. The old ZIPs held CRLF or LF copies depending on the checkout's
  history. The script now stores text files with LF endings, a fixed timestamp and sorted order,
  so a checkout packs to byte-identical ZIPs every run on the same PowerShell runtime. The
  released 2.1.0 ZIP also matches its commit's blobs once line endings are normalized.
- **Plugin package:** functionally equivalent. It has the same 24 entries, and 22 are
  byte-identical, `InstantEdit.json` included. `InstantEdit.dll` and `.pdb` differ because HEAD
  has newer code and PDBs embed the build path. DalamudPackager stamps build times, so the plugin
  ZIP is not byte-reproducible.

## Release steps from now on

1. Bump the versions as before (csproj, `repo.json`, `blender_manifest.toml`, changelog).
2. Commit, then run `./scripts/prepare-release.ps1` on the clean tree.
3. Commit the rewritten `Blender-Addon/blender_repo/index.json` and `Dalamud-Plugin/repo.json`.
4. Push the tag before main, so the asset exists before the index that names it ships:
   `git tag <ver>` then `git push origin <ver>`.
5. Create the release:
   `gh release create <ver> --verify-tag --title "XIV Instant Edit v<ver>" --notes-file <notes> dist/release/XIV-Instant-Edit.zip dist/release/XIV-Instant-Edit-Blender.zip`
6. Check the published asset: `./scripts/verify-blender-repository.ps1 -RepositoryPath Blender-Addon/blender_repo`.
7. Push main, then bump DalamudPlugins (AssemblyVersion, LastUpdate, changelog).

For a plugin-only hotfix, use `-SkipBlender` and attach only the plugin ZIP.

## Migration (each step needs the user's go-ahead)

f964f72 recommitted a repackaged Blender ZIP (290671 bytes) while `index.json` still described
the released one (262837 bytes, `sha256:c9d6cc6b…`), and that reached origin/main, so installs
from the raw index failed the checksum. The 2026-10-16 review put the 2.1.0 bytes back (taken from
the `2.1.0` tag). Keep the committed ZIP at those bytes until step 2 is done.

1. **Attach the 2.1.0 add-on package to the 2.1.0 release.** Use the exact bytes the index
   describes. In PowerShell:
   ```
   cmd /c "git cat-file blob origin/main:Blender-Addon/blender_repo/XIV-Instant-Edit.zip > XIV-Instant-Edit-Blender.zip"
   (Get-FileHash XIV-Instant-Edit-Blender.zip).Hash   # C9D6CC6B1C8F04A6561466844D1152721963F3535358C9393CED979BBDB55E86
   gh release upload 2.1.0 XIV-Instant-Edit-Blender.zip
   ```
   Nothing changes for users: the new asset sits next to `XIV-Instant-Edit.zip`.
2. **Point the index at that asset.** Change one line in `Blender-Addon/blender_repo/index.json`
   and leave size and hash as they are:
   `"archive_url": "https://github.com/link-0402/XIV-Instant-Edit/releases/download/2.1.0/XIV-Instant-Edit-Blender.zip",`
   Check it with `./scripts/verify-blender-repository.ps1 -RepositoryPath Blender-Addon/blender_repo`,
   then commit.
3. **Merge this branch into main** (`claude/release-zips-fb04ec`: scripts, `.gitignore`,
   csproj, in-repo `repo.json`, Pages workflow). The in-repo `repo.json` then serves the 2.1.0
   release asset, which is byte-identical to the committed `latest.zip` on `origin/main`
   (`sha256:af7486ed…`).
4. **Push main.** This is the shipping step. Check afterwards: the Pages workflow is green,
   Blender's "Check for updates" on the raw URL reports no error, and Dalamud still offers the
   plugin from the in-repo `repo.json`.
5. **Stop tracking the ZIPs.** Wait at least 10 minutes, since raw.githubusercontent.com caches
   for 5. Then run
   `git rm --cached Dalamud-Plugin/InstantEdit/latest.zip Blender-Addon/blender_repo/XIV-Instant-Edit.zip`,
   commit and push. The local files stay, and `.gitignore` now ignores them. This stops the
   repository from growing. Old tags and commits still serve the old raw URLs.
6. **Optional: rewrite history** with `git filter-repo --path Dalamud-Plugin/InstantEdit/latest.zip --path Blender-Addon/blender_repo/XIV-Instant-Edit.zip --invert-paths`.
   This saves about 66 MB of the 174 MB pack. It rewrites every commit and every release tag
   (1.2.0 to 2.1.0), so main and all tags need a force push. Every clone and worktree has to be
   re-cloned, and the parallel session worktrees lose their base. GitHub keeps the old objects
   until its own garbage collection runs. Only worth it if clone size matters.
7. **Optional, separate from the ZIPs:** most of the 430 MB `.git` is not history. 253 MB is
   seven abandoned `objects/c8/tmp_obj_*` files from 2026-08-22, left by interrupted writes.
   `git prune` (or `git gc`) deletes them without rewriting anything. That brings `.git` to about
   177 MB.
