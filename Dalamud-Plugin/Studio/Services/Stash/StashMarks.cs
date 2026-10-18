using System;
using System.Collections.Generic;
using System.Linq;

namespace InstantEdit.Studio.Services.Stash;

/// <summary>
/// Which of the file stash's rows are marked, to be dragged together: files of one kind only, in the
/// order they were marked. A click marks just that row, Ctrl + click adds or takes away one, and
/// Shift + click marks the rows from the last one clicked to this one (adding to the rest with Ctrl).
/// </summary>
internal sealed class StashMarks
{
    private readonly List<string> _files = new();
    private string? _anchor;

    public IReadOnlyList<string> Files => _files;
    public StashKind Kind { get; private set; }
    public int Count => _files.Count;

    public bool Contains(string file, StashKind kind)
        => kind == Kind && _files.Contains(file, StringComparer.OrdinalIgnoreCase);

    public void Clear()
    {
        _files.Clear();
        _anchor = null;
    }

    /// <summary>A click on a row; <paramref name="order"/> is every markable row as listed, so a range can be worked out.</summary>
    public void Click(string file, StashKind kind, bool ctrl, bool shift, IReadOnlyList<(string File, StashKind Kind)> order)
    {
        // Files of another kind can't be dropped together with these: marking one starts over.
        if (kind != Kind)
        {
            Clear();
            Kind = kind;
        }

        if (shift && _anchor != null)
        {
            var rows = order.Where(r => r.Kind == kind).Select(r => r.File).ToList();
            int from = rows.FindIndex(f => Same(f, _anchor));
            int to   = rows.FindIndex(f => Same(f, file));
            if (from >= 0 && to >= 0)
            {
                if (!ctrl)
                    _files.Clear();
                for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++)
                    Add(rows[i]);
                return;   // the anchor stays, so the range can be changed with another Shift + click
            }
        }

        if (ctrl)
        {
            if (_files.RemoveAll(f => Same(f, file)) == 0)
                _files.Add(file);
        }
        else
        {
            _files.Clear();
            _files.Add(file);
        }
        _anchor = file;
    }

    private void Add(string file)
    {
        if (!_files.Any(f => Same(f, file)))
            _files.Add(file);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
