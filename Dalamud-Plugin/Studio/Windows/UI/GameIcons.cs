using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using InstantEdit.Studio.Models;

namespace InstantEdit.Studio.Windows.UI;

/// <summary>The game's own icons (item icons), drawn through Dalamud's shared texture cache.</summary>
internal static class GameIcons
{
    /// <summary>Draws an icon of <paramref name="side"/> pixels; a blank square while it loads.</summary>
    public static void Draw(uint iconId, float side)
    {
        if (iconId == 0)
        {
            TextureThumbnailCache.Placeholder(side, Dalamud.Interface.FontAwesomeIcon.Tshirt, Theme.Faint, string.Empty);
            return;
        }

        try
        {
            var wrap = StudioServices.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
            ImGui.Image(wrap.Handle, new Vector2(side));
        }
        catch (Exception)
        {
            ImGui.Dummy(new Vector2(side));
        }
    }

    /// <summary>The icon of the item a subject stands for, or 0 (character features have none).</summary>
    public static uint Of(PortSubject subject) => subject is GearSubject gear ? gear.Item.Icon : 0;
}
