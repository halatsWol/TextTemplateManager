using Microsoft.Win32;
using System;
using System.Runtime.InteropServices;

namespace TextTemplateManager.Helpers
{
    /// <summary>
    /// Registers the .ttmdata file association for the current user (HKCU — no admin). The installer
    /// writes the same keys; this lets the app re-assert them if another program took the association
    /// over. The values mirror installer.iss exactly, pointed at the running executable.
    /// </summary>
    public static class FileAssociation
    {
        private const string Ext = ".ttmdata";
        private const string ProgId = "TextTemplateManager.ttmdata";
        private const string FriendlyType = "Text Template Manager data";

        /// <summary>Points .ttmdata at the currently running ttm.exe. Returns false on failure.</summary>
        public static bool RegisterTtmData()
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            try
            {
                using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");

                using (var ext = classes.CreateSubKey(Ext))
                    ext.SetValue("", ProgId);

                using var prog = classes.CreateSubKey(ProgId);
                prog.SetValue("", FriendlyType);
                using (var icon = prog.CreateSubKey("DefaultIcon"))
                    icon.SetValue("", exe + ",0");
                using (var cmd = prog.CreateSubKey(@"shell\open\command"))
                    cmd.SetValue("", $"\"{exe}\" \"%1\"");
            }
            catch { return false; }

            // Tell Explorer the association changed so it takes effect without a sign-out.
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            return true;
        }

        private const uint SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
    }
}
