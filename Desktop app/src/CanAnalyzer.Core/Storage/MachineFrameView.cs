using System.Collections;
using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Storage;

/// <summary>Excludes proven logger diagnostics from machine decoding while retaining original frame indices.</summary>
internal sealed class MachineFrameView : IReadOnlyList<RawCanFrame>
{
    private readonly IReadOnlyList<RawCanFrame> _frames;
    private readonly HashSet<string> _internalChannels;
    private readonly CancellationToken _cancellationToken;
    public MachineFrameView(IReadOnlyList<RawCanFrame> frames, IReadOnlyList<string> internalChannels, CancellationToken token)
    {
        _frames = frames;
        _internalChannels = internalChannels.ToHashSet(StringComparer.Ordinal);
        _cancellationToken = token;
        var count = 0;
        foreach (var frame in frames)
        {
            token.ThrowIfCancellationRequested();
            if (!_internalChannels.Contains(frame.Channel)) count++;
        }
        Count = count;
    }
    public int Count { get; }
    public RawCanFrame this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            foreach (var frame in this) if (index-- == 0) return frame;
            throw new InvalidOperationException("De bronlijst is tijdens het lezen gewijzigd.");
        }
    }
    public IEnumerator<RawCanFrame> GetEnumerator()
    {
        // Enumerate the disk-backed source sequentially rather than seek for every frame.
        foreach (var frame in _frames)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!_internalChannels.Contains(frame.Channel)) yield return frame;
        }
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
