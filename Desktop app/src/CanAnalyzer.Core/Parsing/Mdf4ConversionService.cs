using System.Diagnostics;
using System.Security.Cryptography;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace CanAnalyzer.Core.Parsing;

/// <summary>Runs the pinned CSS Electronics MDF4 converter shipped with CANalyser.</summary>
public sealed class Mdf4ConversionService : IMdf4ConversionService
{
    internal const string ExpectedSha256 = "30B7524CC5CEAF7B46E64BB2F4E3AF90262D2DB0607122B08B83606C1CA8AE9C";
    internal const string EmbeddedResourceName = "CanAnalyzer.Core.Tools.Mdf4.mdf2peak.exe";
    private readonly string _converterPath;
    private readonly bool _allowEmbeddedFallback;
    private readonly ILogger<Mdf4ConversionService> _logger;

    public Mdf4ConversionService(ILogger<Mdf4ConversionService> logger)
        : this(Path.Combine(AppContext.BaseDirectory, "Tools", "Mdf4", "mdf2peak.exe"), logger)
    {
        _allowEmbeddedFallback = true;
    }

    internal Mdf4ConversionService(string converterPath, ILogger<Mdf4ConversionService> logger)
    {
        _converterPath = converterPath;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> ConvertToPeakTrcAsync(
        IReadOnlyList<string> inputPaths,
        string outputDirectory,
        IProgress<LoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        if (inputPaths.Count == 0) throw new ArgumentException("Er zijn geen MF4-bestanden geselecteerd.", nameof(inputPaths));
        var converterPath = await ResolveConverterPathAsync(cancellationToken).ConfigureAwait(false);

        await VerifyConverterAsync(converterPath, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(outputDirectory);
        progress?.Report(new LoadProgress($"MF4 converteren ({inputPaths.Count:N0} bestand(en))...", 3));

        var startInfo = new ProcessStartInfo
        {
            FileName = converterPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(converterPath) ?? AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add("--non-interactive");
        startInfo.ArgumentList.Add("--verbosity=1");
        startInfo.ArgumentList.Add("--trace-format=version1");
        startInfo.ArgumentList.Add("--output-directory");
        startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add("--input-files");
        foreach (var inputPath in inputPaths)
        {
            if (!File.Exists(inputPath)) throw new FileNotFoundException("Een MF4-logbestand ontbreekt.", inputPath);
            startInfo.ArgumentList.Add(Path.GetFullPath(inputPath));
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("De MF4-converter kon niet worden gestart.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            _logger.LogError("MDF4 converter failed with exit code {ExitCode}. Stdout: {Stdout}. Stderr: {Stderr}",
                process.ExitCode, stdout, stderr);
            throw new InvalidDataException($"MF4-conversie is mislukt (code {process.ExitCode}). {stderr.Trim()}");
        }

        var outputs = Directory.EnumerateFiles(outputDirectory, "*.trc", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (outputs.Length != inputPaths.Count)
            throw new InvalidDataException($"MF4-conversie leverde {outputs.Length:N0} TRC-bestand(en) op voor {inputPaths.Count:N0} invoerbestand(en).");

        _logger.LogInformation("Converted {InputCount} MDF4 file(s) to {OutputCount} PEAK TRC file(s)", inputPaths.Count, outputs.Length);
        progress?.Report(new LoadProgress("MF4-conversie gereed; tijdlijn samenvoegen...", 6));
        return outputs;
    }

    private async Task<string> ResolveConverterPathAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_converterPath))
        {
            return _converterPath;
        }

        if (!_allowEmbeddedFallback)
        {
            throw new FileNotFoundException("De ingebouwde CANedge MF4-converter ontbreekt. Installeer CANalyser opnieuw.", _converterPath);
        }

        var converterDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CANalyser", "tools", "mdf4", ExpectedSha256[..12]);
        var extractedPath = Path.Combine(converterDirectory, "mdf2peak.exe");
        if (File.Exists(extractedPath))
        {
            try
            {
                await VerifyConverterAsync(extractedPath, cancellationToken).ConfigureAwait(false);
                return extractedPath;
            }
            catch (InvalidDataException)
            {
                try { File.Delete(extractedPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        await using var embedded = typeof(Mdf4ConversionService).Assembly.GetManifestResourceStream(EmbeddedResourceName)
                                   ?? throw new FileNotFoundException("De ingebouwde CANedge MF4-converter ontbreekt in dit programmabestand.");
        Directory.CreateDirectory(converterDirectory);
        var temporaryPath = extractedPath + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var target = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await embedded.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, extractedPath, overwrite: true);
            return extractedPath;
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task VerifyConverterAsync(string converterPath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(converterPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(actual, ExpectedSha256, StringComparison.Ordinal))
            throw new InvalidDataException("De ingebouwde MF4-converter heeft een onverwachte controlehash. Conversie is uit veiligheid geblokkeerd.");
    }
}
