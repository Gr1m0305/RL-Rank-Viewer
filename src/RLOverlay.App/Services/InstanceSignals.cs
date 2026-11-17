using System.IO;
using System.Threading;

namespace RLOverlay.App.Services;

/// <summary>
/// Named kernel objects shared by every copy of the app in this Windows session: the single-instance mutex, and two
/// events a second process can set to make the running copy open its settings or quit (setup and uninstall use the
/// latter before replacing or removing the exe).
/// </summary>
public static class InstanceSignals
{
    public const string MutexName = @"Local\RLOverlay.SingleInstance";

    public const string ShowSettingsEventName = @"Local\RLOverlay.ShowSettings";

    public const string QuitEventName = @"Local\RLOverlay.Quit";

    /// <summary>True while a copy of the app is running in normal mode (it holds the mutex).</summary>
    public static bool IsAppRunning()
    {
        try
        {
            using var mutex = Mutex.OpenExisting(MutexName);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Asks the running copy to quit and waits for it. False if it is still running after the timeout.</summary>
    public static bool CloseRunningApp(TimeSpan timeout)
    {
        if (!IsAppRunning())
        {
            return true;
        }

        try
        {
            using var quit = EventWaitHandle.OpenExisting(QuitEventName);
            quit.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            // An older version without the quit event: the caller tells the user to quit it from the tray.
        }

        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (!IsAppRunning())
            {
                // Give the process a moment to release its exe after the mutex goes.
                Thread.Sleep(400);
                return true;
            }

            Thread.Sleep(200);
        }

        return !IsAppRunning();
    }
}
