using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
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

    /// <summary>A hair mesh the game uses under hats, with the top cut off; not ticked by default.</summary>
    public bool HatVersion;

    /// <summary>
    /// The hairstyle the asset belongs to (its hair CAS parts share meshes: one part per colour
    /// swatch), or <see cref="NoHairStyle"/>. Only a hairstyle's textures are cut to its part of the
    /// sheet and get an alpha map.
    /// </summary>
    public int HairStyle = NoHairStyle;

    public const int NoHairStyle = -1;

    // Textures
    public int Width;
    public int Height;
    public string SourceFormat = string.Empty;

    /// <summary>PNG bytes of a small preview, decoded during the scan.</summary>
    public byte[]? ThumbnailPng;

    /// <summary>Set when this asset could not be decoded; it is listed but cannot be extracted.</summary>
    public string? Error;

    public string Describe() => Kind == SimsAssetKind.Mesh
        ? $"{VertexCount:N0} verts / {FaceCount:N0} tris{(HatVersion ? " · hat version" : "")}"
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

    /// <summary>True when a CAS part is a hair: its hat versions and all but one diffuse start unticked.</summary>
    public bool IsHair;

    /// <summary>Detail levels present in this package, ascending; drives the LOD picker.</summary>
    public List<int> AvailableLods = new();

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
        result.IsHair = parts.Any(p => p.IsHair);
        var styles = HairStyles(parts);

        AddMeshes(package, parts, styles, result, progress, cancel);
        AddTextures(package, parts, styles, result, progress, cancel);
        SelectDefaults(result);

        if (result.Assets.Count == 0)
            result.Warnings.Add("No meshes or textures were found in this package.");

        return result;
    }

    /// <summary>
    /// Narrows what starts ticked to what an FFXIV port actually needs: the LOD 0 meshes
    /// only, since the lower levels are the game's reduced-detail copies, and of a hair not
    /// its hat versions. A package with no LOD 0 keeps every level ticked rather than
    /// silently offering nothing. Textures have already had the roles a port cannot use
    /// unticked as they were listed.
    /// </summary>
    private static void SelectDefaults(SimsScanResult result)
    {
        bool hasLod0 = result.AvailableLods.Contains(0);
        foreach (var asset in result.Meshes)
            asset.Selected = (!hasLod0 || asset.LodLevel == 0) && !asset.HatVersion;
    }

    /// <summary>
    /// The hairstyle of each hair part: parts that share a mesh are one style, as a hair's colour
    /// swatches are (one CAS part each, every one listing the same meshes).
    /// </summary>
    private static Dictionary<SimsCasPart, int> HairStyles(List<SimsCasPart> parts)
    {
        var styles = new Dictionary<SimsCasPart, int>();
        var styleOfMesh = new Dictionary<SimsResourceKey, int>();
        var merged = new List<int>();   // each style's representative, for styles joined by a later part
        int Find(int style) { while (merged[style] != style) style = merged[style]; return style; }

        foreach (var part in parts.Where(p => p.IsHair))
        {
            var keys = part.Meshes.Concat(part.Lods.SelectMany(l => l.Meshes)).Distinct().ToList();
            var joined = keys.Where(styleOfMesh.ContainsKey).Select(k => Find(styleOfMesh[k])).Distinct().ToList();
            int style;
            if (joined.Count == 0)
            {
                style = merged.Count;
                merged.Add(style);
            }
            else
            {
                style = joined.Min();
                foreach (var other in joined)
                    merged[other] = style;
            }
            styles[part] = style;
            foreach (var key in keys)
                styleOfMesh.TryAdd(key, style);
        }

        // Numbered 0, 1, ... in the order the styles first appear.
        var numbers = new Dictionary<int, int>();
        foreach (var part in styles.Keys.ToList())
        {
            var root = Find(styles[part]);
            if (!numbers.TryGetValue(root, out var number))
                numbers[root] = number = numbers.Count;
            styles[part] = number;
        }
        return styles;
    }

    /// <summary>
    /// The hairstyle of a mesh or texture: that of the first hair part listing it. One no part lists
    /// belongs to the package's hairstyle when it has just one; one only another kind of part lists, to none.
    /// </summary>
    private static int StyleOf(SimsResourceKey key, List<SimsCasPart> parts, Dictionary<SimsCasPart, int> styles,
        Func<SimsCasPart, IEnumerable<SimsResourceKey>> keysOf)
    {
        bool listed = false;
        foreach (var part in parts)
        {
            if (!keysOf(part).Contains(key))
                continue;
            if (styles.TryGetValue(part, out var style))
                return style;
            listed = true;
        }
        var count = styles.Values.Distinct().Count();
        return !listed && count == 1 ? styles.Values.First() : SimsAsset.NoHairStyle;
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

    private static void AddMeshes(DbpfPackage package, List<SimsCasPart> parts, Dictionary<SimsCasPart, int> styles,
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
        var shapes = new Dictionary<SimsResourceKey, (uint Shader, int Vertices, float Top)>();
        var meshAssets = new Dictionary<SimsResourceKey, SimsAsset>();

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

            var key = new SimsResourceKey(entry.Type, entry.Group, entry.Instance);
            owners.TryGetValue(key, out var partName);

            int level = levels.TryGetValue(key, out var found) ? found : SimsAsset.UnknownLod;
            if (level == SimsAsset.UnknownLod)
                unlevelled++;

            var asset = new SimsAsset
            {
                Kind        = SimsAssetKind.Mesh,
                Entry       = entry,
                PartName    = partName ?? string.Empty,
                VertexCount = mesh.VertexCount,
                FaceCount   = mesh.FaceCount,
                LodLevel    = level,
                HairStyle   = StyleOf(key, parts, styles, p => p.Meshes.Concat(p.Lods.SelectMany(l => l.Meshes))),
            };
            result.Assets.Add(asset);
            shapes[key] = (mesh.ShaderHash, mesh.VertexCount, mesh.VertexCount > 0 ? mesh.Positions.Max(p => p.Y) : 0f);
            meshAssets[key] = asset;
        }

        MarkHatVersions(parts, shapes, meshAssets);

        if (unlevelled > 0)
        {
            result.Warnings.Add(
                $"{unlevelled} mesh(es) are not listed by any CAS part, so their detail level is unknown. " +
                "They are grouped under \"LOD ?\".");
        }

        result.AvailableLods = result.Assets
            .Where(a => a.Kind == SimsAssetKind.Mesh)
            .Select(a => a.LodLevel)
            .Distinct()
            .OrderBy(l => l == SimsAsset.UnknownLod ? int.MaxValue : l)
            .ToList();
    }

    /// <summary>
    /// Marks the hat versions of every hair part's detail levels (see <see cref="SimsHairDefaults"/>).
    /// A mesh one part uses in full stays unmarked even when another part has it as a hat version.
    /// </summary>
    private static void MarkHatVersions(List<SimsCasPart> parts, Dictionary<SimsResourceKey, (uint Shader, int Vertices, float Top)> shapes,
        Dictionary<SimsResourceKey, SimsAsset> meshAssets)
    {
        var hat = new HashSet<SimsResourceKey>();
        var full = new HashSet<SimsResourceKey>();
        foreach (var part in parts.Where(p => p.IsHair))
            foreach (var lod in part.Lods)
            {
                var meshes = lod.Meshes.Distinct().Where(shapes.ContainsKey)
                    .Select(k => (k, shapes[k].Shader, shapes[k].Vertices, shapes[k].Top)).ToList();
                var cut = SimsHairDefaults.HatVersions(meshes);
                hat.UnionWith(cut);
                full.UnionWith(meshes.Select(m => m.k).Where(k => !cut.Contains(k)));
            }
        foreach (var key in hat.Except(full))
            meshAssets[key].HatVersion = true;
    }

    private static void AddTextures(DbpfPackage package, List<SimsCasPart> parts, Dictionary<SimsCasPart, int> styles,
        SimsScanResult result, Action<string>? progress, CancellationToken cancel)
    {
        // Roles claimed by CAS parts win over the resource-type guess.
        var roles = new Dictionary<SimsResourceKey, (SimsTextureRole Role, string PartName)>();
        var hairDiffuses = new HashSet<SimsResourceKey>();
        foreach (var part in parts)
            foreach (var (key, role) in part.TextureKeys())
            {
                roles.TryAdd(key, (role, part.Name));
                if (part.IsHair && role == SimsTextureRole.Diffuse)
                    hairDiffuses.Add(key);
            }
        var measured = new List<(SimsAsset Asset, float Lightness, float Saturation)>();
        var textureKeys = parts.ToDictionary(part => part, part => part.TextureKeys().Select(t => t.Key).ToHashSet());

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
                HairStyle       = StyleOf(key, parts, styles, part => textureKeys[part]),
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
                if (hairDiffuses.Contains(key) && asset.Role == SimsTextureRole.Diffuse)
                {
                    var (lightness, saturation) = SimsHairDefaults.Measure(decoded.Rgba);
                    measured.Add((asset, lightness, saturation));
                }
            }
            catch (Exception ex)
            {
                asset.Error    = ex.Message;
                asset.Selected = false;
            }

            result.Assets.Add(asset);
        }

        // A hair port wants one diffuse of each hairstyle's swatches, to turn into its normal texture.
        foreach (var style in measured.GroupBy(m => m.Asset.HairStyle))
        {
            var swatches = style.ToList();
            int keep = SimsHairDefaults.PickDiffuse(swatches.Select(m => (m.Lightness, m.Saturation)).ToList());
            for (int i = 0; i < swatches.Count; i++)
                swatches[i].Asset.Selected = i == keep;
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
    /// Each run starts from an empty folder: a previous extraction of this same package (every
    /// file in it listed in its manifest) is set aside, and only deleted once this run finishes;
    /// a run that stops or fails is removed and the previous one put back. When the folder holds
    /// anything else (another package with the same file name, files the user put there, or
    /// output from before manifests existed) nothing is touched and a fresh numbered folder is
    /// used instead, so the diffuse variant folder only ever holds this run's swatches.
    /// </summary>
    public static ExtractionResult Extract(SimsScanResult scan, string outputRoot,
        Action<string>? progress = null, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        var packageName = MakeSafeName(Path.GetFileNameWithoutExtension(scan.PackagePath), "sims_package");
        var source = Path.GetFullPath(scan.PackagePath);
        var claim = Stash.ExtractionFolder.Claim(outputRoot, packageName, folder => IsOwnRun(folder, source));
        bool finished = false;
        try
        {
            var extraction = Write(scan, claim.Path, packageName, claim.Numbered, progress, cancel);
            finished = true;
            return extraction;
        }
        finally
        {
            if (finished) claim.Finish();
            else          claim.Abandon();
        }
    }

    private static ExtractionResult Write(SimsScanResult scan, string root, string packageName, bool newFolder,
        Action<string>? progress, CancellationToken cancel)
    {
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
            report.AppendLine();

            using var package = new DbpfPackage(scan.PackagePath);

            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int extracted = 0, failed = 0;

            var meshes = new Dictionary<SimsAsset, SimsGeomMesh>();
            var regions = HairRegions(package, scan, meshes, report, cancel);

            foreach (var asset in scan.Assets.Where(a => a.Selected))
            {
                // Stops between files; the manifest (finally, below) still lists what was written.
                cancel.ThrowIfCancellationRequested();
                UvRegion? region = regions.TryGetValue(asset.HairStyle, out var cut) ? cut : null;
                try
                {
                    if (asset.Kind == SimsAssetKind.Mesh)
                        ExtractMesh(package, asset, result, packageName, usedNames, report, progress, meshes, region);
                    else
                        ExtractTexture(package, asset, result, root, usedNames, report, progress,
                            asset.HairStyle != SimsAsset.NoHairStyle, region);
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
            // Written whatever happened, so the folder is known to be ours next time. (A run that
            // stops is removed whole by Extract.)
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
    /// Whether every file in <paramref name="folder"/> is one an earlier extraction of
    /// <paramref name="source"/> listed in its manifest. False when the manifest names another
    /// package or any other file is there.
    /// </summary>
    private static bool IsOwnRun(string folder, string source)
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

    /// <summary>
    /// The part of the texture sheet each hairstyle's meshes use (see <see cref="SimsHairDefaults.HairRegion"/>),
    /// by style. Measured on the style's LOD 0 meshes (all of them, ticked or not; every mesh when it has
    /// no LOD 0) and on its largest texture (or the usual 1024×2048 hair sheet without one), so every
    /// extraction of the package cuts its models and textures the same way. The lower levels are left
    /// out: their UVs often reach a little past the sheet's edge, which would leave nothing to cut. The
    /// ticked meshes read for it are kept for writing. A style without an entry is left as it is.
    /// </summary>
    private static Dictionary<int, UvRegion> HairRegions(DbpfPackage package, SimsScanResult scan, Dictionary<SimsAsset, SimsGeomMesh> meshes,
        StringBuilder report, CancellationToken cancel)
    {
        var regions = new Dictionary<int, UvRegion>();
        var styles = scan.Assets.Where(a => a.HairStyle != SimsAsset.NoHairStyle).GroupBy(a => a.HairStyle).ToList();
        foreach (var style in styles)
        {
            var uvs = new List<Vector2>();
            var styleMeshes = style.Where(a => a.Kind == SimsAssetKind.Mesh).ToList();
            var measuredMeshes = styleMeshes.Any(a => a.LodLevel == 0) ? styleMeshes.Where(a => a.LodLevel == 0) : styleMeshes;
            foreach (var asset in measuredMeshes)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    var mesh = SimsGeom.Parse(package.Read(asset.Entry));
                    if (asset.Selected)
                        meshes[asset] = mesh;
                    uvs.AddRange(SimsMeshExporter.UsedUvs(mesh));
                }
                catch (Exception)
                {
                    // Fails again, and is reported, when the mesh itself is written.
                }
            }

            var sheet = style.Where(a => a.Kind == SimsAssetKind.Texture && a.Width > 0 && a.Height > 0)
                .OrderByDescending(a => (long)a.Width * a.Height).FirstOrDefault();
            int width = sheet?.Width ?? 1024, height = sheet?.Height ?? 2048;
            if (SimsHairDefaults.HairRegion(uvs, width, height) is not { } region)
                continue;
            regions[style.Key] = region;
            var (x, y, w, h) = region.Pixels(width, height);
            var label = styles.Count > 1 ? $"Hair sheet {style.Key + 1}:" : "Hair sheet:";
            report.AppendLine($"{label,-16}only {w}x{h} at ({x}, {y}) of the {width}x{height} sheet is used, so textures are cut");
            report.AppendLine("                down to it and the models' UVs stretched to fill it.");
            report.AppendLine();
        }
        return regions;
    }

    private static void ExtractMesh(DbpfPackage package, SimsAsset asset, ExtractionResult result,
        string packageName, HashSet<string> usedNames, StringBuilder report, Action<string>? progress,
        Dictionary<SimsAsset, SimsGeomMesh> parsed, UvRegion? region)
    {
        progress?.Invoke($"Exporting mesh {asset.Entry.Instance:X16}");

        var mesh = parsed.TryGetValue(asset, out var read) ? read : SimsGeom.Parse(package.Read(asset.Entry));

        var stem  = MakeSafeName(string.IsNullOrWhiteSpace(asset.PartName) ? packageName : asset.PartName);
        var level = asset.LodLevel == SimsAsset.UnknownLod ? "lodx" : $"lod{asset.LodLevel}";
        var name  = UniqueName(usedNames, $"{stem}_{level}", ".glb");
        var path  = Path.Combine(result.ModelFolder, name);

        int skippedFaces = 0;
        WriteOwned(result, path, () => skippedFaces = SimsMeshExporter.ExportGlb(mesh, path, Path.GetFileNameWithoutExtension(name), region));

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

    /// <summary>
    /// Writes one texture as a PNG into the folder of its role. A hair's diffuse also gets its
    /// alpha map, in a folder of its own beside the diffuse folder: that one is a variant
    /// folder, where any image counts as another colour. With a <paramref name="region"/>, only
    /// that part of the sheet is written.
    /// </summary>
    private static void ExtractTexture(DbpfPackage package, SimsAsset asset, ExtractionResult result,
        string root, HashSet<string> usedNames, StringBuilder report, Action<string>? progress, bool hair, UvRegion? region)
    {
        progress?.Invoke($"Writing texture {asset.Entry.Instance:X16}");

        var data    = package.Read(asset.Entry);
        var decoded = SimsTextureDecoder.Decode(asset.Entry.Type, data);
        var sheetSize = $"{decoded.Width}x{decoded.Height}";
        if (region is { } cut)
        {
            decoded.Rgba   = cut.Crop(decoded.Rgba, decoded.Width, decoded.Height, out var croppedWidth, out var croppedHeight);
            decoded.Width  = croppedWidth;
            decoded.Height = croppedHeight;
        }

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
                          $"({decoded.Width}x{decoded.Height}{(region != null ? $", cut from {sheetSize}" : string.Empty)}, {decoded.SourceFormat})");

        if (hair && asset.Role == SimsTextureRole.Diffuse)
        {
            var alphaFolder = Path.Combine(root, "textures", "alpha");
            Directory.CreateDirectory(alphaFolder);
            var alphaName = UniqueName(usedNames, $"{stem}_alpha", ".png");
            var alphaPath = Path.Combine(alphaFolder, alphaName);
            var alphaPng = new DecodedTexture
            {
                Width  = decoded.Width,
                Height = decoded.Height,
                Rgba   = SimsHairDefaults.AlphaMap(decoded.Rgba),
            }.ToPng();
            WriteOwned(result, alphaPath, () => File.WriteAllBytes(alphaPath, alphaPng));
            result.Textures.Add((alphaPath, SimsTextureRole.Unknown, asset.PartName));
            report.AppendLine($"  ok:       textures/alpha/{alphaName}  (the diffuse's alpha map, to lay over a normal)");
        }
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
