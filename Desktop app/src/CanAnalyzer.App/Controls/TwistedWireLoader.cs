using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CanAnalyzer.App.Controls;

public sealed class TwistedWireLoader : FrameworkElement
{
    private const int SegmentCount = 64;
    private const int DepthStepCount = 12;
    private const int AnimationFrameCount = 36;
    private const int AnimationFrameRate = 30;
    private const double Turns = 2.15;

    private static readonly Color DefaultBlue = Color.FromRgb(0x1F, 0x5F, 0xA8);
    private static readonly Color DefaultOrange = Color.FromRgb(0xEC, 0x7A, 0x2D);
    private static readonly Brush DefaultBlueBrush = CreateFrozenBrush(DefaultBlue);
    private static readonly Brush DefaultOrangeBrush = CreateFrozenBrush(DefaultOrange);
    private static readonly Brush ShadowBrush = CreateFrozenBrush(Color.FromArgb(0x24, 0x16, 0x26, 0x3B));

    private readonly List<WireSegment> _segments = new(SegmentCount * 2);
    private readonly List<WireEndpoint> _endpoints = new(4);
    private readonly List<WireSegment>[] _segmentBuckets = Enumerable
        .Range(0, DepthStepCount * 2)
        .Select(static _ => new List<WireSegment>())
        .ToArray();
    private Pen[] _bluePens = [];
    private Pen[] _orangePens = [];
    private Pen? _shadowPen;
    private Color _cachedBlueColor;
    private Color _cachedOrangeColor;
    private double _cachedStroke = -1;
    private DrawingGroup[] _animationFrames = [];
    private double _cachedFrameWidth = -1;
    private double _cachedFrameHeight = -1;
    private double _cachedFrameStroke = -1;
    private Color _cachedFrameBlueColor;
    private Color _cachedFrameOrangeColor;

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(
            nameof(IsActive),
            typeof(bool),
            typeof(TwistedWireLoader),
            new PropertyMetadata(false, OnIsActiveChanged));

    public static readonly DependencyProperty PhaseProperty =
        DependencyProperty.Register(
            nameof(Phase),
            typeof(double),
            typeof(TwistedWireLoader),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BlueWireBrushProperty =
        DependencyProperty.Register(
            nameof(BlueWireBrush),
            typeof(Brush),
            typeof(TwistedWireLoader),
            new FrameworkPropertyMetadata(DefaultBlueBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OrangeWireBrushProperty =
        DependencyProperty.Register(
            nameof(OrangeWireBrush),
            typeof(Brush),
            typeof(TwistedWireLoader),
            new FrameworkPropertyMetadata(DefaultOrangeBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(
            nameof(StrokeThickness),
            typeof(double),
            typeof(TwistedWireLoader),
            new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public TwistedWireLoader()
    {
        Focusable = false;
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
        UseLayoutRounding = true;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public double Phase
    {
        get => (double)GetValue(PhaseProperty);
        set => SetValue(PhaseProperty, value);
    }

    public Brush BlueWireBrush
    {
        get => (Brush)GetValue(BlueWireBrushProperty);
        set => SetValue(BlueWireBrushProperty, value);
    }

    public Brush OrangeWireBrush
    {
        get => (Brush)GetValue(OrangeWireBrushProperty);
        set => SetValue(OrangeWireBrushProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(52, 24);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 12 || height < 8)
        {
            return;
        }

        var stroke = Math.Clamp(StrokeThickness, 1.5, height * 0.3);
        var centerY = height * 0.5;
        var radius = Math.Min(height * 0.275, (height - stroke) * 0.5 - 1.0);
        var endpointPadding = stroke * 0.8 + 1.0;
        var left = endpointPadding;
        var right = width - endpointPadding;
        if (right <= left || radius <= 0)
        {
            return;
        }

        var blueColor = GetBrushColor(BlueWireBrush, DefaultBlue);
        var orangeColor = GetBrushColor(OrangeWireBrush, DefaultOrange);
        EnsurePens(stroke, blueColor, orangeColor);
        EnsureAnimationFrames(width, height, stroke, blueColor, orangeColor, left, right, centerY, radius);

        var normalizedPhase = Phase % (Math.PI * 2);
        if (normalizedPhase < 0)
        {
            normalizedPhase += Math.PI * 2;
        }

        var frameIndex = (int)Math.Floor(normalizedPhase / (Math.PI * 2) * AnimationFrameCount) % AnimationFrameCount;
        drawingContext.DrawDrawing(_animationFrames[frameIndex]);
    }

    private static void OnIsActiveChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
    {
        ((TwistedWireLoader)dependencyObject).UpdateAnimation();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateAnimation();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopAnimation();
    }

    private void UpdateAnimation()
    {
        if (!IsLoaded)
        {
            return;
        }

        if (IsActive && SystemParameters.ClientAreaAnimation)
        {
            var animation = new DoubleAnimation
            {
                From = 0,
                To = Math.PI * 2,
                Duration = TimeSpan.FromSeconds(1.35),
                RepeatBehavior = RepeatBehavior.Forever
            };

            Timeline.SetDesiredFrameRate(animation, AnimationFrameRate);
            BeginAnimation(PhaseProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            StopAnimation();
        }
    }

    private void StopAnimation()
    {
        BeginAnimation(PhaseProperty, null);
        SetCurrentValue(PhaseProperty, 0.0);
    }

    private void EnsureAnimationFrames(
        double width,
        double height,
        double stroke,
        Color blueColor,
        Color orangeColor,
        double left,
        double right,
        double centerY,
        double radius)
    {
        if (_animationFrames.Length == AnimationFrameCount &&
            _cachedFrameWidth.Equals(width) &&
            _cachedFrameHeight.Equals(height) &&
            _cachedFrameStroke.Equals(stroke) &&
            _cachedFrameBlueColor.Equals(blueColor) &&
            _cachedFrameOrangeColor.Equals(orangeColor))
        {
            return;
        }

        _cachedFrameWidth = width;
        _cachedFrameHeight = height;
        _cachedFrameStroke = stroke;
        _cachedFrameBlueColor = blueColor;
        _cachedFrameOrangeColor = orangeColor;

        var frames = new DrawingGroup[AnimationFrameCount];
        for (var frameIndex = 0; frameIndex < AnimationFrameCount; frameIndex++)
        {
            var phase = frameIndex / (double)AnimationFrameCount * Math.PI * 2;
            CreateSegments(left, right, centerY, radius, phase);
            _segments.Sort(static (first, second) => first.Depth.CompareTo(second.Depth));

            var drawing = new DrawingGroup();
            using (var frameContext = drawing.Open())
            {
                DrawShadow(frameContext);
                DrawWireSegments(frameContext);
                DrawEndpoints(frameContext, stroke);
            }

            drawing.Freeze();
            frames[frameIndex] = drawing;
        }

        _animationFrames = frames;
    }

    private void CreateSegments(
        double left,
        double right,
        double centerY,
        double radius,
        double animationPhase)
    {
        _segments.Clear();
        AddWireSegments(left, right, centerY, radius, wireIndex: 0, phaseOffset: 0.0, animationPhase);
        AddWireSegments(left, right, centerY, radius, wireIndex: 1, phaseOffset: Math.PI, animationPhase);
    }

    private void AddWireSegments(
        double left,
        double right,
        double centerY,
        double radius,
        int wireIndex,
        double phaseOffset,
        double animationPhase)
    {
        var previous = CreatePoint(0, left, right, centerY, radius, phaseOffset, animationPhase);

        for (var index = 1; index <= SegmentCount; index++)
        {
            var progress = index / (double)SegmentCount;
            var current = CreatePoint(progress, left, right, centerY, radius, phaseOffset, animationPhase);
            _segments.Add(new WireSegment(
                (previous.Depth + current.Depth) * 0.5,
                previous.Position,
                current.Position,
                wireIndex,
                index == 1,
                index == SegmentCount));
            previous = current;
        }
    }

    private HelixPoint CreatePoint(
        double progress,
        double left,
        double right,
        double centerY,
        double radius,
        double phaseOffset,
        double animationPhase)
    {
        var angle = (progress * Turns * Math.PI * 2) + animationPhase + phaseOffset;
        var position = new Point(
            left + ((right - left) * progress),
            centerY + (radius * Math.Sin(angle)));
        return new HelixPoint(position, Math.Cos(angle));
    }

    private void EnsurePens(double stroke, Color blueColor, Color orangeColor)
    {
        if (_cachedStroke.Equals(stroke) &&
            _cachedBlueColor.Equals(blueColor) &&
            _cachedOrangeColor.Equals(orangeColor))
        {
            return;
        }

        _cachedStroke = stroke;
        _cachedBlueColor = blueColor;
        _cachedOrangeColor = orangeColor;
        _shadowPen = CreateRoundPen(ShadowBrush, stroke + 1.4);
        _bluePens = CreateDepthPens(blueColor, stroke);
        _orangePens = CreateDepthPens(orangeColor, stroke);
    }

    private static Pen[] CreateDepthPens(Color color, double stroke)
    {
        var pens = new Pen[DepthStepCount];
        for (var index = 0; index < DepthStepCount; index++)
        {
            var depth = index / (double)(DepthStepCount - 1);
            var alpha = (byte)Math.Round(175 + (80 * depth));
            var thickness = stroke * (0.91 + (0.09 * depth));
            var brush = CreateFrozenBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
            pens[index] = CreateRoundPen(brush, thickness);
        }

        return pens;
    }

    private void DrawShadow(DrawingContext drawingContext)
    {
        drawingContext.DrawGeometry(null, _shadowPen!, CreateSegmentGeometry(_segments));
    }

    private void DrawWireSegments(DrawingContext drawingContext)
    {
        foreach (var bucket in _segmentBuckets)
        {
            bucket.Clear();
        }

        foreach (var segment in _segments)
        {
            var depthIndex = GetDepthIndex(segment.Depth);
            _segmentBuckets[(segment.WireIndex * DepthStepCount) + depthIndex].Add(segment);
        }

        for (var depthIndex = 0; depthIndex < DepthStepCount; depthIndex++)
        {
            for (var wireIndex = 0; wireIndex < 2; wireIndex++)
            {
                var segments = _segmentBuckets[(wireIndex * DepthStepCount) + depthIndex];
                if (segments.Count == 0)
                {
                    continue;
                }

                var pens = wireIndex == 0 ? _bluePens : _orangePens;
                drawingContext.DrawGeometry(null, pens[depthIndex], CreateSegmentGeometry(segments));
            }
        }
    }

    private static StreamGeometry CreateSegmentGeometry(IEnumerable<WireSegment> segments)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            foreach (var segment in segments)
            {
                context.BeginFigure(segment.Start, isFilled: false, isClosed: false);
                context.LineTo(segment.End, isStroked: true, isSmoothJoin: true);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private void DrawEndpoints(DrawingContext drawingContext, double stroke)
    {
        _endpoints.Clear();
        foreach (var segment in _segments)
        {
            if (segment.IsStart)
            {
                _endpoints.Add(new WireEndpoint(segment.Depth, segment.Start, segment.WireIndex));
            }

            if (segment.IsEnd)
            {
                _endpoints.Add(new WireEndpoint(segment.Depth, segment.End, segment.WireIndex));
            }
        }

        _endpoints.Sort(static (first, second) => first.Depth.CompareTo(second.Depth));
        foreach (var endpoint in _endpoints)
        {
            var depth = (endpoint.Depth + 1.0) * 0.5;
            var endpointRadius = stroke * (0.62 + (0.12 * depth));
            var pens = endpoint.WireIndex == 0 ? _bluePens : _orangePens;
            var brush = pens[GetDepthIndex(endpoint.Depth)].Brush;
            drawingContext.DrawEllipse(ShadowBrush, null, endpoint.Position, endpointRadius + 0.7, endpointRadius + 0.7);
            drawingContext.DrawEllipse(brush, null, endpoint.Position, endpointRadius, endpointRadius);
        }
    }

    private static int GetDepthIndex(double depth)
    {
        var normalizedDepth = Math.Clamp((depth + 1.0) * 0.5, 0.0, 1.0);
        return (int)Math.Round(normalizedDepth * (DepthStepCount - 1));
    }

    private static Pen CreateRoundPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };

        if (pen.CanFreeze)
        {
            pen.Freeze();
        }

        return pen;
    }

    private static Color GetBrushColor(Brush brush, Color fallback)
    {
        return brush is SolidColorBrush solidColorBrush ? solidColorBrush.Color : fallback;
    }

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private readonly record struct HelixPoint(Point Position, double Depth);

    private readonly record struct WireSegment(
        double Depth,
        Point Start,
        Point End,
        int WireIndex,
        bool IsStart,
        bool IsEnd);

    private readonly record struct WireEndpoint(double Depth, Point Position, int WireIndex);
}
