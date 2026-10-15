using System.Runtime.CompilerServices;

namespace InstantEdit.TestSupport;

/// <summary>
/// Runs a suite's scenarios one after another. An exception that escapes a scenario is reported as a
/// failure with its stack trace and the next scenario still runs; <see cref="Finish"/> prints the summary.
/// Scenarios share the process, so one that changes static state must put it back, as before.
/// </summary>
public static class Scenarios
{
    private static int count;

    public static void Run(Action scenario, [CallerArgumentExpression(nameof(scenario))] string name = "")
    {
        count++;
        try { scenario(); }
        catch (Exception e) { Stopped(name, e); }
    }

    public static async Task RunAsync(Func<Task> scenario, [CallerArgumentExpression(nameof(scenario))] string name = "")
    {
        count++;
        try { await scenario(); }
        catch (Exception e) { Stopped(name, e); }
    }

    /// <summary>Prints the totals and every failure again; the result is the process exit code.</summary>
    public static int Finish(string suite)
    {
        var failures = Assertions.Failures;
        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine($"{suite}: {Assertions.PassCount} passed in {count} scenarios.");
            return 0;
        }
        Console.WriteLine($"{suite}: {failures.Count} failed, {Assertions.PassCount} passed in {count} scenarios.");
        foreach (var failure in failures)
            Console.WriteLine($"  [FAIL] {failure}");
        return 1;
    }

    private static void Stopped(string name, Exception e)
    {
        Assertions.Fail($"{Describe(name)} stopped: {e.GetType().Name}: {e.Message}");
        Console.WriteLine(e);
    }

    /// <summary>"() => Fixture.Run(Check, Reject)" reads as "Fixture.Run".</summary>
    private static string Describe(string expression)
    {
        var arrow = expression.IndexOf("=>", StringComparison.Ordinal);
        var body = (arrow < 0 ? expression : expression[(arrow + 2)..]).Trim();
        if (body.StartsWith("await ", StringComparison.Ordinal))
            body = body[6..].TrimStart();
        var call = body.IndexOf('(');
        return call > 0 ? body[..call] : body;
    }
}
