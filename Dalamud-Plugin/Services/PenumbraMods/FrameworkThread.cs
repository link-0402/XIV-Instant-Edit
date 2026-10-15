namespace InstantEdit.Services.PenumbraMods;

internal static class FrameworkThread
{
    /// <summary>
    /// Resumes on the thread pool. An await on RunOnFrameworkThread continues inline on the game
    /// thread that ran the work, so the file work that usually follows would stall the frame.
    /// </summary>
    internal static ThreadPoolHop LeaveFrameworkThread() => default;

    internal readonly struct ThreadPoolHop : System.Runtime.CompilerServices.ICriticalNotifyCompletion
    {
        public ThreadPoolHop GetAwaiter() => this;
        public bool IsCompleted => false;
        public void GetResult() { }
        public void OnCompleted(Action continuation)
            => ThreadPool.QueueUserWorkItem(static run => run(), continuation, preferLocal: false);
        public void UnsafeOnCompleted(Action continuation)
            => ThreadPool.UnsafeQueueUserWorkItem(static run => run(), continuation, preferLocal: false);
    }
}
