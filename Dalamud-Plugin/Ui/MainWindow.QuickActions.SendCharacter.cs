using System.Diagnostics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.Animations;
using InstantEdit.Services.CharacterSend;
using InstantEdit.Services.Painter;
using InstantEdit.Services.Skeletons;

namespace InstantEdit.Ui;

/// <summary>
/// Quick Actions' Send my character to Blender: your character as the game draws it now goes to
/// Blender as one scene, bound to one armature built from your character's whole skeleton, in its
/// rest pose, its current pose or the animation it plays. The models its draw object holds go over
/// with the parts and shape keys the game has on; parts it hides go over hidden. Each model is an
/// import of its own with its own context, so Quick Export still writes each one back on its own.
/// Models of another race always go over racially scaled for the character, so preview only.
/// </summary>
public sealed partial class MainWindow
{
    private sealed record CharacterSendRequest(OnScreenObject Character, CharacterPose Pose, bool Weapons, AnimationCapture? Animation);

    private sealed record CharacterSendMessage(string Text, FeedbackSeverity Severity);

    // How long a send waits for Blender's count of pending imports to fall before it sends the pose
    // anyway; Blender keys it once the imports are done either way.
    private static readonly TimeSpan CharacterImportStall = TimeSpan.FromSeconds(90);
    private IReadOnlyList<OnScreenObject>? _sendCharacterSnapshot;
    private OnScreenObject? _sendCharacter;
    private int _characterSendRunning;
    private volatile string _characterSendProgress = string.Empty;
    private volatile CharacterSendMessage? _characterSendMessage;
    private CancellationTokenSource? _characterSendCancellation;
    private PainterLiveReader? _characterDrawState;

    internal void AttachCharacterDrawState(PainterLiveReader? reader) => _characterDrawState = reader;

    private const string SendCharacterKey = "send-character";

    /// <summary> Whether the card keeps the Animations tab's listener running, to see the animation your character plays. </summary>
    private bool CharacterSendListens => _config.CharacterSendPose == CharacterPose.Animation && QuickActionShown(SendCharacterKey);

    private QuickAction SendCharacterCard => new(SendCharacterKey, FontAwesomeIcon.Cubes, "Send my character to Blender",
        "Sends your character to Blender as the game shows it now. " +
        "Each model keeps its own context, so Quick Export still writes it back whole. " +
        "Models made for another race, such as the c0201 gear most female races wear, are reshaped for yours as the game " +
        "shows them. Those are for preview only: Quick Export refuses them.",
        DrawSendCharacterAction);

    private void DrawSendCharacterAction()
    {
        if (SendCharacterTarget() is not { } character)
        {
            Widgets.HintWrapped(_onScreen.IsRefreshing
                ? "Looking for your character"
                : "Your character is not in the On Screen list. Refresh the list once your character is drawn.");
            return;
        }

        var running = Volatile.Read(ref _characterSendRunning) != 0;
        using (ImRaii.Disabled(running))
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Pose");
            ImGui.SameLine();
            CharacterPoseChoice("Rest pose", CharacterPose.Rest);
            ImGui.SameLine();
            CharacterPoseChoice("Current pose", CharacterPose.Current);
            ImGui.SameLine();
            CharacterPoseChoice("Playing animation", CharacterPose.Animation);
            ImGui.SameLine();
            Widgets.HelpTip("Current pose records one frame of your character's live pose, with the game's bone physics and " +
                            "LivePose; Customize+ is paused for it, as for recordings. Playing animation samples the file of the " +
                            "animation your character plays, whose physics bones keep their rest pose. Either becomes an action " +
                            "on the character's armature.");
            var weapons = _config.CharacterSendWeapons;
            if (ImGui.Checkbox("Include weapons", ref weapons))
            {
                _config.CharacterSendWeapons = weapons;
                _saveConfig();
            }
            ImGui.SameLine();
            Widgets.HelpTip("A weapon's own skeleton names its bones like the body's, so each weapon gets an armature of its own, " +
                            "hung from the bone that holds it in the game. It follows the pose and still exports where its " +
                            "skeleton puts it.");
        }

        var capture = DrawCharacterAnimation();
        var blocked = CharacterSendBlock(capture);
        using (ImRaii.Disabled(running || blocked is not null))
        {
            if (ImGui.Button("Send to Blender##quick-send-character"))
                StartCharacterSend(character, capture);
        }
        if (blocked is not null && !running && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(blocked);
        if (running)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel##quick-send-character"))
                _characterSendCancellation?.Cancel();
            Widgets.Spinner();
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, _characterSendProgress);
        }
        else if (_characterSendMessage is { } message)
        {
            var (accent, _, icon) = Theme.Severity(message.Severity);
            ImGui.TextColored(accent, icon);
            ImGui.SameLine(0, Theme.Gap);
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextUnformatted(message.Text);
        }
        Widgets.HintWrapped("Sending again replaces what your last send of this character left in Blender: its armature and models.");
    }

    private void CharacterPoseChoice(string label, CharacterPose pose)
    {
        if (ImGui.RadioButton($"{label}##character-pose", _config.CharacterSendPose == pose) && _config.CharacterSendPose != pose)
        {
            _config.CharacterSendPose = pose;
            _saveConfig();
        }
    }

    /// <summary> With Playing animation chosen, the animation your character plays, named below the choice; otherwise null. </summary>
    private AnimationCapture? DrawCharacterAnimation()
    {
        if (!CharacterSendListens || animations is not { } service)
            return null;
        // The Draw loop keeps the listener running while this card needs it.
        service.StartObservation();
        var capture = PlayingAnimation(service.Observer.History);
        Widgets.HintWrapped(capture is null
            ? "No animation detected yet: play an emote, idle or walk outside combat."
            : $"Playing: {AnimationPresentation.AnimationName(capture)}");
        return capture;
    }

    /// <summary> The animation your character plays on its body, else any it plays. </summary>
    private static AnimationCapture? PlayingAnimation(IEnumerable<AnimationCapture> history)
    {
        AnimationCapture? any = null;
        foreach (var capture in history)
        {
            if (!capture.Playing)
                continue;
            if (capture.Clip.Partial == 0)
                return capture;
            any ??= capture;
        }
        return any;
    }

    /// <summary> Why the character can't be sent now, or null when it can. </summary>
    private string? CharacterSendBlock(AnimationCapture? capture)
    {
        if (Volatile.Read(ref _characterSendRunning) == 0 && Volatile.Read(ref _editing) != 0)
            return "Another model is being sent to Blender.";
        if (_config.CharacterSendPose == CharacterPose.Current)
        {
            if (recorder is null)
                return "The live pose recorder is unavailable.";
            if (recorder.Active)
                return "A recording is running. Send your character once it is done.";
        }
        if (_config.CharacterSendPose == CharacterPose.Animation)
        {
            if (animations is null)
                return animationError ?? "Animation integration is unavailable.";
            if (capture is null)
                return "No animation is playing.";
            if (!AnimationPresentation.Ready(capture))
                return capture.Clip.Resolution is { State: SkeletonResolutionState.Ambiguous }
                    ? "Several skeletons fit this animation. Choose the one it was made for in the Animations tab."
                    : capture.Clip.Resolution?.Reason ?? "Finding the skeleton the animation was made for.";
        }
        return null;
    }

    /// <summary> Your character in the On Screen snapshot, looked up again only when the snapshot changes. </summary>
    private OnScreenObject? SendCharacterTarget()
    {
        var items = _onScreen.Items;
        if (!ReferenceEquals(items, _sendCharacterSnapshot))
        {
            _sendCharacterSnapshot = items;
            _sendCharacter = items.FirstOrDefault(item => item.PresentationCategory == ActorPresentationCategory.Player);
        }
        return _sendCharacter;
    }

    private void StartCharacterSend(OnScreenObject character, AnimationCapture? capture)
    {
        if (Interlocked.CompareExchange(ref _editing, 1, 0) != 0)
        {
            ReportCharacterSend("Another model is already being sent.", FeedbackSeverity.Warning);
            return;
        }
        Volatile.Write(ref _characterSendRunning, 1);
        _characterSendCancellation?.Dispose();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _characterSendCancellation = cancellation;
        _characterSendMessage = null;
        _characterSendProgress = "Checking Blender";
        var request = new CharacterSendRequest(character, _config.CharacterSendPose, _config.CharacterSendWeapons, capture);
        _ = Task.Run(() => SendCharacterAsync(request, cancellation.Token));
    }

    private async Task SendCharacterAsync(CharacterSendRequest request, CancellationToken token)
    {
        var character = request.Character;
        try
        {
            var port = _config.BlenderPort;
            // The character's armature is always built from its skeleton: the existing-armature option doesn't apply.
            var importOptions = BlenderImportOptions.GeneratedWithPreview(
                _config.ApplyTexturesAndMaterials, _config.ExcludeBodyAndGeneralMaterials);
            var models = CharacterSendPlan.Models(character.ResourceRoots, request.Weapons);
            if (models.Count == 0)
                throw new InvalidOperationException("Your character shows no model that can be sent. Refresh the On Screen list and try again.");
            if (_skeletons is not { } resolver)
                throw new InvalidOperationException("The game skeleton reader did not start.");
            await EnsureBlenderReadyForImportAsync(port, importOptions,
                models.Any(model => model.Node.SourceState == ResourceSourceState.GameData), token).ConfigureAwait(false);
            if (!await _blender.SupportsImportSkeletonAsync(port, token).ConfigureAwait(false) ||
                !await _blender.SupportsCharacterImportAsync(port, token).ConfigureAwait(false))
                throw new InvalidOperationException("The XIV Instant Edit add-on is too old to receive a whole character. Update the add-on and restart Blender.");
            if (request.Pose != CharacterPose.Rest && !await _blender.SupportsAnimationImportAsync(port, token).ConfigureAwait(false))
                throw new InvalidOperationException("The XIV Instant Edit add-on is too old to receive animations. Update the add-on and restart Blender.");

            _characterSendProgress = "Reading your character";
            var snapshot = await resolver.CharacterSnapshotAsync(character.ObjectIndex, character.Address, request.Weapons).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Your character changed since the On Screen list was made. Refresh it and try again.");
            // What the game draws now: the models the character's draw object holds, and their parts and shapes.
            var plan = CharacterDrawState.Plan(models, character.ResourceRoots, await ReadCharacterDrawStateAsync(character).ConfigureAwait(false),
                path => Path.IsPathRooted(path) && _resourceSources.AttributionFor(path).State != ResourceSourceState.LoadedMod);
            if (plan.Models.Count == 0)
                throw new InvalidOperationException("Your character draws none of the models in the On Screen list. Refresh it and try again.");
            var skeletonResult = await resolver.ResolveCharacterAsync(character.ObjectIndex, character.Address, token).ConfigureAwait(false);
            var skeleton = skeletonResult.Skeleton
                ?? throw new InvalidOperationException(skeletonResult.Problem ?? "Your character's skeleton could not be read.");
            var skeletonPayload = skeleton.ToPayload();
            // Models of another race are always reshaped for the character, whatever the import option:
            // the game draws them so, and they are bound to the character's own skeleton.
            var (scaling, scalingProblem) = await resolver.RacialScalingAsync(character.ObjectIndex, character.Address, token).ConfigureAwait(false);

            // The pose is taken first: your character may move while the models go over.
            var take = await CaptureCharacterPoseAsync(request, token).ConfigureAwait(false);

            var actor = CharacterActor(character);
            var previewResources = await ResolvePreviewResourcesAsync(actor).ConfigureAwait(false);
            var collection = await _penumbra.GetCollectionTargetAsync(character.ObjectIndex).ConfigureAwait(false);
            var sendId = Guid.NewGuid().ToString("N");
            var key = CharacterSendPlan.Key(snapshot.Name, snapshot.HomeWorld);
            var armatureName = CharacterSendPlan.ArmatureName(_config.AnimationArmatureName);
            var sent = new List<CharacterSentModel>();
            var failed = new List<string>();
            var weaponNotes = new List<string>();
            var leftOut = plan.LeftOut.ToList();
            var hiddenParts = 0;
            for (var i = 0; i < plan.Models.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var drawn = plan.Models[i];
                var item = drawn.Model;
                _characterSendProgress = $"Sending model {i + 1} of {plan.Models.Count}: {item.FileName}";
                var weapon = item.Role == CharacterModelRole.Weapon;
                var view = ResourceViews.FromNode(item.Node);
                var model = new MdlFile { GamePath = view.GamePath, LocalPath = view.ActualPath };
                try
                {
                    var bytes = await ReadModelBytesAsync(model, token).ConfigureAwait(false);
                    var hidden = 0;
                    if (plan.Known && CharacterDrawState.PartMasks(bytes) is { } parts)
                    {
                        var (shown, turnedOff) = CharacterDrawState.Count(parts, drawn.AttributeMasks);
                        if (shown == 0)
                        {
                            // A model the game draws nothing of, such as a seam connector with every band off.
                            leftOut.Add(new CharacterLeftOutModel(item.FileName, CharacterDrawState.NoPartReason));
                            continue;
                        }
                        hidden = turnedOff;
                    }
                    var entry = new CharacterImportEntry(sendId, key, snapshot.Name,
                        weapon ? CharacterImportEntry.WeaponRole : CharacterImportEntry.BodyRole, armatureName,
                        weapon ? WeaponAttach(item, snapshot.Weapons, weaponNotes) : null,
                        drawn.AttributeMasks.Count > 0 ? drawn.AttributeMasks : null, drawn.Shapes);
                    var result = await SendModelToBlenderAsync(actor, model, view, port, _config.ListenPort, importOptions,
                        new CharacterModelSend(entry, skeletonPayload, scaling, scalingProblem, previewResources, collection, bytes),
                        token).ConfigureAwait(false);
                    sent.Add(new CharacterSentModel(result.FileName, result.Notes, result.Scaled));
                    hiddenParts += hidden;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    _log.Warning(e, $"Could not send {item.FileName} of the character to Blender.");
                    failed.Add($"{item.FileName}: {e.Message}");
                }
            }
            if (sent.Count == 0)
                throw new InvalidOperationException($"No model reached Blender. {failed.FirstOrDefault() ?? "Your character shows no part of its models now."}");

            string? poseResult = null, poseError = null;
            if (take is not null)
            {
                await WaitForBlenderImportsAsync(port, token).ConfigureAwait(false);
                _characterSendProgress = "Sending the pose to Blender";
                try
                {
                    poseResult = await DeliverTakeAsync(take, sendId, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    poseError = e.Message;
                }
            }
            var summary = CharacterSendPlan.Summary(new CharacterSendOutcome(snapshot.Name, request.Pose, sent, failed,
                poseResult, poseError, request.Pose == CharacterPose.Current && take is not null ? RecordingWarning(take) : null,
                weaponNotes, skeleton.Warnings)
            {
                LeftOut = leftOut, HiddenParts = hiddenParts, Missing = plan.Missing, External = plan.External, DrawStateKnown = plan.Known,
            });
            ReportCharacterSend(summary.Text, summary.Warned ? FeedbackSeverity.Warning : FeedbackSeverity.Success);
            _chat.Print($"XIV Instant Edit: {sent.Count} models of {snapshot.Name} sent to Blender.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!_lifetimeCts.IsCancellationRequested)
                ReportCharacterSend("Sending your character was cancelled. Models that reached Blender stay there.", FeedbackSeverity.Warning);
        }
        catch (Exception e)
        {
            _log.Warning(e, "Could not send the character to Blender.");
            ReportCharacterSend($"Could not send your character: {e.Message}", FeedbackSeverity.Error);
        }
        finally
        {
            Volatile.Write(ref _editing, 0);
            Volatile.Write(ref _characterSendRunning, 0);
        }
    }

    /// <summary> What the game draws on your character now; null when it can't be read, and then the send takes the whole list. </summary>
    private async Task<PainterLiveCharacter?> ReadCharacterDrawStateAsync(OnScreenObject character)
    {
        if (_characterDrawState is not { } reader)
            return null;
        try
        {
            return await reader.ReadAsync(character.ObjectIndex, character.Address).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.Warning(e, "Could not read what the character draws.");
            return null;
        }
    }

    /// <summary> The take a send keys on the character's armature; none for the rest pose. </summary>
    private async Task<AnimationTake?> CaptureCharacterPoseAsync(CharacterSendRequest request, CancellationToken token)
    {
        switch (request.Pose)
        {
            case CharacterPose.Current:
                _characterSendProgress = "Capturing your pose";
                var active = recorder ?? throw new InvalidOperationException("The live pose recorder is unavailable.");
                return await active.CapturePoseAsync(RecordingSubject.Self, token).ConfigureAwait(false);
            case CharacterPose.Animation:
                if (animations is not { } service || request.Animation is not { } capture)
                    throw new InvalidOperationException("No animation is playing. Play an emote, idle or walk, then send again.");
                _characterSendProgress = "Reading the animation";
                return await service.SampleForCharacterAsync(capture, message => _characterSendProgress = message, token)
                    .ConfigureAwait(false);
            default:
                return null;
        }
    }

    /// <summary> Where the game holds a weapon of the send, or null with a note when it can't tell. </summary>
    private CharacterAttach? WeaponAttach(CharacterSendModel weapon, IReadOnlyList<CharacterWeaponPlacement> placements, List<string> notes)
    {
        var path = PathRules.NormalizeGamePath(weapon.Node.GamePath);
        var placement = placements.FirstOrDefault(item => string.Equals(item.ModelPath, path, StringComparison.OrdinalIgnoreCase));
        if (placement is { Bone: { } bone, Offset: { } offset })
        {
            _log.Information($"Weapon {weapon.FileName} hangs from {bone} ({placement.Method}, {placement.Error * 1000:0.#} mm off).");
            return new CharacterAttach(bone, offset);
        }
        notes.Add($"{weapon.FileName} stays at the origin: no bone holding it was found.");
        return null;
    }

    /// <summary>
    /// Waits until Blender has run every queued import, so the pose sent next finds the armature they
    /// built. A count that stops falling (Blender held up by a dialog, say) ends the wait: Blender keys
    /// the pose once the imports are done either way.
    /// </summary>
    private async Task WaitForBlenderImportsAsync(int port, CancellationToken token)
    {
        _characterSendProgress = "Waiting for Blender to import the models";
        var fewest = int.MaxValue;
        var sinceProgress = Stopwatch.StartNew();
        while (sinceProgress.Elapsed < CharacterImportStall)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                // Blender answers slowly while an import holds its main thread; that counts as no progress.
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var status = await _blender.GetStatusAsync(port, timeout.Token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (status.Reachable)
                {
                    if (status.PendingImports <= 0)
                        return;
                    if (status.PendingImports < fewest)
                    {
                        fewest = status.PendingImports;
                        sinceProgress.Restart();
                    }
                    _characterSendProgress = $"Blender is importing the models: {status.PendingImports} to go";
                }
            }
            await Task.Delay(500, token).ConfigureAwait(false);
        }
    }

    /// <summary> The character as the model sends see it: its On Screen entry and every resource it shows. </summary>
    private static ActorView CharacterActor(OnScreenObject character)
        => new(character, character.PresentationCategory.ToString(), Safe(character.Name),
            character.ResourceRoots.Select(ResourceViews.FromNode).ToList(), character.ObjectIndex);

    private void ReportCharacterSend(string text, FeedbackSeverity severity)
    {
        _characterSendMessage = new CharacterSendMessage(text, severity);
        SetStatus(text, severity);
    }
}
