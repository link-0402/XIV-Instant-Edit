using System.Runtime.InteropServices;

namespace InstantEdit.Services;

/// <summary>
/// Moves files to the Windows Recycle Bin, so a delete can be undone there. The shell asks before deleting a file
/// for good, on a drive without a Recycle Bin. It runs on a thread of its own, so that question never stops the game.
/// </summary>
internal static partial class RecycleBin
{
    private const uint Delete = 3;
    private const ushort Silent = 0x4, NoConfirmation = 0x10, AllowUndo = 0x40, NoErrorUi = 0x400, WantNukeWarning = 0x4000;

    // SHFILEOPSTRUCTW as 64-bit Windows lays it out.
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct FileOperation
    {
        public nint Window;
        public uint Function;
        public char* From;
        public char* To;
        public ushort Flags;
        public int Aborted;
        public nint NameMappings;
        public char* ProgressTitle;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    private static partial int SHFileOperation(ref FileOperation operation);

    /// <summary>Moves <paramref name="path"/> to the Recycle Bin; fails with the reason when it is still there afterwards.</summary>
    public static Task MoveAsync(string path)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The shell wants a single-threaded apartment.
        var thread = new Thread(() =>
        {
            try
            {
                Move(path);
                done.SetResult();
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        }) { IsBackground = true, Name = "InstantEdit.RecycleBin" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static unsafe void Move(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException("The file is gone.", full);
        // The shell reads a list of paths, each ended by a null and the list by another.
        var from = full + "\0\0";
        int result;
        bool aborted;
        fixed (char* list = from)
        {
            var operation = new FileOperation
            {
                Function = Delete,
                From = list,
                Flags = (ushort)(Silent | NoConfirmation | AllowUndo | NoErrorUi | WantNukeWarning),
            };
            result = SHFileOperation(ref operation);
            aborted = operation.Aborted != 0;
        }
        if (aborted)
            throw new OperationCanceledException("The file was kept.");
        if (result != 0 || File.Exists(full))
            throw new IOException($"The file could not be moved to the Recycle Bin (error {result}).");
    }
}
