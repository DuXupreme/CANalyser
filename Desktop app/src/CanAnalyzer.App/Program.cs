using Velopack;

namespace CanAnalyzer.App;

/// <summary>
/// Expliciet entry-point. Velopack moet als allereerste draaien zodat install-,
/// update- en uninstall-hooks (die met speciale argumenten worden aangeroepen)
/// correct worden afgehandeld voordat WPF opstart.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
