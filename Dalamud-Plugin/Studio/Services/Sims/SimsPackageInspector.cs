using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace InstantEdit.Studio.Services.Sims;

public enum SimsAssetKind
{
    Mesh,
    Texture,
}

/// <summary>One extractable thing found in a package, as shown in the importer list.</summary>
public sealed class SimsAsset
{
    public SimsAssetKind   Kind;
    public SimsTextureRole Role = SimsTextureRole.Unknown;

    public DbpfEntry Entry = null!;

    /// <summary>Name of the CAS part that referenced this, or empty when nothing did.</summary>
    public string PartName = string.Empty;

    /// <summary>
    /// Ticked in the UI. Assets start ticked and the scan unticks the ones an FFXIV port
    /// has no use for; the window then narrows the meshes to the chosen detail level.
    /// </summary>
    public bool Selected = true;

    /// <summary>True when the role came from a CAS part rather than from the resource type.</summary>
    public bool RoleFromCasPart;

    // Meshes
    public int VertexCount;
    public int FaceCount;

    /// <summary>
    /// Detail level, 0 being the highest. <see cref="UnknownLod"/> when no CAS part
    /// listed this mesh, so its level could not be established.
    /// </summary>
    public int LodLevel = UnknownLod;

    public const int UnknownLod = -1;

    public string LodLabel => LodLevel == UnknownLod ? "LOD ?" : $"LOD {LodLevel}";

    // Textures
    public int Width;
    public int Height;
    public string SourceFormat = string.Empty;

    /// <summary>PNG bytes of a small preview, decoded during the scan.</summary>
    public byte[]? ThumbnailPng;

    /// <summary>Set when this asset could not be decoded; it is listed but cannot be extracted.</summary>
    public string? Error;

    public string Describe() => Kind == SimsAssetKind.Mesh
        ? $"{VertexCount:N0} verts / {FaceCount:N0} tris"
        : Width > 0 ? $"{Width} x {Height}" : "—";
}

/// <summary>Everything a scan found, plus whatever went wrong along the way.</summary>
public sealed class SimsScanResult
{
    public string PackagePath = string.Empty;
    public List<SimsAsset> Assets = new();
    public List<string> Warnings = new();

    /// <summary>True when at least one CAS part parsed, so roles are authoritative.</summary>
    public bool HasCasPartData;

    /// <summary>Detail levels present in this package, ascending; drives the LOD picker.</summary>
    public List<int> AvailableLods = new();

    /// <summary>
    /// Which half of the texture sheet the meshes actually use, when they all fit in one.
    /// Offered in the UI as a crop; see <see cref="SimsHalfCrop"/>.
    /// </summary>
    public SimsHalfCrop SuggestedCrop = SimsHalfCrop.None;

    public IEnumerable<SimsAsset> Meshes   => Assets.Where(a => a.Kind == SimsAssetKind.Mesh);
    public IEnumerable<SimsAsset> Textures => Assets.Where(a => a.Kind == SimsAssetKind.Texture);
}

/// <summary>
/// Scans a .package and works out what is worth extracting.
///
/// The CAS parts are the authority on textures: each one names the resources it uses,
/// and its name is what the extracted files get called, so a package of colour swatches
/// comes out as one readably-named image per swatch. When no CAS part parses, textures
/// are classified by resource type instead — which is why every row stays editable.
///
/// Every mesh is listed with the detail level its CAS part assigns it, and the window
/// decides which levels to export. Meshes no CAS part mentions keep an unknown level
/// rather than being guessed at: neither vertex count nor group ID is a sound proxy,
/// since creators routinely copy LOD 0 into every level (leaving several meshes with
/// identical counts) and the group IDs do not run in level order.
/// </summary>
public static class SimsPackageInspector
{
    private const int ThumbnailSize = 96;

    /// <summary>Lists what a package holds. <paramref name="cancel"/> stops it between resources (OperationCanceledException).</summary>
    public static SimsScanResult Scan(string packagePath, Action<string>? progress = null, CancellationToken cancel = default)
    {
        var result = new SimsScanResult { PackagePath = packagePath };

        cancel.ThrowIfCancellationRequested();
        using var package = new DbpfPackage(packagePath);
        progress?.Invoke($"Reading index ({package.Entries.Count} resources)");

        var parts = ReadCasParts(package, result, progress, cancel);
        result.HasCasPartData = parts.Count > 0;

        AddMeshes(package, parts, result, progress, cancel);
        AddTextures(package, parts, result, progress, cancel);
        SelectDefaults(result);

        if (result.Assets.Count == 0)
            result.Warnings.Add("No meshes or textures were found in this package.");

        return result;
    }

    /// <summary>
    /// Narrows what starts ticked to what an FFXIV port actually needs: the LOD 0 meshes
    /// only, since the lower levels are the game's reduced-detail copies. A package with
    /// no LOD 0 keeps every mesh ticked rather than silently offering nothing. Textures
    /// have already had the roles a port cannot use unticked as they were listed.
    /// </summary>
    private static void SelectDefaults(SimsScanResult result)
    {
        if (!result.AvailableLods.Contains(0))
            return;

        foreach (var asset in result.Meshes)
            asset.Selected = asset.LodLevel == 0;
    }

    private static List<SimsCasPart> ReadCasParts(DbpfPackage package, SimsScanResult result, Action<string>? progress, CancellationToken cancel)
    {
        var parts = new List<SimsCasPart>();
        var caspEntries = package.Entries.Where(e => e.Type == SimsResourceType.Casp).ToList();
        if (caspEntries.Count == 0)
            return parts;

        progress?.Invoke($"Reading {caspEntries.Count} CAS part(s)");
        int failed = 0;

        foreach (var entry in caspEntries)
        {
            cancel.ThrowIfCancellationRequested();
            var data = package.TryRead(entry, out var readError);
            if (data == null)
            {
                result.Warnings.Add($"CAS part {entry.KeyString} could not be read: {readError}");
                failed++;
                continue;
            }

            if (CasPartReader.TryParse(data, out var part, out var parseError))
            {
                if (string.IsNullOrWhiteSpace(part.Name))
                    part.Name = $"Part {entry.Instance:X16}";
                parts.Add(part);
            }
            else
            {
                failed++;
                result.Warnings.Add($"CAS part {entry.KeyString} could not be parsed: {parseError}");
            }
        }

        if (failed > 0 && parts.Count == 0)
        {
            result.Warnings.Add(
                "No CAS part could be parsed, so texture roles were guessed from the resource type. " +
                "Check each row before extracting.");
        }

        return parts;
    }

    private static void AddMeshes(DbpfPackage package, List<SimsCasPart> parts,
        SimsScanResult result, Action<string>? progress, CancellationToken cancel)
    {
        var geomEntries = package.Entries.Where(e => e.Type == SimsResourceType.Geom).ToList();
        if (geomEntries.Count == 0)
            return;

        progress?.Invoke($"Reading {geomEntries.Count} mesh(es)");

        // Which CAS part each mesh belongs to (for naming) and which detail level it is.
        var owners = new Dictionary<SimsResourceKey, string>();
        var levels = new Dictionary<SimsResourceKey, int>();
        foreach (var part in parts)
        {
            foreach (var key in part.Meshes)
                owners.TryAdd(key, part.Name);

            foreach (var lod in part.Lods)
                foreach (var key in lod.Meshes)
                {
                    owners.TryAdd(key, part.Name);
                    // Several parts can share a mesh; the lowest level claimed wins, so a
                    // mesh reused as LOD 0 by one part is not demoted by another.
                    if (!levels.TryGetValue(key, out var existing) || lod.Level < existing)
                        levels[key] = lod.Level;
                }
        }

        int unlevelled = 0;
        var parsedMeshes = new List<SimsGeomMesh>();

        foreach (var entry in geomEntries)
        {
            cancel.ThrowIfCancellationRequested();
            var data = package.TryRead(entry, out var readError);
            if (data == null)
            {
                result.Warnings.Add($"Mesh {entry.KeyString} could not be read: {readError}");
                continue;
            }

            SimsGeomMesh mesh;
            try
            {
                mesh = SimsGeom.Parse(data);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Mesh {entry.KeyString} could not be parsed: {ex.Message}");
                continue;
            }

            parsedMeshes.Add(mesh);

            var key = new SimsResourceKey(entry.Type, entry.Group, entry.Instance);
            owners.TryGetValue(key, out var partName);

            int level = levels.TryGetValue(key, out var found) ? found : SimsAsset.UnknownLod;
            if (level == SimsAsset.UnknownLod)
                unlevelled++;

            result.Assets.Add(new SimsAsset
            {
                Kind        = SimsAssetKind.Mesh,
                Entry       = entry,
                PartName    = partName ?? string.Empty,
                VertexCount = mesh.VertexCount,
                FaceCount   = mesh.FaceCount,
                LodLevel    = level,
            });
        }

        if (unlevelled > 0)
        {
            result.Warnings.Add(
                $"{unlevelled} mesh(es) are not listed by any CAS part, so their detail level is unknown. " +
                "They are grouped under \"LOD ?\".");
        }

        // Detected from every mesh at once: they share one UV rescale, so a crop is only
        // safe if all of them sit in the same half.
        result.SuggestedCrop = SimsHalfCrop.Detect(parsedMeshes);

        result.AvailableLods = result.Assets
            .Where(a => a.Kind == SimsAssetKind.Mesh)
            .Select(a => a.LodLevel)
            .Distinct()
            .OrderBy(l => l == SimsAsset.UnknownLod ? int.MaxValue : l)
            .ToList();
    }

    private static void AddTextures(DbpfPackage package, List<SimsCasPart> parts,
        SimsScanResult result, Action<string>? progress, CancellationToken cancel)
    {
        // Roles claimed by CAS parts win over the resource-type guess.
        var roles = new Dictionary<SimsResourceKey, (SimsTextureRole Role, string PartName)>();
        foreach (var part in parts)
            foreach (var (key, role) in part.TextureKeys())
                roles.TryAdd(key, (role, part.Name));

        var textureEntries = package.Entries.Where(e => SimsResourceType.IsTexture(e.Type)).ToList();
        if (textureEntries.Count == 0)
            return;

        progress?.Invoke($"Decoding {textureEntries.Count} texture(s)");

        foreach (var entry in textureEntries)
        {
            cancel.ThrowIfCancellationRequested();
            var key = new SimsResourceKey(entry.Type, entry.Group, entry.Instance);
            bool known = roles.TryGetValue(key, out var claimed);

            var asset = new SimsAsset
            {
                Kind            = SimsAssetKind.Texture,
                Entry           = entry,
                Role            = known ? claimed.Role : RoleFromResourceType(entry.Type),
                PartName        = known ? claimed.PartName : string.Empty,
                RoleFromCasPart = known,
            };

            // Region maps carry no visual detail, and the Sims specular does not map onto
            // anything FFXIV uses — its shaders take a mask/specular built differently.
            // Both are still listed and can be ticked; they just are not on by default.
            if (asset.Role is SimsTextureRole.RegionMap or SimsTextureRole.Specular)
                asset.Selected = false;

            var data = package.TryRead(entry, out var readError);
            if (data == null)
            {
                asset.Error    = readError;
                asset.Selected = false;
                result.Assets.Add(asset);
                continue;
            }

            try
            {
                var decoded = SimsTextureDecoder.Decode(entry.Type, data);
                asset.Width        = decoded.Width;
                asset.Height       = decoded.Height;
                asset.SourceFormat = decoded.SourceFormat;
                asset.ThumbnailPng = decoded.ToThumbnailPng(ThumbnailSize);
            }
            catch (Exception ex)
            {
                asset.Error    = ex.Message;
                asset.Selected = false;
            }

            result.Assets.Add(asset);
        }

        // Diffuse first, then the other roles — the diffuse swatches are the long list.
        result.Assets.Sort((a, b) =>
        {
            if (a.Kind != b.Kind) return a.Kind == SimsAssetKind.Mesh ? -1 : 1;

            if (a.Kind == SimsAssetKind.Mesh)
            {
                int byLod = LodOrder(a.LodLevel).CompareTo(LodOrder(b.LodLevel));
                return byLod != 0 ? byLod : b.VertexCount.CompareTo(a.VertexCount);
            }

            int byRole = RoleOrder(a.Role).CompareTo(RoleOrder(b.Role));
            return byRole != 0 ? byRole : string.CompareOrdinal(a.PartName, b.PartName);
        });
    }

    /// <summary>
    /// The fallback classification, used when no CAS part named the texture. It follows
    /// how the game normally stores each role, but packages do deviate — an RLE2 is
    /// nearly always a diffuse, while a plain _IMG could be either a normal or a diffuse.
    /// </summary>
    private static SimsTextureRole RoleFromResourceType(uint type) => type switch
    {
        SimsResourceType.Rle2      => SimsTextureRole.Diffuse,
        SimsResourceType.Rles      => SimsTextureRole.Specular,
        SimsResourceType.ImgDds    => SimsTextureRole.Normal,
        SimsResourceType.ImgOverlay => SimsTextureRole.Diffuse,
        _                          => SimsTextureRole.Unknown,
    };

    /// <summary>Unknown levels sort last, everything else by level.</summary>
    private static int LodOrder(int level) => level == SimsAsset.UnknownLod ? int.MaxValue : level;

    private static int RoleOrder(SimsTextureRole role) => role switch
    {
        SimsTextureRole.Diffuse   => 0,
        SimsTextureRole.Normal    => 1,
        SimsTextureRole.Specular  => 2,
        SimsTextureRole.Shadow    => 3,
        SimsTextureRole.Emission  => 4,
        SimsTextureRole.RegionMap => 5,
        _                         => 6,
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Extraction
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Where the extracted files landed, so the UI can offer to wire them up.</summary>
    public sealed class ExtractionResult
    {
        public string RootFolder     = string.Empty;
        public string ModelFolder    = string.Empty;
        public string DiffuseFolder  = string.Empty;
        public string? NormalFile;
        public string? SpecularFile;

        /// <summary>Highest-detail model written, shown in the result panel.</summary>
        public string? ModelFile;
        public int ModelFileLod = int.MaxValue;
        public int ModelCount;

        public int DiffuseCount;
        public int Extracted;
        public int Failed;

        /// <summary>The crop that was applied, for the result panel to describe.</summary>
        public SimsHalfCrop Crop;

        /// <summary>Every file this run wrote, report included; the result list is built from these.</summary>
        public List<string> WrittenFiles = new();

        /// <summary>Every texture written, with the role and CAS part it was written for; the stash sorts by these.</summary>
        public List<(string File, SimsTextureRole Role, string Part)> Textures = new();

        /// <summary>Every file this run created or started to, failed ones included; goes into the manifest.</summary>
        internal readonly List<string> OwnedFiles = new();

        public string Report = string.Empty;
    }

    /// <summary>
    /// Lists the files an extraction wrote into its folder, with the package they came
    /// from. The next extraction of the same package deletes exactly these and nothing
    /// else, so files from an earlier run cannot mix into the new one.
    /// </summary>
    internal const string ManifestName = "extract-files.txt";
    private const string ManifestSource = "source=";

    /// <summary>
    /// Writes the ticked assets under <paramref name="outputRoot"/> in the layout the
    /// material editor consumes: one folder of diffuse images to point a variant slot
    /// at, and single files for the remaining roles.
    ///
    /// Each run starts from a folder holding nothing but its own earlier output: the files
    /// a previous extraction of this same package listed in its manifest are deleted first.
    /// When the folder holds anything else (another package with the same file name, files
    /// the user put there, or output from before manifests existed) nothing is deleted and
    /// a fresh numbered folder is used instead, so the diffuse variant folder only ever
    /// holds this run's swatches.
    /// </summary>
    public static ExtractionResult Extract(SimsScanResult scan, string outputRoot,
        SimsHalfCrop crop = default, Action<string>? progress = null, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        var packageName = MakeSafeName(Path.GetFileNameWithoutExtension(scan.PackagePath), "sims_package");
        var root = ClaimOutputFolder(outputRoot, packageName, scan.PackagePath, out bool newFolder);

        var result = new ExtractionResult
        {
            RootFolder    = root,
            ModelFolder   = Path.Combine(root, "model"),
            DiffuseFolder = Path.Combine(root, "textures", "diffuse"),
        };

        try
        {
            Directory.CreateDirectory(result.ModelFolder);
            Directory.CreateDirectory(result.DiffuseFolder);

            var report = new StringBuilder();
            report.AppendLine($"Source package: {scan.PackagePath}");
            report.AppendLine($"Extracted:      {DateTime.Now:yyyy-MM-dd HH:mm}");
            if (newFolder)
                report.AppendLine($"Folder:         {packageName} holds other files, so this run went into {Path.GetFileName(root)}.");
            if (crop.Crops)
            {
                report.AppendLine($"Half crop:      keeping the {crop.Describe()}; textures are halved " +
                                   "and UV0 rescaled to match.");
            }
            report.AppendLine();

            using var package = new DbpfPackage(scan.PackagePath);

            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int extracted = 0, failed = 0;

            foreach (var asset in scan.Assets.Where(a => a.Selected))
            {
                // Stops between files; the manifest (finally, below) still lists what was written.
                cancel.ThrowIfCancellationRequested();
                try
                {
                    if (asset.Kind == SimsAssetKind.Mesh)
                        ExtractMesh(package, asset, result, packageName, usedNames, report, crop, progress);
                    else
                        ExtractTexture(package, asset, result, root, usedNames, report, crop, progress);
                    extracted++;
                }
                catch (Exception ex)
                {
                    failed++;
                    report.AppendLine($"  X failed:  {asset.Entry.KeyString} — {ex.Message}");
                }
            }

            result.Extracted = extracted;
            result.Failed    = failed;
            result.Crop      = crop;

            if (scan.Warnings.Count > 0)
            {
                report.AppendLine();
                report.AppendLine("Warnings from the scan:");
                foreach (var warning in scan.Warnings)
                    report.AppendLine($"  - {warning}");
            }

            report.AppendLine();
            report.AppendLine("The .glb is an intermediate, not a finished FFXIV model: open it in Blender,");
            report.AppendLine("fit it to the target body, and export a .mdl before setting it as a race model.");

            result.Report = report.ToString();
            var reportPath = Path.Combine(root, "extract-report.txt");
            WriteOwned(result, reportPath, () => File.WriteAllText(reportPath, result.Report));

            // What the import window's stash lists; owned like the rest, so the next run replaces it.
            var stashPath = Path.Combine(root, Stash.StashManifest.FileName);
            WriteOwned(result, stashPath, () => Stash.StashManifest.ForSims(scan.PackagePath, result).Write(root));
        }
        finally
        {
            // Written even when the run fails part-way, so whatever it did write is still
            // known to be ours next time.
            WriteManifest(root, scan.PackagePath, result.OwnedFiles);
        }

        return result;
    }

    /// <summary>Records <paramref name="path"/> as ours before writing it, so a half-written file is cleaned up too.</summary>
    private static void WriteOwned(ExtractionResult result, string path, Action write)
    {
        result.OwnedFiles.Add(path);
        write();
        result.WrittenFiles.Add(path);
    }

    /// <summary>
    /// Picks the folder this run writes into: <c>outputRoot/name</c> when it is new or
    /// holds only this package's earlier output (which is deleted), otherwise the first
    /// <c>name_2</c>, <c>name_3</c> and so on that qualifies.
    /// </summary>
    private static string ClaimOutputFolder(string outputRoot, string name, string packagePath, out bool newFolder)
    {
        var source = Path.GetFullPath(packagePath);
        for (int n = 1; n < 1000; n++)
        {
            var folder = Path.GetFullPath(Path.Combine(outputRoot, n == 1 ? name : $"{name}_{n}"));
            if (!Directory.Exists(folder) || TryReuseFolder(folder, source))
            {
                newFolder = n > 1;
                return folder;
            }
        }

        throw new IOException($"No free output folder for {name} in {outputRoot}.");
    }

    /// <summary>
    /// Clears <paramref name="folder"/> for a new run when every file in it is one an
    /// earlier extraction of <paramref name="source"/> listed in its manifest. Returns false
    /// (deleting nothing) when the manifest names another package or any other file is
    /// there.
    /// </summary>
    private static bool TryReuseFolder(string folder, string source)
    {
        try
        {
            var prefix = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
            var manifestPath = Path.Combine(folder, ManifestName);
            var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (File.Exists(manifestPath))
            {
                var lines = File.ReadAllLines(manifestPath);
                var from = lines.FirstOrDefault(l => l.StartsWith(ManifestSource, StringComparison.Ordinal));
                if (from == null ||
                    !string.Equals(Path.GetFullPath(from[ManifestSource.Length..]), source, StringComparison.OrdinalIgnoreCase))
                    return false;

                foreach (var line in lines)
                {
                    if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(ManifestSource, StringComparison.Ordinal) ||
                        Path.IsPathRooted(line))
                        continue;
                    var path = Path.GetFullPath(Path.Combine(folder, line));
                    if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        owned.Add(path);
                }
                owned.Add(manifestPath);
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                if (!owned.Contains(Path.GetFullPath(file)))
                    return false;

            foreach (var file in owned)
                if (File.Exists(file))
                    File.Delete(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static void WriteManifest(string root, string packagePath, IEnumerable<string> files)
    {
        try
        {
            var lines = new List<string>
            {
                "# Files Instant Edit wrote here. Extracting the same package again deletes these, and only these.",
                ManifestSource + Path.GetFullPath(packagePath),
            };
            lines.AddRange(files.Select(f => Path.GetRelativePath(root, f)));
            File.WriteAllLines(Path.Combine(root, ManifestName), lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without a manifest the next run treats these files as someone else's and
            // uses a fresh folder, which is safe.
        }
    }

    private static void ExtractMesh(DbpfPackage package, SimsAsset asset, ExtractionResult result,
        string packageName, HashSet<string> usedNames, StringBuilder report, SimsHalfCrop crop,
        Action<string>? progress)
    {
        progress?.Invoke($"Exporting mesh {asset.Entry.Instance:X16}");

        var data = package.Read(asset.Entry);
        var mesh = SimsGeom.Parse(data);

        var stem  = MakeSafeName(string.IsNullOrWhiteSpace(asset.PartName) ? packageName : asset.PartName);
        var level = asset.LodLevel == SimsAsset.UnknownLod ? "lodx" : $"lod{asset.LodLevel}";
        var name  = UniqueName(usedNames, $"{stem}_{level}", ".glb");
        var path  = Path.Combine(result.ModelFolder, name);

        int skippedFaces = 0;
        WriteOwned(result, path, () => skippedFaces = SimsMeshExporter.ExportGlb(mesh, path, Path.GetFileNameWithoutExtension(name), crop));

        // The path shown afterwards should be the highest detail one, whatever order
        // the assets happened to be extracted in.
        // LodOrder, not the raw level: UnknownLod is negative and would otherwise beat LOD 0.
        if (result.ModelFile == null || LodOrder(asset.LodLevel) < result.ModelFileLod)
        {
            result.ModelFile    = path;
            result.ModelFileLod = LodOrder(asset.LodLevel);
        }
        result.ModelCount++;

        report.AppendLine($"  ok:       model/{name}  ({mesh.VertexCount:N0} verts, {mesh.FaceCount - skippedFaces:N0} tris" +
                          (skippedFaces > 0 ? $", {skippedFaces:N0} with a bad vertex index left out)" : ")"));
    }

    private static void ExtractTexture(DbpfPackage package, SimsAsset asset, ExtractionResult result,
        string root, HashSet<string> usedNames, StringBuilder report, SimsHalfCrop crop,
        Action<string>? progress)
    {
        progress?.Invoke($"Writing texture {asset.Entry.Instance:X16}");

        var data    = package.Read(asset.Entry);
        var decoded = SimsTextureDecoder.Decode(asset.Entry.Type, data);

        var original = $"{decoded.Width}x{decoded.Height}";
        bool lostContent = crop.Crops && SimsTextureCrop.DiscardedHalfHasContent(decoded, crop);
        decoded = SimsTextureCrop.Apply(decoded, crop);
        string cropNote = crop.Crops
            ? $", cropped from {original}" +
              (lostContent ? " — the discarded half held content no mesh references" : string.Empty)
            : string.Empty;

        var folderName = asset.Role switch
        {
            SimsTextureRole.Diffuse  => "diffuse",
            SimsTextureRole.Normal   => "normal",
            SimsTextureRole.Specular => "specular",
            _                        => "other",
        };
        var folder = Path.Combine(root, "textures", folderName);
        Directory.CreateDirectory(folder);

        var stem = MakeSafeName(string.IsNullOrWhiteSpace(asset.PartName)
            ? $"{folderName}_{asset.Entry.Instance:X16}"
            : asset.PartName);
        var name = UniqueName(usedNames, $"{stem}_{folderName}", ".png");
        var path = Path.Combine(folder, name);

        var png = decoded.ToPng();
        WriteOwned(result, path, () => File.WriteAllBytes(path, png));
        result.Textures.Add((path, asset.Role, asset.PartName));

        switch (asset.Role)
        {
            case SimsTextureRole.Diffuse:
                result.DiffuseCount++;
                break;
            case SimsTextureRole.Normal:
                result.NormalFile ??= path;
                break;
            case SimsTextureRole.Specular:
                result.SpecularFile ??= path;
                break;
        }

        report.AppendLine($"  ok:       textures/{folderName}/{name}  " +
                          $"({decoded.Width}x{decoded.Height}, {decoded.SourceFormat}{cropNote})");
    }

    private static string UniqueName(HashSet<string> used, string stem, string extension)
    {
        var name = stem + extension;
        int counter = 2;
        while (!used.Add(name))
            name = $"{stem}_{counter++}{extension}";
        return name;
    }

    /// <summary>
    /// Longest file or folder stem taken from a package or CAS part name. CAS names run to
    /// 2048 characters; suffixes such as "_diffuse_12.png" still have to fit in the
    /// 255-character limit for one path component, with room left for the full path.
    /// </summary>
    internal const int MaxNameLength = 80;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Turns an untrusted name from the package into one safe file or folder name: invalid
    /// characters and whitespace become underscores, leading and trailing dots go (so "."
    /// and ".." can never point at the folder itself or its parent, and Windows would drop
    /// trailing ones anyway), device names such as CON get a prefix, and the result is
    /// capped at <see cref="MaxNameLength"/>.
    /// </summary>
    internal static string MakeSafeName(string name, string fallback = "sims_part")
    {
        if (string.IsNullOrWhiteSpace(name))
            return fallback;

        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim()
            .Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c)
            .ToArray();

        var safe = new string(chars).Trim('_', '.');
        if (safe.Length > MaxNameLength)
        {
            int cut = char.IsHighSurrogate(safe[MaxNameLength - 1]) ? MaxNameLength - 1 : MaxNameLength;
            safe = safe[..cut].TrimEnd('_', '.');
        }

        if (safe.Length == 0)
            return fallback;

        // Windows reserves device names even with an extension after them ("CON.txt").
        if (ReservedNames.Contains(safe.Split('.')[0]))
            safe = "_" + safe;
        return safe;
    }
}
