using System.Diagnostics;

namespace CanAnalyzer.App.Infrastructure;

public enum UiActivity
{
    None,
    PlotRender,
    ViewportSampling,
    PlotModelPublish
}

public readonly record struct UiActivitySnapshot(string Name, TimeSpan Elapsed);

/// <summary>
/// Privacy-minimal UI breadcrumbs. Begin/Dispose must be nested on the UI thread; Capture may run
/// on any thread. Scopes allocate no objects and the short lock only protects scalar state.
/// </summary>
public static class UiActivityDiagnostics
{
    private static readonly object Gate = new();
    private static ActivityState _current;
    private static long _version;

    internal readonly record struct ActivityState(UiActivity Activity, long Started, long Version);

    public static Scope Begin(string activity) => Begin(activity switch
    {
        "plot_render" => UiActivity.PlotRender,
        "viewport_sampling" => UiActivity.ViewportSampling,
        "plot_model_publish" => UiActivity.PlotModelPublish,
        _ => throw new ArgumentOutOfRangeException(nameof(activity))
    });

    public static Scope Begin(UiActivity activity)
    {
        if (activity is not (UiActivity.PlotRender or UiActivity.ViewportSampling or UiActivity.PlotModelPublish))
            throw new ArgumentOutOfRangeException(nameof(activity));
        lock (Gate)
        {
            var previous = _current;
            _current = new(activity, Stopwatch.GetTimestamp(), ++_version);
            return new Scope(previous, _current.Version);
        }
    }

    public static UiActivitySnapshot? Capture()
    {
        lock (Gate)
        {
            var name = _current.Activity switch
            {
                UiActivity.PlotRender => "plot_render",
                UiActivity.ViewportSampling => "viewport_sampling",
                UiActivity.PlotModelPublish => "plot_model_publish",
                _ => null
            };
            return name is null ? null : new(name, Stopwatch.GetElapsedTime(_current.Started));
        }
    }

    public readonly struct Scope : IDisposable
    {
        private readonly ActivityState _previous;
        private readonly long _version;

        internal Scope(ActivityState previous, long version)
        {
            _previous = previous;
            _version = version;
        }

        public void Dispose()
        {
            lock (Gate)
            {
                // A copied/disposed scope must not erase a newer activity.
                if (_version != 0 && _current.Version == _version) _current = _previous;
            }
        }
    }
}
