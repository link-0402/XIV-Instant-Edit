using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using InstantEdit.Studio.Models;

namespace InstantEdit.Studio.Services.Animation;

/// <summary>Copies an animation item's files into a mod folder. Dalamud-free, so the regression suite runs it.</summary>
internal static class AnimationFileWriter
{
    /// <summary>
    /// Copies each plan's source to <paramref name="relFor"/>(game path) under <paramref name="modPath"/>,
    /// reporting each file. Returns (game path, path in the mod) for every file written.
    /// </summary>
    public static List<(string GamePath, string Rel)> Write(IReadOnlyList<AnimationFilePlan> plans, string modPath, Func<string, string> relFor,
        BuildReport report, Target source, Action<string> step, CancellationToken ct)
    {
        var written = new List<(string, string)>();
        foreach (var plan in plans)
        {
            ct.ThrowIfCancellationRequested();
            var label = $"{Path.GetFileName(plan.Source)} ({string.Join(", ", plan.Races.Select(r => $"c{r}"))})";
            step(label);
            if (!File.Exists(plan.Source))
            {
                report.Add(BuildOutcome.Failed, BuildCategory.Animation, label, plan.GamePath, "file not found", source);
                continue;
            }
            try
            {
                var rel = relFor(plan.GamePath);
                var target = Path.Combine(modPath, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(plan.Source, target, overwrite: true);
                File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                written.Add((plan.GamePath, rel));
                report.Add(BuildOutcome.Ok, BuildCategory.Animation, label, plan.GamePath, null, source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Add(BuildOutcome.Failed, BuildCategory.Animation, label, plan.GamePath, ex.Message, source);
            }
        }
        return written;
    }
}
