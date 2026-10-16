using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace InstantEdit.Services.Materials;

/// <summary>
/// The in-memory preview of a material's colour rows: the character keeps one colour-table texture
/// per model slot and material slot, which this swaps for one built from the edited rows (and back).
/// The original is kept alive with a reference of its own while swapped out. When the game makes a
/// new table texture itself (a dye change), the binding follows it. Framework thread only.
/// </summary>
internal sealed unsafe class LiveColorTable : IDisposable
{
    private const uint CreateUnknown = 7;

    private readonly IObjectTable objects;
    private readonly IPluginLog log;
    private readonly List<Slot> slots = [];
    private ColorTable? pending;
    private RowHighlight? pendingHighlight;
    private bool dirty;
    private bool warned;

    /// <summary>
    /// One bound table slot. <see cref="Original"/> is the game's texture, held with a reference of
    /// ours; <see cref="Current"/> is ours while it is in the slot, else 0.
    /// </summary>
    private sealed class Slot(MaterialBinding binding, nint characterBase, nint original)
    {
        public MaterialBinding Binding { get; } = binding;
        public nint CharacterBase { get; } = characterBase;
        public nint Original { get; set; } = original;
        public nint Current { get; set; }
    }

    public LiveColorTable(IObjectTable objects, IPluginLog log)
    {
        this.objects = objects;
        this.log = log;
    }

    public bool Bound => slots.Count > 0;

    /// <summary>Slots that show the edited rows now.</summary>
    public int Swapped => slots.Count(slot => slot.Current != 0);

    public string LastUpdate { get; private set; } = string.Empty;

    /// <summary>Binds the table slots of the places given (places without a table texture are skipped); returns how many were added.</summary>
    public int Bind(IReadOnlyList<MaterialBinding> bindings)
    {
        Revalidate();
        var added = 0;
        foreach (var binding in bindings)
        {
            if (slots.Any(slot => slot.Binding == binding))
                continue;
            var characterBase = LiveMaterial.DrawObject(objects, binding.ObjectIndex, binding.Kind);
            var pointer = SlotPointer(characterBase, binding);
            if (pointer == null || *pointer == null)
                continue;
            (*pointer)->IncRef();
            slots.Add(new Slot(binding, (nint)characterBase, (nint)(*pointer)));
            added++;
        }
        return added;
    }

    /// <summary>Asks for the rows to be shown (with a pair lit up); the next <see cref="Update"/> builds the texture.</summary>
    public void Set(ColorTable table, RowHighlight? highlight)
    {
        pending = table;
        pendingHighlight = highlight;
        dirty = true;
    }

    /// <summary>Builds and swaps in the asked-for table once per call; a highlight pulses, so it is rebuilt every frame while shown.</summary>
    public void Update(float seconds)
    {
        if (!dirty || pending == null || slots.Count == 0)
            return;
        dirty = pendingHighlight != null;
        Revalidate();
        Half[] halves;
        try
        {
            halves = ColorTablePacking.Pack(pending, pendingHighlight, seconds);
        }
        catch (InvalidOperationException)
        {
            return;
        }
        foreach (var slot in slots)
        {
            var pointer = SlotPointer((CharacterBase*)slot.CharacterBase, slot.Binding);
            if (pointer == null)
                continue;
            var texture = Texture.CreateTexture2D(ColorTablePacking.Width, ColorTablePacking.Height, 1, TextureFormat.R16G16B16A16_FLOAT,
                TextureFlags.TextureNoSwizzle | TextureFlags.Immutable | TextureFlags.Managed, CreateUnknown);
            if (texture == null)
            {
                Warn("The game could not make a colour table texture.");
                continue;
            }
            bool filled;
            fixed (Half* data = halves)
                filled = texture->InitializeContents(data);
            if (!filled)
            {
                texture->DecRef();
                Warn("The game could not fill a colour table texture.");
                continue;
            }
            var previous = (Texture*)Interlocked.Exchange(ref *(nint*)pointer, (nint)texture);
            // The slot's reference to the original moves to ours; one to our previous texture is let go.
            if (previous != null)
                previous->DecRef();
            slot.Current = (nint)texture;
        }
        LastUpdate = pendingHighlight is { } lit ? $"rows with rows {lit.RowA + 1} and {lit.RowB + 1} lit" : "rows";
    }

    /// <summary>Puts the game's own table textures back where ours still sit, and lets go of everything.</summary>
    public void Restore()
    {
        try
        {
            foreach (var slot in slots)
            {
                var characterBase = LiveMaterial.DrawObject(objects, slot.Binding.ObjectIndex, slot.Binding.Kind);
                var pointer = (nint)characterBase == slot.CharacterBase ? SlotPointer(characterBase, slot.Binding) : null;
                if (pointer != null && slot.Current != 0 && *pointer == (Texture*)slot.Current)
                {
                    // Our reference to the original becomes the slot's again.
                    *pointer = (Texture*)slot.Original;
                    ((Texture*)slot.Current)->DecRef();
                }
                else
                    ((Texture*)slot.Original)->DecRef();
            }
        }
        catch (Exception e)
        {
            log.Warning(e, "Could not put the colour table back.");
        }
        slots.Clear();
        pending = null;
        pendingHighlight = null;
        dirty = false;
    }

    public void Dispose() => Restore();

    /// <summary>
    /// Drops slots whose draw object is gone (letting go of our reference to the original), and
    /// follows a table texture the game made anew: it becomes the original, and the rows are shown again.
    /// </summary>
    private void Revalidate()
    {
        for (var i = slots.Count - 1; i >= 0; i--)
        {
            var slot = slots[i];
            var characterBase = LiveMaterial.DrawObject(objects, slot.Binding.ObjectIndex, slot.Binding.Kind);
            var pointer = (nint)characterBase == slot.CharacterBase ? SlotPointer(characterBase, slot.Binding) : null;
            if (pointer == null || *pointer == null)
            {
                ((Texture*)slot.Original)->DecRef();
                slots.RemoveAt(i);
                continue;
            }
            var holding = (nint)(*pointer);
            if (holding == slot.Current || (slot.Current == 0 && holding == slot.Original))
                continue;
            // The game swapped in a table of its own, letting go of ours.
            ((Texture*)slot.Original)->DecRef();
            (*pointer)->IncRef();
            slot.Original = holding;
            slot.Current = 0;
            dirty = pending != null;
        }
    }

    private static Texture** SlotPointer(CharacterBase* characterBase, MaterialBinding binding)
    {
        if (characterBase == null || characterBase->ColorTableTextures == null || binding.MaterialSlot >= CharacterBase.MaterialsPerSlot)
            return null;
        var index = binding.ModelSlot * CharacterBase.MaterialsPerSlot + binding.MaterialSlot;
        return index < 0 || index >= characterBase->SlotCount * CharacterBase.MaterialsPerSlot ? null : characterBase->ColorTableTextures + index;
    }

    private void Warn(string message)
    {
        if (warned)
            return;
        warned = true;
        log.Warning($"Material live preview: {message}");
    }
}
