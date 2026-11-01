using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The Link layer picker: a bone of this clip, a partner workspace's open clip and its bone, and whether the partner links back.
/// A menu asks for it (<see cref="Request"/>) and it opens on the next draw at the own level of the window that asked, as the event
/// pickers do.
/// Link adds the layer here as one undo step, and asks the host to add the link back to the partner's clip on the next framework
/// update, as one undo step there. It also gives the link panel (<see cref="LinkFields"/>) the partners and the link back.
/// </summary>
internal sealed class LinkPicker(TimelineSession session, BonePosing posing, ITimelineHost host, WorkspaceKey key)
{
    private const string Popup = "##link-picker";

    /// <summary>What the picker and the panel show while the partners' motion is still read.</summary>
    public const string Waiting = "Reading the partner's clip";

    private bool requested;
    // Whether the Bones window asked, not the Timeline window: popups are per window, so only that one opens and draws it.
    private bool inBones;
    private string? bone;
    private WorkspaceKey? partner;
    private string? partnerBone;
    // The partner bone was picked by hand, so another bone doesn't preset it again; another partner does.
    private bool partnerBonePicked;
    private bool linkBack = true;
    private string? error;
    // The linkable bones of the skeleton last asked about, by index (see Linkable).
    private static (SkeletonDescription Skeleton, bool[] Bones)? linkable;
    // The partner skeleton's bone index, for the skeleton it was made for.
    private (SkeletonDescription Skeleton, Dictionary<string, int> Index)? partnerIndex;

    /// <summary>This workspace's key: whose links these are.</summary>
    public WorkspaceKey Key => key;

    /// <summary>Whether another workspace has an editable body clip to link to; reads no motion.</summary>
    public bool Available => host.HasLinkPartner(key);

    /// <summary>The other workspaces' ready views. Asking keeps their motion read for a couple of seconds, so ask only where it is shown.</summary>
    public IReadOnlyList<LinkView> Partners() => host.LinkPartners(key);

    /// <summary>The ready view of the workspace <paramref name="preset"/> names (null for You); null while it isn't ready.</summary>
    public LinkView? Partner(Guid? preset) => Partners().FirstOrDefault(view => view.Key.Preset == preset);

    /// <summary>The ready view of the workspace <paramref name="preset"/> names (null for You), without asking for it: what a link reads anyway.</summary>
    public LinkView? Linked(Guid? preset) => host.LinkView(new WorkspaceKey(preset));

    /// <summary>
    /// Asks <paramref name="view"/>'s workspace to link back to <paramref name="link"/> of this clip, on the next framework update, if its
    /// clip is still the one the view shows. Nothing without an open clip here.
    /// </summary>
    public void AddLinkBack(LinkView view, MotionLink link)
    {
        if (session.Source is not { } source)
            return;
        host.AddLinkBack(view.Key, Back(link, key.Preset, source.Clip.GamePath, source.Clip.BindingIndex),
            TimelineWindowViews.LinkName(session.ActorName()), view.GamePath, view.Binding);
    }

    /// <summary>
    /// The link back to <paramref name="link"/>: on the partner's clip, its bone following this clip's, with this workspace's
    /// <paramref name="self"/> preset (null for You) as the partner and this clip's <paramref name="path"/> and <paramref name="binding"/>.
    /// </summary>
    public static MotionLink Back(MotionLink link, Guid? self, string path, int binding) => new(link.PartnerBone, self, link.Bone, path, binding);

    /// <summary>
    /// Whether a link may join <paramref name="bone"/> of <paramref name="skeleton"/> (<paramref name="index"/> its bone index): not the
    /// root, the body (<see cref="PinChains.Body"/>) or a bone above it, which would move the whole character, and not a bone right
    /// under the body other than the hips, which nothing could bend to follow.
    /// </summary>
    public static bool Linkable(SkeletonDescription skeleton, Dictionary<string, int> index, int bone)
    {
        var name = skeleton.Bones[bone].Name;
        if (skeleton.Bones[bone].Parent < 0 || name == PinChains.Body || index.TryGetValue(PinChains.Body, out var body) && PinChains.IsBelow(skeleton, body, bone))
            return false;
        return name == PinChains.Hips || PinChains.Chain(skeleton, index, new MotionPin(name)).Shape != PinChainShape.None;
    }

    /// <summary><see cref="Linkable(SkeletonDescription, Dictionary{string, int}, int)"/> for each bone, by index; kept for the last skeleton asked.</summary>
    public static bool[] Linkable(SkeletonDescription skeleton)
    {
        if (linkable is { } kept && ReferenceEquals(kept.Skeleton, skeleton))
            return kept.Bones;
        var index = MotionEvaluator.Index(skeleton);
        var bones = new bool[skeleton.Bones.Length];
        for (var b = 0; b < bones.Length; b++)
            bones[b] = Linkable(skeleton, index, b);
        linkable = (skeleton, bones);
        return bones;
    }

    /// <summary>
    /// Why the partner of <paramref name="view"/> can't link back to a link from <paramref name="bone"/> to its <paramref name="partnerBone"/>
    /// (<paramref name="index"/> its skeleton's bone index), the link back pointing at this workspace's <paramref name="self"/> preset and
    /// clip <paramref name="path"/> at <paramref name="binding"/>: its clip is read-only, it already links back so, or nothing can bend its
    /// bone. Null when it can, or while a bone isn't picked. Linking one way to such a bone stays allowed.
    /// </summary>
    public static string? BackRefused(LinkView view, string? bone, string? partnerBone, Dictionary<string, int> index, Guid? self, string path,
        int binding)
    {
        if (view.ReadOnly)
            return TimelineWindowViews.LinkReadOnly(view.Name);
        if (bone == null || partnerBone == null)
            return null;
        if (MotionLinks.HasLink(view.Document.Motion, Back(new MotionLink(bone, view.Key.Preset, partnerBone, view.GamePath, view.Binding), self, path, binding)))
            return TimelineWindowViews.LinkAlreadyBack(view.Name);
        if (!index.TryGetValue(partnerBone, out var at) || !Linkable(view.Played.Skeleton, index, at))
            return Refused(partnerBone);
        return null;
    }

    /// <summary>Why a bone can't be linked: the pin's words for a bone nothing can bend to hold.</summary>
    public static string Refused(string bone) => $"{AnimationPresentation.BoneName(bone)} sits right under the body, so nothing can bend to hold it.";

    /// <summary>
    /// The Add menus' Link layer item, <paramref name="label"/> with <paramref name="hint"/> as its hover: opens the picker, in the Bones
    /// window with <paramref name="bones"/>; greyed, saying why, while no other workspace has a clip open.
    /// </summary>
    public void DrawAddItem(string label, bool readOnly, string hint, bool bones = false)
    {
        var available = Available;
        if (ImGui.MenuItem(label, "", false, !readOnly && available))
            Request(bones);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(available ? hint : TimelineWindowViews.LinkNoPartner);
    }

    /// <summary>
    /// Opens the picker on the next draw of the window that asks (the Bones window with <paramref name="bones"/>), on the primary selected
    /// bone when it can be linked, and starts reading the partners' motion.
    /// </summary>
    public void Request(bool bones = false)
    {
        requested = true;
        inBones = bones;
        var skeleton = session.EditingSkeleton();
        bone = posing.Selection.Primary is { } primary && skeleton != null && MotionEvaluator.Index(skeleton).TryGetValue(primary, out var at) &&
               Linkable(skeleton)[at] ? primary : null;
        partner = null;
        partnerBone = null;
        partnerBonePicked = false;
        linkBack = true;
        error = null;
        Partners();
    }

    /// <summary>
    /// The picker, while it is open; drawn at the window's own level by the window that asked for it: the Bones window with
    /// <paramref name="bones"/>, else the Timeline window.
    /// </summary>
    public void Draw(TimelineEditor current, bool bones)
    {
        if (bones != inBones)
            return;
        if (requested)
        {
            ImGui.OpenPopup(Popup);
            requested = false;
        }
        using var popup = ImRaii.Popup(Popup);
        if (!popup.Success)
            return;
        using var wrap = ImRaii.TextWrapPos(Theme.Scaled(360));
        ImGui.TextColored(Theme.Label, "Link layer");
        ImGui.Separator();
        var views = Partners();
        if (views.Count == 0)
        {
            if (Available)
            {
                // A partner whose motion couldn't be read never gets a view: say so instead of waiting for it.
                var reading = false;
                foreach (var candidate in host.LinkCandidates(key))
                {
                    if (candidate.ReadFailed)
                        ImGui.TextColored(Theme.Warning, TimelineWindowViews.LinkUnreadable(candidate.Name));
                    else
                        reading = true;
                }
                if (reading)
                {
                    Widgets.Spinner();
                    ImGui.SameLine(0, Theme.Gap);
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(Theme.Muted, Waiting);
                }
            }
            else
                ImGui.TextColored(Theme.Hint, TimelineWindowViews.LinkNoPartner);
            return;
        }
        if (session.Source is not { } source || session.EditingSkeleton(source) is not { } skeleton)
        {
            ImGui.TextColored(Theme.Hint, "Choose the skeleton this animation was made for under Source to pick bones.");
            return;
        }

        var view = views.FirstOrDefault(known => known.Key == partner) ?? views[0];
        if (partner != view.Key)
        {
            partner = view.Key;
            partnerBonePicked = false;
        }
        var theirs = view.Played.Skeleton;
        if (partnerIndex is not { } made || !ReferenceEquals(made.Skeleton, theirs))
            partnerIndex = made = (theirs, MotionEvaluator.Index(theirs));
        // The same bone on the partner, unless one was picked by hand there; one the partner's skeleton lacks goes.
        if (!partnerBonePicked)
            partnerBone = bone != null && made.Index.ContainsKey(bone) ? bone : null;
        else if (partnerBone != null && !made.Index.ContainsKey(partnerBone))
        {
            partnerBone = null;
            partnerBonePicked = false;
        }

        using (var table = ImRaii.Table("##link-picker-fields", 2, ImGuiTableFlags.SizingFixedFit))
        {
            if (table.Success)
            {
                ImGui.TableSetupColumn("##fact", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch, Theme.Scaled(220));
                TimelineUi.Fact("Bone");
                if (LinkFields.BoneCombo("##link-pick-bone", bone, () => skeleton, Linkable(skeleton), null, out var picked))
                    bone = picked;
                TimelineUi.Fact("Partner");
                ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(140), ImGui.GetContentRegionAvail().X));
                using (var combo = ImRaii.Combo("##link-pick-partner", view.Name))
                {
                    if (combo.Success)
                    {
                        foreach (var other in views)
                            if (ImGui.Selectable($"{other.Name}##{other.Key.Id}", other.Key == view.Key))
                                partner = other.Key;
                    }
                    else if (ComboWheel.Scroll(views, view, out var next))
                        partner = next.Key;
                }
                TimelineUi.Fact("Partner bone");
                if (LinkFields.BoneCombo("##link-pick-partner-bone", partnerBone, () => theirs, null, null, out var pickedPartner))
                {
                    partnerBone = pickedPartner;
                    partnerBonePicked = true;
                }
            }
        }

        var backRefused = BackRefused(view, bone, partnerBone, made.Index, key.Preset, source.Clip.GamePath, source.Clip.BindingIndex);
        using (ImRaii.Disabled(backRefused != null))
        {
            var back = linkBack && backRefused == null;
            if (ImGui.Checkbox("Link back##link-pick-back", ref back))
                linkBack = back;
        }
        if (backRefused != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(backRefused);

        ImGui.Spacing();
        // A second link of the same bones would move the bone twice as far.
        var there = bone != null && partnerBone != null &&
                    MotionLinks.HasLink(current.Document.Motion, new MotionLink(bone, view.Key.Preset, partnerBone, view.GamePath, view.Binding));
        using (ImRaii.Disabled(session.ReadOnly || bone == null || partnerBone == null || there))
        {
            if (ImGui.Button("Link##link-pick-link") && bone != null && partnerBone != null && !there)
            {
                var link = new MotionLink(bone, view.Key.Preset, partnerBone, view.GamePath, view.Binding);
                var next = MotionEdits.AddLink(current.Document, link, TimelineWindowViews.LinkName(view.Name), out var added);
                if (added.Length == 0)
                    error = $"An animation can have at most {MotionEvaluator.MaxLayers} bone layers.";
                else if (current.Commit(next))
                {
                    current.SelectLayer(added);
                    if (linkBack && backRefused == null)
                        AddLinkBack(view, link);
                    ImGui.CloseCurrentPopup();
                }
            }
        }
        if (there && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(TimelineWindowViews.LinkAlreadyThere);
        if (error != null)
            ImGui.TextColored(Theme.Error, error);
    }
}
