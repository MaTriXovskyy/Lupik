using Lupik.Core;
using Velopack;

namespace Lupik;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack's install / update / uninstall hooks run here and exit before any UI
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => Autostart.Set(false))
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
