using Dalamud.Interface.DragDrop;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace InstantEdit.Studio;

/// <summary> Dalamud's services for the Mod Browser's code, set once by <see cref="StudioHost"/>. </summary>
internal static class StudioServices
{
    public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    public static IPluginLog Log { get; private set; } = null!;
    public static IDataManager DataManager { get; private set; } = null!;
    public static ITextureProvider TextureProvider { get; private set; } = null!;
    public static IDragDropManager DragDrop { get; private set; } = null!;
    public static IFramework Framework { get; private set; } = null!;

    public static void Initialize(IDalamudPluginInterface pluginInterface, IPluginLog log, IDataManager dataManager,
        ITextureProvider textureProvider, IDragDropManager dragDrop, IFramework framework)
    {
        PluginInterface = pluginInterface;
        Log = log;
        DataManager = dataManager;
        TextureProvider = textureProvider;
        DragDrop = dragDrop;
        Framework = framework;
    }
}
