using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using TerraFX.Interop.DirectX;

namespace InstantEdit.Ui.Material;

/// <summary>
/// ImGui images of single slices of the texture arrays the game keeps loaded for colour rows: the
/// detail patterns (tile_orb_array, tile_norm_array) and the reflection maps (sphere_d_array). Each
/// slice gets its own Direct3D view over the game's texture, so a mod that replaces an array shows.
/// Lumina can't read these files (in game 2026-10-17 all three threw), so they come from the GPU.
/// </summary>
/// <remarks>
/// A view is released a few frames after it was last drawn, never in the frame that drew it, since
/// ImGui renders the frame's images after the windows drew. Framework updates count the frames, so
/// views go away while the window is closed too.
/// </remarks>
internal sealed unsafe class TextureArraySlices : IDisposable
{
    public enum ArrayKind
    {
        PatternColour,
        PatternNormal,
        Reflection,
    }

    // CharacterUtility's resource handles for the three arrays.
    private const int TileOrbHandle = 81;
    private const int TileNormHandle = 82;
    private const int SphereHandle = 97;
    private const int KeepFrames = 3;

    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Dictionary<(nint Texture, int Slice), View> views = [];
    private bool failed;

    private sealed class View(nint resource, ID3D11ShaderResourceView* handle)
    {
        public readonly nint Resource = resource;
        public readonly ID3D11ShaderResourceView* Handle = handle;
        public int FramesLeft = KeepFrames;
    }

    public TextureArraySlices(IFramework framework, IPluginLog log)
    {
        this.framework = framework;
        this.log = log;
        framework.Update += Tick;
    }

    /// <summary>How many slices the array has and their size, or null while the game hasn't loaded it.</summary>
    public (int Count, Vector2 Size)? Info(ArrayKind array)
    {
        var texture = TextureOf(array);
        return texture == null ? null : (texture->ArraySize, new Vector2(texture->ActualWidth, texture->ActualHeight));
    }

    /// <summary>Draws one slice into the rectangle (kept square, centred), on a dark backing; false when it can't be shown.</summary>
    public bool Draw(ImDrawListPtr draw, ArrayKind array, int slice, Vector2 min, Vector2 size)
    {
        var texture = TextureOf(array);
        if (texture == null || slice < 0 || slice >= texture->ArraySize || HandleOf(texture, slice) is not { } handle)
            return false;
        draw.AddRectFilled(min, min + size, ImGui.GetColorU32(new Vector4(0, 0, 0, 1)));
        var uv = new Vector2((float)texture->ActualWidth / Math.Max(1u, texture->AllocatedWidth), (float)texture->ActualHeight / Math.Max(1u, texture->AllocatedHeight));
        draw.AddImage(handle, min, min + size, Vector2.Zero, uv);
        return true;
    }

    private static Texture* TextureOf(ArrayKind array)
    {
        var utility = CharacterUtility.Instance();
        if (utility == null)
            return null;
        var index = array switch
        {
            ArrayKind.PatternColour => TileOrbHandle,
            ArrayKind.PatternNormal => TileNormHandle,
            _ => SphereHandle,
        };
        var handle = (TextureResourceHandle*)utility->ResourceHandles[index].Value;
        var texture = handle == null ? null : handle->Texture;
        return texture == null || texture->D3D11ShaderResourceView == null || texture->D3D11Texture2D == null ? null : texture;
    }

    private ImTextureID? HandleOf(Texture* texture, int slice)
    {
        var key = ((nint)texture, slice);
        if (views.TryGetValue(key, out var view))
        {
            // The game may have loaded another texture at the same address (a mod replacing the array).
            if (view.Resource == (nint)texture->D3D11Texture2D)
            {
                view.FramesLeft = KeepFrames;
                return new ImTextureID(view.Handle);
            }
            Retire(key, view);
        }
        if (failed)
            return null;
        var created = Create(texture, slice);
        if (created == null)
            return null;
        views[key] = new View((nint)texture->D3D11Texture2D, created);
        return new ImTextureID(created);
    }

    private ID3D11ShaderResourceView* Create(Texture* texture, int slice)
    {
        var source = (ID3D11ShaderResourceView*)texture->D3D11ShaderResourceView;
        D3D11_SHADER_RESOURCE_VIEW_DESC description;
        source->GetDesc(&description);
        if (description.ViewDimension != D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_TEXTURE2DARRAY)
            return null;
        description.Texture2DArray.FirstArraySlice = (uint)slice;
        description.Texture2DArray.ArraySize = 1;
        ID3D11Device* device = null;
        ID3D11Resource* resource = null;
        ID3D11ShaderResourceView* view = null;
        source->GetDevice(&device);
        source->GetResource(&resource);
        try
        {
            var result = device->CreateShaderResourceView(resource, &description, &view);
            if (result.FAILED)
            {
                // One failure stops the thumbnails for the session instead of retrying every frame.
                failed = true;
                log.Warning($"Could not make a view of a colour row texture array (0x{result.Value:X8}); the material editor shows no thumbnails.");
                return null;
            }
            return view;
        }
        finally
        {
            resource->Release();
            device->Release();
        }
    }

    private void Tick(IFramework _)
    {
        if (views.Count == 0)
            return;
        foreach (var (key, view) in views.ToArray())
            if (--view.FramesLeft < 0)
                Retire(key, view);
    }

    private void Retire((nint, int) key, View view)
    {
        views.Remove(key);
        view.Handle->Release();
    }

    public void Dispose()
    {
        framework.Update -= Tick;
        foreach (var view in views.Values)
            view.Handle->Release();
        views.Clear();
    }
}
