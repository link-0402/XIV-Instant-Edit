using InstantEdit.Services;
using InstantEdit.Services.PenumbraMods;
using InstantEdit.Studio.Services.Project;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The checks every plugin-bound path goes through before a file is touched: network paths in any
/// spelling and Windows device names.
/// </summary>
internal static class PathRuleScenarios
{
    public static void Run()
    {
        NetworkPathsInEverySpelling();
        DeviceNamesAreNotFileNames();
    }

    private static void NetworkPathsInEverySpelling()
    {
        // Windows reads / and \ alike, so these are all \\host\share\a.mdl.
        string[] network = [@"\\host\share\a.mdl", "//host/share/a.mdl", @"/\host\share\a.mdl", @"\/host/share/a.mdl", @"\\?\C:\a.mdl"];
        Require(network.All(PathRules.IsNetworkPath), "every spelling of a network or device path is recognised");
        Require(network.All(path => !ModPaths.IsSafeLocalModelPath(path)),
            "a stored model target on a network share is refused, whichever slashes it is written with");
        Require(!PathRules.IsNetworkPath(@"C:\Mods\a.mdl") && !PathRules.IsNetworkPath("C:/Mods/a.mdl") &&
                !PathRules.IsNetworkPath("/home/a.mdl") && !PathRules.IsNetworkPath("a.mdl") && !PathRules.IsNetworkPath("/"),
            "local paths and short strings are not network paths");
        Require(network.All(path => Throws(() => TextureFiles.EnsureLocalPath(path))),
            "texture editing refuses a network path before looking at it");
    }

    private static void DeviceNamesAreNotFileNames()
    {
        string[] devices = ["nul", "NUL", "con", "Aux", "prn", "COM1", "lpt9", "aux.v2", "nul .x", "Com5.name"];
        Require(devices.All(PathRules.IsReservedDeviceName), "device names are recognised with or without an extension");
        Require(devices.All(name => !PathRules.IsSafeVariantName(name)),
            "a variant cannot be named after a device: its .mdl would be written to the device and vanish");
        Require(devices.Where(name => !name.Contains(' ')).All(name => !ModPaths.IsSafeNewModName(name)),
            "a new mod cannot be named after a device either");
        Require(PathNormalizer.SafeSegment("Aux") == "_Aux" && PathNormalizer.SafeSegment("nul.png") == "_nul.png" &&
                PathNormalizer.SafeSegment("COM3") == "_COM3" && PathNormalizer.SafeSegment("Blue Hair") == "Blue Hair" &&
                PathNormalizer.SafeSegment("a/b") == "a_b" && PathNormalizer.SafeSegment("") == "_",
            "an option folder named after a device gets a '_' in front, and other names are unchanged");
        string[] fine = ["Hair", "Nullify", "console", "COM0", "COM10", "LPT", "Aux2", "Blue Hair", "my.nul"];
        Require(fine.All(name => !PathRules.IsReservedDeviceName(name) && PathRules.IsSafeVariantName(name)),
            "names that only start like a device are fine");
    }

    private static bool Throws(Action action)
    {
        try { action(); }
        catch (IOException) { return true; }
        return false;
    }
}
