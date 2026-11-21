namespace StreamSift;

internal static class Program
{
    internal const string Name = "StreamSift";
    internal static string Version { get; } = typeof(Program).Assembly.GetName().Version!.ToString(3);

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (AppUpdate.Run(args)) return 0;
        using var window = new MainForm();
        if (args is ["--check-controls"]) return window.CheckControls();
        Application.Run(window);
        return 0;
    }
}
