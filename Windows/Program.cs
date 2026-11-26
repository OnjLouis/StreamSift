namespace StreamSift;

internal static class Program
{
#if STREAMSIFT_WINDOWS7
    internal const string Name = "StreamSift for Windows 7";
#else
    internal const string Name = "StreamSift";
#endif
    internal static string Version { get; } = typeof(Program).Assembly.GetName().Version!.ToString(3);

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (AppUpdate.Run(args)) return 0;
        bool checkingControls = args is ["--check-controls"];
        using var window = new MainForm(readHistory: !checkingControls);
        if (checkingControls) return window.CheckControls();
        Application.Run(window);
        return 0;
    }
}
