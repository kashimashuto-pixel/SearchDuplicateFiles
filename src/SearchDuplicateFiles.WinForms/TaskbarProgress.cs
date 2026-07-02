using System.Runtime.InteropServices;

namespace SearchDuplicateFiles.WinForms;

internal static class TaskbarProgress
{
    private static readonly Lazy<ITaskbarList3?> Taskbar = new(CreateTaskbarList);

    public static void SetIndeterminate(IWin32Window window)
    {
        SetState(window, TaskbarProgressState.Indeterminate);
    }

    public static void SetNormal(IWin32Window window, int completed, int total)
    {
        if (total <= 0)
        {
            SetState(window, TaskbarProgressState.Indeterminate);
            return;
        }

        var handle = GetHandle(window);
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var taskbar = Taskbar.Value;
        if (taskbar is null)
        {
            return;
        }

        var safeTotal = Math.Max(1, total);
        var safeCompleted = Math.Clamp(completed, 0, safeTotal);

        Try(() =>
        {
            taskbar.SetProgressState(handle, TaskbarProgressState.Normal);
            taskbar.SetProgressValue(handle, (ulong)safeCompleted, (ulong)safeTotal);
        });
    }

    public static void SetError(IWin32Window window)
    {
        SetState(window, TaskbarProgressState.Error);
    }

    public static void SetPaused(IWin32Window window)
    {
        SetState(window, TaskbarProgressState.Paused);
    }

    public static void Clear(IWin32Window window)
    {
        SetState(window, TaskbarProgressState.NoProgress);
    }

    private static void SetState(IWin32Window window, TaskbarProgressState state)
    {
        var handle = GetHandle(window);
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var taskbar = Taskbar.Value;
        if (taskbar is null)
        {
            return;
        }

        Try(() => taskbar.SetProgressState(handle, state));
    }

    private static IntPtr GetHandle(IWin32Window window)
    {
        if (window is Control { IsHandleCreated: false })
        {
            return IntPtr.Zero;
        }

        return window.Handle;
    }

    private static ITaskbarList3? CreateTaskbarList()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
        {
            return null;
        }

        try
        {
            var taskbar = (ITaskbarList3)(object)new TaskbarList();
            taskbar.HrInit();
            return taskbar;
        }
        catch (COMException)
        {
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (COMException)
        {
        }
        catch (InvalidComObjectException)
        {
        }
    }

    private enum TaskbarProgressState
    {
        NoProgress = 0,
        Indeterminate = 1,
        Normal = 2,
        Error = 4,
        Paused = 8
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class TaskbarList
    {
    }

    [ComImport]
    [Guid("C43DC798-95D1-4BEA-9030-BB99E2983A1A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();

        void AddTab(IntPtr hwnd);

        void DeleteTab(IntPtr hwnd);

        void ActivateTab(IntPtr hwnd);

        void SetActiveAlt(IntPtr hwnd);

        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);

        void SetProgressState(IntPtr hwnd, TaskbarProgressState state);
    }
}
