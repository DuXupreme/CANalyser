using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Media;
using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.Core.Decoding;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Utilities;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CanAnalyzer.App.ViewModels;

/// <summary>
/// Database (DBC) editor tab: author frames + signals with a live bit-layout grid and save to a .dbc file.
/// </summary>
public sealed partial class DbcEditorViewModel : ObservableObject
{
    private static readonly Brush[] Palette = CreatePalette();
    private static readonly Brush OverlapBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x52, 0x52)));
    private static readonly Brush EmptyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5)));

    private readonly IDbcLoader _dbcLoader;
    private readonly IDbcWriter _dbcWriter;
    private readonly IFileDialogService _fileDialogService;
    private readonly IMessageDialogService _messageDialogService;
    private readonly ILogger<DbcEditorViewModel> _logger;

    private readonly DbcBitCell[] _cells = new DbcBitCell[64];
    private readonly DbcBitCell[] _cellByLsb0 = new DbcBitCell[64];
    private readonly List<DbcSignalRow> _attachedSignals = [];
    private DbcFrameRow? _attachedFrame;

    [ObservableProperty]
    private DbcFrameRow? _selectedFrame;

    [ObservableProperty]
    private DbcSignalRow? _selectedSignal;

    [ObservableProperty]
    private string _validationSummary = "Geen frame geselecteerd.";

    [ObservableProperty]
    private string _statusText = "Maak een nieuwe database of open een bestaand DBC-bestand om te bewerken.";

    [ObservableProperty]
    private string? _currentFilePath;

    [ObservableProperty]
    private bool _isReadOnly;

    public DbcEditorViewModel(
        IDbcLoader dbcLoader,
        IDbcWriter dbcWriter,
        IFileDialogService fileDialogService,
        IMessageDialogService messageDialogService,
        ILogger<DbcEditorViewModel> logger)
    {
        _dbcLoader = dbcLoader;
        _dbcWriter = dbcWriter;
        _fileDialogService = fileDialogService;
        _messageDialogService = messageDialogService;
        _logger = logger;

        for (var byteIndex = 0; byteIndex < 8; byteIndex++)
        {
            for (var column = 0; column < 8; column++)
            {
                var cell = new DbcBitCell(byteIndex, column);
                _cells[(byteIndex * 8) + column] = cell;
                _cellByLsb0[cell.Lsb0Index] = cell;
            }
        }

        BitCells = _cells;
        Frames.CollectionChanged += OnFramesChanged;

        NewDatabaseCommand = new RelayCommand(NewDatabase);
        OpenDbcCommand = new AsyncRelayCommand(OpenDbcAsync);
        SaveDbcCommand = new AsyncRelayCommand(SaveDbcAsync, () => Frames.Count > 0 && !IsReadOnly);
        AddFrameCommand = new RelayCommand(AddFrame, () => !IsReadOnly);
        RemoveFrameCommand = new RelayCommand(RemoveFrame, () => SelectedFrame is not null && !IsReadOnly);
        AddSignalCommand = new RelayCommand(AddSignal, () => SelectedFrame is not null && !IsReadOnly);
        RemoveSignalCommand = new RelayCommand(RemoveSignal, () => SelectedSignal is not null && !IsReadOnly);

        RecomputeLayout();
        UpdateValidation();
    }

    /// <summary>Frames (DBC messages) in the database being edited.</summary>
    public ObservableCollection<DbcFrameRow> Frames { get; } = [];

    /// <summary>The fixed 64-cell payload bit-layout grid for the selected frame.</summary>
    public IReadOnlyList<DbcBitCell> BitCells { get; }

    public IRelayCommand NewDatabaseCommand { get; }

    public IAsyncRelayCommand OpenDbcCommand { get; }

    public IAsyncRelayCommand SaveDbcCommand { get; }

    public IRelayCommand AddFrameCommand { get; }

    public IRelayCommand RemoveFrameCommand { get; }

    public IRelayCommand AddSignalCommand { get; }

    public IRelayCommand RemoveSignalCommand { get; }

    partial void OnSelectedFrameChanged(DbcFrameRow? value)
    {
        AttachFrame(value);
        SelectedSignal = value?.Signals.FirstOrDefault();
        RemoveFrameCommand.NotifyCanExecuteChanged();
        AddSignalCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedSignalChanged(DbcSignalRow? value)
    {
        RemoveSignalCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsReadOnlyChanged(bool value)
    {
        SaveDbcCommand.NotifyCanExecuteChanged();
        AddFrameCommand.NotifyCanExecuteChanged();
        RemoveFrameCommand.NotifyCanExecuteChanged();
        AddSignalCommand.NotifyCanExecuteChanged();
        RemoveSignalCommand.NotifyCanExecuteChanged();
    }

    private void NewDatabase()
    {
        if (Frames.Count > 0 &&
            !_messageDialogService.Confirm("Nieuwe database", "De huidige frames worden gewist. Doorgaan?"))
        {
            return;
        }

        SelectedSignal = null;
        SelectedFrame = null;
        Frames.Clear();
        CurrentFilePath = null;
        IsReadOnly = false;
        StatusText = "Nieuwe lege database.";
        UpdateValidation();
    }

    private async Task OpenDbcAsync()
    {
        if (Frames.Count > 0 &&
            !_messageDialogService.Confirm("DBC openen", "De huidige frames worden vervangen door de inhoud van het bestand. Doorgaan?"))
        {
            return;
        }

        var path = _fileDialogService.PickDbcFile(CurrentFilePath);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var database = await _dbcLoader.LoadAsync(path, CancellationToken.None);
            LoadFromDatabase(database);
            CurrentFilePath = path;
            IsReadOnly = !database.IsLosslessWritable;
            StatusText = IsReadOnly
                ? $"ALLEEN-LEZEN: {path} — deze geïmporteerde DBC bevat constructies die de editor niet aantoonbaar lossless kan terugschrijven."
                : $"Geladen: {path}  ({Frames.Count} frames, {Frames.Sum(f => f.Signals.Count)} signalen)";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DBC open failed");
            _messageDialogService.ShowError("Openen mislukt", ex.Message);
        }
    }

    private async Task SaveDbcAsync()
    {
        if (IsReadOnly)
        {
            _messageDialogService.ShowError(
                "Lossless opslaan niet mogelijk",
                "Deze geïmporteerde DBC is bewust alleen-lezen. De editor kan niet garanderen dat alle metadata en multiplexconstructies semantisch identiek worden teruggeschreven.");
            return;
        }

        if (Frames.Count == 0)
        {
            _messageDialogService.ShowInfo("Niets op te slaan", "Voeg eerst minstens één frame toe.");
            return;
        }

        var path = _fileDialogService.SaveDbcFile(CurrentFilePath);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var database = BuildDatabase();
            await _dbcWriter.WriteAsync(database, path, CancellationToken.None);
            CurrentFilePath = path;
            var signalCount = Frames.Sum(f => f.Signals.Count);
            StatusText = $"Opgeslagen: {path}  ({Frames.Count} frames, {signalCount} signalen)";
            _messageDialogService.ShowInfo(
                "DBC opgeslagen",
                $"Database opgeslagen als:\n{path}\n\nJe kunt dit bestand nu laden via 'Open DBC' op het hoofdscherm.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DBC save failed");
            _messageDialogService.ShowError("Opslaan mislukt", ex.Message);
        }
    }

    private void AddFrame()
    {
        var frame = new DbcFrameRow
        {
            Name = $"Message_{Frames.Count + 1}",
            FrameId = FindFreeStandardId(),
            Dlc = 8
        };
        Frames.Add(frame);
        SelectedFrame = frame;
    }

    private void RemoveFrame()
    {
        if (SelectedFrame is null)
        {
            return;
        }

        var index = Frames.IndexOf(SelectedFrame);
        Frames.Remove(SelectedFrame);
        SelectedFrame = Frames.Count == 0 ? null : Frames[Math.Clamp(index, 0, Frames.Count - 1)];
    }

    private void AddSignal()
    {
        var frame = SelectedFrame;
        if (frame is null)
        {
            return;
        }

        const int length = 8;
        var signal = new DbcSignalRow
        {
            Name = $"Signal_{frame.Signals.Count + 1}",
            Length = length,
            StartBit = FindFreeStartBit(frame, length),
            LittleEndian = true,
            Scale = 1d
        };
        frame.Signals.Add(signal);
        SelectedSignal = signal;
    }

    private void RemoveSignal()
    {
        var frame = SelectedFrame;
        var signal = SelectedSignal;
        if (frame is null || signal is null)
        {
            return;
        }

        frame.Signals.Remove(signal);
        SelectedSignal = frame.Signals.FirstOrDefault();
    }

    private void OnFramesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SaveDbcCommand.NotifyCanExecuteChanged();
        AddFrameCommand.NotifyCanExecuteChanged();
        RemoveFrameCommand.NotifyCanExecuteChanged();
        UpdateValidation();
    }

    private void AttachFrame(DbcFrameRow? frame)
    {
        if (_attachedFrame is not null)
        {
            _attachedFrame.PropertyChanged -= OnFramePropertyChanged;
            _attachedFrame.Signals.CollectionChanged -= OnSelectedSignalsChanged;
        }

        foreach (var signal in _attachedSignals)
        {
            signal.PropertyChanged -= OnSignalRowChanged;
        }

        _attachedSignals.Clear();
        _attachedFrame = frame;

        if (frame is not null)
        {
            frame.PropertyChanged += OnFramePropertyChanged;
            frame.Signals.CollectionChanged += OnSelectedSignalsChanged;
            foreach (var signal in frame.Signals)
            {
                signal.PropertyChanged += OnSignalRowChanged;
                _attachedSignals.Add(signal);
            }
        }

        RecomputeLayout();
        UpdateValidation();
    }

    private void OnSelectedSignalsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var signal in _attachedSignals)
        {
            signal.PropertyChanged -= OnSignalRowChanged;
        }

        _attachedSignals.Clear();

        if (_attachedFrame is not null)
        {
            foreach (var signal in _attachedFrame.Signals)
            {
                signal.PropertyChanged += OnSignalRowChanged;
                _attachedSignals.Add(signal);
            }
        }

        RecomputeLayout();
        UpdateValidation();
    }

    private void OnFramePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DbcFrameRow.Dlc)
            or nameof(DbcFrameRow.FrameId)
            or nameof(DbcFrameRow.IsExtended)
            or nameof(DbcFrameRow.Name))
        {
            UpdateValidation();
        }
    }

    private void OnSignalRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DbcSignalRow.StartBit)
            or nameof(DbcSignalRow.Length)
            or nameof(DbcSignalRow.LittleEndian)
            or nameof(DbcSignalRow.Name))
        {
            RecomputeLayout();
            UpdateValidation();
        }
    }

    private void RecomputeLayout()
    {
        foreach (var cell in _cells)
        {
            cell.FillBrush = EmptyBrush;
            cell.OwnerLabel = string.Empty;
            cell.IsOverlap = false;
        }

        var frame = SelectedFrame;
        if (frame is null)
        {
            return;
        }

        var ownerIndex = new int[64];
        Array.Fill(ownerIndex, -1);
        var overlap = new bool[64];
        var names = new string[frame.Signals.Count];

        for (var i = 0; i < frame.Signals.Count; i++)
        {
            var signal = frame.Signals[i];
            names[i] = string.IsNullOrWhiteSpace(signal.Name) ? "(naamloos)" : signal.Name;
            foreach (var bit in DbcBitLayout.GetOccupiedLsb0Bits(signal.StartBit, signal.Length, signal.LittleEndian))
            {
                if (bit is < 0 or > 63)
                {
                    continue;
                }

                if (ownerIndex[bit] == -1)
                {
                    ownerIndex[bit] = i;
                }
                else
                {
                    overlap[bit] = true;
                }
            }
        }

        for (var bit = 0; bit < 64; bit++)
        {
            var cell = _cellByLsb0[bit];
            if (overlap[bit])
            {
                cell.IsOverlap = true;
                cell.OwnerLabel = "OVERLAP";
                cell.FillBrush = OverlapBrush;
            }
            else if (ownerIndex[bit] >= 0)
            {
                var owner = ownerIndex[bit];
                cell.OwnerLabel = names[owner];
                cell.FillBrush = Palette[owner % Palette.Length];
            }
        }
    }

    private void UpdateValidation()
    {
        var issues = new List<string>();
        var frame = SelectedFrame;

        if (frame is not null)
        {
            var window = Math.Clamp(frame.Dlc, 0, 8) * 8;
            var seen = new bool[64];
            var overlapBits = 0;
            var outOfDlc = 0;
            var outOfPayload = 0;

            foreach (var signal in frame.Signals)
            {
                foreach (var bit in DbcBitLayout.GetOccupiedLsb0Bits(signal.StartBit, signal.Length, signal.LittleEndian))
                {
                    if (bit is < 0 or > 63)
                    {
                        outOfPayload++;
                        continue;
                    }

                    if (bit >= window)
                    {
                        outOfDlc++;
                    }

                    if (seen[bit])
                    {
                        overlapBits++;
                    }
                    else
                    {
                        seen[bit] = true;
                    }
                }
            }

            if (overlapBits > 0)
            {
                issues.Add($"{overlapBits} overlappende bit(s)");
            }

            if (outOfDlc > 0)
            {
                issues.Add($"{outOfDlc} bit(s) buiten DLC ({frame.Dlc} byte)");
            }

            if (outOfPayload > 0)
            {
                issues.Add($"{outOfPayload} bit(s) buiten 64-bit payload");
            }

            var unnamed = frame.Signals.Count(signal => string.IsNullOrWhiteSpace(signal.Name));
            if (unnamed > 0)
            {
                issues.Add($"{unnamed} signaal/signalen zonder naam");
            }
        }

        var duplicateIds = Frames
            .GroupBy(f => (f.IsExtended, CanIdUtilities.NormalizeDbcFrameId(f.FrameId, f.IsExtended)))
            .Count(group => group.Count() > 1);
        if (duplicateIds > 0)
        {
            issues.Add($"{duplicateIds} dubbele frame-id('s) in de database");
        }

        if (issues.Count > 0)
        {
            ValidationSummary = "⚠ " + string.Join("  ·  ", issues);
        }
        else
        {
            ValidationSummary = frame is null ? "Geen frame geselecteerd." : "✓ Geen conflicten in dit frame.";
        }
    }

    private int FindFreeStartBit(DbcFrameRow frame, int length)
    {
        var window = Math.Clamp(frame.Dlc, 1, 8) * 8;
        var occupied = new bool[64];
        foreach (var signal in frame.Signals)
        {
            foreach (var bit in DbcBitLayout.GetOccupiedLsb0Bits(signal.StartBit, signal.Length, signal.LittleEndian))
            {
                if (bit is >= 0 and < 64)
                {
                    occupied[bit] = true;
                }
            }
        }

        for (var start = 0; start + length <= window; start++)
        {
            var free = true;
            for (var k = 0; k < length; k++)
            {
                if (occupied[start + k])
                {
                    free = false;
                    break;
                }
            }

            if (free)
            {
                return start;
            }
        }

        return 0;
    }

    private uint FindFreeStandardId()
    {
        var used = new HashSet<uint>(Frames.Where(f => !f.IsExtended).Select(f => f.FrameId));
        var id = 0x100u;
        while (used.Contains(id) && id < 0x7FF)
        {
            id++;
        }

        return id;
    }

    private DbcDatabase BuildDatabase()
    {
        var messages = new List<DbcMessage>(Frames.Count);
        foreach (var frame in Frames)
        {
            var rawFrameId = frame.IsExtended
                ? (frame.FrameId & CanIdUtilities.CanExtendedMask) | CanIdUtilities.DbcExtendedFlag
                : frame.FrameId;

            var message = new DbcMessage
            {
                RawFrameId = rawFrameId,
                IsExtendedFrame = frame.IsExtended,
                Name = frame.Name,
                Dlc = frame.Dlc
            };

            foreach (var signal in frame.Signals)
            {
                message.Signals.Add(new DbcSignal
                {
                    Name = signal.Name,
                    StartBit = signal.StartBit,
                    Length = signal.Length,
                    IsLittleEndian = signal.LittleEndian,
                    IsSigned = signal.Signed,
                    Scale = signal.Scale,
                    Offset = signal.Offset,
                    Minimum = signal.Minimum,
                    Maximum = signal.Maximum,
                    Unit = signal.Unit,
                    IsMultiplexer = signal.IsMultiplexerSwitch,
                    MultiplexerIds = signal.MultiplexedValue.HasValue ? [signal.MultiplexedValue.Value] : []
                });
            }

            messages.Add(message);
        }

        return new DbcDatabase { Messages = messages };
    }

    private void LoadFromDatabase(DbcDatabase database)
    {
        SelectedSignal = null;
        SelectedFrame = null;
        Frames.Clear();

        foreach (var message in database.Messages)
        {
            var frame = new DbcFrameRow
            {
                Name = message.Name,
                FrameId = message.NormalizedFrameId,
                IsExtended = message.IsExtendedFrame,
                Dlc = message.Dlc
            };

            foreach (var signal in message.Signals)
            {
                frame.Signals.Add(new DbcSignalRow
                {
                    Name = signal.Name,
                    StartBit = signal.StartBit,
                    Length = signal.Length,
                    LittleEndian = signal.IsLittleEndian,
                    Signed = signal.IsSigned,
                    Scale = signal.Scale,
                    Offset = signal.Offset,
                    Minimum = signal.Minimum,
                    Maximum = signal.Maximum,
                    Unit = signal.Unit,
                    IsMultiplexerSwitch = signal.IsMultiplexer,
                    MultiplexedValue = signal.MultiplexerIds.Count > 0 ? signal.MultiplexerIds[0] : null
                });
            }

            Frames.Add(frame);
        }

        SelectedFrame = Frames.FirstOrDefault();
    }

    private static Brush[] CreatePalette()
    {
        uint[] colors =
        [
            0x90CAF9, 0xA5D6A7, 0xFFCC80, 0xCE93D8, 0x80CBC4,
            0xFFF59D, 0xBCAAA4, 0xB0BEC5, 0xF48FB1, 0xC5E1A5
        ];

        var brushes = new Brush[colors.Length];
        for (var i = 0; i < colors.Length; i++)
        {
            var rgb = colors[i];
            var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            brushes[i] = Freeze(brush);
        }

        return brushes;
    }

    private static Brush Freeze(Brush brush)
    {
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }

        return brush;
    }
}
