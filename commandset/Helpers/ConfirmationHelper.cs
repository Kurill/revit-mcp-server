using System.Diagnostics;
using System.Runtime.InteropServices;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Helpers
{
    public static class ConfirmationHelper
    {
        /// <summary>
        /// Confirmation dialogs are opt-in. REVIT_MCP_CONFIRM=1 (user environment variable)
        /// turns them on for every model-changing command and for send_code_to_revit;
        /// REVIT_MCP_CONFIRM_CODE=1 is still honoured. Read on every call, so changing it
        /// needs no Revit restart; anything unreadable means no dialogs.
        /// </summary>
        public static bool DialogsEnabled()
        {
            try
            {
                return Environment.GetEnvironmentVariable("REVIT_MCP_CONFIRM", EnvironmentVariableTarget.User) == "1"
                    || Environment.GetEnvironmentVariable("REVIT_MCP_CONFIRM_CODE", EnvironmentVariableTarget.User) == "1";
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Asks the user to confirm a destructive operation when dialogs are enabled.
        /// Returns false if the user clicks No, or if the call that queued <paramref name="handler"/>
        /// has already timed out - with or without a dialog.
        /// </summary>
        public static bool Confirm(object handler, string action, int elementCount)
        {
            if (AbandonedCalls.IsAbandoned(handler)) return false;
            if (elementCount <= 0 || !DialogsEnabled()) return true;

            var dialog = new TaskDialog("MCP Operation Confirmation")
            {
                MainContent = $"About to {action} {elementCount} element(s). Continue?",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No
            };

            return Ask(handler, dialog);
        }

        /// <summary>
        /// Shows a Yes/No dialog in front of the user. A Yes that arrives after the caller
        /// timed out counts as No: the agent has already been told the call failed.
        /// </summary>
        public static bool Ask(object handler, TaskDialog dialog)
        {
            BringRevitToFront();
            bool yes = dialog.Show() == TaskDialogResult.Yes;
            return yes && !AbandonedCalls.IsAbandoned(handler);
        }

        // A dialog raised while Revit is in the background opens behind the user's active
        // window, and Revit stays blocked on it with nothing visible. Windows may refuse to
        // hand focus to a background process; the taskbar button then flashes instead.
        private static void BringRevitToFront()
        {
            try
            {
                IntPtr main = Process.GetCurrentProcess().MainWindowHandle;
                if (main == IntPtr.Zero) return;
                if (IsIconic(main)) ShowWindow(main, SW_RESTORE);
                if (SetForegroundWindow(main)) return;

                var flash = new FLASHWINFO
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO)),
                    hwnd = main,
                    dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
                    uCount = uint.MaxValue,
                    dwTimeout = 0
                };
                FlashWindowEx(ref flash);
            }
            catch
            {
                // Best effort: the dialog still shows, just maybe behind another window.
            }
        }

        private const int SW_RESTORE = 9;
        private const uint FLASHW_ALL = 3;
        private const uint FLASHW_TIMERNOFG = 12;

        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
    }
}
