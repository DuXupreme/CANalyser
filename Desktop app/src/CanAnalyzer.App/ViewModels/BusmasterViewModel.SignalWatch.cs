using System.ComponentModel;
using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.State;
using CanAnalyzer.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CanAnalyzer.App.ViewModels;

public sealed partial class BusmasterViewModel
{
    private const int MaximumWatchedSignals = 128;
    private HashSet<SignalIdentity> _watched = [];
    private SignalWatchLookup? _watchLookup;
    private LatestWorkQueue<WatchRequest, WatchValue[]>? _watchWork;
    private Task _watchTask = Task.CompletedTask;
    private sealed record WatchRequest(CanDataset Dataset, SignalWatchLookup Lookup, SignalIdentity[] Identities, long Timestamp, bool Latest);
    private sealed record WatchValue(SignalIdentity Identity, DecodedSignalSample? Sample, string Status);
    private double? _watchCursor;
    private bool _applyingWatch;
    [ObservableProperty] private bool _watchLatestValues;
    [ObservableProperty] private bool _onlyWatchedSignals;
    [ObservableProperty] private string _watchCursorText = "Cursor: nog niet ingesteld";

    public void ApplySignalWatchOptions(SignalWatchOptions? options)
    {
        options ??= new();
        _watched = (options.Signals ?? []).Take(MaximumWatchedSignals).ToHashSet();
        WatchLatestValues = options.LatestValues;
        OnlyWatchedSignals = options.OnlyWatched;
        InitializeWatchRows();
    }

    public SignalWatchOptions CaptureSignalWatchOptions() => new()
    {
        Signals = _watched.ToList(), LatestValues = WatchLatestValues, OnlyWatched = OnlyWatchedSignals
    };

    public void SetWatchCursorAt(double? seconds)
    {
        _watchCursor = seconds is { } time && double.IsFinite(time) && time > long.MinValue / 1e9 && time < long.MaxValue / 1e9 ? time : null;
        WatchCursorText = _watchCursor is { } valid ? $"Cursor: {valid:G17} s" : "Cursor: nog niet ingesteld";
        RefreshWatchValues();
    }

    private void InitializeWatchRows()
    {
        _applyingWatch = true;
        foreach (var row in _allSignalWatchRows)
        {
            row.PropertyChanged -= WatchRowChanged;
            row.IsWatched = _watched.Contains(row.Identity);
            row.PropertyChanged += WatchRowChanged;
        }
        _applyingWatch = false;
        RefreshWatchValues();
        ApplySignalWatchFilter();
    }

    private void WatchRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_applyingWatch || e.PropertyName != nameof(BusmasterSignalWatchRow.IsWatched) || sender is not BusmasterSignalWatchRow row) return;
        if (row.IsWatched)
        {
            if (_watched.Count >= MaximumWatchedSignals && !_watched.Contains(row.Identity)) { row.IsWatched = false; return; }
            _watched.Add(row.Identity);
        }
        else _watched.Remove(row.Identity);
        RefreshWatchValues();
        ApplySignalWatchFilter();
    }

    partial void OnWatchLatestValuesChanged(bool value) => RefreshWatchValues();
    partial void OnOnlyWatchedSignalsChanged(bool value) => ApplySignalWatchFilter();

    [RelayCommand]
    private void WatchVisibleSignals()
    {
        foreach (var row in SignalWatchRows.ToArray())
        {
            if (_watched.Count >= MaximumWatchedSignals) break;
            row.IsWatched = true;
        }
    }

    [RelayCommand]
    private void ClearWatchedSignals()
    {
        _watched.Clear();
        InitializeWatchRows();
    }

    private void RefreshWatchValues()
    {
        if (_watchLookup is null || _dataset is null) return;
        var rows = _allSignalWatchRows.Where(row => row.IsWatched).ToArray();
        if (!WatchLatestValues && _watchCursor is null)
        {
            _watchWork?.CancelPending();
            foreach (var row in rows) row.UpdateSample(null, null, "Kies cursor in grafiek of berichtlijst");
            return;
        }
        var timestamp = WatchLatestValues ? long.MaxValue : (long)Math.Round(_watchCursor!.Value * 1e9);
        _watchWork ??= new(ReadWatchAsync, PublishWatchValues, _ => { }, error =>
        {
            foreach (var row in _allSignalWatchRows.Where(row => row.IsWatched)) row.Status = "Waarde niet beschikbaar: " + error.Message;
        });
        foreach (var row in rows) row.Status = "Waarden bijwerken…";
        _watchTask = _watchWork.RequestAsync(new(_dataset, _watchLookup, rows.Select(row => row.Identity).ToArray(), timestamp, WatchLatestValues));
    }

    private static async Task<WatchValue[]> ReadWatchAsync(WatchRequest request, CancellationToken token)
    {
        using var lease = request.Dataset.AcquireReadLease();
        return await Task.Run(() => request.Identities.Select(identity =>
        {
            token.ThrowIfCancellationRequested();
            var sample = request.Lookup.AtOrBefore(identity, request.Timestamp);
            return new WatchValue(identity, sample, request.Latest ? "Laatste sample in opname" : request.Lookup.Status(identity, sample, request.Timestamp));
        }).ToArray(), token);
    }

    private void PublishWatchValues(WatchRequest request, WatchValue[] values)
    {
        if (!ReferenceEquals(request.Dataset, _dataset) || !ReferenceEquals(request.Lookup, _watchLookup)) return;
        var byIdentity = values.ToDictionary(value => value.Identity);
        foreach (var row in _allSignalWatchRows.Where(row => row.IsWatched))
            if (byIdentity.TryGetValue(row.Identity, out var value))
                row.UpdateSample(value.Sample, request.Latest ? null : request.Timestamp, value.Status);
    }

    public Task WaitForWatchValuesAsync() => _watchTask;
    public Task CancelAndWaitAsync() => _watchWork?.CancelAndWaitAsync() ?? Task.CompletedTask;
}
