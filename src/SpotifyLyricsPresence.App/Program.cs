using System.Runtime.InteropServices;

namespace SpotifyLyricsPresence.App;

internal static class Program
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--probe"))
        {
            AttachConsole(-1);
            try { Console.WriteLine(await new SmtcPlaybackSource().ProbeAsync()); return 0; }
            catch (Exception ex) { Console.WriteLine("Probe failed: " + ex); return 1; }
        }

        using var mutex = new Mutex(true, "SpotifyLyricsPresence.SingleInstance", out var first);
        if (!first)
        {
            MessageBox.Show("Spotify Lyrics Presence is already running (check the system tray).", "Spotify Lyrics Presence");
            return 0;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
