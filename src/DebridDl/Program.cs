using System.Windows;
using Velopack;

namespace DebridDl;

static class Program
{
    [STAThread]
    static void Main()
    {
        VelopackApp.Build().Run();

        using var mutex = new Mutex(true, "debrid-dl", out var first);
        if (!first)
        {
            MessageBox.Show("Debrid DL est déjà ouvert.", "Debrid DL");
            return;
        }

        new Application().Run(new MainWindow());
    }
}
