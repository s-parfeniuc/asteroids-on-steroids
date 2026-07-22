// Asteroids on Steroids — WinForms + SkiaSharp (GPU) entry point (Windows only).
//
// Mirror of Game/Program.cs: it only constructs the concrete window; the backend-agnostic
// bootstrap + loop live in GameHost (AsteroidsGameCore).
//
//   dotnet run --project Game.WinForms      (on Windows)

using System.Windows.Forms;
using AsteroidsGame;
using AsteroidsEngine.Platform.WinForms;

namespace AsteroidsGame.WinFormsHost;

internal static class Program
{
    [STAThread]   // WinForms requires a single-threaded-apartment entry thread
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var (w, h) = WinFormsGameWindow.QueryDisplaySize();
        using var window = new WinFormsGameWindow("Asteroids on Steroids", w, h, fullscreen: true);
        GameHost.Run(window);
    }
}
