using System;
using System.Buffers.Binary;
using System.IO;

namespace InstantEdit.Studio.Services.Resources;

internal enum FileKind
{
    Unknown,
    Empty,
    Tex,
    Mdl,
    Mtrl,
    Png,
    Dds,
    Jpeg,
}

/// <summary>Tells a file by its content, so a PNG renamed to .tex or a cut-off texture is caught before the game reads it.</summary>
internal static class FileSniffer
{
    public static FileKind SniffFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> head = stackalloc byte[TexHeader.Size];
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return Sniff(head[..read], stream.Length);
    }

    public static FileKind Sniff(ReadOnlySpan<byte> head, long fileLength)
    {
        if (fileLength == 0 || head.Length == 0) return FileKind.Empty;
        if (head.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47])) return FileKind.Png;
        if (head.StartsWith("DDS "u8)) return FileKind.Dds;
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return FileKind.Jpeg;
        if (ModelLayoutReader.LooksLikeModel(head)) return FileKind.Mdl;
        if (MtrlReader.LooksLikeMtrl(head)) return FileKind.Mtrl;
        return IsTex(head, fileLength) ? FileKind.Tex : FileKind.Unknown;
    }

    /// <summary>A texture needs a sane header and a first surface that starts behind the header and inside the file.</summary>
    private static bool IsTex(ReadOnlySpan<byte> head, long fileLength)
    {
        if (head.Length < TexHeader.Size || fileLength <= TexHeader.Size || !TexHeader.TryRead(head, out _))
            return false;
        var surface = BinaryPrimitives.ReadUInt32LittleEndian(head[28..]);
        return surface >= TexHeader.Size && surface < fileLength;
    }

    public static string Describe(FileKind kind) => kind switch
    {
        FileKind.Empty => "an empty file",
        FileKind.Tex   => "a game texture",
        FileKind.Mdl   => "a model",
        FileKind.Mtrl  => "a material",
        FileKind.Png   => "a PNG image",
        FileKind.Dds   => "a DDS image",
        FileKind.Jpeg  => "a JPEG image",
        _              => "a file it can't tell",
    };

    /// <summary>The kind a game path's extension asks for, or null when the extension is not checked.</summary>
    public static FileKind? ExpectedKind(string gamePath)
        => Path.GetExtension(gamePath).ToLowerInvariant() switch
        {
            ".tex"  => FileKind.Tex,
            ".mdl"  => FileKind.Mdl,
            ".mtrl" => FileKind.Mtrl,
            _       => null,
        };
}
