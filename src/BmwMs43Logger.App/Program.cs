using BmwMs43Logger.Core;

namespace BmwMs43Logger.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            AppRuntimeOptions options = AppRuntimeOptions.Parse(args);
            ILoggerRuntime runtime = options.CreateRuntime();
            Application.Run(new MainForm(runtime, options.RepositoryRoot));
            return 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "BMW MS43 Logger — startup blocked",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }
}
