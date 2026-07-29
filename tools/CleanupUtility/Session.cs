using System.Security.Principal;

namespace TtmCleanup;

/// <summary>Answers "are we elevated?" and "can we actually prompt the user?" — the latter distinguishes a
/// local interactive session (GUI or console prompt possible) from a remote/non-interactive shell.</summary>
internal static class Session
{
    public static bool Elevated
    {
        get
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    // A local, attended desktop session (session != 0 and equal to the active console session).
    public static bool InteractiveDesktop
    {
        get
        {
            try
            {
                if (!Environment.UserInteractive) return false;
                Native.ProcessIdToSessionId(Native.GetCurrentProcessId(), out uint mine);
                uint active = Native.WTSGetActiveConsoleSessionId();
                return mine != 0 && active != 0xFFFFFFFF && mine == active;
            }
            catch { return false; }
        }
    }

    public static bool ConsolePresent => Native.GetConsoleWindow() != nint.Zero;

    // We can put a Y/N prompt on the console only if there's a console with real (non-redirected) input.
    public static bool CanPromptConsole
    {
        get { try { return ConsolePresent && !Console.IsInputRedirected; } catch { return false; } }
    }
}
