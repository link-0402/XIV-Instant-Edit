namespace InstantEdit.TestSupport;

/// <summary>
/// The checks every regression suite makes. A failed check prints [FAIL] and is counted, and the
/// scenario carries on, so one failure never hides the checks after it. <see cref="Scenarios.Finish"/>
/// prints the totals and turns any failure into a non-zero exit code.
/// </summary>
public static class Assertions
{
    private static readonly Lock Gate = new();
    private static readonly List<string> FailureList = [];
    private static int passCount;

    public static int PassCount => Volatile.Read(ref passCount);

    public static IReadOnlyList<string> Failures
    {
        get { lock (Gate) return FailureList.ToArray(); }
    }

    public static void Require(bool condition, string message)
    {
        if (!condition)
        {
            Fail(message);
            return;
        }
        Console.WriteLine($"[PASS] {message}");
        Interlocked.Increment(ref passCount);
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
        catch (Exception e)
        {
            Fail($"Expected rejection: {name} (threw {e.GetType().Name}: {e.Message})");
            return;
        }
        Fail("Expected rejection: " + name);
    }

    public static void Fail(string message)
    {
        lock (Gate) FailureList.Add(message);
        Console.WriteLine($"[FAIL] {message}");
    }
}
