using System.Text.Json.Nodes;
using InstantEdit.Models;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace InstantEdit.Services;

public sealed partial class PenumbraService : ITextureEditBackend
{
    async Task ITextureEditBackend.ConvertAsync(string input, string output, TextureType format, bool mipMaps)
    {
        TextureFiles.EnsureLocalPath(input);
        TextureFiles.EnsureLocalPath(output);
        if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Texture conversions require a separate output file.");
        if (File.Exists(output)) File.Delete(output);
        var conversion = new ConvertTextureFile(_pi).Invoke(input, output, format, mipMaps);
        await conversion.ConfigureAwait(false);
    }

    async Task<TextureSource> ITextureEditBackend.CaptureAsync(TextureEditRequest request, CancellationToken token)
    {
        if (!IsSafeGameResourcePath(request.GamePath, ".tex")) throw new IOException("Select a valid TEX game resource.");
        var vanilla = string.IsNullOrEmpty(request.ModDirectory);
        var collection = request.ObjectIndex is { } index ? await GetCollectionTargetAsync(index).ConfigureAwait(false) : null;
        var actor = await _framework.RunOnFrameworkThread(() =>
        {
            var obj = request.ObjectIndex is { } i ? _objects?[i] : null;
            if (request.ObjectIndex.HasValue && (obj is null || obj.Address.ToInt64() != request.ActorAddress))
                throw new IOException("The selected actor changed. Refresh the character list.");
            return (Id: obj?.GameObjectId ?? 0UL, Name: obj?.Name.ToString() ?? "");
        }).ConfigureAwait(false);
        byte[] bytes;
        var modRoot = request.ModRoot;
        var modDirectory = request.ModDirectory;
        var relative = request.RelativePath;
        if (vanilla)
        {
            if (Path.IsPathRooted(request.ActualPath)) throw new IOException("This external file has no verified Penumbra destination.");
            if (!IsSafeNewModName(request.NewModName)) throw new IOException("Enter a valid, unused name for the new texture mod.");
            if (collection is null) throw new IOException("The actor's collection is unavailable. Refresh and try again.");
            var root = await _framework.RunOnFrameworkThread(GetModDirectory).ConfigureAwait(false);
            if (string.IsNullOrEmpty(root)) throw new IOException("Penumbra's mod directory is unavailable.");
            modDirectory = request.NewModName;
            modRoot = Path.Combine(root, modDirectory);
            relative = "Files/" + request.GamePath;
            TextureFiles.EnsureLocalPath(modRoot);
            await EnsureNewTextureModNameAsync(modDirectory, modRoot).ConfigureAwait(false);
            bytes = await _framework.RunOnFrameworkThread(() => _data?.GetFile(request.GamePath)?.Data.ToArray()
                ?? throw new IOException("The vanilla texture could not be read.")).ConfigureAwait(false);
        }
        else
        {
            await ValidateTextureDestinationAsync(modDirectory, modRoot, relative, request.ActualPath, token).ConfigureAwait(false);
            bytes = TextureFiles.Read(request.ActualPath);
        }
        var header = TextureFiles.ReadTex(bytes);
        var session = new TextureEditSession
        {
            GamePath = request.GamePath, ModDirectory = modDirectory, ModRoot = modRoot, RelativePath = relative,
            NewModName = request.NewModName, NeedsMod = vanilla, SetupPending = vanilla,
            ObjectIndex = request.ObjectIndex, ActorAddress = request.ActorAddress, ActorId = actor.Id, ActorName = actor.Name,
            CollectionId = collection?.Id, CollectionName = collection?.Name ?? "",
            Format = header.Format, Width = header.Width, Height = header.Height, MipMaps = header.Mips > 1,
            LastCommittedHash = vanilla ? "" : TextureFiles.Hash(bytes),
            MappingFingerprint = vanilla ? "" : TextureMappingFingerprint(modRoot),
        };
        await ValidateLiveTextureMappingAsync(session, vanilla ? request.GamePath : request.ActualPath, true).ConfigureAwait(false);
        return new TextureSource(bytes, session);
    }

    private async Task EnsureNewTextureModNameAsync(string name, string path)
    {
        var exists = await _framework.RunOnFrameworkThread(() => GetMods().Any(m =>
            string.Equals(m.Directory, name, StringComparison.OrdinalIgnoreCase) || string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))).ConfigureAwait(false);
        if (exists || Directory.Exists(path) || File.Exists(path)) throw new TextureConflictException("That mod name is already in use. Start a session with a different name.");
    }

    private async Task ValidateTextureDestinationAsync(string mod, string root, string relative, string file, CancellationToken token)
    {
        if (!IsSafeModName(mod) || !IsSafeGameResourcePath(relative, ".tex")) throw new IOException("Invalid texture destination.");
        TextureFiles.EnsureLocalPath(root);
        TextureFiles.EnsureLocalPath(file);
        if (!PathRules.IsPathWithin(file, root) || !string.Equals(Path.GetFullPath(Path.Combine(root, relative)), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
            throw new TextureConflictException("The texture no longer belongs to its captured mod directory.");
        token.ThrowIfCancellationRequested();
        var registered = await _framework.RunOnFrameworkThread(() => ResolveModScanOnFramework(mod)).ConfigureAwait(false);
        if (registered is null || !registered.CandidateRoots.Any(candidate =>
                string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) ||
                (Path.GetFileName(candidate).Equals("Files", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(Path.GetDirectoryName(Path.GetFullPath(candidate)), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))) ||
            !File.Exists(file))
            throw new TextureConflictException("The texture's mod moved, was removed, or is no longer available. Reopen it from the browser.");
    }

    private bool MatchesTextureActor(TextureEditSession session)
    {
        var obj = session.ObjectIndex is { } i ? _objects?[i] : null;
        return obj is not null && obj.Address.ToInt64() == session.ActorAddress && obj.GameObjectId == session.ActorId &&
            obj.Name.ToString() == session.ActorName;
    }

    private async Task ValidateLiveTextureMappingAsync(TextureEditSession session, string expected, bool requireActor)
    {
        if (!session.ObjectIndex.HasValue) return;
        await _framework.RunOnFrameworkThread(() =>
        {
            if (!MatchesTextureActor(session))
            {
                if (requireActor) throw new TextureConflictException("The selected actor changed. Refresh and reopen the texture.");
                return;
            }
            var paths = GetResourcePaths((ushort)session.ObjectIndex.Value);
            if (paths is null || !paths.Any(p => string.Equals(p.Key, expected, StringComparison.OrdinalIgnoreCase) &&
                    p.Value.Contains(session.GamePath, StringComparer.OrdinalIgnoreCase)))
                throw new TextureConflictException("The actor's texture mapping changed. Refresh and reopen the texture.");
        }).ConfigureAwait(false);
    }

    internal static string TextureMappingFingerprint(string root)
    {
        // Hash the complete v4 document so option renames, reordering, and mapping edits invalidate the session.
        _ = LoadV4ModMetadata(root);
        var path = Path.Combine(root, "meta.json");
        return TextureFiles.Hash(TextureFiles.Read(path));
    }

    async Task<TextureCommit> ITextureEditBackend.CommitAsync(TextureEditSession session, byte[] tex, Func<bool> stillCurrent, CancellationToken token, bool restoring)
    {
        TextureFiles.ValidateOutput(tex, session, restoring);
        await _exportGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            string backup = "";
            if (session.NeedsMod)
            {
                await EnsureNewTextureModNameAsync(session.ModDirectory, session.ModRoot).ConfigureAwait(false);
                await ValidateLiveTextureMappingAsync(session, session.GamePath, false).ConfigureAwait(false);
                TextureFiles.EnsureLocalPath(session.ModRoot);
                var staging = session.ModRoot + $".instant-edit-{session.Id:N}.tmp";
                TextureFiles.EnsureLocalPath(staging);
                if (Directory.Exists(staging)) throw new IOException("A previous staging directory remains; open its parent folder to inspect it.");
                Directory.CreateDirectory(staging);
                try
                {
                    StageGameTextureMod(staging, session.ModDirectory, session.GamePath, tex);
                    token.ThrowIfCancellationRequested();
                    if (!stillCurrent()) throw new OperationCanceledException("A newer save is pending.");
                    var mappingFingerprint = TextureMappingFingerprint(staging);
                    Directory.Move(staging, session.ModRoot);
                    session.NeedsMod = false;
                    session.MappingFingerprint = mappingFingerprint;
                }
                finally
                {
                    // This unique staging directory contains only files produced by this call.
                    if (Directory.Exists(staging)) DeleteTextureStaging(staging, session.ModRoot + $".instant-edit-{session.Id:N}.tmp");
                }
            }
            else
            {
                await ValidateTextureDestinationAsync(session.ModDirectory, session.ModRoot, session.RelativePath, session.TargetFile, token).ConfigureAwait(false);
                if (TextureMappingFingerprint(session.ModRoot) != session.MappingFingerprint)
                    throw new TextureConflictException("The mod's options or mappings changed. Reopen the texture from the browser.");
                await ValidateLiveTextureMappingAsync(session, session.TargetFile, false).ConfigureAwait(false);
                if (_backups is null) throw new IOException("Texture backup storage is unavailable.");
                backup = TextureFiles.Replace(session.TargetFile, session.ModRoot, session.RelativePath,
                    session.ModDirectory, tex, session.LastCommittedHash, _backups, () =>
                    {
                        if (TextureMappingFingerprint(session.ModRoot) != session.MappingFingerprint)
                            throw new TextureConflictException("The mod's mappings changed while saving. Reopen the texture.");
                        return stillCurrent();
                    }, token);
            }
            return new TextureCommit(TextureFiles.Hash(tex), backup, "Texture saved");
        }
        finally { _exportGate.Release(); }
    }

    async Task<string> ITextureEditBackend.RefreshAsync(TextureEditSession session, CancellationToken token)
    {
        var warnings = new List<string>();
        try
        {
            token.ThrowIfCancellationRequested();
            // Adding an already registered mod is unnecessary; this also retries registration after a failed first save.
            var registered = await _framework.RunOnFrameworkThread(() => GetMods().Any(m => m.Directory == session.ModDirectory)).ConfigureAwait(false);
            if (!registered && session.SetupPending)
            {
                token.ThrowIfCancellationRequested();
                var added = await AddNewModAsync(session.ModDirectory).ConfigureAwait(false);
                if (added is not null) warnings.Add(added.Message);
            }
            await _framework.RunOnFrameworkThread(() =>
            {
                token.ThrowIfCancellationRequested();
                var reload = ReloadModOnFramework(session.ModDirectory);
                if (reload is not null) warnings.Add(reload.Message);
                if (session.SetupPending && session.CollectionId is { } collection)
                {
                    var configure = ConfigureModForCollectionOnFramework(session.ModDirectory, collection, session.CollectionName,
                        setPriority: false, priority: 0, redraw: false);
                    if (!configure.Success) warnings.Add(configure.Message);
                    warnings.AddRange(configure.WarningList);
                    if (configure.Success && warnings.Count == 0) session.SetupPending = false;
                }
                try
                {
                    if (MatchesTextureActor(session))
                    {
                        if (session.ObjectIndex != _objects?.LocalPlayer?.ObjectIndex) _redrawObject.Invoke(session.ObjectIndex!.Value);
                    }
                    else if (session.ObjectIndex.HasValue) warnings.Add("Selected actor is no longer present; its redraw was skipped.");
                }
                catch (Exception error) { warnings.Add("Selected actor redraw failed: " + error.Message); }
                var owned = RedrawPlayerOwnedEntitiesOnFramework();
                if (owned is not null) warnings.Add(owned);
            }).ConfigureAwait(false);
        }
        catch (Exception error) { warnings.Add(error.Message); }
        return warnings.Count == 0 ? "Texture saved and redrawn" : "Texture saved; refresh needs attention: " + string.Join(" ", warnings.Distinct());
    }

    internal static void StageGameTextureMod(string staging, string name, string gamePath, byte[] bytes)
    {
        if (!IsSafeNewModName(name) || !IsSafeGameResourcePath(gamePath, ".tex")) throw new IOException("Invalid vanilla texture destination.");
        _ = TextureFiles.ReadTex(bytes);
        var relative = "Files/" + gamePath;
        WriteBytesAtomic(staging, relative, bytes);
        WriteJsonAtomic(Path.Combine(staging, "meta.json"), CreateV4ModMetadata(
            name,
            "XIV Instant Edit",
            $"Texture edit for {gamePath}",
            "",
            new JsonObject
        {
            ["Files"] = new JsonObject { [gamePath] = relative },
            ["FileSwaps"] = new JsonObject(), ["Manipulations"] = new JsonArray(),
        }));
    }

    private static void DeleteTextureStaging(string path, string expected)
    {
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid staging path.");
        TextureFiles.EnsureLocalPath(path);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)) TextureFiles.EnsureLocalPath(entry);
        Directory.Delete(path, true);
    }
}

internal sealed class TextureConflictException(string message) : IOException(message);
