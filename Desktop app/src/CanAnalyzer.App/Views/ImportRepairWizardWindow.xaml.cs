using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.ViewModels;
using CanAnalyzer.Core.Decoding;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Utilities;
using Microsoft.Win32;

namespace CanAnalyzer.App.Views;

public partial class ImportRepairWizardWindow : Window
{
    private const int MaxSamplesPerCategory = 80;
    private const int MaxDisplayedIssues = 800;
    private const int MaxNavigableDbcIssues = 500;

    private static readonly Regex DbcFrameLineRegex = new(
        @"^\s*BO_\s+(?<id>\d+)\s+(?<name>[^:\s]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DbcSignalLineRegex = new(
        @"^\s*SG_\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LayoutTargetRegex = new(
        @"^(?<name>.*?)\s*\(0x(?<id>[0-9A-Fa-f]+)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SignalInMessageRegex = new(
        @"\bsignals?\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex DuplicateSignalMessageRegex = new(
        @"Duplicated signal '(?<name>[^']+)' in message \(ID (?<id>\d+)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex LayoutConflictRegex = new(
        @"signals?\s+(?<left>[A-Za-z_][A-Za-z0-9_]*)\s+en\s+(?<right>[A-Za-z_][A-Za-z0-9_]*)\s+overlappen",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex AttributeDefinitionRegex = new(
        "^\\s*BA_DEF_\\s+(?<scope>\\w+)\\s+\"(?<name>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SignalMuxTokenRegex = new(
        @"\s(?<mux>M|m\d+)\s*:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ExtendedMuxLineRegex = new(
        @"^\s*SG_MUL_VAL_\s+(?<id>\d+)\s+(?<signal>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DecodeDlcMismatchRegex = new(
        @"^(?<name>.*?)\s*\(0x(?<id>[0-9A-Fa-f]+)\):\s*(?<count>[\d.,]+)\s+frame\(s\)\s+hebben payloadlengte\s+(?<actual>\d+)\s+byte\(s\);\s*de DBC verwacht\s+(?<expected>\d+(?:\s*,\s*\d+)*)\s+byte\(s\)\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ImportReport _report;
    private readonly string _originalLogPath;
    private readonly string _originalDbcPath;
    private readonly IImportRepairService _repairService;
    private readonly DbcEditorViewModel _dbcEditor;
    private readonly int _errorCount;
    private readonly int _warningCount;
    private readonly int _removableLogErrorCount;
    private readonly int _dbcErrorCount;
    private IReadOnlyList<ImportIssue> _activeDbcIssues;
    private string[] _dbcSourceLines = [];
    private string? _loadedDbcPath;
    private int _dbcIssueIndex;
    private bool _allowClose;
    private readonly Dictionary<long, DbcSignalRow> _appliedSignalFixes = [];
    private readonly Dictionary<string, string> _appliedFixResults = [];
    private DbcSignalRow? _pendingSuggestedSignal;
    private string? _pendingSuggestedName;
    private DbcSignalRow? _pendingAggregateSignal;
    private IReadOnlyList<DbcSignalRow> _pendingDetailSignals = [];
    private DbcFrameRow? _pendingDlcFrame;
    private int? _pendingDlcValue;
    private string? _pendingSuggestedResult;

    internal ImportRepairWizardWindow(
        ImportReport report,
        string logPath,
        string dbcPath,
        IImportRepairService repairService,
        DbcEditorViewModel dbcEditor,
        ImportRepairAnalysis analysis)
    {
        InitializeComponent();
        FitToAvailableScreen();
        _report = report;
        _originalLogPath = logPath;
        _originalDbcPath = dbcPath;
        _repairService = repairService;
        _dbcEditor = dbcEditor;
        DbcEditorHost.DataContext = dbcEditor;

        _errorCount = analysis.ErrorCount;
        _warningCount = analysis.WarningCount;
        _removableLogErrorCount = analysis.RemovableLogErrorCount;
        _dbcErrorCount = analysis.DbcErrorCount;
        _activeDbcIssues = analysis.DbcIssues;

        Result = new ImportRepairWizardResult(ImportRepairDecision.Cancel, false, logPath, dbcPath);
        ConfigureOverview(analysis.Rows);
        ConfigureRepairPage();
        ShowOverview();
    }

    private void FitToAvailableScreen()
    {
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(1500, workArea.Width * 0.94);
        Height = Math.Min(900, workArea.Height * 0.94);
    }

    public ImportRepairWizardResult Result { get; private set; }

    private void ConfigureOverview(IReadOnlyList<ImportIssueDisplayRow> rows)
    {
        var limitedText = rows.Count < _report.Issues.Count
            ? $" Voor de snelheid worden {rows.Count:N0} representatieve meldingen getoond; de tellingen en herstelactie gebruiken wel het volledige rapport."
            : string.Empty;
        OverviewSummaryText.Text =
            $"{_errorCount:N0} fout(en), {_warningCount:N0} waarschuwing(en) en {_report.RejectedLines:N0} afgewezen logregel(s) gevonden door {_report.ParserName}. " +
            "Selecteer een melding om de bijbehorende bronregel te bekijken." + limitedText;

        var view = new ListCollectionView(rows.ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ImportIssueDisplayRow.Category)));
        IssuesGrid.ItemsSource = view;
        IssuesGrid.SelectedIndex = rows.Count > 0 ? 0 : -1;
    }

    private void ConfigureRepairPage()
    {
        CurrentFilesText.Text = $"Log: {_originalLogPath}\nDBC: {_originalDbcPath}";
        CleanLogCheckBox.Content = _removableLogErrorCount > 0
            ? $"Maak een opgeschoonde logkopie zonder {_removableLogErrorCount:N0} aantoonbaar afgewezen regel(s)"
            : "Geen logregels kunnen veilig automatisch worden verwijderd";
        CleanLogCheckBox.IsEnabled = _removableLogErrorCount > 0;
        CleanLogCheckBox.IsChecked = _removableLogErrorCount > 0;
        RepairedLogPathTextBox.Text = _repairService.GetDefaultRepairedPath(_originalLogPath);
        ReplacementDbcPathTextBox.Text = _originalDbcPath;

        DbcRepairActionPanel.Visibility = _dbcErrorCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        DbcIssueSummaryText.Text = _dbcErrorCount == 1
            ? "Er is 1 DBC-gerelateerde fout. Loop deze in de editor na en maak een gevalideerde herstelkopie."
            : $"Er zijn {_dbcErrorCount:N0} DBC-gerelateerde fouten. Loop deze in de editor na en maak een gevalideerde herstelkopie.";
        UpdateRepairPreview();
    }

    private void ShowOverview()
    {
        StepNumberText.Text = $"Stap 1 van {GetStepCount()}";
        StepDescriptionText.Text = "Controleer welke gegevens niet betrouwbaar konden worden geïmporteerd.";
        OverviewPage.Visibility = Visibility.Visible;
        RepairPage.Visibility = Visibility.Collapsed;
        DbcRepairPage.Visibility = Visibility.Collapsed;
        OverviewButtons.Visibility = Visibility.Visible;
        RepairButtons.Visibility = Visibility.Collapsed;
        DbcRepairButtons.Visibility = Visibility.Collapsed;
        ContinuePartialButton.Visibility = Visibility.Visible;
    }

    private void ShowRepairPage()
    {
        StepNumberText.Text = $"Stap 2 van {GetStepCount()}";
        StepDescriptionText.Text = "Kies herstelacties die de originele bestanden intact en controleerbaar houden.";
        OverviewPage.Visibility = Visibility.Collapsed;
        RepairPage.Visibility = Visibility.Visible;
        DbcRepairPage.Visibility = Visibility.Collapsed;
        OverviewButtons.Visibility = Visibility.Collapsed;
        RepairButtons.Visibility = Visibility.Visible;
        DbcRepairButtons.Visibility = Visibility.Collapsed;
        ContinuePartialButton.Visibility = Visibility.Visible;
        UpdateRepairPreview();
    }

    private void ShowDbcRepairPage()
    {
        StepNumberText.Text = $"Stap {GetStepCount()} van {GetStepCount()}";
        StepDescriptionText.Text = "Loop de DBC-fouten één voor één na, pas frames of signalen aan en valideer een nieuwe kopie.";
        OverviewPage.Visibility = Visibility.Collapsed;
        RepairPage.Visibility = Visibility.Collapsed;
        DbcRepairPage.Visibility = Visibility.Visible;
        OverviewButtons.Visibility = Visibility.Collapsed;
        RepairButtons.Visibility = Visibility.Collapsed;
        DbcRepairButtons.Visibility = Visibility.Visible;
        ContinuePartialButton.Visibility = Visibility.Collapsed;
    }

    private int GetStepCount() => _dbcErrorCount > 0 ? 3 : 2;

    private void OnIssueSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SourceLineText.Text = IssuesGrid.SelectedItem is ImportIssueDisplayRow row && !string.IsNullOrWhiteSpace(row.SourceLine)
            ? row.SourceLine
            : "Voor deze fout is geen afzonderlijke bronregel beschikbaar.";
    }

    private void OnRepairOptionChanged(object sender, RoutedEventArgs e) => UpdateRepairPreview();

    private void UpdateRepairPreview()
    {
        if (!IsInitialized)
        {
            return;
        }

        var cleanLog = CleanLogCheckBox.IsChecked == true && _removableLogErrorCount > 0;
        var replaceDbc = ReplaceDbcCheckBox.IsChecked == true;
        var resolvedCategories = (cleanLog ? _removableLogErrorCount : 0) + (replaceDbc ? _dbcErrorCount : 0);
        var unresolved = Math.Max(0, _errorCount - resolvedCategories);

        var actions = new List<string>();
        if (cleanLog)
        {
            actions.Add("een nieuwe logkopie wordt gemaakt zonder expliciet afgewezen parserregels");
        }

        if (replaceDbc)
        {
            actions.Add("de geselecteerde, opnieuw te valideren DBC wordt gebruikt");
        }

        if (actions.Count == 0)
        {
            actions.Add("de huidige bestanden worden opnieuw gevalideerd");
        }

        RepairPreviewText.Text =
            $"Bij de volgende poging {string.Join(" en ", actions)}. Daarna voert CANalyser de volledige STRICT-validatie opnieuw uit. " +
            (unresolved == 0
                ? "Alle bekende foutcategorieën worden met deze keuzes opnieuw beoordeeld."
                : $"{unresolved:N0} bekende fout(en) hebben nog geen gekozen herstelactie en kunnen STRICT opnieuw blokkeren.");
    }

    private async void OnOpenDbcRepairClick(object sender, RoutedEventArgs e)
    {
        var path = ReplaceDbcCheckBox.IsChecked == true
            ? ReplacementDbcPathTextBox.Text.Trim()
            : _originalDbcPath;
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "De gekozen DBC bestaat niet.", "DBC niet gevonden", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ShowDbcRepairPage();
        DbcLoadingText.Text = "DBC in de editor voorbereiden...";
        DbcLoadingOverlay.Visibility = Visibility.Visible;
        await Task.Yield();
        try
        {
            if (!string.Equals(_loadedDbcPath, path, StringComparison.OrdinalIgnoreCase))
            {
                await _dbcEditor.LoadForRepairAsync(path, CancellationToken.None);
                _dbcSourceLines = await File.ReadAllLinesAsync(path);
                _loadedDbcPath = path;
                RepairedDbcPathTextBox.Text = _dbcEditor.CurrentFilePath ?? string.Empty;
            }

            _dbcIssueIndex = 0;
            DisplayCurrentDbcIssue();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "DBC-editor kon niet openen", MessageBoxButton.OK, MessageBoxImage.Error);
            ShowRepairPage();
        }
        finally
        {
            DbcLoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void DisplayCurrentDbcIssue()
    {
        _pendingSuggestedSignal = null;
        _pendingSuggestedName = null;
        _pendingAggregateSignal = null;
        _pendingDetailSignals = [];
        _pendingDlcFrame = null;
        _pendingDlcValue = null;
        _pendingSuggestedResult = null;
        SuggestedFixButton.Visibility = Visibility.Collapsed;
        SuggestedFixResultText.Visibility = Visibility.Collapsed;

        if (_activeDbcIssues.Count == 0)
        {
            CurrentDbcIssueCounterText.Text = "GEREED";
            CurrentDbcIssueTitleText.Text = "Geen blokkerende DBC-fouten meer";
            CurrentDbcIssueMeaningText.Text = "De opgeslagen herstelkopie is opnieuw door dezelfde DBC-loader gecontroleerd en kan betrouwbaar worden gebruikt.";
            CurrentDbcIssueActionText.Text = "Ga terug naar de herstelkeuzes en probeer de STRICT-import opnieuw met deze kopie.";
            CurrentDbcIssueMessageText.Text = string.Empty;
            CurrentDbcIssueSourceText.Text = string.Empty;
            DbcConflictPanel.Visibility = Visibility.Collapsed;
            _dbcEditor.ClearRepairTargets();
            return;
        }

        _dbcIssueIndex = Math.Clamp(_dbcIssueIndex, 0, _activeDbcIssues.Count - 1);
        var issue = _activeDbcIssues[_dbcIssueIndex];
        var target = ResolveDbcTarget(issue);
        var relatedSignals = ResolveRelatedSignalNames(issue, target);
        var targetFound = _dbcEditor.SelectRelatedFrame(
            target.FrameId,
            target.IsExtended,
            target.FrameName,
            relatedSignals,
            $"Betrokken bij DBC-fout {_dbcIssueIndex + 1:N0} van {_activeDbcIssues.Count:N0}.");
        if (!targetFound)
        {
            _dbcEditor.ClearRepairSelection();
        }
        DbcEditorHost.SetBitLayoutExpanded(issue.Code == "DBC_SIGNAL_LAYOUT");
        DbcEditorHost.RevealSelection();

        var explanation = BuildDbcIssueExplanation(issue, target);
        CurrentDbcIssueCounterText.Text = $"FOUT {_dbcIssueIndex + 1:N0}/{_activeDbcIssues.Count:N0}";
        CurrentDbcIssueTitleText.Text = explanation.Title;
        CurrentDbcIssueMeaningText.Text = explanation.Meaning;
        CurrentDbcIssueActionText.Text = explanation.Action;
        CurrentDbcIssueMessageText.Text = $"{issue.Code}: {issue.Message}";
        CurrentDbcIssueSourceText.Text = string.IsNullOrWhiteSpace(issue.SourceLine)
            ? "Geen afzonderlijke DBC-bronregel beschikbaar; controleer het geselecteerde frame en de validatiemelding in de editor."
            : issue.SourceLine;

        DbcConflictPanel.Visibility = explanation.HasConflict ? Visibility.Visible : Visibility.Collapsed;
        ConflictLeftLabelText.Text = explanation.LeftLabel;
        ConflictLeftSourceText.Text = explanation.LeftSource;
        ConflictRightLabelText.Text = explanation.RightLabel;
        ConflictRightSourceText.Text = explanation.RightSource;
        PrepareSuggestedFix(issue);
    }

    private DbcIssueTarget ResolveDbcTarget(ImportIssue issue)
    {
        uint? frameId = null;
        bool? isExtended = null;
        string? frameName = null;
        string? signalName = null;

        if (issue.SourceLineNumber > 0 && issue.SourceLineNumber <= _dbcSourceLines.LongLength)
        {
            var lineIndex = checked((int)issue.SourceLineNumber - 1);
            var sourceLine = _dbcSourceLines[lineIndex];
            var signalMatch = DbcSignalLineRegex.Match(sourceLine);
            if (signalMatch.Success)
            {
                signalName = signalMatch.Groups["name"].Value;

                for (var index = lineIndex; index >= 0; index--)
                {
                    var frameMatch = DbcFrameLineRegex.Match(_dbcSourceLines[index]);
                    if (!frameMatch.Success ||
                        !uint.TryParse(frameMatch.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawId))
                    {
                        continue;
                    }

                    isExtended = (rawId & CanIdUtilities.DbcExtendedFlag) != 0
                        ? true
                        : rawId <= 0x7FF
                            ? false
                            : null;
                    frameId = CanIdUtilities.NormalizeDbcFrameId(rawId, isExtended);
                    frameName = frameMatch.Groups["name"].Value;
                    break;
                }
            }

            var extendedMuxMatch = ExtendedMuxLineRegex.Match(sourceLine);
            if (extendedMuxMatch.Success &&
                uint.TryParse(extendedMuxMatch.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var extendedMuxId))
            {
                isExtended = (extendedMuxId & CanIdUtilities.DbcExtendedFlag) != 0
                    ? true
                    : extendedMuxId <= 0x7FF
                        ? false
                        : null;
                frameId = CanIdUtilities.NormalizeDbcFrameId(extendedMuxId, isExtended);
                signalName = extendedMuxMatch.Groups["signal"].Value;
            }
        }

        var layoutMatch = LayoutTargetRegex.Match(issue.Message);
        if (layoutMatch.Success &&
            uint.TryParse(layoutMatch.Groups["id"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var layoutId))
        {
            frameId = layoutId;
            isExtended = layoutId > 0x7FF;
            frameName = layoutMatch.Groups["name"].Value.Trim();
        }

        var messageSignalMatch = SignalInMessageRegex.Match(issue.Message);
        if (messageSignalMatch.Success)
        {
            signalName = messageSignalMatch.Groups["name"].Value;
        }

        return new DbcIssueTarget(frameId, isExtended, frameName, signalName);
    }

    private static string GetDbcGuidance(string code) => code switch
    {
        "DBC_PARSE" => "Controleer de gemarkeerde syntax; de herstelkopie bevat alleen constructies die de loader betrouwbaar kon interpreteren.",
        "DBC_SIGNAL_LAYOUT" => "Controleer startbit, lengte, bytevolgorde, DLC en rode overlapvakken in het geselecteerde frame.",
        "DBC_EXT_MUX" => "Controleer de multiplexer en muxwaarde van het genoemde signaal.",
        "DBC_NO_MESSAGES" => "Voeg minimaal één frame met een geldige CAN-ID en DLC toe.",
        "DECODE_ERROR" => "Controleer vooral DLC, signaallengtes en signalen die buiten de payload vallen.",
        "AMBIGUOUS_J1939" => "Controleer dubbele of overlappende extended frame-ID/PGN-definities.",
        _ => "Controleer het gekoppelde frame en de live validatiemelding in de editor."
    };

    private IReadOnlyCollection<string> ResolveRelatedSignalNames(ImportIssue issue, DbcIssueTarget target)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var duplicateMatch = DuplicateSignalMessageRegex.Match(issue.Message);
        if (duplicateMatch.Success)
        {
            names.Add(duplicateMatch.Groups["name"].Value);
        }

        var layoutMatch = LayoutConflictRegex.Match(issue.Message);
        if (layoutMatch.Success)
        {
            names.Add(layoutMatch.Groups["left"].Value);
            names.Add(layoutMatch.Groups["right"].Value);
        }

        if (!string.IsNullOrWhiteSpace(target.SignalName))
        {
            names.Add(target.SignalName);
        }

        if (_appliedSignalFixes.TryGetValue(issue.SourceLineNumber, out var appliedSignal))
        {
            names.Add(appliedSignal.Name);
        }

        return names;
    }

    private DbcIssueExplanation BuildDbcIssueExplanation(ImportIssue issue, DbcIssueTarget target)
    {
        if (_appliedFixResults.TryGetValue(GetIssueKey(issue), out var appliedResult))
        {
            return new DbcIssueExplanation(
                "Voorgestelde oplossing toegepast",
                appliedResult,
                "Controleer de gemarkeerde frame- of signaaldefinitie en sla daarna de herstelkopie op. De volledige STRICT-import bepaalt vervolgens of deze fout is verdwenen.",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                false);
        }

        if (issue.Code == "DECODE_ERROR" && TryParseDlcMismatch(issue, out var mismatch))
        {
            var frame = _dbcEditor.SelectedFrame;
            var expectedText = FormatByteLengths(mismatch.ExpectedPayloadLengths);
            var observedLengths = GetObservedPayloadLengths(target.FrameId);
            var hasMixedLengths = observedLengths.Count > 1;
            var outsideSignals = frame is null
                ? []
                : FindSignalsOutsidePayload(frame, mismatch.ActualPayloadLength);
            var canApply = CanApplyDlcSuggestion(mismatch, frame, hasMixedLengths, outsideSignals);

            var action = canApply
                ? $"Alle signalen passen binnen {mismatch.ActualPayloadLength} byte en alle waargenomen frames met dit CAN-ID hebben dezelfde lengte. Gebruik de voorgestelde knop om de frame-DLC naar {mismatch.ActualPayloadLength} te zetten en valideer daarna opnieuw."
                : hasMixedLengths
                    ? $"Voor dit CAN-ID komen meerdere payloadlengtes voor ({FormatByteLengths(observedLengths)}). Een DBC-frame kan maar een DLC hebben; controleer daarom eerst of de log verschillende berichtvarianten onder hetzelfde CAN-ID mengt."
                    : outsideSignals.Count > 0
                        ? $"Verlaag de DLC niet automatisch: {string.Join(", ", outsideSignals)} gebruikt bits buiten de {mismatch.ActualPayloadLength}-byte payload. Controleer of de log is afgekapt of corrigeer startbit/lengte van deze signalen."
                        : mismatch.ExpectedPayloadLengths.Count > 1
                            ? "Er passen meerdere DBC-definities met verschillende DLC's op dit frame. Maak eerst de frame-ID's of definities uniek."
                            : "De wizard kan deze DLC niet veilig automatisch wijzigen. Controleer de payloadlengte in Raw Frames en vergelijk die met de verzendende ECU-specificatie.";

            return new DbcIssueExplanation(
                $"Payloadlengte klopt niet voor {target.FrameName ?? frame?.Name ?? "dit frame"}",
                $"In de log zijn {mismatch.CountText} frame(s) met CAN-ID 0x{target.FrameId:X} en payloadlengte {mismatch.ActualPayloadLength} byte gevonden. De gekoppelde DBC-definitie verwacht {expectedText}. CANalyser vereist hier een exacte overeenkomst om geen bytes stilzwijgend weg te laten of ontbrekende bits te verzinnen.",
                action,
                "DBC-definitie",
                frame is null
                    ? $"Verwachte DLC: {expectedText}. Het gekoppelde frame kon niet in de editor worden gevonden."
                    : $"{frame.Name} ({frame.FrameIdHex}): DLC {frame.Dlc} byte; {frame.SignalCount} signalen.",
                "Waargenomen logframes",
                $"{mismatch.CountText} frame(s): payloadlengte {mismatch.ActualPayloadLength} byte. Waargenomen lengtes voor dit CAN-ID: {FormatByteLengths(observedLengths)}.",
                true);
        }

        var duplicateSignalMatch = DuplicateSignalMessageRegex.Match(issue.Message);
        if (duplicateSignalMatch.Success)
        {
            var signalName = duplicateSignalMatch.Groups["name"].Value;
            var previous = FindPreviousSignalDefinition(issue.SourceLineNumber, signalName);
            var current = GetSourceReference(issue);
            var frame = string.IsNullOrWhiteSpace(target.FrameName) ? "hetzelfde frame" : $"frame {target.FrameName}";
            var leftMux = DescribeMuxPath(previous?.Text);
            var rightMux = DescribeMuxPath(current.Text);
            return new DbcIssueExplanation(
                $"Dubbele signaalnaam: {signalName}",
                $"Binnen {frame} bestaan twee signalen met exact dezelfde naam. De eerste hoort bij {leftMux} en de tweede bij {rightMux}. Signaalnamen moeten binnen een frame uniek zijn; anders kan software niet betrouwbaar naar de juiste definitie verwijzen.",
                "Behoud beide multiplexvarianten, maar geef de tweede definitie een unieke naam. De knop rechts stelt een naam voor waarin de muxwaarde herkenbaar blijft.",
                previous is null ? "Eerste definitie" : $"Eerste definitie - regel {previous.LineNumber:N0}",
                previous?.Text ?? "De eerste definitie kon niet in de bron worden teruggevonden.",
                current.LineNumber > 0 ? $"Botsende definitie - regel {current.LineNumber:N0}" : "Botsende definitie",
                current.Text,
                true);
        }

        var currentSource = GetSourceReference(issue);
        var attributeMatch = AttributeDefinitionRegex.Match(currentSource.Text);
        if (attributeMatch.Success)
        {
            var attributeName = attributeMatch.Groups["name"].Value;
            var scope = attributeMatch.Groups["scope"].Value;
            var previous = FindPreviousAttributeDefinition(issue.SourceLineNumber, scope, attributeName);
            var identical = previous is not null &&
                string.Equals(previous.Text.Trim(), currentSource.Text.Trim(), StringComparison.Ordinal);
            return new DbcIssueExplanation(
                $"Dubbele attribuutdefinitie: {attributeName}",
                $"Het attribuut {attributeName} wordt voor scope {scope} meer dan een keer gedefinieerd. Een DBC-parser verwacht per scope maar een definitie." +
                (identical ? " Deze twee regels zijn inhoudelijk gelijk, dus de tweede is overbodig." : " De definities verschillen; kies bewust welke definitie leidend moet zijn."),
                "Verwijder de tweede BA_DEF_-regel in de bron-DBC, of bewaar slechts een definitie. Let op: de genormaliseerde herstelkopie neemt editor-onbekende attributen niet over; controleer of andere tooling deze metadata nodig heeft.",
                previous is null ? "Eerste definitie" : $"Eerste definitie - regel {previous.LineNumber:N0}",
                previous?.Text ?? "De eerste definitie kon niet in de bron worden teruggevonden.",
                currentSource.LineNumber > 0 ? $"Dubbele definitie - regel {currentSource.LineNumber:N0}" : "Dubbele definitie",
                currentSource.Text,
                true);
        }

        var layoutConflictMatch = LayoutConflictRegex.Match(issue.Message);
        if (issue.Code == "DBC_SIGNAL_LAYOUT" && layoutConflictMatch.Success)
        {
            var leftName = layoutConflictMatch.Groups["left"].Value;
            var rightName = layoutConflictMatch.Groups["right"].Value;
            var frame = _dbcEditor.SelectedFrame;
            var left = frame?.Signals.FirstOrDefault(signal => signal.Name == leftName);
            var right = frame?.Signals.FirstOrDefault(signal => signal.Name == rightName);
            var aggregateRecommendation = FindAggregateOverlapRecommendation(frame, left, right);
            if (aggregateRecommendation is not null)
            {
                var aggregate = aggregateRecommendation.Aggregate;
                var detailSummary = SummarizeSignals(aggregateRecommendation.DetailSignals);
                return new DbcIssueExplanation(
                    $"Ruwe samenvatting overlapt {aggregateRecommendation.DetailSignals.Count:N0} detailvelden",
                    $"{aggregate.Name} leest {DescribeOccupiedBits(aggregate)} als een ruwe waarde. De {aggregateRecommendation.DetailSignals.Count:N0} detailvelden verdelen exact diezelfde bits zonder elkaar te overlappen. Dit is dus een redundante totaalweergave naast de afzonderlijke commandovelden, niet een onbekende verschuiving in de payload.",
                    $"Aanbevolen: behoud de {aggregateRecommendation.DetailSignals.Count:N0} betekenisvolle detailvelden en verwijder alleen {aggregate.Name}. Je houdt daarmee alle afzonderlijke commando's; de originele bytes en het ruwe 16-bits woord blijven terug te vinden via Raw Frames.",
                    "Redundante ruwe samenvatting",
                    FormatSignalDefinition(aggregate),
                    $"Detailvelden die samen dezelfde bits vullen ({aggregateRecommendation.DetailSignals.Count:N0})",
                    detailSummary,
                    true);
            }

            return new DbcIssueExplanation(
                $"Gelijktijdige bit-overlap: {leftName} en {rightName}",
                $"Deze twee signalen gebruiken een of meer dezelfde payloadbits en kunnen tegelijk actief zijn. Daardoor is niet eenduidig welke waarde die bits voorstellen; CANalyser blokkeert daarom de decode van frame {frame?.Name ?? target.FrameName ?? "(onbekend)"}.",
                "Pas startbit of lengte aan zodat de signalen niet meer overlappen. Als het multiplex-signalen zijn, geef ze dan correcte, niet-overlappende muxwaarden. Alleen echte gelijktijdige conflicten worden rood getoond.",
                "Eerste betrokken signaal",
                FormatSignalDefinition(left),
                "Tweede betrokken signaal",
                FormatSignalDefinition(right),
                true);
        }

        var genericTitle = issue.Code switch
        {
            "DBC_EXT_MUX" => "Ongeldige uitgebreide multiplexdefinitie",
            "DBC_NO_MESSAGES" => "Geen bruikbare CAN-frames gevonden",
            "DECODE_ERROR" => "DBC-definitie kan niet betrouwbaar decoderen",
            "AMBIGUOUS_J1939" => "Meerdere J1939-definities passen op hetzelfde frame",
            _ => "DBC-regel kan niet betrouwbaar worden gelezen"
        };
        var genericAction = issue.Code switch
        {
            "DBC_EXT_MUX" => "Controleer de genoemde multiplexer, muxwaarde en het gekoppelde signaal. De oranje rij toont het betrokken object.",
            "DBC_NO_MESSAGES" => "Voeg minimaal een frame toe met een geldige CAN-ID, DLC en naam.",
            "DECODE_ERROR" => "Controleer DLC, startbit, lengte en bytevolgorde van de oranje gemarkeerde definitie.",
            "AMBIGUOUS_J1939" => "Maak de extended frame-ID of PGN-definitie uniek zodat maar een bericht kan matchen.",
            _ => "Controleer de technische bronregel en het oranje gemarkeerde frame of signaal. Pas de definitie aan en valideer daarna de herstelkopie opnieuw."
        };
        return new DbcIssueExplanation(
            genericTitle,
            TranslateParserMeaning(issue),
            genericAction,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            false);
    }

    private void PrepareSuggestedFix(ImportIssue issue)
    {
        var issueKey = GetIssueKey(issue);
        if (_appliedFixResults.TryGetValue(issueKey, out var appliedResult))
        {
            SuggestedFixResultText.Text = appliedResult;
            SuggestedFixResultText.Visibility = Visibility.Visible;
            return;
        }

        if (issue.Code == "DECODE_ERROR" &&
            TryParseDlcMismatch(issue, out var mismatch) &&
            _dbcEditor.SelectedFrame is { } frame)
        {
            var target = ResolveDbcTarget(issue);
            var hasMixedLengths = GetObservedPayloadLengths(target.FrameId).Count > 1;
            var outsideSignals = FindSignalsOutsidePayload(frame, mismatch.ActualPayloadLength);
            if (CanApplyDlcSuggestion(mismatch, frame, hasMixedLengths, outsideSignals))
            {
                _pendingDlcFrame = frame;
                _pendingDlcValue = mismatch.ActualPayloadLength;
                _pendingSuggestedResult =
                    $"Toegepast in de editor: DLC van {frame.Name} is gewijzigd van {frame.Dlc} naar {mismatch.ActualPayloadLength} byte. Sla de herstelkopie op en probeer STRICT opnieuw met de log.";
                SuggestedFixButton.Content = new TextBlock { Text = $"Pas DLC aan naar {mismatch.ActualPayloadLength} (aanbevolen)" };
                SuggestedFixButton.Visibility = Visibility.Visible;
                return;
            }
        }

        var duplicateMatch = DuplicateSignalMessageRegex.Match(issue.Message);
        if (duplicateMatch.Success && _dbcEditor.SelectedFrame is not null)
        {
            var duplicateName = duplicateMatch.Groups["name"].Value;
            var matches = _dbcEditor.SelectedFrame.Signals
                .Where(signal => string.Equals(signal.Name, duplicateName, StringComparison.Ordinal))
                .ToList();
            if (matches.Count < 2)
            {
                return;
            }

            _pendingSuggestedSignal = matches[^1];
            var suffix = _pendingSuggestedSignal.MultiplexedValue.HasValue
                ? $"m{_pendingSuggestedSignal.MultiplexedValue.Value.ToString(CultureInfo.InvariantCulture)}"
                : "variant2";
            var baseName = $"{duplicateName}_{suffix}";
            var candidate = baseName;
            for (var number = 2; _dbcEditor.SelectedFrame.Signals.Any(signal => signal != _pendingSuggestedSignal && signal.Name == candidate); number++)
            {
                candidate = $"{baseName}_{number.ToString(CultureInfo.InvariantCulture)}";
            }

            _pendingSuggestedName = candidate;
            _pendingSuggestedResult = $"Toegepast in de editor: het tweede signaal heet nu {candidate}. Sla de herstelkopie op om dit te valideren.";
            SuggestedFixButton.Content = new TextBlock { Text = $"Hernoem tweede naar {candidate}" };
            SuggestedFixButton.Visibility = Visibility.Visible;
            return;
        }

        var layoutMatch = LayoutConflictRegex.Match(issue.Message);
        if (issue.Code == "DBC_SIGNAL_LAYOUT" && layoutMatch.Success && _dbcEditor.SelectedFrame is not null)
        {
            var left = _dbcEditor.SelectedFrame.Signals.FirstOrDefault(signal => signal.Name == layoutMatch.Groups["left"].Value);
            var right = _dbcEditor.SelectedFrame.Signals.FirstOrDefault(signal => signal.Name == layoutMatch.Groups["right"].Value);
            var recommendation = FindAggregateOverlapRecommendation(_dbcEditor.SelectedFrame, left, right);
            if (recommendation is null)
            {
                return;
            }

            _pendingAggregateSignal = recommendation.Aggregate;
            _pendingDetailSignals = recommendation.DetailSignals;
            _pendingSuggestedResult =
                $"Toegepast in de editor: {recommendation.Aggregate.Name} is verwijderd; de {recommendation.DetailSignals.Count:N0} detailvelden zijn behouden. Sla de herstelkopie op om dit te valideren.";
            SuggestedFixButton.Content = new TextBlock { Text = $"Verwijder {recommendation.Aggregate.Name} (aanbevolen)" };
            SuggestedFixButton.Visibility = Visibility.Visible;
        }
    }

    private void OnApplySuggestedFixClick(object sender, RoutedEventArgs e)
    {
        if (_activeDbcIssues.Count == 0)
        {
            return;
        }

        var issue = _activeDbcIssues[_dbcIssueIndex];
        if (_pendingSuggestedSignal is not null && !string.IsNullOrWhiteSpace(_pendingSuggestedName))
        {
            _pendingSuggestedSignal.Name = _pendingSuggestedName;
            _pendingSuggestedSignal.IsRepairTarget = true;
            _pendingSuggestedSignal.RepairHint = "Deze rij is door de herstelwizard hernoemd om de dubbele signaalnaam op te lossen.";
            _appliedSignalFixes[issue.SourceLineNumber] = _pendingSuggestedSignal;
        }
        else if (_pendingAggregateSignal is not null && _dbcEditor.SelectedFrame is not null)
        {
            _dbcEditor.SelectedFrame.Signals.Remove(_pendingAggregateSignal);
            foreach (var signal in _pendingDetailSignals)
            {
                signal.IsRepairTarget = true;
                signal.RepairHint = "Dit detailveld is behouden; de overlappende ruwe samenvatting is verwijderd.";
            }

            _dbcEditor.SelectedSignal = _pendingDetailSignals.FirstOrDefault();
        }
        else if (_pendingDlcFrame is not null && _pendingDlcValue.HasValue)
        {
            _pendingDlcFrame.Dlc = _pendingDlcValue.Value;
            _pendingDlcFrame.IsRepairTarget = true;
            _pendingDlcFrame.RepairHint = "De herstelwizard heeft de DLC aangepast aan de eenduidig waargenomen payloadlengte in de log.";
            _dbcEditor.SelectedFrame = _pendingDlcFrame;
        }
        else
        {
            return;
        }

        var result = _pendingSuggestedResult ?? "De voorgestelde oplossing is toegepast in de editor.";
        _appliedFixResults[GetIssueKey(issue)] = result;
        SuggestedFixResultText.Text = result;
        SuggestedFixResultText.Visibility = Visibility.Visible;
        SuggestedFixButton.Visibility = Visibility.Collapsed;
        CurrentDbcIssueActionText.Text = _pendingDlcFrame is not null
            ? "De DLC is aangepast. Loop eventuele overige fouten na, sla de herstelkopie eenmaal op en probeer daarna de volledige STRICT-import opnieuw."
            : "De voorgestelde wijziging is toegepast. Controleer de gemarkeerde signalen en sla daarna de herstelkopie op voor een volledige validatie.";
        DbcEditorHost.RevealSelection();
        _pendingSuggestedSignal = null;
        _pendingSuggestedName = null;
        _pendingAggregateSignal = null;
        _pendingDetailSignals = [];
        _pendingDlcFrame = null;
        _pendingDlcValue = null;
        _pendingSuggestedResult = null;
    }

    private static AggregateOverlapRecommendation? FindAggregateOverlapRecommendation(
        DbcFrameRow? frame,
        DbcSignalRow? left,
        DbcSignalRow? right)
    {
        if (frame is null || left is null || right is null)
        {
            return null;
        }

        foreach (var aggregate in new[] { left, right }.OrderByDescending(signal => signal.Length))
        {
            var aggregateBits = DbcBitLayout
                .GetOccupiedLsb0Bits(aggregate.StartBit, aggregate.Length, aggregate.LittleEndian)
                .ToHashSet();
            if (aggregateBits.Count != aggregate.Length)
            {
                continue;
            }

            var details = frame.Signals
                .Where(signal => signal != aggregate && SignalsCanBeActiveTogether(aggregate, signal))
                .Select(signal => new
                {
                    Signal = signal,
                    Bits = DbcBitLayout.GetOccupiedLsb0Bits(signal.StartBit, signal.Length, signal.LittleEndian).ToHashSet()
                })
                .Where(candidate => candidate.Bits.Count > 0 && candidate.Bits.IsSubsetOf(aggregateBits))
                .ToList();
            if (details.Count < 2)
            {
                continue;
            }

            var union = new HashSet<int>();
            var detailsOverlap = false;
            foreach (var detail in details)
            {
                if (union.Overlaps(detail.Bits))
                {
                    detailsOverlap = true;
                    break;
                }

                union.UnionWith(detail.Bits);
            }

            if (!detailsOverlap && union.SetEquals(aggregateBits))
            {
                return new AggregateOverlapRecommendation(aggregate, details.Select(candidate => candidate.Signal).ToArray());
            }
        }

        return null;
    }

    private static bool SignalsCanBeActiveTogether(DbcSignalRow left, DbcSignalRow right)
    {
        IReadOnlyList<int> leftIds = left.MultiplexedValue.HasValue ? [left.MultiplexedValue.Value] : [];
        IReadOnlyList<int> rightIds = right.MultiplexedValue.HasValue ? [right.MultiplexedValue.Value] : [];
        return DbcBitLayout.CanBeActiveTogether(
            left.IsMultiplexerSwitch,
            leftIds,
            left.MultiplexerRanges,
            right.IsMultiplexerSwitch,
            rightIds,
            right.MultiplexerRanges);
    }

    private static string SummarizeSignals(IReadOnlyList<DbcSignalRow> signals)
    {
        var visible = signals.Take(8).Select(signal => $"{signal.Name} [{DescribeOccupiedBits(signal)}]").ToList();
        if (signals.Count > visible.Count)
        {
            visible.Add($"en {signals.Count - visible.Count:N0} andere detailvelden");
        }

        return string.Join("  |  ", visible);
    }

    private static string DescribeOccupiedBits(DbcSignalRow signal)
    {
        var bits = DbcBitLayout.GetOccupiedLsb0Bits(signal.StartBit, signal.Length, signal.LittleEndian);
        if (bits.Count == 0)
        {
            return "geen geldige bits";
        }

        var minimum = bits.Min();
        var maximum = bits.Max();
        return minimum == maximum ? $"bit {minimum}" : $"bits {minimum}-{maximum}";
    }

    private static string GetIssueKey(ImportIssue issue)
        => $"{issue.Code}|{issue.SourceLineNumber.ToString(CultureInfo.InvariantCulture)}|{issue.Message}";

    private DbcSourceReference? FindPreviousSignalDefinition(long sourceLineNumber, string signalName)
    {
        var startIndex = Math.Min(checked((int)Math.Max(sourceLineNumber - 2, 0)), _dbcSourceLines.Length - 1);
        for (var index = startIndex; index >= 0; index--)
        {
            if (DbcFrameLineRegex.IsMatch(_dbcSourceLines[index]))
            {
                break;
            }

            var match = DbcSignalLineRegex.Match(_dbcSourceLines[index]);
            if (match.Success && string.Equals(match.Groups["name"].Value, signalName, StringComparison.Ordinal))
            {
                return new DbcSourceReference(index + 1, _dbcSourceLines[index]);
            }
        }

        return null;
    }

    private DbcSourceReference? FindPreviousAttributeDefinition(long sourceLineNumber, string scope, string name)
    {
        var startIndex = Math.Min(checked((int)Math.Max(sourceLineNumber - 2, 0)), _dbcSourceLines.Length - 1);
        for (var index = startIndex; index >= 0; index--)
        {
            var match = AttributeDefinitionRegex.Match(_dbcSourceLines[index]);
            if (match.Success &&
                string.Equals(match.Groups["scope"].Value, scope, StringComparison.Ordinal) &&
                string.Equals(match.Groups["name"].Value, name, StringComparison.Ordinal))
            {
                return new DbcSourceReference(index + 1, _dbcSourceLines[index]);
            }
        }

        return null;
    }

    private DbcSourceReference GetSourceReference(ImportIssue issue)
    {
        if (issue.SourceLineNumber > 0 && issue.SourceLineNumber <= _dbcSourceLines.LongLength)
        {
            return new DbcSourceReference(
                checked((int)issue.SourceLineNumber),
                _dbcSourceLines[checked((int)issue.SourceLineNumber - 1)]);
        }

        return new DbcSourceReference(0, issue.SourceLine ?? string.Empty);
    }

    private static string DescribeMuxPath(string? sourceLine)
    {
        if (string.IsNullOrWhiteSpace(sourceLine))
        {
            return "een onbekend muxpad";
        }

        var match = SignalMuxTokenRegex.Match(sourceLine);
        if (!match.Success)
        {
            return "het altijd actieve pad";
        }

        var token = match.Groups["mux"].Value;
        return token.Equals("M", StringComparison.OrdinalIgnoreCase)
            ? "de multiplexer-schakelaar"
            : $"muxwaarde {token[1..]}";
    }

    private static string FormatSignalDefinition(DbcSignalRow? signal)
    {
        if (signal is null)
        {
            return "Het signaal kon niet in de bewerkbare DBC worden teruggevonden.";
        }

        var endian = signal.LittleEndian ? "Intel/little-endian" : "Motorola/big-endian";
        var mux = string.IsNullOrWhiteSpace(signal.MuxText) ? "altijd actief" : $"mux {signal.MuxText}";
        return $"{signal.Name}: startbit {signal.StartBit}, lengte {signal.Length} bit, {endian}, {mux}.";
    }

    private static bool TryParseDlcMismatch(ImportIssue issue, out DlcMismatchDetails details)
    {
        var match = DecodeDlcMismatchRegex.Match(issue.Message);
        if (!match.Success ||
            !uint.TryParse(match.Groups["id"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var frameId) ||
            !int.TryParse(match.Groups["actual"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var actualLength))
        {
            details = default!;
            return false;
        }

        var expected = match.Groups["expected"].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) ? length : -1)
            .Where(static length => length >= 0)
            .Distinct()
            .Order()
            .ToArray();
        if (expected.Length == 0)
        {
            details = default!;
            return false;
        }

        details = new DlcMismatchDetails(
            frameId,
            match.Groups["count"].Value,
            actualLength,
            expected);
        return true;
    }

    private IReadOnlyList<int> GetObservedPayloadLengths(uint? frameId)
    {
        if (!frameId.HasValue)
        {
            return [];
        }

        return _activeDbcIssues
            .Select(issue => TryParseDlcMismatch(issue, out var mismatch) ? mismatch : null)
            .Where(mismatch => mismatch?.FrameId == frameId.Value)
            .Select(mismatch => mismatch!.ActualPayloadLength)
            .Distinct()
            .Order()
            .ToArray();
    }

    private static IReadOnlyList<string> FindSignalsOutsidePayload(DbcFrameRow frame, int payloadLength)
    {
        var bitLength = Math.Max(0, payloadLength) * 8;
        return frame.Signals
            .Where(signal => DbcBitLayout
                .GetOccupiedLsb0Bits(signal.StartBit, signal.Length, signal.LittleEndian)
                .Any(bit => bit < 0 || bit >= bitLength))
            .Select(signal => signal.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool CanApplyDlcSuggestion(
        DlcMismatchDetails mismatch,
        DbcFrameRow? frame,
        bool hasMixedLengths,
        IReadOnlyList<string> outsideSignals)
    {
        return frame is not null &&
               mismatch.ActualPayloadLength is >= 0 and <= 64 &&
               mismatch.ActualPayloadLength != frame.Dlc &&
               mismatch.ExpectedPayloadLengths.Count == 1 &&
               mismatch.ExpectedPayloadLengths[0] == frame.Dlc &&
               !hasMixedLengths &&
               outsideSignals.Count == 0;
    }

    private static string FormatByteLengths(IEnumerable<int> lengths)
    {
        var values = lengths.Distinct().Order().ToArray();
        return values.Length switch
        {
            0 => "onbekend",
            1 => $"{values[0]} byte",
            _ => $"{string.Join(", ", values)} byte"
        };
    }

    private static string TranslateParserMeaning(ImportIssue issue)
    {
        return issue.Code switch
        {
            "DBC_EXT_MUX" => "Een uitgebreide muxregel verwijst naar een frame, multiplexer of signaal dat niet eenduidig gevonden kan worden.",
            "DBC_NO_MESSAGES" => "De DBC bevat geen frames die de loader veilig kan gebruiken.",
            "DECODE_ERROR" => "Een of meer DBC-eigenschappen maken betrouwbare omzetting van CAN-bits naar signaalwaarden onmogelijk.",
            "AMBIGUOUS_J1939" => "Meer dan een extended DBC-bericht kan hetzelfde J1939-frame beschrijven.",
            _ => "De parser kan deze DBC-constructie niet eenduidig interpreteren. Onder Technische details staat de oorspronkelijke melding en bronregel."
        };
    }

    private void OnPreviousDbcIssueClick(object sender, RoutedEventArgs e)
    {
        if (_activeDbcIssues.Count == 0)
        {
            return;
        }

        _dbcIssueIndex = (_dbcIssueIndex - 1 + _activeDbcIssues.Count) % _activeDbcIssues.Count;
        DisplayCurrentDbcIssue();
    }

    private void OnNextDbcIssueClick(object sender, RoutedEventArgs e)
    {
        if (_activeDbcIssues.Count == 0)
        {
            return;
        }

        _dbcIssueIndex = (_dbcIssueIndex + 1) % _activeDbcIssues.Count;
        DisplayCurrentDbcIssue();
    }

    private async void OnSaveAndValidateDbcClick(object sender, RoutedEventArgs e)
    {
        var path = RepairedDbcPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(this, "Kies een bestandsnaam voor de gecorrigeerde DBC-kopie.", "Pad ontbreekt", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DbcLoadingText.Text = "Herstelkopie opslaan en opnieuw valideren...";
        DbcLoadingOverlay.Visibility = Visibility.Visible;
        await Task.Yield();
        try
        {
            var remainingErrors = await _dbcEditor.SaveRepairCopyAsync(path, CancellationToken.None);
            var savedPath = Path.GetFullPath(path);
            ReplacementDbcPathTextBox.Text = savedPath;
            ReplaceDbcCheckBox.IsChecked = true;
            _dbcSourceLines = await File.ReadAllLinesAsync(savedPath);
            _loadedDbcPath = savedPath;
            _appliedSignalFixes.Clear();
            _appliedFixResults.Clear();
            _activeDbcIssues = remainingErrors.Take(MaxNavigableDbcIssues).ToArray();
            _dbcIssueIndex = 0;
            DbcValidationResultText.Foreground = remainingErrors.Count == 0 ? Brushes.DarkGreen : Brushes.DarkOrange;
            DbcValidationResultText.Text = remainingErrors.Count == 0
                ? "Validatie geslaagd. Deze DBC-kopie is geselecteerd voor de volgende STRICT-poging."
                : $"Kopie opgeslagen, maar de validator vindt nog {remainingErrors.Count:N0} fout(en). Loop de bijgewerkte lijst verder na of probeer STRICT om het volledige rapport opnieuw op te bouwen.";
            DisplayCurrentDbcIssue();
            UpdateRepairPreview();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "DBC-herstel opslaan mislukt", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            DbcLoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void OnBrowseRepairedLogClick(object sender, RoutedEventArgs e)
    {
        var extension = Path.GetExtension(_originalLogPath);
        var dialog = new SaveFileDialog
        {
            Title = "Opgeschoonde CAN-logkopie opslaan",
            Filter = string.IsNullOrWhiteSpace(extension)
                ? "Alle bestanden (*.*)|*.*"
                : $"CAN-log (*{extension})|*{extension}|Alle bestanden (*.*)|*.*",
            FileName = Path.GetFileName(RepairedLogPathTextBox.Text),
            InitialDirectory = Path.GetDirectoryName(RepairedLogPathTextBox.Text) ?? string.Empty,
            AddExtension = !string.IsNullOrWhiteSpace(extension),
            DefaultExt = extension
        };
        if (dialog.ShowDialog(this) == true)
        {
            RepairedLogPathTextBox.Text = dialog.FileName;
        }
    }

    private void OnBrowseReplacementDbcClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Gecorrigeerde of andere DBC selecteren",
            Filter = "DBC-bestanden (*.dbc)|*.dbc|Alle bestanden (*.*)|*.*",
            FileName = Path.GetFileName(ReplacementDbcPathTextBox.Text),
            InitialDirectory = Path.GetDirectoryName(ReplacementDbcPathTextBox.Text) ?? string.Empty
        };
        if (dialog.ShowDialog(this) == true)
        {
            ReplacementDbcPathTextBox.Text = dialog.FileName;
            ReplaceDbcCheckBox.IsChecked = true;
        }
    }

    private void OnBrowseRepairedDbcClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Gecorrigeerde DBC-kopie opslaan",
            Filter = "DBC-bestanden (*.dbc)|*.dbc|Alle bestanden (*.*)|*.*",
            FileName = Path.GetFileName(RepairedDbcPathTextBox.Text),
            InitialDirectory = Path.GetDirectoryName(RepairedDbcPathTextBox.Text) ?? string.Empty,
            AddExtension = true,
            DefaultExt = ".dbc"
        };
        if (dialog.ShowDialog(this) == true)
        {
            RepairedDbcPathTextBox.Text = dialog.FileName;
        }
    }

    private void OnNextClick(object sender, RoutedEventArgs e) => ShowRepairPage();

    private void OnBackClick(object sender, RoutedEventArgs e) => ShowOverview();

    private void OnBackFromDbcRepairClick(object sender, RoutedEventArgs e)
    {
        if (_dbcEditor.HasUnsavedChanges &&
            MessageBox.Show(
                this,
                "De DBC-aanpassingen zijn nog niet als herstelkopie opgeslagen. Teruggaan zonder opslaan?",
                "Niet-opgeslagen DBC-aanpassingen",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        ShowRepairPage();
    }

    private void OnContinuePartialClick(object sender, RoutedEventArgs e)
    {
        Result = new ImportRepairWizardResult(
            ImportRepairDecision.ContinuePartial,
            false,
            _originalLogPath,
            _originalDbcPath);
        _allowClose = true;
        Close();
    }

    private void OnRetryStrictClick(object sender, RoutedEventArgs e)
    {
        var cleanLog = CleanLogCheckBox.IsChecked == true && _removableLogErrorCount > 0;
        var logPath = cleanLog ? RepairedLogPathTextBox.Text.Trim() : _originalLogPath;
        var replaceDbc = ReplaceDbcCheckBox.IsChecked == true;
        var dbcPath = replaceDbc ? ReplacementDbcPathTextBox.Text.Trim() : _originalDbcPath;

        if (cleanLog && string.IsNullOrWhiteSpace(logPath))
        {
            MessageBox.Show(this, "Kies een bestandsnaam voor de opgeschoonde logkopie.", "Pad ontbreekt", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (cleanLog && !TryValidateRepairedPath(logPath))
        {
            return;
        }

        if (replaceDbc && !File.Exists(dbcPath))
        {
            MessageBox.Show(this, "De gekozen DBC bestaat niet of is nog niet opgeslagen.", "DBC niet gevonden", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (replaceDbc && _dbcEditor.HasUnsavedChanges)
        {
            MessageBox.Show(
                this,
                "Sla de DBC-aanpassingen eerst op via 'Kopie opslaan en valideren'. Anders gebruikt STRICT alleen de laatst opgeslagen versie.",
                "DBC-aanpassingen nog niet opgeslagen",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Result = new ImportRepairWizardResult(ImportRepairDecision.RetryStrict, cleanLog, logPath, dbcPath);
        _allowClose = true;
        Close();
    }

    private bool TryValidateRepairedPath(string path)
    {
        try
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(_originalLogPath), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "De opgeschoonde kopie mag het originele logbestand niet overschrijven.", "Ongeldig pad", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            MessageBox.Show(this, $"Het gekozen pad is ongeldig:\n{ex.Message}", "Ongeldig pad", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Result = new ImportRepairWizardResult(ImportRepairDecision.Cancel, false, _originalLogPath, _originalDbcPath);
        Close();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        OnCancelClick(sender, e);
        e.Handled = true;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !_dbcEditor.HasUnsavedChanges)
        {
            return;
        }

        var discard = MessageBox.Show(
            this,
            "De DBC-aanpassingen zijn nog niet als herstelkopie opgeslagen. De wizard toch sluiten?",
            "Niet-opgeslagen DBC-aanpassingen",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (discard != MessageBoxResult.Yes)
        {
            e.Cancel = true;
        }
    }

    internal static ImportRepairAnalysis AnalyzeReport(ImportReport report, IImportRepairService repairService)
    {
        var errorCount = 0;
        var warningCount = 0;
        var removableLogErrorCount = 0;
        var dbcErrorCount = 0;
        var categories = new Dictionary<(string Parser, string Code), CategorySample>();
        var categoryOrder = new List<(string Parser, string Code)>();
        var dbcIssues = new List<ImportIssue>();

        foreach (var issue in report.Issues)
        {
            if (issue.Severity == ImportIssueSeverity.Error)
            {
                errorCount++;
                if (repairService.IsRemovableLogIssue(issue))
                {
                    removableLogErrorCount++;
                }

                if (IsDbcRelated(issue))
                {
                    dbcErrorCount++;
                    if (dbcIssues.Count < MaxNavigableDbcIssues)
                    {
                        dbcIssues.Add(issue);
                    }
                }
            }
            else
            {
                warningCount++;
            }

            var key = (issue.Parser, issue.Code);
            if (!categories.TryGetValue(key, out var category))
            {
                category = new CategorySample();
                categories[key] = category;
                categoryOrder.Add(key);
            }

            category.Count++;
            if (category.Issues.Count < MaxSamplesPerCategory)
            {
                category.Issues.Add(issue);
            }
        }

        var rows = new List<ImportIssueDisplayRow>(Math.Min(MaxDisplayedIssues, report.Issues.Count));
        foreach (var key in categoryOrder)
        {
            var category = categories[key];
            foreach (var issue in category.Issues)
            {
                if (rows.Count >= MaxDisplayedIssues)
                {
                    break;
                }

                rows.Add(new ImportIssueDisplayRow(
                    $"{key.Parser} / {key.Code} ({category.Count:N0})",
                    issue.Severity == ImportIssueSeverity.Error ? "Fout" : "Waarschuwing",
                    issue.SourceLineNumber > 0 ? issue.SourceLineNumber.ToString(CultureInfo.CurrentCulture) : "-",
                    issue.Message,
                    issue.SourceLine));
            }
        }

        return new ImportRepairAnalysis(
            errorCount,
            warningCount,
            removableLogErrorCount,
            dbcErrorCount,
            rows,
            dbcIssues);
    }

    private static bool IsDbcRelated(ImportIssue issue)
    {
        return issue.Parser.Contains("DBC", StringComparison.OrdinalIgnoreCase) ||
               issue.Code.StartsWith("DBC_", StringComparison.Ordinal) ||
               issue.Code is "DECODE_ERROR" or "AMBIGUOUS_J1939";
    }

    private sealed class CategorySample
    {
        public int Count { get; set; }

        public List<ImportIssue> Issues { get; } = [];
    }

    internal sealed record ImportRepairAnalysis(
        int ErrorCount,
        int WarningCount,
        int RemovableLogErrorCount,
        int DbcErrorCount,
        IReadOnlyList<ImportIssueDisplayRow> Rows,
        IReadOnlyList<ImportIssue> DbcIssues);

    internal sealed record ImportIssueDisplayRow(
        string Category,
        string Severity,
        string SourceLineNumber,
        string Message,
        string SourceLine);

    private sealed record DbcIssueExplanation(
        string Title,
        string Meaning,
        string Action,
        string LeftLabel,
        string LeftSource,
        string RightLabel,
        string RightSource,
        bool HasConflict);

    private sealed record DbcSourceReference(int LineNumber, string Text);

    private sealed record AggregateOverlapRecommendation(
        DbcSignalRow Aggregate,
        IReadOnlyList<DbcSignalRow> DetailSignals);

    private sealed record DlcMismatchDetails(
        uint FrameId,
        string CountText,
        int ActualPayloadLength,
        IReadOnlyList<int> ExpectedPayloadLengths);

    private sealed record DbcIssueTarget(
        uint? FrameId,
        bool? IsExtended,
        string? FrameName,
        string? SignalName);
}
