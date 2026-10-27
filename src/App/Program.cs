using System;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace CalcBJ.App
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
                MessageBox.Show("Unexpected error:\n" + e.Exception.Message + "\n\n" + e.Exception.StackTrace, "CalcBJ", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Theme.Init();
            var form = new MainForm();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--screenshots" && i + 1 < args.Length) form.ScreenshotDir = args[++i];
                else if (args[i].EndsWith(".bjev", StringComparison.OrdinalIgnoreCase)) form.StartupFile = args[i];
            }
            Application.Run(form);
        }
    }
}
