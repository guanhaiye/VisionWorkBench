using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VisionWorkbench.Application;

/// <summary>暂停或恢复训练 Worker 进程。训练 Worker 是独立进程，暂停其全部线程可以保留当前训练状态。</summary>
internal static class ProcessPauseController
{
    public static bool TrySuspend(Process process)
    {
        try
        {
            return NtSuspendProcess(process.Handle) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryResume(Process process)
    {
        try
        {
            return NtResumeProcess(process.Handle) == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);
}
