using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using InstantEdit.Models;

namespace InstantEdit.Ui.Main;

/// <summary> The game's armoury slot icons for On Screen's item rows, loaded once the UI is ready. </summary>
internal sealed class SlotIcons : IDisposable
{
    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private readonly object _stateLock = new();
    private IReadOnlyDictionary<string, IDalamudTextureWrap> _slotIcons = new Dictionary<string, IDalamudTextureWrap>();
    // Icon keys by slot, Penumbra icon and section: working one out builds strings, and rows draw every frame.
    private readonly Dictionary<(string Slot, string Icon, string Section), string> _keys = new();

    public SlotIcons(IUiBuilder uiBuilder, ITextureProvider textureProvider, IDataManager data, IPluginLog log)
    {
        _data = data;
        _log = log;
        _ = uiBuilder.RunWhenUiPrepared(() => LoadSlotIcons(uiBuilder, textureProvider), true);
    }

    public void Dispose()
    {
        IReadOnlyDictionary<string, IDalamudTextureWrap> icons;
        lock (_stateLock)
        {
            icons = _slotIcons;
            _slotIcons = new Dictionary<string, IDalamudTextureWrap>();
        }

        foreach (var icon in icons.Values.Distinct())
            icon.Dispose();
    }

    public void Draw(string slot, string resourceIcon, string section)
    {
        IDalamudTextureWrap? icon;
        if (!_keys.TryGetValue((slot, resourceIcon, section), out var key))
        {
            if (_keys.Count >= 1024)
                _keys.Clear();
            _keys[(slot, resourceIcon, section)] = key =
                string.Equals(section, ResourceSection.CharacterFeatures.ToString(), StringComparison.OrdinalIgnoreCase)
                    ? "Unknown"
                    : ResourceViews.NormalizeSlotIcon(slot, resourceIcon);
        }
        lock (_stateLock)
            _slotIcons.TryGetValue(key, out icon);

        if (icon is null)
        {
            ImGui.Dummy(new Vector2(Theme.IconSize));
            return;
        }

        ImGui.Image(icon.Handle, new Vector2(Theme.IconSize));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(slot);
    }

    private bool LoadSlotIcons(IUiBuilder uiBuilder, ITextureProvider textureProvider)
    {
        using var armoury = uiBuilder.LoadUld("ui/uld/ArmouryBoard.uld");
        if (!armoury.Valid)
            return false;

        var icons = new Dictionary<string, IDalamudTextureWrap>(StringComparer.OrdinalIgnoreCase);
        try
        {
            void Add(string slot, int part)
            {
                var texture = armoury.LoadTexturePart("ui/uld/ArmouryBoard_hr1.tex", part);
                if (texture is not null)
                    icons.Add(slot, texture);
            }

            Add("Mainhand", 0);
            Add("Head", 1);
            Add("Body", 2);
            Add("Hands", 3);
            Add("Legs", 5);
            Add("Feet", 6);
            Add("Offhand", 7);
            Add("Ears", 8);
            Add("Neck", 9);
            Add("Wrists", 10);
            Add("Finger", 11);

            var unknown = LoadUnknownSlotIcon(textureProvider);
            if (unknown is not null)
                icons.Add("Unknown", unknown);

            lock (_stateLock)
                _slotIcons = icons;
            return true;
        }
        catch (Exception e)
        {
            foreach (var icon in icons.Values)
                icon.Dispose();
            _log.Debug($"Could not load armoury slot icons: {e.Message}");
            return false;
        }
    }

    private IDalamudTextureWrap? LoadUnknownSlotIcon(ITextureProvider textureProvider)
    {
        var texture = _data.GetFile<Lumina.Data.Files.TexFile>("ui/uld/levelup2_hr1.tex");
        if (texture is null)
            return null;

        // This is the same square crop Penumbra uses for its unknown-slot '?' icon.
        var source = texture.GetRgbaImageData();
        var size = texture.Header.Height;
        var bytes = new byte[size * size * 4];
        var horizontalOffset = 2 * (texture.Header.Height - texture.Header.Width);
        for (var y = 0; y < size; ++y)
            source.AsSpan(4 * y * texture.Header.Width, 4 * texture.Header.Width)
                .CopyTo(bytes.AsSpan(4 * y * size + horizontalOffset));

        return textureProvider.CreateFromRaw(RawImageSpecification.Rgba32(size, size), bytes, "InstantEdit.UnknownSlotIcon");
    }
}
