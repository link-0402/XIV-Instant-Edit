using System;
using System.IO;

namespace InstantEdit.Studio.Services.Animation;

/// <summary>Whether a dropped file is the kind an animation row takes, by the first bytes of the file.</summary>
internal static class AnimationFileCheck
{
    /// <summary>Null when the file exists and starts with "pap " (a PAP) or "TMLB" (a TMB); else what is wrong, in a few words.</summary>
    public static string? Problem(string path, bool tmb)
    {
        try
        {
            if (!File.Exists(path))
                return "File not found.";
            Span<byte> magic = stackalloc byte[4];
            using var stream = File.OpenRead(path);
            if (stream.ReadAtLeast(magic, 4, throwOnEndOfStream: false) < 4)
                return tmb ? "Not a .tmb file." : "Not a .pap file.";
            bool ok = tmb
                ? magic.SequenceEqual("TMLB"u8)
                : magic.SequenceEqual("pap "u8);
            return ok ? null : tmb ? "Not a .tmb file." : "Not a .pap file.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Can't be read: {ex.Message}";
        }
    }
}
