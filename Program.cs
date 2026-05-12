using System;
using System.Windows.Forms;
using BareRecord.UI;
using BareRecord.Win32;

namespace BareRecord;

internal static class Program
{
    [STAThread]
    public static int Main()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            #pragma warning disable WFO5001
            Application.SetColorMode(SystemColorMode.Dark);
#pragma warning restore WFO5001

            Application.Run(new MainWindow());
            return 0;
        }
        catch (Exception ex)
        {
            Native.MessageBoxW(IntPtr.Zero, ex.ToString(), "BareRecord — fatal error", 0x10);
            return 1;
        }
    }
}