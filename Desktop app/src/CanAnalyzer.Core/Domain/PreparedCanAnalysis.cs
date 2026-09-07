using System.Security.Cryptography;

namespace CanAnalyzer.Core.Domain;

/// <summary>Owns a processed dataset until its integrity report has been explicitly accepted.</summary>
public sealed class PreparedCanAnalysis(CanDataset dataset) : IDisposable
{
    private CanDataset? _dataset = dataset;
    public ImportReport? Report { get; } = dataset.ImportReport;
    public bool RequiresPartialConfirmation => Report?.HasErrors == true;

    /// <summary>Edits made during review must trigger fresh processing, including edits at the same path.</summary>
    public async Task<bool> MatchesSourcesAsync(string logPath, string dbcPath, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_dataset is null, this);
        return await MatchesHashAsync(dbcPath, _dataset.DbcSha256, cancellationToken).ConfigureAwait(false) &&
               await MatchesHashAsync(logPath, _dataset.SourceLogSha256, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> MatchesHashAsync(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        return string.Equals(actual, expected, StringComparison.Ordinal);
    }

    public CanDataset Accept(bool allowPartial = false)
    {
        ObjectDisposedException.ThrowIf(_dataset is null, this);
        if (RequiresPartialConfirmation && !allowPartial)
            throw new InvalidOperationException("PARTIAL-import vereist expliciete bevestiging van de integriteitsfouten.");
        var accepted = _dataset;
        _dataset = null;
        return accepted;
    }

    public void Dispose()
    {
        _dataset?.Dispose();
        _dataset = null;
    }
}

public sealed record LoadTimings(long ParseMilliseconds, long DbcMilliseconds, long DecodeMilliseconds,
    long HashMilliseconds, long DatasetMilliseconds);
