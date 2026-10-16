using System;

namespace InstantEdit.Studio.Services.Stash;

/// <summary>
/// A typed value that is acted on only once it has stopped changing for a moment, so a folder path
/// is listed when the user is done typing it rather than on every keystroke. The first value, and
/// any after <see cref="Invalidate"/>, are acted on at once.
/// </summary>
internal sealed class SettledInput
{
    private readonly TimeSpan _delay;
    private bool _force = true;
    private string? _acted;
    private string? _seen;
    private DateTime _seenAt;

    public SettledInput(TimeSpan delay) => _delay = delay;

    /// <summary>Whether <paramref name="value"/> should be acted on now; true once per settled value.</summary>
    public bool Due(string value, DateTime now)
    {
        if (_force || (value == _seen && value != _acted && now - _seenAt >= _delay))
        {
            _force = false;
            _acted = _seen = value;
            return true;
        }
        if (value != _seen)
        {
            _seen = value;
            _seenAt = now;
        }
        return false;
    }

    /// <summary>The next value is acted on at once, changed or not.</summary>
    public void Invalidate() => _force = true;
}
