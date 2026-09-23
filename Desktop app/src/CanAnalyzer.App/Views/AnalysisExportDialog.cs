using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CanAnalyzer.Core.Analysis;

namespace CanAnalyzer.App.Views;

/// <summary>Export identity belongs to the measurement, never inferred from the PC or current date.</summary>
public sealed class AnalysisExportDialog : Window
{
    public AnalysisExportOptions? Options { get; private set; }
    public AnalysisExportDialog(string loggerId)
    {
        Title = "Analyses exporteren"; Width = 560; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "CSV + JSON in één ZIP voor vergelijking over sessies. Controleer vóór export de signaalkeuze en kalibratie in de analysetabs.", TextWrapping = TextWrapping.Wrap });
        TextBox Field(string title, string value)
        {
            panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 10, 0, 3) });
            var input = new TextBox { Text = value }; panel.Children.Add(input); return input;
        }
        var machine = Field("Machine-ID (leeg = onbekend)", loggerId == "48EDFD35" ? "Vlindermachine 1" : loggerId == "22484AAA" ? "Vlindermachine 2" : "");
        var logger = Field("Logger-ID (leeg = onbekend)", loggerId);
        var battery = Field("Batterij-ID (leeg = onbekend)", "");
        var gap = Field("Maximale sampleafstand voor algemene signaalstatistieken [s]", "5");
        var samples = new CheckBox { Content = "Ook alle gedecodeerde meetpunten opnemen (grotere ZIP)", Margin = new Thickness(0, 12, 0, 8) };
        panel.Children.Add(samples);
        panel.Children.Add(new TextBlock { Text = "Algemene statistieken: hele dataset. Analysetabs: hun ingestelde tijdvenster. Ontbrekende referenties blijven onbekend. Overlappende exports moeten bij samenvoegen worden ontdubbeld.", TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Annuleren", IsCancel = true, Padding = new Thickness(12, 5, 12, 5) };
        var save = new Button { Content = "Exporteren…", IsDefault = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 5, 12, 5) };
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        save.Click += (_, _) =>
        {
            if (!double.TryParse(gap.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds) || seconds <= 0)
            { MessageBox.Show(this, "Vul een positieve sampleafstand in.", Title); return; }
            Options = new(machine.Text.Trim(), logger.Text.Trim(), battery.Text.Trim(), seconds, samples.IsChecked == true);
            DialogResult = true;
        };
    }
}
