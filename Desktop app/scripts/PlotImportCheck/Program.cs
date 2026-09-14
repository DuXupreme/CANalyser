using System.IO;
using System.Reflection;
using System.Windows.Threading;
using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.ViewModels;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Export;
using Microsoft.Extensions.Logging.Abstractions;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        Exception? failure = null;
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await CheckAsync(); }
            catch (Exception ex) { failure = ex; }
            finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        }));
        Dispatcher.Run();
        if (failure is not null) { Console.Error.WriteLine(failure); return 1; }
        Console.WriteLine("PASS: import builds off UI thread, dispatcher ticks with busy indicator, failure clears busy state.");
        return 0;
    }

    private static async Task CheckAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var identity = new SignalIdentity("1", CanFrameFormat.Classic, false, 0x123, "Message", "Signal");
            var series = new SignalSeries(identity, [0L, 1000000000L], [0d, 1d]);
            using var dataset = new CanDataset
            {
                RawFrames = [], DecodedSamples = [], MessageSummaries = [],
                SignalLabels = [series.Label], SignalSeriesByLabel = new Dictionary<string, SignalSeries> { [series.Label] = series },
                SignalSeriesByIdentity = new Dictionary<SignalIdentity, SignalSeries> { [identity] = series },
                Diagnostics = new DecoderDiagnostics(0, 0, 1, 0, 0, "test")
            };
            var serializer = new PresetSerializer();
            File.WriteAllText(path, serializer.Serialize(new PlotPreset
            {
                Version = 1, PlotGroups = [new PlotGroup { Title = "Test", Signals = [series.Label] }]
            }));
            var files = DispatchProxy.Create<IFileDialogService, Stub>();
            ((Stub)(object)files).FilePath = path;
            var telemetry = DispatchProxy.Create<ITelemetryService, Stub>();
            var builder = new SlowBuilder(Environment.CurrentManagedThreadId);
            var vm = new AnalysisViewModel(builder, files, null!, serializer,
                new XAxisSyncService(), telemetry, new ActiveUsageViewModel(telemetry), NullLogger<AnalysisViewModel>.Instance);
            vm.LoadDataset(dataset);
            var ticks = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { if (vm.IsBusy && !vm.ImportPresetCommand.CanExecute(null)) ticks++; };
            timer.Start();
            await vm.ImportPresetCommand.ExecuteAsync(null);
            timer.Stop();
            if (ticks < 3 || vm.IsBusy || builder.Calls != 1 || !vm.PresetStatus.StartsWith("Layout geïmporteerd"))
                throw new InvalidOperationException($"Import responsiveness failed: ticks={ticks}, calls={builder.Calls}, status={vm.PresetStatus}");
            builder.Fail = true;
            await vm.ImportPresetCommand.ExecuteAsync(null);
            if (vm.IsBusy || !vm.ImportPresetCommand.CanExecute(null) || !vm.PresetStatus.StartsWith("Import mislukt"))
                throw new InvalidOperationException("Failed import did not restore the UI.");
        }
        finally { File.Delete(path); }
    }

    private sealed class SlowBuilder(int uiThread) : IPlotModelBuilder
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public IReadOnlyList<PlotPanelModel> Build(CanDataset dataset, IReadOnlyList<PlotGroup> groups, PlotViewOptions options)
        {
            Calls++;
            if (Environment.CurrentManagedThreadId == uiThread) throw new InvalidOperationException("Build ran on UI thread");
            Thread.Sleep(300);
            if (Fail) throw new InvalidDataException("Simulated build failure");
            return new PlotModelBuilder().Build(dataset, groups, options);
        }
    }
}

public class Stub : DispatchProxy
{
    public string? FilePath { get; set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args) =>
        method?.Name == "PickPresetFile" ? FilePath : method?.ReturnType == typeof(Task) ? Task.CompletedTask : null;
}
