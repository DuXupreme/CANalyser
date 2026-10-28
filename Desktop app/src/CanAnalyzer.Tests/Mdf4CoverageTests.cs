using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Parsing;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class Mdf4CoverageTests
{
    [Fact]
    public async Task InternalDiagnosticsDoNotFillMachineSilenceAndRemainRaw()
    {
        var lines = new List<string> { Row(0, "1"), Row(200, "1") };
        for (var i = 1; i <= 9; i++) lines.Add(Row(i * 1000, "9"));
        lines.Add(Row(10000, "1"));
        lines.Add(Row(12000, "9"));
        var result = await ImportAsync(new Trace(0, lines));
        try
        {
            Assert.Equal(lines.Count, result.Frames.Count);
            Assert.Equal(12_000_000_000L, result.Frames[^1].TimestampNanoseconds);
            Assert.Equal([new MeasurementGap(.2, 10), new MeasurementGap(10, 12)], result.Report.Gaps);
            Assert.Equal(result.Report.Gaps, result.Report.GetGapsForChannel("1"));
            Assert.Equal(["9"], result.Report.InternalDiagnosticChannels);
            Assert.True(result.Report.IsConsistent);
        }
        finally { (result.Frames as IDisposable)?.Dispose(); }
    }

    [Fact]
    public async Task OtherPhysicalBusCannotFillSelectedBusGap()
    {
        var lines = new List<string> { Row(0, "1"), Row(0, "2") };
        for (var i = 1; i < 10; i++) lines.Add(Row(i * 1000, "2"));
        lines.Add(Row(10000, "1"));
        lines.Add(Row(10000, "2"));
        var result = await ImportAsync(new Trace(0, lines));
        try
        {
            Assert.Empty(result.Report.Gaps);
            Assert.Equal([new MeasurementGap(0, 10)], result.Report.GetGapsForChannel("1"));
            Assert.Empty(result.Report.GetGapsForChannel("2"));
        }
        finally { (result.Frames as IDisposable)?.Dispose(); }
    }

    [Fact]
    public async Task DiagnosticOnlyFileDoesNotBridgeSeparateMachineRecordings()
    {
        var result = await ImportAsync(new Trace(0, [Row(0, "1"), Row(500, "1")]),
            new Trace(4, [Row(0, "9"), Row(1000, "9")]),
            new Trace(10, [Row(0, "1"), Row(500, "1")]));
        try
        {
            Assert.Equal([new MeasurementGap(.5, 10)], result.Report.Gaps);
            Assert.Equal(10_500_000_000L, result.Frames[^1].TimestampNanoseconds);
        }
        finally { (result.Frames as IDisposable)?.Dispose(); }
    }

    [Fact]
    public async Task ContinuousTrafficAcross120SecondSplitCreatesNoGap()
    {
        var result = await ImportAsync(new Trace(0, [Row(119500, "1")]),
            new Trace(120, [Row(0, "1"), Row(500, "1")]));
        try
        {
            Assert.Empty(result.Report.Gaps);
            Assert.Empty(result.Report.GetGapsForChannel("1"));
            Assert.Equal([119_500_000_000L, 120_000_000_000L, 120_500_000_000L],
                result.Frames.Select(frame => frame.TimestampNanoseconds));
        }
        finally { (result.Frames as IDisposable)?.Dispose(); }
    }

    [Theory]
    [InlineData(ImportMode.Strict)]
    [InlineData(ImportMode.Partial)]
    public async Task BackwardClockWithinFileCannotBeHiddenByPartialImport(ImportMode mode)
    {
        var path = Path.Combine(Path.GetTempPath(), $"peak-clock-{Guid.NewGuid():N}.trc");
        await File.WriteAllTextAsync(path, Header(0) + Row(2000, "1") + "\n" + Row(1000, "1"));
        try
        {
            var error = await Assert.ThrowsAsync<ImportIntegrityException>(() =>
                new PeakTrcParser().ParseAsync(path, mode, null, CancellationToken.None));
            Assert.Contains(error.Report.Issues, issue => issue.Code == "CLOCK_REVERSAL");
            Assert.True(error.Report.IsConsistent);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task BackwardClockAcrossSequenceCannotBeHiddenBySorting()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync(
            new Trace(20, [Row(0, "1")]), new Trace(10, [Row(0, "1")])));
    }

    [Fact]
    public async Task BackwardClockAcrossIncreasingHexSessionsCannotBeHiddenBySorting()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync(
            new Trace(20, [Row(0, "1")], Session: 10), new Trace(10, [Row(0, "1")], Session: 11)));
        Assert.Contains("sessienummers", error.Message);
    }

    [Theory]
    [InlineData("M03;I0002;C0001")]
    [InlineData("M02;I0001;C0001")]
    public async Task ProvenDifferentInstallationsCannotBecomeOneMachine(string secondIdentity)
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync(
            new Trace(0, [Row(0, "1")], Metadata: "M03;I0001;C0001"),
            new Trace(10, [Row(0, "1")], Metadata: secondIdentity)));
        Assert.Contains("machine-installaties", error.Message);
    }

    [Fact]
    public async Task ConfigurationChangeWithinSameInstallationCanBeCombined()
    {
        var result = await ImportAsync(new Trace(0, [Row(0, "1")], Metadata: "M03;I0001;C0001"),
            new Trace(10, [Row(0, "1")], Metadata: "M03;I0001;C0002"));
        try
        {
            Assert.Equal(2, result.Frames.Count);
            Assert.All(result.SourceFiles, source => Assert.Equal("embedded-installation", source.MachineAssignmentQuality));
            Assert.Equal(["C0001", "C0002"], result.SourceFiles.Select(source => source.ConfigurationId));
        }
        finally { (result.Frames as IDisposable)?.Dispose(); }
    }

    [Fact]
    public async Task Peak21PreservesExtendedLowIdBusAndFdFlags()
    {
        var path = Path.Combine(Path.GetTempPath(), $"peak-flags-{Guid.NewGuid():N}.trc");
        await File.WriteAllTextAsync(path, Header(0) +
            "1 0.000 DT 1 00000007 Rx 1 AB\n" +
            "2 0.500 DT 2 0007 Rx 1 CD\n" +
            "3 1.000 BI 9 0020 Tx 9 00 01 02 03 04 05 06 07 08 09 0A 0B\n" +
            "4 2.000 RR 2 0123 Rx 8\n");
        var result = await new PeakTrcParser().ParseAsync(path, ImportMode.Strict, null, CancellationToken.None);
        try
        {
            Assert.NotNull(result);
            Assert.Equal(4, result!.Frames.Count);
            Assert.True(result.Frames[0].IsExtended);
            Assert.False(result.Frames[1].IsExtended);
            var fd = result.Frames[2];
            Assert.Equal("9", fd.Channel);
            Assert.Equal(12, fd.PayloadLength);
            Assert.Equal(9, fd.Dlc);
            Assert.True(fd.BitRateSwitch);
            Assert.True(fd.ErrorStateIndicator);
            Assert.Equal(CanFrameDirection.Transmit, fd.Direction);
            Assert.Equal(CanFrameKind.Remote, result.Frames[3].Kind);
            Assert.Empty(result.Report.InternalDiagnosticChannels); // Generic bus 9 is not CANedge proof.
            Assert.True(result.Report.IsConsistent);
        }
        finally { (result?.Frames as IDisposable)?.Dispose(); File.Delete(path); }
    }

    [Fact]
    public async Task Peak21PayloadLengthColumnIsNotMistakenForDlc()
    {
        var path = Path.Combine(Path.GetTempPath(), $"peak-length-{Guid.NewGuid():N}.trc");
        await File.WriteAllTextAsync(path, ";$FILEVERSION=2.1\n;$COLUMNS=O,T,B,I,d,l,D\n" +
            "0.000 FD 2 0123 Rx 12 00 01 02 03 04 05 06 07 08 09 0A 0B\n");
        var result = await new PeakTrcParser().ParseAsync(path, ImportMode.Strict, null, CancellationToken.None);
        try { Assert.Equal(9, Assert.Single(result!.Frames).Dlc); }
        finally { (result?.Frames as IDisposable)?.Dispose(); File.Delete(path); }
    }

    [Fact]
    public async Task Peak21DlcColumnCannotSilentlyAcceptAByteLength()
    {
        var path = Path.Combine(Path.GetTempPath(), $"peak-invalid-dlc-{Guid.NewGuid():N}.trc");
        await File.WriteAllTextAsync(path, Header(0) + Row(0, "1") + "\n" +
            "2 1.000 FD 2 0123 Rx 12 00 01 02 03 04 05 06 07 08 09 0A 0B\n");
        var result = await new PeakTrcParser().ParseAsync(path, ImportMode.Partial, null, CancellationToken.None);
        try
        {
            Assert.Single(result!.Frames);
            Assert.Equal(1, result.Report.RejectedLines);
            Assert.Equal(DatasetCompleteness.Partial, result.Completeness);
        }
        finally { (result?.Frames as IDisposable)?.Dispose(); File.Delete(path); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ArchiveManifestBindsReceiptMetadataToExactInputBytes(bool tampered, bool incorrectVerifiedTime)
    {
        var path = Path.Combine(Path.GetTempPath(), $"manifest-import-{Guid.NewGuid():N}.zip");
        var payload = "fake converter input"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(payload));
        var uploaded = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
        const string name = "48EDFD35/00000001/00000001.MF4";
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                await using (var data = archive.CreateEntry(name).Open()) await data.WriteAsync(payload);
                await using var metadata = archive.CreateEntry(OnlineLogArchiveManifestCodec.EntryName).Open();
                await OnlineLogArchiveManifestCodec.WriteAsync(metadata, new OnlineLogArchiveManifest
                {
                    MetadataRevision = "receipt-2",
                    Files = [new OnlineLogArchiveFile
                    {
                        ArchiveName = name, ObjectKey = "LOG/" + name, LoggerId = "48EDFD35", Session = "00000001",
                        SizeBytes = payload.Length, Sha256 = tampered ? new string('0', 64) : hash,
                        UploadedAtUtc = uploaded, RawRecordedAtUtc = uploaded.AddDays(-10),
                        MachineName = "Unverified previous owner",
                        ClockQuality = incorrectVerifiedTime ? "verified" : "unverified",
                        ClockEvidence = incorrectVerifiedTime ? "An external time check attached to a different date" : null
                    }]
                });
            }
            var parser = new Mdf4Parser(new TraceConverter([new Trace(0, [Row(0, "1")])]), new PeakTrcParser());
            if (tampered || incorrectVerifiedTime)
            {
                var error = await Assert.ThrowsAsync<InvalidDataException>(() => parser.ParseAsync(path, ImportMode.Strict, null, default));
                Assert.Contains(tampered ? "SHA-256" : "geverifieerde tijd", error.Message);
                return;
            }
            var result = await parser.ParseAsync(path, ImportMode.Strict, null, default);
            try
            {
                var source = Assert.Single(result!.SourceFiles);
                Assert.Equal(uploaded, source.UploadedAtUtc);
                Assert.Equal(hash, source.Sha256);
                Assert.Equal("receipt-2", source.MetadataRevision);
                Assert.Equal("LOG/" + name, source.ObjectKey);
                Assert.Equal("unverified", source.ClockQuality);
                Assert.Null(source.MachineId);
                Assert.Equal("unknown", source.MachineAssignmentQuality);
                Assert.Equal(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero), source.RecordedAt);
            }
            finally { (result?.Frames as IDisposable)?.Dispose(); }
        }
        finally { File.Delete(path); }
    }

    private static string Row(double milliseconds, string channel) =>
        $"1 {milliseconds.ToString("F3", CultureInfo.InvariantCulture)} DT {channel} 0200 Rx 1 AA";

    private static string Header(int seconds) => ";$FILEVERSION=2.1\n;$STARTTIME=" +
        new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc).AddSeconds(seconds).ToOADate().ToString("R", CultureInfo.InvariantCulture) +
        "\n;$COLUMNS=N,O,T,B,I,d,L,D\n";

    private static async Task<CanLogParseResult> ImportAsync(params Trace[] traces)
    {
        var path = Path.Combine(Path.GetTempPath(), $"coverage-{Guid.NewGuid():N}.zip");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                for (var i = 0; i < traces.Length; i++)
                {
                    await using var stream = archive.CreateEntry($"48EDFD35/{traces[i].Session:X8}/{i + 1:00000000}.MF4").Open();
                    await stream.WriteAsync(traces[i].Metadata is { } metadata
                        ? HeaderBytes(metadata, traces[i].Session) : "fake converter input"u8.ToArray());
                }
            return (await new Mdf4Parser(new TraceConverter(traces), new PeakTrcParser())
                .ParseAsync(path, ImportMode.Strict, null, CancellationToken.None))!;
        }
        finally { File.Delete(path); }
    }

    private static byte[] HeaderBytes(string metadata, int session)
    {
        var xml = Encoding.UTF8.GetBytes($"<HDcomment><common_properties><tree name=\"Device Information\"><e name=\"serial number\">48EDFD35</e></tree><tree name=\"File Information\"><e name=\"session\">{session:X}</e><e name=\"comment\">{metadata}</e></tree></common_properties></HDcomment>\0");
        var bytes = new byte[224 + xml.Length];
        "UnFinMF "u8.CopyTo(bytes); "CE"u8.CopyTo(bytes.AsSpan(16)); "##HD"u8.CopyTo(bytes.AsSpan(64));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(72), 104);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(80), 6);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(128), 200);
        "##MD"u8.CopyTo(bytes.AsSpan(200));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(208), (ulong)(24 + xml.Length));
        xml.CopyTo(bytes, 224);
        return bytes;
    }

    private sealed record Trace(int Seconds, IReadOnlyList<string> Lines, int Session = 1, string? Metadata = null);
    private sealed class TraceConverter(Trace[] traces) : IMdf4ConversionService
    {
        public async Task<IReadOnlyList<string>> ConvertToPeakTrcAsync(IReadOnlyList<string> inputPaths,
            string outputDirectory, IProgress<LoadProgress>? progress, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(outputDirectory);
            var outputs = new List<string>();
            for (var i = 0; i < inputPaths.Count; i++)
            {
                var path = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(inputPaths[i]) + "_CAN.trc");
                await File.WriteAllTextAsync(path, Header(traces[i].Seconds) + string.Join('\n', traces[i].Lines), cancellationToken);
                outputs.Add(path);
            }
            return outputs;
        }
    }
}
