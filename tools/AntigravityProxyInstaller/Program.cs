namespace AntigravityProxyInstaller;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // Paint metrics have to know the device DPI before the first frame.
        Theme.InitializeScale(Theme.DetectScale());

        Application.Run(new MainForm());
    }
}
