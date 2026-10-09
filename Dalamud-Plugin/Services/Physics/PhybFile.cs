using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace InstantEdit.Services.Physics;

/// <summary>
/// A 32-byte name field of a physics file: ASCII up to a null, then 0xFE padding, or 32 zeros when
/// empty, as the game's files write them. The bytes read are kept, so a name that isn't changed is
/// written back exactly; a new name gets the game's padding.
/// </summary>
internal sealed class PhybName : IEquatable<PhybName>
{
    public const int Size = 32;
    public const int MaxLength = Size - 1;
    public static readonly PhybName Empty = From(string.Empty);

    private readonly byte[] raw;

    private PhybName(string text, byte[] raw)
    {
        Text = text;
        this.raw = raw;
    }

    public string Text { get; }
    public bool IsEmpty => Text.Length == 0;

    /// <summary> A name for new data. Bone and shape names are printable ASCII of at most 31 characters. </summary>
    public static PhybName From(string text)
    {
        if (!IsValid(text))
            throw new ArgumentException($"\"{text}\" can't be a physics name: use up to {MaxLength} letters, digits or symbols.", nameof(text));
        var raw = new byte[Size];
        if (text.Length > 0)
        {
            Encoding.ASCII.GetBytes(text, raw);
            raw.AsSpan(text.Length + 1).Fill(0xFE);
        }
        return new PhybName(text, raw);
    }

    public static bool IsValid(string? text)
        => text is not null && text.Length <= MaxLength && text.All(c => c is >= ' ' and <= '~');

    public static PhybName Read(ReadOnlySpan<byte> field)
    {
        var end = field[..Size].IndexOf((byte)0);
        if (end < 0)
            throw new InvalidDataException("A name in the physics file isn't terminated.");
        return new PhybName(Encoding.Latin1.GetString(field[..end]), field[..Size].ToArray());
    }

    public void CopyTo(Span<byte> destination) => raw.CopyTo(destination);

    public bool Equals(PhybName? other) => other is not null && string.Equals(Text, other.Text, StringComparison.Ordinal);
    public override bool Equals(object? obj) => Equals(obj as PhybName);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Text);
    public override string ToString() => Text;
}

/// <summary> How a simulator or chain keeps its nodes relative to a collision shape. </summary>
internal enum PhybCollisionType
{
    /// <summary> Pulled toward the shape when away from it, pushed out when touching it. </summary>
    Both = 0,
    /// <summary> Kept outside the shape. </summary>
    Outside = 1,
    /// <summary> Kept inside the shape. </summary>
    Inside = 2,
}

internal sealed class PhybCapsule
{
    public PhybName Name = PhybName.Empty;
    public PhybName StartBone = PhybName.Empty;
    public PhybName EndBone = PhybName.Empty;
    public Vector3 StartOffset;
    public Vector3 EndOffset;
    public float Radius;

    public PhybCapsule Clone() => (PhybCapsule)MemberwiseClone();
}

internal sealed class PhybEllipsoid
{
    public PhybName Name = PhybName.Empty;
    public PhybName Bone = PhybName.Empty;
    public Vector3 Offset1, Offset2, Offset3, Offset4;
    public float Radius;

    public PhybEllipsoid Clone() => (PhybEllipsoid)MemberwiseClone();
}

internal sealed class PhybNormalPlane
{
    public PhybName Name = PhybName.Empty;
    public PhybName Bone = PhybName.Empty;
    public Vector3 Offset;
    public Vector3 Normal;
    public float Thickness;

    public PhybNormalPlane Clone() => (PhybNormalPlane)MemberwiseClone();
}

internal sealed class PhybThreePointPlane
{
    public PhybName Name = PhybName.Empty;
    public PhybName Bone = PhybName.Empty;
    public Vector4 Unknown1, Unknown2, Unknown3, Unknown4;
    public Vector3 Offset, Unknown5, Unknown6;
    public float Thickness;

    public PhybThreePointPlane Clone() => (PhybThreePointPlane)MemberwiseClone();
}

internal sealed class PhybSphere
{
    public PhybName Name = PhybName.Empty;
    public PhybName Bone = PhybName.Empty;
    public Vector3 Offset;
    /// <summary> The sphere's radius. </summary>
    public float Thickness;

    public PhybSphere Clone() => (PhybSphere)MemberwiseClone();
}

/// <summary> The collision shapes a file defines; simulators name the ones they use. </summary>
internal sealed class PhybCollision
{
    public List<PhybCapsule> Capsules = [];
    public List<PhybEllipsoid> Ellipsoids = [];
    public List<PhybNormalPlane> NormalPlanes = [];
    public List<PhybThreePointPlane> ThreePointPlanes = [];
    public List<PhybSphere> Spheres = [];
    /// <summary> The three bytes after the counts, 0xCC in the game's files. </summary>
    public byte[] Padding = [0xCC, 0xCC, 0xCC];

    public bool IsEmpty => Capsules.Count + Ellipsoids.Count + NormalPlanes.Count + ThreePointPlanes.Count + Spheres.Count == 0;

    /// <summary> Every shape's name, in file order. </summary>
    public IEnumerable<PhybName> Names
        => Capsules.Select(s => s.Name).Concat(Ellipsoids.Select(s => s.Name)).Concat(NormalPlanes.Select(s => s.Name))
            .Concat(ThreePointPlanes.Select(s => s.Name)).Concat(Spheres.Select(s => s.Name));

    public PhybCollision Clone() => new()
    {
        Capsules = Capsules.Select(s => s.Clone()).ToList(),
        Ellipsoids = Ellipsoids.Select(s => s.Clone()).ToList(),
        NormalPlanes = NormalPlanes.Select(s => s.Clone()).ToList(),
        ThreePointPlanes = ThreePointPlanes.Select(s => s.Clone()).ToList(),
        Spheres = Spheres.Select(s => s.Clone()).ToList(),
        Padding = (byte[])Padding.Clone(),
    };
}

/// <summary> A collision shape a simulator or chain uses, by name. </summary>
internal sealed class PhybCollisionRef
{
    public PhybName Name = PhybName.Empty;
    public PhybCollisionType Type = PhybCollisionType.Outside;

    public PhybCollisionRef Clone() => (PhybCollisionRef)MemberwiseClone();
}

/// <summary>
/// One point of a chain. The first node is the chain's bone, which the animation places; the others
/// move. The last node is the chain's tip: no bone (or a name the skeleton lacks), placed at the
/// chain's <see cref="PhybChain.LastBoneOffset"/> from the bone before it.
/// </summary>
internal sealed class PhybNode
{
    public PhybName Bone = PhybName.Empty;
    public float Radius;
    public float AttractByAnimation;
    public float WindScale;
    public float GravityScale;
    /// <summary> How far the node may swing from its axis, in radians. </summary>
    public float ConeMaxAngle;
    public Vector3 ConeAxisOffset;
    public Vector3 ConstraintPlaneNormal;
    /// <summary> Bit n: collide with the simulator's collision shape n. </summary>
    public uint CollisionFlags;
    public uint ContinuousCollisionFlags;

    public PhybNode Clone() => (PhybNode)MemberwiseClone();
}

internal sealed class PhybChain
{
    public float Dampening;
    public float MaxSpeed;
    public float Friction;
    public float CollisionDampening;
    public float RepulsionStrength;
    /// <summary> The tip's place, in the space of the last bone. </summary>
    public Vector3 LastBoneOffset;
    /// <summary> 0: nodes collide as spheres, 1: as capsules between neighbouring nodes. </summary>
    public uint Type;
    public List<PhybCollisionRef> Collisions = [];
    public List<PhybNode> Nodes = [];

    public PhybChain Clone()
    {
        var copy = (PhybChain)MemberwiseClone();
        copy.Collisions = Collisions.Select(c => c.Clone()).ToList();
        copy.Nodes = Nodes.Select(n => n.Clone()).ToList();
        return copy;
    }
}

/// <summary> Collision between two nodes, of the same chain or of two chains (indices from 0). </summary>
internal sealed class PhybConnector
{
    public short Chain1, Chain2, Node1, Node2;
    public float CollisionRadius;
    public float Friction;
    public float Dampening;
    public float Repulsion;
    /// <summary> Bit n: collide with the simulator's collision shape n (its Collisions, not CollisionConnectors). </summary>
    public uint CollisionFlags;
    public uint ContinuousCollisionFlags;

    public PhybConnector Clone() => (PhybConnector)MemberwiseClone();
}

/// <summary> A spring pulling a node toward a point fixed to a bone. </summary>
internal sealed class PhybAttract
{
    public PhybName Bone = PhybName.Empty;
    public Vector3 Offset;
    public short Chain, Node;
    public float Stiffness;

    public PhybAttract Clone() => (PhybAttract)MemberwiseClone();
}

internal sealed class PhybPin
{
    public PhybName Bone = PhybName.Empty;
    public Vector3 Offset;
    public short Chain, Node;

    public PhybPin Clone() => (PhybPin)MemberwiseClone();
}

internal sealed class PhybSpring
{
    public short Chain1, Chain2, Node1, Node2;
    public float StretchStiffness;
    public float CompressStiffness;

    public PhybSpring Clone() => (PhybSpring)MemberwiseClone();
}

internal sealed class PhybPostAlignment
{
    public PhybName CollisionName = PhybName.Empty;
    public short Chain, Node;

    public PhybPostAlignment Clone() => (PhybPostAlignment)MemberwiseClone();
}

[Flags]
internal enum PhybSimulatorFlags : byte
{
    None = 0,
    Simulating = 0x01,
    CollisionsHandled = 0x02,
    ContinuousCollisions = 0x04,
    UsingGroundPlane = 0x08,
    FixedLength = 0x10,
}

/// <summary> One set of chains simulated together, with the shapes and constraints they share. </summary>
internal sealed class PhybSimulator
{
    public Vector3 Gravity = new(0, -9.8f, 0);
    public Vector3 Wind;
    public short ConstraintLoop = 2;
    public short CollisionLoop = 1;
    public PhybSimulatorFlags Flags = PhybSimulatorFlags.Simulating | PhybSimulatorFlags.CollisionsHandled | PhybSimulatorFlags.FixedLength;
    /// <summary> The row of the game's PhysicsGroup sheet: which settings and gear turn the simulator off. </summary>
    public byte Group;
    /// <summary> The two bytes after the group, 0xCCCC in the game's files. </summary>
    public ushort Padding = 0xCCCC;
    public List<PhybCollisionRef> Collisions = [];
    public List<PhybCollisionRef> CollisionConnectors = [];
    public List<PhybChain> Chains = [];
    public List<PhybConnector> Connectors = [];
    public List<PhybAttract> Attracts = [];
    public List<PhybPin> Pins = [];
    public List<PhybSpring> Springs = [];
    public List<PhybPostAlignment> PostAlignments = [];
    /// <summary> The builder leaves this simulator out of the files it writes; not part of the format. </summary>
    public bool Excluded;

    public PhybSimulator Clone()
    {
        var copy = (PhybSimulator)MemberwiseClone();
        copy.Collisions = Collisions.Select(c => c.Clone()).ToList();
        copy.CollisionConnectors = CollisionConnectors.Select(c => c.Clone()).ToList();
        copy.Chains = Chains.Select(c => c.Clone()).ToList();
        copy.Connectors = Connectors.Select(c => c.Clone()).ToList();
        copy.Attracts = Attracts.Select(c => c.Clone()).ToList();
        copy.Pins = Pins.Select(c => c.Clone()).ToList();
        copy.Springs = Springs.Select(c => c.Clone()).ToList();
        copy.PostAlignments = PostAlignments.Select(c => c.Clone()).ToList();
        return copy;
    }
}

/// <summary>
/// Dawntrail's extension block ("EPHB"): extra values per simulator, chain and node, keyed by bone name
/// hashes. Kept as read; it only stays correct while the simulators keep their layout.
/// </summary>
internal sealed class PhybExtension
{
    public ushort Version = 1;
    public ushort Count;
    public byte[] Payload = [];

    public PhybExtension Clone() => new() { Version = Version, Count = Count, Payload = (byte[])Payload.Clone() };
}

/// <summary>
/// A <c>.phyb</c> file: the bone physics of one skeleton (collision shapes, then simulators of bone
/// chains), with Dawntrail's optional extension block at the end. Written in the game's own layout,
/// so a file read and written unchanged keeps its bytes.
/// </summary>
internal sealed class PhybFile
{
    public const uint CurrentVersion = 0x0100_0001;
    public const int MaxFileBytes = 16 * 1024 * 1024;
    private const int HeaderSize = 16;
    private const int SimulatorHeaderSize = 72;
    private const int CollisionRefSize = 36;
    private const int ChainHeaderSize = 48;
    private const int NodeSize = 84;
    private const int ConnectorSize = 32;
    private const int AttractSize = 52;
    private const int PinSize = 48;
    private const int SpringSize = 16;
    private const int PostAlignmentSize = 36;
    private const int CapsuleSize = 124;
    private const int EllipsoidSize = 116;
    private const int NormalPlaneSize = 92;
    private const int ThreePointPlaneSize = 168;
    private const int SphereSize = 80;
    private const uint ExtensionMagic = 0x4248_5045; // "EPHB"
    private const uint PackMagic = 0x4B43_4150; // "PACK"
    private const int PackHeaderSize = 16;
    private const int PackFooterSize = 24;
    private const uint EmptyPostAlignments = 0xCCCC_CCCC;

    /// <summary> 0 for a file that turns physics off entirely; <see cref="CurrentVersion"/> otherwise. </summary>
    public uint Version = CurrentVersion;
    /// <summary> 0 nothing, 1 collision only, 2 simulation only, 3 both. </summary>
    public uint DataType = 3;
    public PhybCollision Collision = new();
    public List<PhybSimulator> Simulators = [];
    public PhybExtension? Extension;
    /// <summary> The file had a simulator count of 0, which is written back when there still are none. </summary>
    public bool EmptySimulatorList;

    public PhybFile Clone() => new()
    {
        Version = Version,
        DataType = DataType,
        Collision = Collision.Clone(),
        Simulators = Simulators.Select(s => s.Clone()).ToList(),
        Extension = Extension?.Clone(),
        EmptySimulatorList = EmptySimulatorList,
    };

    // ---- Reading ----

    public static PhybFile Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || data.Length > MaxFileBytes)
            throw new InvalidDataException("This isn't a physics file: its size is wrong.");
        var file = new PhybFile { Version = U32(data, 0) };
        if (file.Version == 0)
        {
            file.DataType = 0;
            return file;
        }
        if (data.Length < HeaderSize)
            throw new InvalidDataException("The physics file is truncated.");
        file.DataType = U32(data, 4);
        var collisionOffset = U32(data, 8);
        var simulatorOffset = U32(data, 12);
        if (file.DataType == 0)
            return file;
        if (collisionOffset < HeaderSize || simulatorOffset < collisionOffset || simulatorOffset > data.Length)
            throw new InvalidDataException("The physics file's sections are out of order.");

        var end = data.Length;
        if (ReadExtension(data, out var extension, out var extensionStart))
        {
            file.Extension = extension;
            end = extensionStart;
        }
        if (simulatorOffset > end)
            throw new InvalidDataException("The physics file's simulators overlap its extension.");
        if (collisionOffset != simulatorOffset)
            file.Collision = ReadCollision(data[..(int)simulatorOffset], (int)collisionOffset);
        if (simulatorOffset < end)
        {
            file.Simulators = ReadSimulators(data[..end], (int)simulatorOffset);
            file.EmptySimulatorList = file.Simulators.Count == 0;
        }
        return file;
    }

    private static PhybCollision ReadCollision(ReadOnlySpan<byte> data, int at)
    {
        Need(data, at, 8);
        var collision = new PhybCollision { Padding = data.Slice(at + 5, 3).ToArray() };
        int capsules = data[at], ellipsoids = data[at + 1], normalPlanes = data[at + 2], threePointPlanes = data[at + 3], spheres = data[at + 4];
        var p = at + 8;
        Need(data, p, capsules * CapsuleSize + ellipsoids * EllipsoidSize + normalPlanes * NormalPlaneSize +
                      threePointPlanes * ThreePointPlaneSize + spheres * SphereSize);
        for (var i = 0; i < capsules; i++, p += CapsuleSize)
            collision.Capsules.Add(new PhybCapsule
            {
                Name = Name(data, p), StartBone = Name(data, p + 32), EndBone = Name(data, p + 64),
                StartOffset = V3(data, p + 96), EndOffset = V3(data, p + 108), Radius = F32(data, p + 120),
            });
        for (var i = 0; i < ellipsoids; i++, p += EllipsoidSize)
            collision.Ellipsoids.Add(new PhybEllipsoid
            {
                Name = Name(data, p), Bone = Name(data, p + 32),
                Offset1 = V3(data, p + 64), Offset2 = V3(data, p + 76), Offset3 = V3(data, p + 88), Offset4 = V3(data, p + 100),
                Radius = F32(data, p + 112),
            });
        for (var i = 0; i < normalPlanes; i++, p += NormalPlaneSize)
            collision.NormalPlanes.Add(new PhybNormalPlane
            {
                Name = Name(data, p), Bone = Name(data, p + 32), Offset = V3(data, p + 64), Normal = V3(data, p + 76), Thickness = F32(data, p + 88),
            });
        for (var i = 0; i < threePointPlanes; i++, p += ThreePointPlaneSize)
            collision.ThreePointPlanes.Add(new PhybThreePointPlane
            {
                Name = Name(data, p), Bone = Name(data, p + 32),
                Unknown1 = V4(data, p + 64), Unknown2 = V4(data, p + 80), Unknown3 = V4(data, p + 96), Unknown4 = V4(data, p + 112),
                Offset = V3(data, p + 128), Unknown5 = V3(data, p + 140), Unknown6 = V3(data, p + 152), Thickness = F32(data, p + 164),
            });
        for (var i = 0; i < spheres; i++, p += SphereSize)
            collision.Spheres.Add(new PhybSphere
            {
                Name = Name(data, p), Bone = Name(data, p + 32), Offset = V3(data, p + 64), Thickness = F32(data, p + 76),
            });
        return collision;
    }

    private static List<PhybSimulator> ReadSimulators(ReadOnlySpan<byte> data, int at)
    {
        Need(data, at, 4);
        var count = U32(data, at);
        if (count > 255)
            throw new InvalidDataException($"The physics file claims {count} simulators.");
        // List offsets count from just after the simulator count.
        var basis = at + 4;
        Need(data, basis, (int)count * SimulatorHeaderSize);
        var simulators = new List<PhybSimulator>((int)count);
        for (var s = 0; s < count; s++)
        {
            var h = basis + s * SimulatorHeaderSize;
            var counts = data.Slice(h, 8).ToArray();
            var simulator = new PhybSimulator
            {
                Gravity = V3(data, h + 8), Wind = V3(data, h + 20),
                ConstraintLoop = I16(data, h + 32), CollisionLoop = I16(data, h + 34),
                Flags = (PhybSimulatorFlags)data[h + 36], Group = data[h + 37], Padding = U16(data, h + 38),
            };

            var p = ListStart(data, basis, h, counts, 0, CollisionRefSize);
            for (var i = 0; i < counts[0]; i++, p += CollisionRefSize)
                simulator.Collisions.Add(CollisionRef(data, p));
            p = ListStart(data, basis, h, counts, 1, CollisionRefSize);
            for (var i = 0; i < counts[1]; i++, p += CollisionRefSize)
                simulator.CollisionConnectors.Add(CollisionRef(data, p));
            p = ListStart(data, basis, h, counts, 2, ChainHeaderSize);
            for (var i = 0; i < counts[2]; i++, p += ChainHeaderSize)
                simulator.Chains.Add(ReadChain(data, p, basis));
            p = ListStart(data, basis, h, counts, 3, ConnectorSize);
            for (var i = 0; i < counts[3]; i++, p += ConnectorSize)
                simulator.Connectors.Add(new PhybConnector
                {
                    Chain1 = I16(data, p), Chain2 = I16(data, p + 2), Node1 = I16(data, p + 4), Node2 = I16(data, p + 6),
                    CollisionRadius = F32(data, p + 8), Friction = F32(data, p + 12), Dampening = F32(data, p + 16), Repulsion = F32(data, p + 20),
                    CollisionFlags = U32(data, p + 24), ContinuousCollisionFlags = U32(data, p + 28),
                });
            p = ListStart(data, basis, h, counts, 4, AttractSize);
            for (var i = 0; i < counts[4]; i++, p += AttractSize)
                simulator.Attracts.Add(new PhybAttract
                {
                    Bone = Name(data, p), Offset = V3(data, p + 32), Chain = I16(data, p + 44), Node = I16(data, p + 46), Stiffness = F32(data, p + 48),
                });
            p = ListStart(data, basis, h, counts, 5, PinSize);
            for (var i = 0; i < counts[5]; i++, p += PinSize)
                simulator.Pins.Add(new PhybPin { Bone = Name(data, p), Offset = V3(data, p + 32), Chain = I16(data, p + 44), Node = I16(data, p + 46) });
            p = ListStart(data, basis, h, counts, 6, SpringSize);
            for (var i = 0; i < counts[6]; i++, p += SpringSize)
                simulator.Springs.Add(new PhybSpring
                {
                    Chain1 = I16(data, p), Chain2 = I16(data, p + 2), Node1 = I16(data, p + 4), Node2 = I16(data, p + 6),
                    StretchStiffness = F32(data, p + 8), CompressStiffness = F32(data, p + 12),
                });
            p = ListStart(data, basis, h, counts, 7, PostAlignmentSize);
            for (var i = 0; i < counts[7]; i++, p += PostAlignmentSize)
                simulator.PostAlignments.Add(new PhybPostAlignment { CollisionName = Name(data, p), Chain = I16(data, p + 32), Node = I16(data, p + 34) });
            simulators.Add(simulator);
        }
        return simulators;
    }

    /// <summary> Where a simulator's list of <paramref name="index"/> starts, or -1 when it is empty. </summary>
    private static int ListStart(ReadOnlySpan<byte> data, int basis, int header, byte[] counts, int index, int size)
    {
        if (counts[index] == 0)
            return -1;
        var offset = U32(data, header + 40 + index * 4);
        if (offset == EmptyPostAlignments)
            throw new InvalidDataException("A simulator list has entries but no place in the physics file.");
        var position = basis + (long)offset;
        if (position < basis || position + (long)counts[index] * size > data.Length)
            throw new InvalidDataException("A simulator list lies outside the physics file.");
        return (int)position;
    }

    private static PhybChain ReadChain(ReadOnlySpan<byte> data, int p, int basis)
    {
        var collisions = U16(data, p);
        var nodes = U16(data, p + 2);
        var chain = new PhybChain
        {
            Dampening = F32(data, p + 4), MaxSpeed = F32(data, p + 8), Friction = F32(data, p + 12),
            CollisionDampening = F32(data, p + 16), RepulsionStrength = F32(data, p + 20), LastBoneOffset = V3(data, p + 24),
            Type = U32(data, p + 36),
        };
        if (collisions > 255 || nodes > 255)
            throw new InvalidDataException("A chain in the physics file is too long.");
        var at = basis + (long)U32(data, p + 40);
        if (collisions > 0 && (at < basis || at + collisions * CollisionRefSize > data.Length))
            throw new InvalidDataException("A chain's collision list lies outside the physics file.");
        for (var i = 0; i < collisions; i++)
            chain.Collisions.Add(CollisionRef(data, (int)at + i * CollisionRefSize));
        at = basis + (long)U32(data, p + 44);
        if (nodes > 0 && (at < basis || at + nodes * NodeSize > data.Length))
            throw new InvalidDataException("A chain's nodes lie outside the physics file.");
        for (var i = 0; i < nodes; i++)
        {
            var n = (int)at + i * NodeSize;
            chain.Nodes.Add(new PhybNode
            {
                Bone = Name(data, n), Radius = F32(data, n + 32), AttractByAnimation = F32(data, n + 36), WindScale = F32(data, n + 40),
                GravityScale = F32(data, n + 44), ConeMaxAngle = F32(data, n + 48), ConeAxisOffset = V3(data, n + 52),
                ConstraintPlaneNormal = V3(data, n + 64), CollisionFlags = U32(data, n + 76), ContinuousCollisionFlags = U32(data, n + 80),
            });
        }
        return chain;
    }

    private static PhybCollisionRef CollisionRef(ReadOnlySpan<byte> data, int p)
        => new() { Name = Name(data, p), Type = (PhybCollisionType)I32(data, p + 32) };

    /// <summary>
    /// The extension block at the end of the file: a PACK footer gives the size of what follows the
    /// simulators (alignment padding included); the block itself starts at the next multiple of 8.
    /// </summary>
    private static bool ReadExtension(ReadOnlySpan<byte> data, out PhybExtension? extension, out int start)
    {
        extension = null;
        start = data.Length;
        if (data.Length < HeaderSize + PackHeaderSize + PackFooterSize || U32(data, data.Length - PackFooterSize) != PackMagic)
            return false;
        var total = BinaryPrimitives.ReadUInt64LittleEndian(data[(data.Length - 8)..]);
        if (total < PackHeaderSize + PackFooterSize || total > (ulong)(data.Length - HeaderSize))
            throw new InvalidDataException("The physics file's extension block has a wrong size.");
        // The game aligns the block to 8 bytes and counts the padding in the size; some tools align
        // to 16 without counting it.
        var unaligned = data.Length - (int)total;
        var header = -1;
        foreach (var candidate in new[] { (unaligned + 7) & ~7, (unaligned + 15) & ~15 })
        {
            if (candidate + PackHeaderSize <= data.Length - PackFooterSize && U32(data, candidate) == ExtensionMagic)
            {
                header = candidate;
                break;
            }
        }
        if (header < 0)
            throw new InvalidDataException("The physics file ends with an unknown block.");
        var length = BinaryPrimitives.ReadInt64LittleEndian(data[(header + 8)..]);
        var payload = header + PackHeaderSize;
        if (length < 0 || payload + length != data.Length - PackFooterSize)
            throw new InvalidDataException("The physics file's extension block has a wrong length.");
        extension = new PhybExtension
        {
            Version = U16(data, header + 4),
            Count = U16(data, header + 6),
            Payload = data.Slice(payload, (int)length).ToArray(),
        };
        start = unaligned;
        return true;
    }

    // ---- Writing ----

    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        if (Version == 0)
        {
            w.Write(0u);
            w.Write(12u);
            w.Write(12u);
            return stream.ToArray();
        }
        w.Write(Version);
        w.Write(DataType);
        if (DataType == 0)
        {
            w.Write((uint)HeaderSize);
            w.Write((uint)HeaderSize);
            return stream.ToArray();
        }
        w.Write((uint)HeaderSize);
        w.Write(0u); // the simulators' offset, known once the shapes are written
        if (!Collision.IsEmpty)
            WriteCollision(w, Collision);
        var simulatorOffset = (uint)stream.Position;
        if (Simulators.Count > 0 || EmptySimulatorList || Extension is not null)
            WriteSimulators(stream, w, Simulators);
        var end = stream.Position;
        stream.Position = 12;
        w.Write(simulatorOffset);
        stream.Position = end;
        if (Extension is { } extension)
            WriteExtension(stream, w, extension);
        return stream.ToArray();
    }

    private static void WriteCollision(BinaryWriter w, PhybCollision collision)
    {
        w.Write(Count(collision.Capsules.Count, "collision capsules"));
        w.Write(Count(collision.Ellipsoids.Count, "collision ellipsoids"));
        w.Write(Count(collision.NormalPlanes.Count, "collision planes"));
        w.Write(Count(collision.ThreePointPlanes.Count, "collision planes"));
        w.Write(Count(collision.Spheres.Count, "collision spheres"));
        w.Write(collision.Padding.Length == 3 ? collision.Padding : [0xCC, 0xCC, 0xCC]);
        foreach (var s in collision.Capsules)
        {
            Write(w, s.Name); Write(w, s.StartBone); Write(w, s.EndBone);
            Write(w, s.StartOffset); Write(w, s.EndOffset); w.Write(s.Radius);
        }
        foreach (var s in collision.Ellipsoids)
        {
            Write(w, s.Name); Write(w, s.Bone);
            Write(w, s.Offset1); Write(w, s.Offset2); Write(w, s.Offset3); Write(w, s.Offset4); w.Write(s.Radius);
        }
        foreach (var s in collision.NormalPlanes)
        {
            Write(w, s.Name); Write(w, s.Bone); Write(w, s.Offset); Write(w, s.Normal); w.Write(s.Thickness);
        }
        foreach (var s in collision.ThreePointPlanes)
        {
            Write(w, s.Name); Write(w, s.Bone);
            Write(w, s.Unknown1); Write(w, s.Unknown2); Write(w, s.Unknown3); Write(w, s.Unknown4);
            Write(w, s.Offset); Write(w, s.Unknown5); Write(w, s.Unknown6); w.Write(s.Thickness);
        }
        foreach (var s in collision.Spheres)
        {
            Write(w, s.Name); Write(w, s.Bone); Write(w, s.Offset); w.Write(s.Thickness);
        }
    }

    /// <summary>
    /// The game's layout: the count, every simulator's header, then each kind of list for all
    /// simulators in turn (shapes, the chains' shapes, connector shapes, chains, connectors, attracts,
    /// pins, springs, post alignments) and every chain's nodes last. Offsets count from after the
    /// count; an empty list has offset 0, except post alignments (0xCCCCCCCC).
    /// </summary>
    private static void WriteSimulators(MemoryStream stream, BinaryWriter w, List<PhybSimulator> simulators)
    {
        w.Write((uint)simulators.Count);
        if (simulators.Count > 255)
            throw new InvalidDataException("A physics file holds at most 255 simulators.");
        var basis = stream.Position;
        stream.Position = basis + simulators.Count * SimulatorHeaderSize;
        var offsets = new uint[simulators.Count, 8];
        uint Here() => (uint)(stream.Position - basis);

        void Lists<T>(int slot, Func<PhybSimulator, List<T>> select, Action<T> write, uint empty = 0)
        {
            for (var s = 0; s < simulators.Count; s++)
            {
                var list = select(simulators[s]);
                offsets[s, slot] = list.Count > 0 ? Here() : empty;
                foreach (var item in list)
                    write(item);
            }
        }

        Lists(0, s => s.Collisions, c => WriteRef(w, c));
        var chainCollisions = new Dictionary<PhybChain, uint>(ReferenceEqualityComparer.Instance);
        foreach (var chain in simulators.SelectMany(s => s.Chains))
        {
            if (chain.Collisions.Count == 0)
                continue;
            chainCollisions[chain] = Here();
            foreach (var c in chain.Collisions)
                WriteRef(w, c);
        }
        Lists(1, s => s.CollisionConnectors, c => WriteRef(w, c));
        var chainHeaders = new List<(PhybChain Chain, long Position)>();
        Lists(2, s => s.Chains, chain =>
        {
            chainHeaders.Add((chain, stream.Position));
            w.Write((ushort)Count(chain.Collisions.Count, "chain shapes"));
            w.Write((ushort)Count(chain.Nodes.Count, "chain nodes"));
            w.Write(chain.Dampening); w.Write(chain.MaxSpeed); w.Write(chain.Friction);
            w.Write(chain.CollisionDampening); w.Write(chain.RepulsionStrength);
            Write(w, chain.LastBoneOffset);
            w.Write(chain.Type);
            w.Write(chainCollisions.GetValueOrDefault(chain));
            w.Write(0u); // the nodes' offset, known once they are written
        });
        Lists(3, s => s.Connectors, c =>
        {
            w.Write(c.Chain1); w.Write(c.Chain2); w.Write(c.Node1); w.Write(c.Node2);
            w.Write(c.CollisionRadius); w.Write(c.Friction); w.Write(c.Dampening); w.Write(c.Repulsion);
            w.Write(c.CollisionFlags); w.Write(c.ContinuousCollisionFlags);
        });
        Lists(4, s => s.Attracts, a =>
        {
            Write(w, a.Bone); Write(w, a.Offset); w.Write(a.Chain); w.Write(a.Node); w.Write(a.Stiffness);
        });
        Lists(5, s => s.Pins, p =>
        {
            Write(w, p.Bone); Write(w, p.Offset); w.Write(p.Chain); w.Write(p.Node);
        });
        Lists(6, s => s.Springs, p =>
        {
            w.Write(p.Chain1); w.Write(p.Chain2); w.Write(p.Node1); w.Write(p.Node2); w.Write(p.StretchStiffness); w.Write(p.CompressStiffness);
        });
        Lists(7, s => s.PostAlignments, p =>
        {
            Write(w, p.CollisionName); w.Write(p.Chain); w.Write(p.Node);
        }, EmptyPostAlignments);
        var nodeOffsets = new List<uint>(chainHeaders.Count);
        foreach (var (chain, _) in chainHeaders)
        {
            nodeOffsets.Add(chain.Nodes.Count > 0 ? Here() : 0u);
            foreach (var n in chain.Nodes)
            {
                Write(w, n.Bone); w.Write(n.Radius); w.Write(n.AttractByAnimation); w.Write(n.WindScale); w.Write(n.GravityScale);
                w.Write(n.ConeMaxAngle); Write(w, n.ConeAxisOffset); Write(w, n.ConstraintPlaneNormal);
                w.Write(n.CollisionFlags); w.Write(n.ContinuousCollisionFlags);
            }
        }
        var end = stream.Position;
        for (var i = 0; i < chainHeaders.Count; i++)
        {
            stream.Position = chainHeaders[i].Position + 44;
            w.Write(nodeOffsets[i]);
        }
        stream.Position = basis;
        for (var s = 0; s < simulators.Count; s++)
        {
            var simulator = simulators[s];
            w.Write(Count(simulator.Collisions.Count, "simulator shapes"));
            w.Write(Count(simulator.CollisionConnectors.Count, "simulator connector shapes"));
            w.Write(Count(simulator.Chains.Count, "chains"));
            w.Write(Count(simulator.Connectors.Count, "connectors"));
            w.Write(Count(simulator.Attracts.Count, "attracts"));
            w.Write(Count(simulator.Pins.Count, "pins"));
            w.Write(Count(simulator.Springs.Count, "springs"));
            w.Write(Count(simulator.PostAlignments.Count, "post alignments"));
            Write(w, simulator.Gravity);
            Write(w, simulator.Wind);
            w.Write(simulator.ConstraintLoop);
            w.Write(simulator.CollisionLoop);
            w.Write((byte)simulator.Flags);
            w.Write(simulator.Group);
            w.Write(simulator.Padding);
            for (var i = 0; i < 8; i++)
                w.Write(offsets[s, i]);
        }
        stream.Position = end;
    }

    private static void WriteExtension(MemoryStream stream, BinaryWriter w, PhybExtension extension)
    {
        var unaligned = stream.Position;
        while (stream.Position % 8 != 0)
            w.Write((byte)0);
        var padding = stream.Position - unaligned;
        w.Write(ExtensionMagic);
        w.Write(extension.Version);
        w.Write(extension.Count);
        w.Write((long)extension.Payload.Length);
        w.Write(extension.Payload);
        w.Write(PackMagic);
        w.Write((ushort)1);
        w.Write((ushort)1);
        w.Write(-(long)(PackHeaderSize + extension.Payload.Length));
        w.Write((ulong)(padding + PackHeaderSize + extension.Payload.Length + PackFooterSize));
    }

    private static void WriteRef(BinaryWriter w, PhybCollisionRef c)
    {
        Write(w, c.Name);
        w.Write((int)c.Type);
    }

    private static byte Count(int count, string what)
        => count is >= 0 and <= 255 ? (byte)count : throw new InvalidDataException($"A physics file holds at most 255 {what} in one place.");

    private static void Write(BinaryWriter w, PhybName name)
    {
        Span<byte> field = stackalloc byte[PhybName.Size];
        name.CopyTo(field);
        w.Write(field);
    }

    private static void Write(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
    }

    private static void Write(BinaryWriter w, Vector4 v)
    {
        w.Write(v.X); w.Write(v.Y); w.Write(v.Z); w.Write(v.W);
    }

    // ---- Primitive reads (bounds-checked) ----

    private static void Need(ReadOnlySpan<byte> data, long at, long length)
    {
        if (at < 0 || length < 0 || at + length > data.Length)
            throw new InvalidDataException("The physics file is truncated.");
    }

    private static uint U32(ReadOnlySpan<byte> d, int at) { Need(d, at, 4); return BinaryPrimitives.ReadUInt32LittleEndian(d[at..]); }
    private static int I32(ReadOnlySpan<byte> d, int at) { Need(d, at, 4); return BinaryPrimitives.ReadInt32LittleEndian(d[at..]); }
    private static ushort U16(ReadOnlySpan<byte> d, int at) { Need(d, at, 2); return BinaryPrimitives.ReadUInt16LittleEndian(d[at..]); }
    private static short I16(ReadOnlySpan<byte> d, int at) { Need(d, at, 2); return BinaryPrimitives.ReadInt16LittleEndian(d[at..]); }
    private static float F32(ReadOnlySpan<byte> d, int at) { Need(d, at, 4); return BinaryPrimitives.ReadSingleLittleEndian(d[at..]); }
    private static Vector3 V3(ReadOnlySpan<byte> d, int at) => new(F32(d, at), F32(d, at + 4), F32(d, at + 8));
    private static Vector4 V4(ReadOnlySpan<byte> d, int at) => new(F32(d, at), F32(d, at + 4), F32(d, at + 8), F32(d, at + 12));
    private static PhybName Name(ReadOnlySpan<byte> d, int at) { Need(d, at, PhybName.Size); return PhybName.Read(d[at..]); }
}
