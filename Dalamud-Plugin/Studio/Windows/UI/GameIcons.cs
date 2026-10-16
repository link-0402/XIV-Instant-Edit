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
    public static uint Of(PortSubject subject) => subject switch
    {
        GearSubject gear     => gear.Item.Icon,
        WeaponSubject weapon => weapon.Model.Icon,
        _                    => 0,
    };

    /// <summary>The glyph shown for a kind of subject where it has no game icon.</summary>
    public static Dalamud.Interface.FontAwesomeIcon Glyph(PortSubject subject) => subject.Kind switch
    {
        SubjectKind.Gear   => Dalamud.Interface.FontAwesomeIcon.Tshirt,
        SubjectKind.Weapon => Dalamud.Interface.FontAwesomeIcon.ShieldAlt,
        _                  => Dalamud.Interface.FontAwesomeIcon.User,
    };
}
