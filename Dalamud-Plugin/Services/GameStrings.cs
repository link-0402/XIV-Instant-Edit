using System.Text;
using FFXIVClientStructs.STD;

namespace InstantEdit.Services;

internal static class GameStrings
{
    /// <summary>
    /// A game string as text. The game keeps file names in UTF-8, while <see cref="StdString.ToString"/>
    /// decodes them as ANSI and garbles mod folders with other characters in their names (such as "✧"),
    /// so they no longer match the paths Penumbra lists.
    /// </summary>
    public static string Utf8(this ref StdString text) => Encoding.UTF8.GetString(text.AsSpan());
}
