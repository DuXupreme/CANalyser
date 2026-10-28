using System.Globalization;
using CanAnalyzer.App.Models;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using OxyPlot;

namespace CanAnalyzer.App.ViewModels;

public sealed partial class JoystickAnalyticsViewModel
{
    private async Task BuildProfessionalCanAnalyticsAsync()
    {
        _exportCan = null;
        ProfessionalCanMetrics.Clear(); ProfessionalTopIdRows.Clear(); ProfessionalCycleRows.Clear();
        if (_dataset is not { } dataset)
        {
            CanFrameRateModel = EmptyPlot("Frames/s over tijd");
            CanBusLoadModel = EmptyPlot("Bus load [%] over tijd"); CanTopIdPlotModel = EmptyPlot("Top CAN-IDs");
            return;
        }
        var channel = SelectedCanChannel ?? string.Empty;
        var token = _analysisCalculation?.Token ?? CancellationToken.None;
        var request = new AnalysisRunRequest
        {
            Modules = AnalysisModules.Can,
            Profile = (CurrentProfile ?? AnalysisProfileResolver.Recognize(dataset)) with
            {
                CanBitrateKbps = CanBitrateKbps, CanDataBitrateKbps = CanDataBitrateKbps,
                CanBitratesConfirmed = CanBitratesConfirmed
            }
        };
        var binWidth = CanTimeBinMilliseconds / 1000d;
        var module = await ComputeAsync(() => new AnalysisRunService().AnalyzeCanDetail(dataset, request, channel, binWidth, token));
        Add(ProfessionalCanMetrics, "Beschikbaarheid", module.Explanation);
        Add(ProfessionalCanMetrics, "Aangevraagd tijdvak [s]", F(module.Coverage.RequestedSeconds));
        Add(ProfessionalCanMetrics, "Meetdekking [s]", F(module.Coverage.ObservedSeconds));
        if (module.Value is not { } detail)
        {
            _exportCan = module;
            CanFrameRateModel = EmptyPlot("Geen bruikbare CAN-meetdekking");
            CanBusLoadModel = EmptyPlot("Geen bruikbare CAN-meetdekking"); CanTopIdPlotModel = EmptyPlot("Geen CAN-frames");
            return;
        }
        string Number(double? value) => value is { } finite && double.IsFinite(finite) ? F(finite) : "niet beschikbaar";
        Add(ProfessionalCanMetrics, "CAN-kanaal", detail.Channel);
        Add(ProfessionalCanMetrics, "Duur log [s]", F(detail.DurationSeconds));
        Add(ProfessionalCanMetrics, "Totale frames", detail.FrameCount.ToString("N0", CultureInfo.CurrentCulture));
        Add(ProfessionalCanMetrics, "Unieke IDs", detail.UniqueIds.ToString("N0", CultureInfo.CurrentCulture));
        Add(ProfessionalCanMetrics, "Unieke frame-reeksen", detail.Streams.Count.ToString("N0", CultureInfo.CurrentCulture));
        Add(ProfessionalCanMetrics, "Extended frames", detail.ExtendedCount.ToString("N0", CultureInfo.CurrentCulture));
        Add(ProfessionalCanMetrics, "Error frames (indien gelogd)", detail.ErrorFrames.ToString("N0", CultureInfo.CurrentCulture));
        Add(ProfessionalCanMetrics, "Remote/RTR (indien gelogd)", detail.RemoteFrames.ToString("N0", CultureInfo.CurrentCulture));
        Add(ProfessionalCanMetrics, "Gem frames/s", Number(detail.AverageFramesPerSecond));
        Add(ProfessionalCanMetrics, "Piek frames/s", Number(detail.PeakFramesPerSecond));
        Add(ProfessionalCanMetrics, "Gem bus load [%] (geschat)", Number(detail.AverageBusLoadPercent));
        Add(ProfessionalCanMetrics, "Piek bus load [%] (geschat)", Number(detail.PeakBusLoadPercent));
        Add(ProfessionalCanMetrics, "Arbitration bitrate [kbps]", F(detail.ArbitrationBitrateKbps));
        Add(ProfessionalCanMetrics, "FD data bitrate [kbps]", F(detail.DataBitrateKbps));
        Add(ProfessionalCanMetrics, "Bitrates", detail.BitratesConfirmed ? "Door gebruiker bevestigd" : "Niet bevestigd; busbelasting ontbreekt");
        Add(ProfessionalCanMetrics, "Busloadmodel", "CAN/CAN-FD velden + 20% stuffingraming; geschat");
        Add(ProfessionalCanMetrics, "Negatieve cyclustijden", detail.Streams.Sum(item => item.NegativeCycleCount).ToString("N0", CultureInfo.CurrentCulture));
        Add(ProfessionalCanMetrics, "Niet-oplopende cyclustijden", detail.Streams.Sum(item => item.NonIncreasingCycleCount).ToString("N0", CultureInfo.CurrentCulture));
        Add(ProfessionalCanMetrics, "Cycli over meetgaten (uitgesloten)", detail.Streams.Sum(item => item.ExcludedGapCycles).ToString("N0", CultureInfo.CurrentCulture));
        Add(ProfessionalCanMetrics, "Venster [ms] (werkelijk)", F(detail.ActualBinWidthSeconds * 1000));
        _exportCan = new
        {
            module.ResultId, module.ModuleId, module.MethodVersion, module.Status, module.ReasonCodes,
            module.Explanation, module.Coverage, module.Unit, module.ValueUnits,
            detail.Channel, detail.StartSeconds, detail.EndSeconds, detail.DurationSeconds, detail.FrameCount,
            detail.ExtendedCount, detail.ErrorFrames, detail.RemoteFrames, detail.UniqueIds,
            detail.EstimatedTransmissionSeconds, detail.AverageFramesPerSecond, detail.PeakFramesPerSecond,
            detail.AverageBusLoadPercent, detail.PeakBusLoadPercent, detail.BitratesConfirmed,
            detail.ArbitrationBitrateKbps, detail.DataBitrateKbps, detail.ActualBinWidthSeconds, detail.Streams, detail.Bins
        };
        var topByCount = detail.Streams.OrderByDescending(item => item.Count)
            .ThenBy(item => item.Key.Channel, StringComparer.Ordinal).ThenBy(item => item.Key.FrameId).Take(30).ToArray();
        foreach (var item in topByCount)
            ProfessionalTopIdRows.Add(new CanTopIdRow(CanDisplayKey(item), item.Count.ToString("N0", CultureInfo.CurrentCulture),
                F(item.Count * 100d / Math.Max(1L, detail.FrameCount)), F(item.MeanPayloadLength),
                $"{item.Key.FrameFormat}/{(item.Key.IsExtended ? "Ext" : "Std")}/{item.Key.Direction}"));
        foreach (var item in detail.Streams.Where(item => item.CycleSamples > 0)
                     .OrderByDescending(item => item.JitterMs).ThenByDescending(item => item.CycleSamples).Take(40))
            ProfessionalCycleRows.Add(new CanCycleTimingRow(CanDisplayKey(item), item.CycleSamples.ToString("N0", CultureInfo.CurrentCulture),
                Number(item.AverageCycleMs), Number(item.JitterMs), Number(item.MinCycleMs), Number(item.MaxCycleMs),
                item.NegativeCycleCount.ToString("N0", CultureInfo.CurrentCulture)));
        CanFrameRateModel = BuildCanTrendPlot("Frames/s over tijd", detail.StartSeconds, detail.ActualBinWidthSeconds,
            detail.Bins.Select(bin => bin.FramesPerSecond ?? double.NaN).ToArray(), "Frames/s", OxyColor.Parse("#4E79A7"));
        CanBusLoadModel = detail.BitratesConfirmed
            ? BuildCanTrendPlot("Bus load [%] over tijd (geschat)", detail.StartSeconds, detail.ActualBinWidthSeconds,
                detail.Bins.Select(bin => bin.BusLoadPercent ?? double.NaN).ToArray(), "Bus load [%]", OxyColor.Parse("#E15759"), true)
            : EmptyPlot("Bevestig bitrates voor geschatte busbelasting");
        CanTopIdPlotModel = BuildTopIdBarPlot(topByCount, detail.FrameCount);
    }

    private static string CanDisplayKey(CanDetailStream stream) =>
        $"{(string.IsNullOrWhiteSpace(stream.Key.Channel) ? "?" : stream.Key.Channel)} / 0x{stream.Key.FrameId:X}";
}
