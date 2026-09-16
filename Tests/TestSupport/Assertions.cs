namespace InstantEdit.TestSupport;

public static class Assertions
{
    public static int PassCount { get; private set; }

    public static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine($"[PASS] {message}");
        PassCount++;
    }

    public static void Check(bool condition, string name) => Require(condition, name);

    public static void Reject(Action action, string name)
    {
        try { action(); }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException)
        {
            Check(true, name);
            return;
        }
        throw new InvalidOperationException("Expected rejection: " + name);
    }
}
