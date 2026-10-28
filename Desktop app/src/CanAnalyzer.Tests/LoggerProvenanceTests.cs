using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Decoding;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Parsing;
using CanAnalyzer.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class LoggerProvenanceTests
{
    [Fact]
    public void DiagnosticOnlyRunCanBeCancelledWithoutYieldingMachineFrames()
    {
        using var cancellation = new CancellationTokenSource();
        var frames = new[] { new RawCanFrame(0L, 5, 8, new byte[8], "Rx", "9", false) };
        var machine = new MachineFrameView(frames, ["9"], cancellation.Token);
        Assert.Empty(machine);
        cancellation.Cancel();
        using var enumerator = machine.GetEnumerator();
        Assert.Throws<OperationCanceledException>(() => enumerator.MoveNext());
    }

    [Fact]
    public void EmbeddedInstallationIdentitySurvivesWrongClockAndDoesNotCertifyUtc()
    {
        var path = HeaderFile("M03;I0001;C0002");
        try
        {
            var source = Mdf4MetadataReader.Read(path, new("log.MF4", path, null, null));
            Assert.Equal("48EDFD35", source.Logger);
            Assert.Equal("00000044", source.Session);
            Assert.Equal("M03", source.MachineId);
            Assert.Equal("I0001", source.InstallationId);
            Assert.Equal("C0002", source.ConfigurationId);
            Assert.Equal("embedded-installation", source.MachineAssignmentQuality);
            Assert.Equal(DateTimeOffset.UnixEpoch, source.LoggerStartTimeUtc);
            Assert.Equal("unverified", source.ClockQuality);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void HeaderDoesNotGuessInstallationAndRejectsWrongLogger()
    {
        var path = HeaderFile("site; ordinary comment");
        try
        {
            var source = Mdf4MetadataReader.Read(path, new("log.MF4", path, null, null)
            { MachineId = "M03", MachineAssignmentQuality = "unverified" });
            Assert.Null(source.MachineId);
            Assert.Equal("unknown", source.MachineAssignmentQuality);
            Assert.Throws<InvalidDataException>(() => Mdf4MetadataReader.Read(path,
                new("log.MF4", path, "22484AAA", null)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void InternalTimeAndStorageAreEvidenceNotLiveStatus()
    {
        byte[] external = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(external, 10);
        BinaryPrimitives.WriteUInt32LittleEndian(external.AsSpan(4), 86410);
        RawCanFrame[] frames = [new(0L, 5, 8, external, "Rx", "1", false),
            new(1_000_000_000L, 5, 8, external, "Rx", "9", false),
            new(2_000_000_000L, 2, 7, [0x04, 10, 0, 0, 0, 100, 0], "Rx", "9", false),
            new(3_000_000_000L, 3, 1, [0], "Rx", "9", false)];
        var diagnostics = LoggerDiagnosticReader.Read(frames, ["9"]);
        Assert.Equal(3, diagnostics.InternalFrameCount);
        var observation = Assert.Single(diagnostics.ClockObservations);
        Assert.Equal(TimeSpan.FromDays(1), observation.ExternalTimeUtc - observation.InternalTimeUtc);
        Assert.Equal(100, diagnostics.LastStorageObservation!.FreeSpaceMegabytes);
        Assert.True(diagnostics.LastStorageObservation.Can1ReceiveEnabled);
        Assert.False(diagnostics.LastStorageObservation.Can2ReceiveEnabled);
        Assert.Equal(1, diagnostics.InvalidInternalMessageCount);
        Assert.Empty(LoggerDiagnosticReader.Read(frames, []).ClockObservations);
    }

    [Fact]
    public void VerifiedHistoricalInstallationIsRetainedButConflictingHeaderIsRejected()
    {
        var path = HeaderFile("");
        var conflict = HeaderFile("M03;I0002;C0001");
        var source = new SourceLogFile("log.MF4",path,"48EDFD35","00000044")
        {
            MachineId="M01",InstallationId="I0001",ConfigurationId="C0001",
            MachineAssignmentQuality="verified-installation",MachineAssignmentEvidence="checked physical handover and content hash"
        };
        try
        {
            var result = Mdf4MetadataReader.Read(path,source);
            Assert.Equal("M01",result.MachineId); Assert.Equal("I0001",result.InstallationId);
            Assert.Equal("verified-installation",result.MachineAssignmentQuality);
            Assert.Throws<InvalidDataException>(() => Mdf4MetadataReader.Read(conflict,source));
            Assert.Null(Mdf4MetadataReader.Read(path,source with { MachineAssignmentEvidence=null }).MachineId);
        }
        finally { File.Delete(path); File.Delete(conflict); }
    }

    [Fact]
    public void CorruptHeaderLinkCountDoesNotBecomePlausibleIdentity()
    {
        var path = HeaderFile("M03;I0001;C0001");
        try
        {
            var bytes=File.ReadAllBytes(path);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(80),7);
            File.WriteAllBytes(path,bytes);
            Assert.Throws<InvalidDataException>(() => Mdf4MetadataReader.Read(path,new("log.MF4",path,null,null)));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PipelineFiltersOnlyProvenDiagnosticsAndKeepsOriginalFrameIndices(bool provenInternalChannel)
    {
        var log = Path.GetTempFileName();
        var dbc = Path.GetTempFileName();
        try
        {
            var message = new DbcMessage { RawFrameId = 5, Name = "Machine", Dlc = 8, IsExtendedFrame = false };
            message.Signals.Add(new DbcSignal { Name = "Value", StartBit = 0, Length = 8, IsLittleEndian = true,
                IsSigned = false, Scale = 1, Offset = 0, Minimum = 0, Maximum = 255, Unit = "" });
            await new DbcWriter().WriteAsync(new DbcDatabase { Messages = [message] }, dbc, default);
            var frames = new[] {
                new RawCanFrame(0L, 5, 8, [7,0,0,0,0,0,0,0], "Rx", "1", false, 0),
                new RawCanFrame(1L, 5, 8, [8,0,0,0,0,0,0,0], "Rx", "9", false, 1),
                new RawCanFrame(2L, 5, 8, [9,0,0,0,0,0,0,0], "Rx", "1", false, 2) };
            var parsed = new CanLogParseResult(frames, new("trace",3,0,3,0,[])
            { InternalDiagnosticChannels = provenInternalChannel ? ["9"] : [] }, DatasetCompleteness.Complete);
            var pipeline = new CanAnalysisPipeline(new FixedParser(parsed), new DbcLoader(), new CanDecodingService(),
                new DatasetBuilder(), NullLogger<CanAnalysisPipeline>.Instance);
            using var dataset = await pipeline.LoadAsync(log, dbc, ImportMode.Strict, null, default);
            Assert.Equal(3, dataset.RawCount);
            Assert.Equal(provenInternalChannel ? new long[] { 0, 2 } : new long[] { 0, 1, 2 },
                dataset.DecodedSamples.Select(sample => sample.FrameIndex));
            Assert.Equal(provenInternalChannel ? new[] { 7d, 9d } : new[] { 7d, 8d, 9d },
                dataset.DecodedSamples.Select(sample => sample.Value));
            if (provenInternalChannel)
            {
                Assert.All(dataset.DecodedSamples, sample => Assert.Equal("1", sample.Channel));
                Assert.Single(dataset.LoggerDiagnostics.ClockObservations);
            }
            else
            {
                Assert.Contains(dataset.DecodedSamples, sample => sample.Channel == "9");
                Assert.Empty(dataset.LoggerDiagnostics.ClockObservations);
            }
        }
        finally { File.Delete(log); File.Delete(dbc); }
    }

    [Fact]
    public void InventoryMatchesContentOnceAndNeverInventsCurrentQueue()
    {
        var hash = new string('A',64);
        LogInventoryEntry[] inventory = [new("48EDFD35","00000044","00000001.MF4",100,hash),
            new("48EDFD35","00000044","00000002.MF4",200,new string('B',64))];
        var receipt = DateTimeOffset.Parse("2026-10-20T12:00:00Z");
        ReceivedLogEvidence[] received = [new("48EDFD35","00000044","00000001-12345678.MF4",100,hash,receipt),
            new("48EDFD35","00000044","00000001-87654321.MF4",100,hash,receipt.AddHours(1)),
            new("48EDFD35","00000044","00000002.MF4",200,null,receipt)];
        var status = LogInventoryReconciler.Reconcile(receipt.AddDays(-1), inventory, received);
        Assert.Equal(1,status.ConfirmedReceivedFiles); Assert.Equal(100,status.ConfirmedReceivedBytes);
        Assert.Single(status.UnconfirmedFiles); Assert.Null(status.CurrentQueueBytes);
        Assert.False(status.InventoryFullyReceived); Assert.Equal(receipt.AddHours(1),status.LastServerReceiptUtc);
        var all = LogInventoryReconciler.Reconcile(receipt, inventory,
            received.Append(new("48EDFD35","00000044","00000002.MF4",200,new string('B',64),receipt)));
        Assert.True(all.InventoryFullyReceived); Assert.Null(all.CurrentQueueBytes);
    }

    [Fact]
    public void ExportRetainsPerChannelGapsAndExcludesOnlyAffectedCoverage()
    {
        var a = new SignalIdentity("1",CanFrameFormat.Classic,false,1,"M","A");
        var b = a with { Channel = "2", SignalName = "B" };
        var samples = new[] { new DecodedSignalSample(0,0,0,a,10,0,""),new DecodedSignalSample(0,1,0,b,10,0,""),
            new DecodedSignalSample(2_000_000_000L,2,0,a,10,0,""),new DecodedSignalSample(2_000_000_000L,3,0,b,10,0,"") };
        using var dataset = new DatasetBuilder().Build([],samples,[],new(0,0,0,0,0,""),
            new("test",0,0,0,0,[]) { GapsByChannel = new Dictionary<string,IReadOnlyList<MeasurementGap>> { ["1"]=[new(.5,1.5)],["2"]=[] } });
        var path = Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".zip");
        try
        {
            AnalysisBundleExporter.Export(path,dataset,new("","",""),JsonSerializer.SerializeToElement(new {}),[]);
            using var zip = ZipFile.OpenRead(path);
            using var reader = new StreamReader(zip.GetEntry("signal_statistics.csv")!.Open());
            reader.ReadLine();
            var first = reader.ReadLine()!.Trim('"').Split("\",\"");
            var second = reader.ReadLine()!.Trim('"').Split("\",\"");
            Assert.Equal("0",first[18]); Assert.Equal("2",second[18]);
            Assert.NotNull(zip.GetEntry("channel_gaps.json")); Assert.NotNull(zip.GetEntry("sources.json"));
            using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
            Assert.Equal("unverified",manifest.RootElement.GetProperty("ClockQuality").GetString());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExportSortsLegacyAndChannelGapsBeforeCalculatingCoverage(bool perChannel)
    {
        var identity = new SignalIdentity("1", CanFrameFormat.Classic, false, 1, "Motor", "RPM");
        var samples = new long[] { 0, 3_000_000_000, 4_000_000_000, 10_000_000_000 }
            .Select((timestamp, index) => new DecodedSignalSample(timestamp, index, index, identity, 10, 0, "rpm")).ToArray();
        MeasurementGap[] unordered = [new(8, 9), new(1, 2)];
        var report = new ImportReport("test", 0, 0, 0, 0, [])
        {
            Gaps = perChannel ? [] : unordered,
            GapsByChannel = perChannel
                ? new Dictionary<string, IReadOnlyList<MeasurementGap>> { ["1"] = unordered }
                : new Dictionary<string, IReadOnlyList<MeasurementGap>>()
        };
        using var dataset = new DatasetBuilder().Build([], samples, [], new(0, 0, 0, 0, 0, ""), report);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip");
        try
        {
            AnalysisBundleExporter.Export(path, dataset, new("", "", "", MaximumGapSeconds: 10),
                JsonSerializer.SerializeToElement(new { }), []);
            using var zip = ZipFile.OpenRead(path);
            using var reader = new StreamReader(zip.GetEntry("signal_statistics.csv")!.Open());
            reader.ReadLine();
            var row = reader.ReadLine()!.Trim('"').Split("\",\"");
            Assert.Equal("1", row[18]); // Only 3..4 seconds has observed coverage.
            Assert.Equal("10", row[19]);
        }
        finally { File.Delete(path); }
    }

    private static string HeaderFile(string metadata)
    {
        var xml = Encoding.UTF8.GetBytes($"<HDcomment><TX>M01;I0001;C0001</TX><common_properties><tree name=\"Device Information\"><e name=\"serial number\">48EDFD35</e></tree><tree name=\"File Information\"><e name=\"session\">44</e><e name=\"comment\">{metadata}</e></tree></common_properties></HDcomment>\0");
        byte[] bytes = new byte[200+24+xml.Length];
        "UnFinMF "u8.CopyTo(bytes); "CE"u8.CopyTo(bytes.AsSpan(16)); "##HD"u8.CopyTo(bytes.AsSpan(64));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(72),104);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(80),6);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(128),200);
        "##MD"u8.CopyTo(bytes.AsSpan(200)); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(208),(ulong)(24+xml.Length));
        xml.CopyTo(bytes,224);
        var path = Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".MF4"); File.WriteAllBytes(path,bytes); return path;
    }

    private sealed class FixedParser(CanLogParseResult result) : ICanLogParsingService
    {
        public Task<CanLogParseResult> ParseAsync(string filePath,ImportMode mode,IProgress<LoadProgress>? progress,CancellationToken cancellationToken) => Task.FromResult(result);
    }
}
