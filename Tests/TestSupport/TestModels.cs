using System.Text;

namespace InstantEdit.TestSupport;

/// <summary>Small synthetic game files the suites build their checks on.</summary>
public static class TestModels
{
    /// <summary>A V6 model with two meshes in the first LOD, both on the one material "/old.mtrl".</summary>
    public static byte[] TwoMeshModel()
    {
        var strings = new MemoryStream();
        int Name(string value)
        {
            int offset = (int)strings.Length;
            strings.Write(Encoding.UTF8.GetBytes(value + "\0"));
            return offset;
        }
        int bone = Name("j_kosi");
        int material = Name("/old.mtrl");
        while (strings.Length % 4 != 0)
            strings.WriteByte(0);

        var vertices = Enumerable.Range(0, 96).Select(i => (byte)(i * 7)).ToArray();
        var indices = Enumerable.Range(0, 24).Select(i => (byte)(200 - i)).ToArray();

        var data = new MemoryStream();
        var w = new BinaryWriter(data);
        w.Write((ushort)2); w.Write((ushort)0); w.Write((uint)strings.Length);
        w.Write(strings.ToArray());
        // Model header.
        w.Write(0.5f);
        w.Write((ushort)2); w.Write((ushort)0); w.Write((ushort)2); w.Write((ushort)1); w.Write((ushort)1); w.Write((ushort)1);
        w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0);
        w.Write((byte)1); w.Write((byte)0); w.Write((ushort)0); w.Write((byte)0); w.Write((byte)0);
        w.Write(0f); w.Write(0f);
        w.Write((ushort)0); w.Write((ushort)0);
        w.Write(0u);
        w.Write((ushort)2); w.Write(0u); w.Write(new byte[6]);
        long lods = data.Position;
        w.Write(new byte[3 * 60]);
        // Meshes: both on material 0.
        for (int m = 0; m < 2; m++)
        {
            w.Write((ushort)4); w.Write((ushort)0); w.Write(6u);
            w.Write((ushort)0); w.Write((ushort)m); w.Write((ushort)1); w.Write((ushort)0);
            w.Write((uint)(m * 6)); w.Write((uint)(m * 48)); w.Write(0u); w.Write(0u);
            w.Write((byte)12); w.Write((byte)0); w.Write((byte)0); w.Write((byte)1);
        }
        for (int s = 0; s < 2; s++)
        {
            w.Write((uint)(s * 6)); w.Write(6u); w.Write(0u); w.Write((ushort)0); w.Write((ushort)1);
        }
        w.Write((uint)material);
        w.Write((uint)bone);
        w.Write((ushort)0); w.Write((ushort)1);   // bone table header
        w.Write((ushort)0); w.Write((ushort)0);   // its two-entry array
        // What the rewriter copies through: bone-map size, padding, bounding boxes.
        w.Write(0u);
        w.Write((byte)3); w.Write(new byte[3]);
        w.Write(Enumerable.Range(0, 128).Select(i => (byte)(i + 1)).ToArray());

        long runtime = data.Length;
        const int stack = 2 * 136;
        long vertexAt = 68 + stack + runtime;
        long indexAt = vertexAt + vertices.Length;

        var lod = new byte[60];
        BitConverter.TryWriteBytes(lod.AsSpan(0, 2), (ushort)0);
        BitConverter.TryWriteBytes(lod.AsSpan(2, 2), (ushort)2);
        BitConverter.TryWriteBytes(lod.AsSpan(52, 4), (uint)vertexAt);
        BitConverter.TryWriteBytes(lod.AsSpan(56, 4), (uint)indexAt);
        var body = data.ToArray();
        lod.CopyTo(body, lods);

        var file = new MemoryStream();
        var h = new BinaryWriter(file);
        h.Write(0x01000006u); h.Write((uint)stack); h.Write((uint)runtime);
        h.Write((ushort)2); h.Write((ushort)1);
        h.Write((uint)vertexAt); h.Write(0u); h.Write(0u);
        h.Write((uint)indexAt); h.Write(0u); h.Write(0u);
        h.Write((uint)vertices.Length); h.Write(0u); h.Write(0u);
        h.Write((uint)indices.Length); h.Write(0u); h.Write(0u);
        h.Write((byte)1); h.Write((byte)0); h.Write((byte)0); h.Write((byte)0);
        var declarations = new byte[stack];
        declarations[0] = declarations[136] = 0xFF;
        h.Write(declarations);
        h.Write(body);
        h.Write(vertices);
        h.Write(indices);
        return file.ToArray();
    }
}
