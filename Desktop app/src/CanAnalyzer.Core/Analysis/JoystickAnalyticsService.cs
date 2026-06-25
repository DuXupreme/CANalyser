using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;

namespace CanAnalyzer.Core.Analysis;

/// <inheritdoc />
public sealed class JoystickAnalyticsService : IJoystickAnalyticsService
{
    public SignalAnalyticsResult AnalyzeSignal(
        SignalSeries series,
        int histogramBins = 40,
        double threshold = 0.0,
        double changeThreshold = 0.01,
        int deltaHistogramBins = 40)
    {
        if (series.Time.Length == 0 || series.Value.Length == 0)
        {
            return new SignalAnalyticsResult(
                series.Label,
                new SignalStatistics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
                new SignalEventStatistics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
                [],
                []);
        }

        var v = series.Value.ToArray();
        var sorted = v.OrderBy(static x => x).ToArray();
        var mean = v.Average();
        var p01 = Percentile(sorted, 0.01);
        var p99 = Percentile(sorted, 0.99);
        var stats = new SignalStatistics(
            v.Length,
            Math.Max(0, series.Time[^1] - series.Time[0]),
            sorted[0],
            sorted[^1],
            mean,
            Percentile(sorted, 0.50),
            Std(v, mean),
            p01,
            p99,
            v[0],
            v[^1],
            v[^1] - v[0]);

        var eventStats = BuildEventStats(series.Time, v, threshold, changeThreshold);
        var hist = Histogram(v, histogramBins, sorted[0], sorted[^1]);
        var d = Deltas(v);
        var dp95 = Percentile(d.Select(Math.Abs).OrderBy(static x => x).ToArray(), 0.95);
        var dHist = d.Length == 0 ? [] : Histogram(d, deltaHistogramBins, d.Min(), d.Max());
        return new SignalAnalyticsResult(series.Label, stats, eventStats, hist, dHist);
    }

    public JoystickPairAnalyticsResult AnalyzePair(
        SignalSeries xSeries,
        SignalSeries ySeries,
        double deadzoneThreshold = 0.10,
        double saturationThreshold = 0.90,
        int radiusHistogramBins = 30,
        int maxPathPoints = 6000)
    {
        var aligned = Align(xSeries, ySeries);
        if (aligned.Time.Length == 0)
        {
            return new JoystickPairAnalyticsResult(
                xSeries.Label,
                ySeries.Label,
                new JoystickPairStatistics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
                [],
                [],
                aligned.Report);
        }

        var sx = aligned.Left.OrderBy(static x => x).ToArray();
        var sy = aligned.Right.OrderBy(static x => x).ToArray();
        var cx = Percentile(sx, 0.50);
        var cy = Percentile(sy, 0.50);
        var scale = Math.Max(1e-9, Math.Max(Percentile(sx, 0.99) - Percentile(sx, 0.01), Percentile(sy, 0.99) - Percentile(sy, 0.01)) / 2.0);

        var points = new List<NormalizedPoint>(aligned.Time.Length);
        var radii = new double[aligned.Time.Length];
        var xValues = new double[aligned.Time.Length];
        var yValues = new double[aligned.Time.Length];
        var deadWeight = 0.0;
        var satWeight = 0.0;
        var totalWeight = 0.0;
        var path = 0.0;
        var maxSpeed = 0.0;
        var sumR = 0.0;
        var weightedX = 0.0;
        var weightedY = 0.0;
        var q1 = 0.0;
        var q2 = 0.0;
        var q3 = 0.0;
        var q4 = 0.0;
        for (var i = 0; i < aligned.Time.Length; i++)
        {
            var nx = (aligned.Left[i] - cx) / scale;
            var ny = (aligned.Right[i] - cy) / scale;
            xValues[i] = nx;
            yValues[i] = ny;
            points.Add(new NormalizedPoint(nx, ny));
            var r = Math.Sqrt((nx * nx) + (ny * ny));
            radii[i] = r;
            var weight = i + 1 < aligned.Time.Length ? Math.Max(0, aligned.Time[i + 1] - aligned.Time[i]) : 0;
            if (aligned.Time.Length == 1) weight = 1;
            totalWeight += weight;
            sumR += r * weight;
            weightedX += nx * weight;
            weightedY += ny * weight;
            if (r <= deadzoneThreshold) deadWeight += weight;
            if (r >= saturationThreshold) satWeight += weight;
            if (nx >= 0 && ny >= 0) q1 += weight;
            else if (nx < 0 && ny >= 0) q2 += weight;
            else if (nx < 0 && ny < 0) q3 += weight;
            else q4 += weight;
            if (i > 0)
            {
                var dx = nx - points[i - 1].X;
                var dy = ny - points[i - 1].Y;
                var dt = aligned.Time[i] - aligned.Time[i - 1];
                var len = Math.Sqrt((dx * dx) + (dy * dy));
                path += len;
                if (dt > 0) maxSpeed = Math.Max(maxSpeed, len / dt);
            }
        }

        var xSorted = xValues.OrderBy(static value => value).ToArray();
        var ySorted = yValues.OrderBy(static value => value).ToArray();
        var usedXRangePercent = Math.Min(100.0, ((Percentile(xSorted, 0.95) - Percentile(xSorted, 0.05)) / 2.0) * 100.0);
        var usedYRangePercent = Math.Min(100.0, ((Percentile(ySorted, 0.95) - Percentile(ySorted, 0.05)) / 2.0) * 100.0);
        var p95Radius = Percentile(radii.OrderBy(static value => value).ToArray(), 0.95);
        var pairStats = new JoystickPairStatistics(
            aligned.Time.Length,
            Math.Max(0, aligned.Time[^1] - aligned.Time[0]),
            cx,
            cy,
            sumR / totalWeight,
            radii.Max(),
            p95Radius,
            (deadWeight / totalWeight) * 100.0,
            (satWeight / totalWeight) * 100.0,
            usedXRangePercent,
            usedYRangePercent,
            weightedX / totalWeight,
            weightedY / totalWeight,
            (q1 / totalWeight) * 100.0,
            (q2 / totalWeight) * 100.0,
            (q3 / totalWeight) * 100.0,
            (q4 / totalWeight) * 100.0,
            path,
            maxSpeed);
        var rh = Histogram(radii, radiusHistogramBins, 0, radii.Max());
        return new JoystickPairAnalyticsResult(xSeries.Label, ySeries.Label, pairStats, rh, Reduce(points, maxPathPoints), aligned.Report);
    }

    public IReadOnlyList<SignalRankingRow> RankSignals(IReadOnlyDictionary<string, SignalSeries> seriesByLabel, double changeThreshold = 0.01, int topN = 20)
    {
        var rows = new List<SignalRankingRow>();
        var change = Math.Max(0, changeThreshold);
        foreach (var kv in seriesByLabel)
        {
            var s = kv.Value;
            if (s.Time.Length < 2 || s.Value.Length < 2) continue;
            var v = s.Value.ToArray();
            var sorted = v.OrderBy(static x => x).ToArray();
            var mean = v.Average();
            var std = Std(v, mean);
            var d = Deltas(v);
            var dur = Math.Max(1e-9, s.Time[^1] - s.Time[0]);
            var changes = d.Count(delta => Math.Abs(delta) >= change);
            var cps = changes / dur;
            var maxSlope = Slopes(s.Time, v).DefaultIfEmpty(0).Max(static x => Math.Abs(x));
            var score = (cps * 0.60) + (std * 0.25) + (maxSlope * 0.15);
            rows.Add(new SignalRankingRow(s.Label, v.Length, dur, sorted[^1] - sorted[0], std, changes, cps, d.Length == 0 ? 0 : d.Max(static x => Math.Abs(x)), maxSlope, score));
        }

        return rows.OrderByDescending(static r => r.ActivityScore).Take(Math.Clamp(topN, 1, 500)).ToList();
    }

    public JoystickActuatorComparisonResult AnalyzeJoystickActuatorTracking(
        SignalSeries joystickXSeries,
        SignalSeries joystickYSeries,
        SignalSeries actuatorXSeries,
        SignalSeries actuatorYSeries,
        double deadzoneThreshold = 0.10,
        int vectorHistogramBins = 40,
        int maxPathPoints = 6000)
    {
        var x = Align(joystickXSeries, actuatorXSeries);
        var y = Align(joystickYSeries, actuatorYSeries);
        if (x.Time.Length == 0 || y.Time.Length == 0)
        {
            var emptyAxis = new AxisTrackingStatistics("X", 0, 0, 0, 0, 0, 0, 0, 0, 0);
            return new JoystickActuatorComparisonResult(joystickXSeries.Label, joystickYSeries.Label, actuatorXSeries.Label, actuatorYSeries.Label, emptyAxis, emptyAxis with { AxisName = "Y" }, 0, 0, 0, [], [], []);
        }

        var xStats = AxisStats("X", x.Left, x.Right, deadzoneThreshold);
        var yStats = AxisStats("Y", y.Left, y.Right, deadzoneThreshold);
        var jPath = BuildPath(joystickXSeries, joystickYSeries, null, null, null);
        var aPath = BuildPath(actuatorXSeries, actuatorYSeries, jPath.CenterX, jPath.CenterY, jPath.Scale);

        var verr = new List<double>(jPath.Time.Length);
        for (var i = 0; i < jPath.Time.Length; i++)
        {
            var idx = Nearest(aPath.Time, jPath.Time[i]);
            var dx = jPath.X[i] - aPath.X[idx];
            var dy = jPath.Y[i] - aPath.Y[idx];
            verr.Add(Math.Sqrt((dx * dx) + (dy * dy)));
        }

        var vs = verr.OrderBy(static x => x).ToArray();
        var vHist = verr.Count == 0 ? [] : Histogram(verr, vectorHistogramBins, 0, Math.Max(1.0, Percentile(vs, 0.99)));
        return new JoystickActuatorComparisonResult(
            joystickXSeries.Label,
            joystickYSeries.Label,
            actuatorXSeries.Label,
            actuatorYSeries.Label,
            xStats,
            yStats,
            verr.Count == 0 ? 0 : verr.Average(),
            Percentile(vs, 0.95),
            verr.Count == 0 ? 0 : verr.Max(),
            vHist,
            Reduce(ToPoints(jPath.X, jPath.Y), maxPathPoints),
            Reduce(ToPoints(aPath.X, aPath.Y), maxPathPoints));
    }

    public DelayAnalysisResult AnalyzeDelay(
        SignalSeries commandSeries,
        SignalSeries responseSeries,
        double searchRangeSeconds = 1.5,
        double thresholdFraction = 0.2,
        int maxPlotPoints = 5000)
    {
        if (commandSeries.Time.Length < 2 || responseSeries.Time.Length < 2 || commandSeries.Value.Length < 2 || responseSeries.Value.Length < 2)
        {
            return CreateEmptyDelayResult(commandSeries.Label, responseSeries.Label, searchRangeSeconds);
        }

        var start = Math.Max(commandSeries.Time[0], responseSeries.Time[0]);
        var end = Math.Min(commandSeries.Time[^1], responseSeries.Time[^1]);
        if (end <= start) return CreateEmptyDelayResult(commandSeries.Label, responseSeries.Label, searchRangeSeconds);

        var slowMedian = Math.Max(
            EstimateMedianDt(commandSeries.Time, commandSeries.Time.Length),
            EstimateMedianDt(responseSeries.Time, responseSeries.Time.Length));
        var maximumGap = Math.Max(MaximumPositiveInterval(commandSeries.Time), MaximumPositiveInterval(responseSeries.Time));
        if (slowMedian > 0 && maximumGap > 5d * slowMedian)
            throw new InvalidOperationException($"Delayanalyse ongeldig: sampleafstand {maximumGap:G15}s overschrijdt 5× de mediane interval van de traagste reeks ({5d * slowMedian:G15}s)." );

        var finestDt = Math.Min(EstimateMedianDt(commandSeries.Time, commandSeries.Time.Length), EstimateMedianDt(responseSeries.Time, responseSeries.Time.Length));
        var nLong = checked((long)Math.Ceiling((end - start) / Math.Max(1e-9, finestDt)) + 1L);
        if (nLong > 2_000_000)
            throw new InvalidOperationException($"Delayanalyse vereist {nLong:N0} punten op bronresolutie; beperk het tijdvenster in plaats van data stil te reduceren.");
        var n = (int)Math.Max(3, nLong);
        var dt = (end - start) / (n - 1);
        var t = new double[n];
        var cRaw = new double[n];
        var rRaw = new double[n];
        for (var i = 0; i < n; i++)
        {
            var ti = start + (i * dt);
            t[i] = ti;
            cRaw[i] = Lerp(commandSeries.Time, commandSeries.Value, ti);
            rRaw[i] = Lerp(responseSeries.Time, responseSeries.Value, ti);
        }

        var c = RobustNormalize(cRaw, out _, out _);
        var r = RobustNormalize(rRaw, out var responseCenter, out var responseScale);
        var cDynamics = SmoothedDerivative(c, dt);
        var rDynamics = SmoothedDerivative(r, dt);

        var maxK = Math.Clamp((int)Math.Round(Math.Max(0.01, searchRangeSeconds) / Math.Max(1e-9, dt)), 1, Math.Max(1, cDynamics.Length - 2));
        var curve = new List<DelayCorrelationPoint>();
        var bestK = 0;
        var bestCorr = double.NegativeInfinity;
        var bestScore = double.NegativeInfinity;
        for (var k = -maxK; k <= maxK; k++)
        {
            var corr = CorrAt(cDynamics, rDynamics, k, out var samples);
            curve.Add(new DelayCorrelationPoint(k * dt, corr, samples));
            var score = Math.Abs(corr);
            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            bestCorr = corr;
            bestK = k;
        }

        var lag = bestK * dt;
        var shiftedTimes = new List<double>(n);
        var rsRaw = new List<double>(n);
        var errors = new List<double>(n);
        for (var i = 0; i < n; i++)
        {
            var shiftedTimestamp = t[i] + lag;
            if (shiftedTimestamp < responseSeries.Time[0] || shiftedTimestamp > responseSeries.Time[^1])
                continue;
            var shiftedRaw = Lerp(responseSeries.Time, responseSeries.Value, shiftedTimestamp);
            shiftedTimes.Add(t[i]);
            rsRaw.Add(shiftedRaw);
            errors.Add(Math.Abs(NormalizeValue(shiftedRaw, responseCenter, responseScale) - c[i]));
        }

        if (errors.Count == 0) return CreateEmptyDelayResult(commandSeries.Label, responseSeries.Label, searchRangeSeconds);
        var se = errors.OrderBy(static x => x).ToArray();
        var dth = DelayByThreshold(t, c, r, thresholdFraction);
        var delayEventMatches = CollectDelayEvents(t, c, r, Math.Max(0.01, searchRangeSeconds));
        var delayValues = delayEventMatches.Select(static match => match.DelaySeconds).ToArray();
        var delayEvents = BuildDelayEventStatistics(delayValues);
        var risingDelay = BuildDelayEventStatistics(delayEventMatches.Where(static match => match.CommandDirection > 0).Select(static match => match.DelaySeconds).ToArray());
        var fallingDelay = BuildDelayEventStatistics(delayEventMatches.Where(static match => match.CommandDirection < 0).Select(static match => match.DelaySeconds).ToArray());
        var histogramHigh = delayValues.Length == 0 ? Math.Max(0.1, searchRangeSeconds) : Math.Max(0.1, Percentile(delayValues.OrderBy(static value => value).ToArray(), 0.99));
        var delayHistogram = Histogram(delayValues, 30, 0, histogramHigh);
        return new DelayAnalysisResult(
            commandSeries.Label,
            responseSeries.Label,
            errors.Count,
            Math.Max(0.01, searchRangeSeconds),
            lag,
            bestCorr,
            errors.Average(),
            Percentile(se, 0.95),
            errors.Max(),
            dth,
            delayEvents.MatchedEventCount,
            delayEvents.MeanDelaySeconds,
            delayEvents.MinimumDelaySeconds,
            delayEvents.MaximumDelaySeconds,
            delayEvents.Percentile95DelaySeconds,
            risingDelay,
            fallingDelay,
            delayHistogram,
            curve,
            ReduceTV(t, cRaw, maxPlotPoints),
            ReduceTV(t, rRaw, maxPlotPoints),
            ReduceTV(shiftedTimes, rsRaw, maxPlotPoints));
    }

    public FirstResponseDelayResult AnalyzeFirstResponseDelay(
        SignalSeries commandSeries,
        SignalSeries responseSeries,
        double searchRangeSeconds = 1.5,
        double responseThresholdFraction = 0.02,
        int histogramBins = 30)
    {
        var frac = Math.Clamp(responseThresholdFraction, 0.001, 0.5);
        if (commandSeries.Time.Length < 2 || responseSeries.Time.Length < 2 || commandSeries.Value.Length < 2 || responseSeries.Value.Length < 2)
        {
            return CreateEmptyFirstResponseResult(commandSeries.Label, responseSeries.Label, frac);
        }

        var window = Math.Max(0.01, searchRangeSeconds);

        // Command step detection: a real edge moves a sizeable fraction of the command swing.
        var cmdValues = commandSeries.Value.ToArray();
        var cmdSorted = cmdValues.OrderBy(static v => v).ToArray();
        var cmdRange = Math.Max(1e-9, Percentile(cmdSorted, 0.99) - Percentile(cmdSorted, 0.01));
        var stepThreshold = cmdRange * 0.30;

        // Feedback reaction threshold: a small fraction of the feedback range above the resting value.
        var rspSorted = responseSeries.Value.OrderBy(static v => v).ToArray();
        var rspRange = Math.Max(1e-9, Percentile(rspSorted, 0.99) - Percentile(rspSorted, 0.01));
        var responseThreshold = Math.Max(1e-9, rspRange * frac);

        var medianDt = EstimateMedianDt(commandSeries.Time, commandSeries.Time.Length);
        var minGap = Math.Max(0.02, Math.Min(window * 0.5, medianDt * 4.0));

        var deadTimes = new List<double>();
        var risingValues = new List<double>();
        var fallingValues = new List<double>();
        var edgeCount = 0;
        var lastEdgeTime = double.NegativeInfinity;
        for (var i = 1; i < commandSeries.Time.Length; i++)
        {
            var delta = cmdValues[i] - cmdValues[i - 1];
            if (Math.Abs(delta) < stepThreshold)
            {
                continue;
            }

            var edgeTime = commandSeries.Time[i];
            if ((edgeTime - lastEdgeTime) < minGap)
            {
                continue;
            }

            var direction = Math.Sign(delta);
            if (direction == 0)
            {
                continue;
            }

            lastEdgeTime = edgeTime;
            edgeCount++;

            // Resting feedback value at the moment the command changes (before it could react).
            var baseline = Lerp(responseSeries.Time, responseSeries.Value, edgeTime);
            var target = baseline + (direction * responseThreshold);

            // First feedback sample after the edge that deviates past the threshold in the command direction.
            var startIdx = UpperBound(responseSeries.Time, edgeTime);
            for (var j = startIdx; j < responseSeries.Time.Length; j++)
            {
                var reactionTime = responseSeries.Time[j];
                if (reactionTime <= edgeTime)
                {
                    continue;
                }

                if ((reactionTime - edgeTime) > window)
                {
                    break;
                }

                var value = responseSeries.Value[j];
                var crossed = direction > 0 ? value >= target : value <= target;
                if (!crossed)
                {
                    continue;
                }

                var previousTime = j > 0 ? Math.Max(edgeTime, responseSeries.Time[j - 1]) : edgeTime;
                var previousValue = previousTime == edgeTime ? baseline : responseSeries.Value[j - 1];
                var interval = reactionTime - previousTime;
                var allowedGap = 5d * Math.Max(
                    EstimateMedianDt(commandSeries.Time, commandSeries.Time.Length),
                    EstimateMedianDt(responseSeries.Time, responseSeries.Time.Length));
                if (allowedGap > 0 && interval > allowedGap) break;
                var valueDelta = value - previousValue;
                var fraction = Math.Abs(valueDelta) <= 1e-15 ? 1d : Math.Clamp((target - previousValue) / valueDelta, 0d, 1d);
                var crossingTime = previousTime + (interval * fraction);
                var deadTime = crossingTime - edgeTime;
                if (deadTime < 0)
                {
                    break;
                }

                deadTimes.Add(deadTime);
                (direction > 0 ? risingValues : fallingValues).Add(deadTime);
                break;
            }
        }

        var stats = BuildDelayEventStatistics(deadTimes);
        var rising = BuildDelayEventStatistics(risingValues);
        var falling = BuildDelayEventStatistics(fallingValues);
        var histogramHigh = deadTimes.Count == 0
            ? Math.Max(0.05, window)
            : Math.Max(0.02, Percentile(deadTimes.OrderBy(static v => v).ToArray(), 0.99));
        var histogram = Histogram(deadTimes, histogramBins, 0, histogramHigh);

        return new FirstResponseDelayResult(
            commandSeries.Label,
            responseSeries.Label,
            edgeCount,
            deadTimes.Count,
            frac,
            stats.MeanDelaySeconds,
            stats.MinimumDelaySeconds,
            stats.MaximumDelaySeconds,
            stats.Percentile95DelaySeconds,
            rising,
            falling,
            histogram);
    }

    private static FirstResponseDelayResult CreateEmptyFirstResponseResult(string commandLabel, string responseLabel, double thresholdFraction)
    {
        var empty = new DelayEventStatistics(0, null, null, null, null);
        return new FirstResponseDelayResult(commandLabel, responseLabel, 0, 0, thresholdFraction, null, null, null, null, empty, empty, []);
    }

    public ButterflyKinematicsResult AnalyzeButterflyKinematics(
        SignalSeries joystickXSeries,
        SignalSeries joystickYSeries,
        SignalSeries joystickYawSeries,
        SignalSeries actuatorLeftSeries,
        SignalSeries actuatorRightSeries,
        SignalSeries actuatorFrontSeries,
        double deadzoneThreshold = 0.10,
        double delaySearchRangeSeconds = 1.5,
        int delayHistogramBins = 24,
        int maxPlotPoints = 6000)
    {
        var leftCommand = BuildButterflyLeftCommand(joystickYSeries, joystickYawSeries);
        var rightCommand = BuildButterflyRightCommand(joystickYSeries, joystickYawSeries);
        var frontCommand = BuildButterflyFrontCommand(joystickXSeries);

        var leftAligned = Align(leftCommand, actuatorLeftSeries);
        var rightAligned = Align(rightCommand, actuatorRightSeries);
        var frontAligned = Align(frontCommand, actuatorFrontSeries);

        var leftStats = AxisStats("Left", leftAligned.Left, leftAligned.Right, deadzoneThreshold);
        var rightStats = AxisStats("Right", rightAligned.Left, rightAligned.Right, deadzoneThreshold);
        var frontStats = AxisStats("Front", frontAligned.Left, frontAligned.Right, deadzoneThreshold);

        var searchWindow = Math.Max(0.01, delaySearchRangeSeconds);
        var leftDelay = BuildDelayEventStatistics(leftAligned.Time, leftAligned.Left, leftAligned.Right, searchWindow);
        var rightDelay = BuildDelayEventStatistics(rightAligned.Time, rightAligned.Left, rightAligned.Right, searchWindow);
        var frontDelay = BuildDelayEventStatistics(frontAligned.Time, frontAligned.Left, frontAligned.Right, searchWindow);

        var leftDelayValues = CollectDelayValues(leftAligned.Time, leftAligned.Left, leftAligned.Right, searchWindow);
        var rightDelayValues = CollectDelayValues(rightAligned.Time, rightAligned.Left, rightAligned.Right, searchWindow);
        var frontDelayValues = CollectDelayValues(frontAligned.Time, frontAligned.Left, frontAligned.Right, searchWindow);

        return new ButterflyKinematicsResult(
            joystickXSeries.Label,
            joystickYSeries.Label,
            joystickYawSeries.Label,
            actuatorLeftSeries.Label,
            actuatorRightSeries.Label,
            actuatorFrontSeries.Label,
            leftStats,
            rightStats,
            frontStats,
            leftDelay,
            rightDelay,
            frontDelay,
            ReduceTV(leftAligned.Time, leftAligned.Left, maxPlotPoints),
            ReduceTV(leftAligned.Time, leftAligned.Right, maxPlotPoints),
            ReduceTV(rightAligned.Time, rightAligned.Left, maxPlotPoints),
            ReduceTV(rightAligned.Time, rightAligned.Right, maxPlotPoints),
            ReduceTV(frontAligned.Time, frontAligned.Left, maxPlotPoints),
            ReduceTV(frontAligned.Time, frontAligned.Right, maxPlotPoints),
            Histogram(leftDelayValues, delayHistogramBins, 0, SafeDelayHistogramHigh(leftDelayValues)),
            Histogram(rightDelayValues, delayHistogramBins, 0, SafeDelayHistogramHigh(rightDelayValues)),
            Histogram(frontDelayValues, delayHistogramBins, 0, SafeDelayHistogramHigh(frontDelayValues)));
    }

    private static SignalEventStatistics BuildEventStats(double[] time, IReadOnlyList<double> value, double threshold, double changeThreshold)
    {
        var safe = Math.Max(0, changeThreshold);
        var rise = 0;
        var fall = 0;
        var changes = 0;
        var above = 0.0;
        var longest = 0.0;
        var run = 0.0;
        var total = 0.0;
        var absD = new List<double>();
        var absS = new List<double>();
        var flat = 0;
        var prevAbove = value[0] >= threshold;
        for (var i = 1; i < value.Count && i < time.Length; i++)
        {
            var dt = Math.Max(0, time[i] - time[i - 1]);
            total += dt;
            if (prevAbove) { above += dt; run += dt; } else if (run > 0) { longest = Math.Max(longest, run); run = 0; }
            var d = value[i] - value[i - 1];
            var ad = Math.Abs(d);
            absD.Add(ad);
            if (dt > 0) absS.Add(ad / dt);
            if (ad > 0 && ad >= safe) changes++;
            if (ad <= 1e-12) flat++;
            var nowAbove = value[i] >= threshold;
            if (!prevAbove && nowAbove) rise++;
            else if (prevAbove && !nowAbove) fall++;
            prevAbove = nowAbove;
        }

        longest = Math.Max(longest, run);
        var s = absD.OrderBy(static x => x).ToArray();
        return new SignalEventStatistics(
            threshold,
            safe,
            rise,
            fall,
            changes,
            total <= 1e-9 ? 0 : (above / total) * 100.0,
            longest,
            absD.Count == 0 ? 0 : absD.Average(),
            Percentile(s, 0.95),
            absD.Count == 0 ? 0 : absD.Max(),
            absS.Count == 0 ? 0 : absS.Average(),
            absS.Count == 0 ? 0 : absS.Max(),
            absD.Count == 0 ? 0 : (flat / (double)absD.Count) * 100.0);
    }

    private static SignalSeries BuildButterflyLeftCommand(SignalSeries joystickY, SignalSeries joystickYaw)
    {
        // Butterfly mapping supplied by operator:
        // Left IN for joystick backward (Y-) and yaw left.
        var aligned = Align(joystickY, joystickYaw);
        var values = new double[aligned.Time.Length];
        for (var i = 0; i < aligned.Time.Length; i++)
        {
            values[i] = -aligned.Left[i] - aligned.Right[i];
        }

        return new SignalSeries("Expected.Left.FromJoystick", aligned.Time, values);
    }

    private static SignalSeries BuildButterflyRightCommand(SignalSeries joystickY, SignalSeries joystickYaw)
    {
        // Butterfly mapping supplied by operator:
        // Right IN for joystick backward (Y-) and yaw right.
        var aligned = Align(joystickY, joystickYaw);
        var values = new double[aligned.Time.Length];
        for (var i = 0; i < aligned.Time.Length; i++)
        {
            values[i] = -aligned.Left[i] + aligned.Right[i];
        }

        return new SignalSeries("Expected.Right.FromJoystick", aligned.Time, values);
    }

    private static SignalSeries BuildButterflyFrontCommand(SignalSeries joystickX)
    {
        // Butterfly mapping supplied by operator:
        // Front IN for joystick right (X+), OUT for joystick left (X-).
        var time = joystickX.Time.ToArray();
        var value = joystickX.Value.ToArray();
        return new SignalSeries("Expected.Front.FromJoystick", time, value);
    }

    private static DelayEventStatistics BuildDelayEventStatistics(
        IReadOnlyList<double> time,
        IReadOnlyList<double> command,
        IReadOnlyList<double> response,
        double searchRangeSeconds)
    {
        return BuildDelayEventStatistics(CollectDelayValues(time, command, response, searchRangeSeconds));
    }

    private static DelayEventStatistics BuildDelayEventStatistics(IReadOnlyList<double> delays)
    {
        if (delays.Count == 0)
        {
            return new DelayEventStatistics(0, null, null, null, null);
        }

        var sorted = delays.OrderBy(static x => x).ToArray();
        return new DelayEventStatistics(
            sorted.Length,
            sorted.Average(),
            sorted[0],
            sorted[^1],
            Percentile(sorted, 0.95));
    }

    private static IReadOnlyList<double> CollectDelayValues(
        IReadOnlyList<double> time,
        IReadOnlyList<double> command,
        IReadOnlyList<double> response,
        double searchRangeSeconds)
    {
        return CollectDelayEvents(time, command, response, searchRangeSeconds)
            .Select(static match => match.DelaySeconds)
            .ToArray();
    }

    private static IReadOnlyList<DelayMatchEvent> CollectDelayEvents(
        IReadOnlyList<double> time,
        IReadOnlyList<double> command,
        IReadOnlyList<double> response,
        double searchRangeSeconds)
    {
        var n = Math.Min(time.Count, Math.Min(command.Count, response.Count));
        if (n < 3)
        {
            return [];
        }

        var cmd = command.Take(n).ToArray();
        var rsp = response.Take(n).ToArray();
        var window = Math.Max(0.01, searchRangeSeconds);
        var medianDt = EstimateMedianDt(time, n);
        var minGapSeconds = Math.Max(0.03, Math.Min(window * 0.35, medianDt * 6.0));
        var commandEvents = DetectEdgeEvents(time, cmd, n, minGapSeconds);
        var responseEvents = DetectEdgeEvents(time, rsp, n, minGapSeconds);
        if (commandEvents.Count == 0 || responseEvents.Count == 0)
        {
            return [];
        }

        var matches = new List<DelayMatchEvent>(Math.Min(commandEvents.Count, responseEvents.Count));
        var responseStart = 0;
        foreach (var commandEvent in commandEvents)
        {
            while (responseStart < responseEvents.Count && responseEvents[responseStart].Time <= commandEvent.Time)
            {
                responseStart++;
            }

            var matchedIndex = -1;
            for (var j = responseStart; j < responseEvents.Count; j++)
            {
                var candidate = responseEvents[j];
                var delay = candidate.Time - commandEvent.Time;
                if (delay < 0)
                {
                    continue;
                }

                if (delay > window)
                {
                    break;
                }

                if (candidate.Direction != commandEvent.Direction)
                {
                    continue;
                }

                matchedIndex = j;
                break;
            }

            if (matchedIndex < 0)
            {
                continue;
            }

            var responseEvent = responseEvents[matchedIndex];
            matches.Add(new DelayMatchEvent(
                responseEvent.Time - commandEvent.Time,
                commandEvent.Direction > 0 ? 1 : -1));
            responseStart = matchedIndex + 1;
        }

        return matches;
    }

    private static IReadOnlyList<EdgeEvent> DetectEdgeEvents(
        IReadOnlyList<double> time,
        IReadOnlyList<double> values,
        int n,
        double minGapSeconds)
    {
        if (n < 3)
        {
            return [];
        }

        var sortedValues = values.Take(n).OrderBy(static value => value).ToArray();
        var range = Math.Max(1e-9, Percentile(sortedValues, 0.99) - Percentile(sortedValues, 0.01));
        var deltas = new double[n];
        var absoluteDeltas = new double[n - 1];
        for (var i = 1; i < n; i++)
        {
            var delta = values[i] - values[i - 1];
            deltas[i] = delta;
            absoluteDeltas[i - 1] = Math.Abs(delta);
        }

        Array.Sort(absoluteDeltas);
        var noiseFloor = Percentile(absoluteDeltas, 0.80);
        var highThreshold = Math.Max(1e-5, Math.Max(range * 0.02, noiseFloor * 2.5));
        var lowThreshold = highThreshold * 0.45;
        var events = new List<EdgeEvent>();
        var lastEventTime = double.NegativeInfinity;
        for (var i = 1; i < n; i++)
        {
            var delta = deltas[i];
            var absoluteDelta = Math.Abs(delta);
            if (absoluteDelta < highThreshold)
            {
                continue;
            }

            if (Math.Abs(deltas[i - 1]) >= lowThreshold)
            {
                continue;
            }

            var direction = Math.Sign(delta);
            if (direction == 0)
            {
                continue;
            }

            var persistent = false;
            var iEnd = Math.Min(n - 1, i + 2);
            for (var k = i; k <= iEnd; k++)
            {
                if (Math.Sign(deltas[k]) == direction && Math.Abs(deltas[k]) >= lowThreshold)
                {
                    persistent = true;
                    break;
                }
            }

            if (!persistent)
            {
                continue;
            }

            var eventTime = time[i];
            if ((eventTime - lastEventTime) < minGapSeconds)
            {
                continue;
            }

            events.Add(new EdgeEvent(i, eventTime, values[i], direction, absoluteDelta));
            lastEventTime = eventTime;
        }

        return events;
    }

    private static double EstimateMedianDt(IReadOnlyList<double> time, int n)
    {
        if (n < 2)
        {
            return 0.01;
        }

        var deltas = new List<double>(Math.Max(1, n - 1));
        for (var i = 1; i < n; i++)
        {
            var dt = time[i] - time[i - 1];
            if (dt > 0 && !double.IsNaN(dt) && !double.IsInfinity(dt))
            {
                deltas.Add(dt);
            }
        }

        if (deltas.Count == 0)
        {
            return 0.01;
        }

        deltas.Sort();
        var mid = deltas.Count / 2;
        return deltas.Count % 2 == 0 ? (deltas[mid - 1] + deltas[mid]) * 0.5 : deltas[mid];
    }

    private static double[] RobustNormalize(IReadOnlyList<double> values, out double center, out double scale)
    {
        if (values.Count == 0)
        {
            center = 0;
            scale = 1;
            return [];
        }

        var sorted = values.OrderBy(static x => x).ToArray();
        var p01 = Percentile(sorted, 0.01);
        var p99 = Percentile(sorted, 0.99);
        center = (p01 + p99) * 0.5;
        scale = Math.Max(1e-9, (p99 - p01) * 0.5);

        var normalized = new double[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            normalized[i] = NormalizeValue(values[i], center, scale);
        }

        return normalized;
    }

    private static double NormalizeValue(double value, double center, double scale)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }

        var normalized = (value - center) / Math.Max(1e-9, scale);
        return Math.Clamp(normalized, -3.0, 3.0);
    }

    private static double[] SmoothedDerivative(IReadOnlyList<double> values, double dt)
    {
        if (values.Count < 2)
        {
            return [];
        }

        var sampleDt = Math.Max(1e-9, dt);
        var derivative = new double[values.Count];
        derivative[0] = 0;
        for (var i = 1; i < values.Count; i++)
        {
            derivative[i] = (values[i] - values[i - 1]) / sampleDt;
        }

        if (derivative.Length < 3)
        {
            return derivative;
        }

        var smoothed = new double[derivative.Length];
        smoothed[0] = derivative[0];
        smoothed[^1] = derivative[^1];
        for (var i = 1; i < derivative.Length - 1; i++)
        {
            smoothed[i] = (derivative[i - 1] + derivative[i] + derivative[i + 1]) / 3.0;
        }

        return smoothed;
    }

    private static double SafeDelayHistogramHigh(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 1.0;
        }

        var sorted = values.OrderBy(static x => x).ToArray();
        return Math.Max(0.01, Percentile(sorted, 0.99));
    }

    private static AxisTrackingStatistics AxisStats(string axis, IReadOnlyList<double> cmd, IReadOnlyList<double> rsp, double deadzone)
    {
        var n = Math.Min(cmd.Count, rsp.Count);
        if (n <= 0)
        {
            return new AxisTrackingStatistics(axis, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        var c = cmd.Take(n).ToArray();
        var r = rsp.Take(n).ToArray();
        var center = Percentile(c.OrderBy(static x => x).ToArray(), 0.50);
        var scale = Math.Max(1e-9, (Percentile(c.OrderBy(static x => x).ToArray(), 0.99) - Percentile(c.OrderBy(static x => x).ToArray(), 0.01)) / 2.0);
        var cn = new double[n];
        var rn = new double[n];
        var ae = new double[n];
        var mse = 0.0;
        var miss = 0;
        var dz = Math.Max(0, deadzone);
        for (var i = 0; i < n; i++)
        {
            cn[i] = (c[i] - center) / scale;
            rn[i] = (r[i] - center) / scale;
            var e = rn[i] - cn[i];
            ae[i] = Math.Abs(e);
            mse += e * e;
            if (Math.Abs(cn[i]) <= dz && Math.Abs(rn[i]) > dz) miss++;
        }

        var (gain, offset) = Reg(cn, rn);
        var sae = ae.OrderBy(static x => x).ToArray();
        return new AxisTrackingStatistics(axis, n, Corr(cn, rn), Math.Sqrt(mse / Math.Max(1, n)), gain, offset, ae.Average(), Percentile(sae, 0.95), ae.Max(), (miss / (double)Math.Max(1, n)) * 100.0);
    }

    private static (double Gain, double Offset) Reg(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        var n = Math.Min(x.Count, y.Count);
        if (n <= 1) return (0, 0);
        var mx = x.Take(n).Average();
        var my = y.Take(n).Average();
        var cov = 0.0;
        var varx = 0.0;
        for (var i = 0; i < n; i++) { var dx = x[i] - mx; cov += dx * (y[i] - my); varx += dx * dx; }
        if (Math.Abs(varx) <= 1e-12) return (0, my);
        var g = cov / varx;
        return (g, my - (g * mx));
    }

    private static PathData BuildPath(SignalSeries x, SignalSeries y, double? cx, double? cy, double? scale)
    {
        var a = Align(x, y);
        if (a.Time.Length == 0) return new PathData([], [], [], 0, 0, 1);
        var centerX = cx ?? Percentile(a.Left.OrderBy(static v => v).ToArray(), 0.50);
        var centerY = cy ?? Percentile(a.Right.OrderBy(static v => v).ToArray(), 0.50);
        var sc = scale ?? Math.Max(1e-9, Math.Max(Percentile(a.Left.OrderBy(static v => v).ToArray(), 0.99) - Percentile(a.Left.OrderBy(static v => v).ToArray(), 0.01), Percentile(a.Right.OrderBy(static v => v).ToArray(), 0.99) - Percentile(a.Right.OrderBy(static v => v).ToArray(), 0.01)) / 2.0);
        var ox = new double[a.Time.Length];
        var oy = new double[a.Time.Length];
        for (var i = 0; i < a.Time.Length; i++) { ox[i] = (a.Left[i] - centerX) / sc; oy[i] = (a.Right[i] - centerY) / sc; }
        return new PathData(a.Time, ox, oy, centerX, centerY, sc);
    }

    private static AlignedData Align(SignalSeries left, SignalSeries right)
    {
        var sourceCount = left.Time.Length + right.Time.Length;
        var duplicateCount = CountDuplicateIntervals(left.Time) + CountDuplicateIntervals(right.Time);
        if (left.Time.Length == 0 || right.Time.Length == 0 || left.Value.Length == 0 || right.Value.Length == 0)
            return new AlignedData([], [], [], new AlignmentReport(AnalysisStatus.InsufficientOverlap, sourceCount, 0, 0, 0, 0, duplicateCount, "Een of beide reeksen zijn leeg."));
        var start = Math.Max(left.Time[0], right.Time[0]);
        var end = Math.Min(left.Time[^1], right.Time[^1]);
        if (end < start)
            return new AlignedData([], [], [], new AlignmentReport(AnalysisStatus.InsufficientOverlap, sourceCount, 0, 0, 0, 0, duplicateCount, "De reeksen hebben geen gemeenschappelijke tijdsperiode."));
        var times = left.Time.Concat(right.Time)
            .Where(time => time >= start && time <= end)
            .Distinct()
            .OrderBy(static time => time)
            .ToArray();
        var l = new double[times.Length];
        var r = new double[times.Length];
        var slowMedianInterval = Math.Max(EstimateMedianDt(left.Time, left.Time.Length), EstimateMedianDt(right.Time, right.Time.Length));
        var maximumAllowedGap = 5d * slowMedianInterval;
        var maximumObservedGap = Math.Max(MaximumPositiveInterval(left.Time), MaximumPositiveInterval(right.Time));
        var validCount = 0;
        for (var i = 0; i < times.Length; i++)
        {
            var leftGap = BracketDistance(left.Time, times[i]);
            var rightGap = BracketDistance(right.Time, times[i]);
            var gap = Math.Max(leftGap, rightGap);
            maximumObservedGap = Math.Max(maximumObservedGap, gap);
            if (maximumAllowedGap <= 0 || gap <= maximumAllowedGap) validCount++;
            l[i] = Lerp(left.Time, left.Value, times[i]);
            r[i] = Lerp(right.Time, right.Value, times[i]);
        }

        var coverage = times.Length == 0 ? 0 : validCount * 100d / times.Length;
        var overlapNanoseconds = checked((long)Math.Round((end - start) * 1_000_000_000d, MidpointRounding.AwayFromZero));
        if (maximumAllowedGap > 0 && maximumObservedGap > maximumAllowedGap)
        {
            var report = new AlignmentReport(
                AnalysisStatus.GapExceeded, sourceCount, validCount, overlapNanoseconds, coverage,
                maximumObservedGap, duplicateCount,
                $"Maximale sampleafstand {maximumObservedGap:G6}s overschrijdt 5× de mediane interval van de traagste reeks ({maximumAllowedGap:G6}s)." );
            return new AlignedData([], [], [], report);
        }

        return new AlignedData(times, l, r, new AlignmentReport(
            AnalysisStatus.Valid, sourceCount, times.Length, overlapNanoseconds, coverage,
            maximumObservedGap, duplicateCount, "Lineaire interpolatie binnen uitsluitend de gemeenschappelijke tijdsperiode."));
    }

    private static double BracketDistance(double[] times, double target)
    {
        var upper = UpperBound(times, target);
        if (upper > 0 && times[upper - 1] == target) return 0;
        if (upper <= 0 || upper >= times.Length) return 0;
        return times[upper] - times[upper - 1];
    }

    private static int CountDuplicateIntervals(double[] times)
    {
        var count = 0;
        for (var i = 1; i < times.Length; i++) if (times[i] == times[i - 1]) count++;
        return count;
    }

    private static IReadOnlyList<NormalizedPoint> ToPoints(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        var n = Math.Min(x.Count, y.Count);
        var p = new List<NormalizedPoint>(n);
        for (var i = 0; i < n; i++) p.Add(new NormalizedPoint(x[i], y[i]));
        return p;
    }

    private static IReadOnlyList<TimeValuePoint> ReduceTV(IReadOnlyList<double> t, IReadOnlyList<double> v, int maxPoints)
    {
        var n = Math.Min(t.Count, v.Count);
        if (n == 0) return [];
        var m = Math.Clamp(maxPoints, 100, 100000);
        if (n <= m) return t.Zip(v, static (a, b) => new TimeValuePoint(a, b)).ToList();
        var step = (int)Math.Ceiling(n / (double)m);
        var outp = new List<TimeValuePoint>(m + 2);
        for (var i = 0; i < n; i += step) outp.Add(new TimeValuePoint(t[i], v[i]));
        var last = new TimeValuePoint(t[n - 1], v[n - 1]);
        if (outp.Count == 0 || outp[^1] != last) outp.Add(last);
        return outp;
    }

    private static double Corr(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        var n = Math.Min(a.Count, b.Count);
        if (n <= 1) return 0;
        var ma = a.Take(n).Average();
        var mb = b.Take(n).Average();
        var cov = 0.0;
        var sa = 0.0;
        var sb = 0.0;
        for (var i = 0; i < n; i++)
        {
            var da = a[i] - ma;
            var db = b[i] - mb;
            cov += da * db;
            sa += da * da;
            sb += db * db;
        }

        var d = Math.Sqrt(sa * sb);
        return d <= 1e-12 ? 0 : cov / d;
    }

    private static double CorrAt(IReadOnlyList<double> a, IReadOnlyList<double> b, int k, out int samples)
    {
        var n = Math.Min(a.Count, b.Count);
        var ia = k < 0 ? -k : 0;
        var ib = k > 0 ? k : 0;
        samples = n - Math.Abs(k);
        if (samples <= 1) return 0;
        var sa = 0.0;
        var sb = 0.0;
        var sab = 0.0;
        var sa2 = 0.0;
        var sb2 = 0.0;
        for (var i = 0; i < samples; i++)
        {
            var va = a[ia + i];
            var vb = b[ib + i];
            sa += va; sb += vb; sab += va * vb; sa2 += va * va; sb2 += vb * vb;
        }

        var ma = sa / samples;
        var mb = sb / samples;
        var cov = (sab / samples) - (ma * mb);
        var va2 = (sa2 / samples) - (ma * ma);
        var vb2 = (sb2 / samples) - (mb * mb);
        var den = Math.Sqrt(Math.Max(0, va2 * vb2));
        return den <= 1e-12 ? 0 : cov / den;
    }

    private static double Lerp(double[] t, double[] v, double x)
    {
        if (t.Length == 0 || v.Length == 0) return 0;
        if (x <= t[0]) return v[0];
        if (x >= t[^1]) return v[^1];
        var up = UpperBound(t, x);
        if (up <= 0) return v[0];
        if (up >= t.Length) return v[^1];
        var i0 = up - 1;
        var i1 = up;
        var dt = t[i1] - t[i0];
        if (Math.Abs(dt) <= 1e-12) return v[i1];
        var p = (x - t[i0]) / dt;
        return v[i0] + ((v[i1] - v[i0]) * p);
    }

    private static double? DelayByThreshold(IReadOnlyList<double> t, IReadOnlyList<double> c, IReadOnlyList<double> r, double fraction)
    {
        var f = Math.Clamp(fraction, 0.01, 0.99);
        var tc = Crossing(t, c, c.Min() + ((c.Max() - c.Min()) * f));
        var tr = Crossing(t, r, r.Min() + ((r.Max() - r.Min()) * f));
        if (!tc.HasValue || !tr.HasValue) return null;
        return tr.Value - tc.Value;
    }

    private static double? Crossing(IReadOnlyList<double> t, IReadOnlyList<double> v, double th)
    {
        var n = Math.Min(t.Count, v.Count);
        for (var i = 1; i < n; i++)
        {
            var v0 = v[i - 1];
            var v1 = v[i];
            if (v0 >= th || v1 < th) continue;
            var dv = v1 - v0;
            if (Math.Abs(dv) <= 1e-12) return t[i];
            var p = (th - v0) / dv;
            return t[i - 1] + ((t[i] - t[i - 1]) * p);
        }

        return null;
    }

    private static int Nearest(double[] v, double x)
    {
        if (v.Length == 0) return -1;
        var lo = 0;
        var hi = v.Length;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (v[mid] <= x) lo = mid + 1; else hi = mid;
        }

        if (lo <= 0) return 0;
        if (lo >= v.Length) return v.Length - 1;
        return Math.Abs(v[lo - 1] - x) <= Math.Abs(v[lo] - x) ? lo - 1 : lo;
    }

    private static int UpperBound(double[] values, double threshold)
    {
        var lo = 0;
        var hi = values.Length;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (values[mid] <= threshold) lo = mid + 1; else hi = mid;
        }

        return lo;
    }

    private static IReadOnlyList<HistogramBin> Histogram(IReadOnlyList<double> values, int bins, double low, double high)
    {
        if (values.Count == 0) return [];
        var n = Math.Clamp(bins, 8, 200);
        var l = Math.Min(low, values.Min());
        var h = Math.Max(high, values.Max());
        if (double.IsNaN(l) || double.IsInfinity(l) || double.IsNaN(h) || double.IsInfinity(h) || h <= l)
        {
            l = values.Min();
            h = values.Max();
            if (h <= l) h = l + 1.0;
        }

        var w = (h - l) / n;
        if (w <= 0 || double.IsNaN(w) || double.IsInfinity(w)) w = 1.0;
        var c = new int[n];
        foreach (var value in values)
        {
            var idx = (int)((value - l) / w);
            if (idx < 0) idx = 0;
            else if (idx >= n) idx = n - 1;
            c[idx]++;
        }

        var outp = new List<HistogramBin>(n);
        for (var i = 0; i < n; i++)
        {
            var s = l + (i * w);
            outp.Add(new HistogramBin(s, s + w, c[i]));
        }

        return outp;
    }

    private static double[] Deltas(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return [];
        var d = new double[values.Count - 1];
        for (var i = 1; i < values.Count; i++) d[i - 1] = values[i] - values[i - 1];
        return d;
    }

    private static double[] Slopes(double[] t, IReadOnlyList<double> v)
    {
        if (v.Count < 2 || t.Length < 2) return [];
        var n = Math.Min(v.Count, t.Length) - 1;
        var s = new List<double>(n);
        for (var i = 1; i <= n; i++)
        {
            var dt = t[i] - t[i - 1];
            if (dt > 0) s.Add((v[i] - v[i - 1]) / dt);
        }
        return s.ToArray();
    }

    private static double Std(IReadOnlyList<double> values, double mean)
    {
        if (values.Count <= 1) return 0;
        var sum = 0.0;
        for (var i = 0; i < values.Count; i++) { var d = values[i] - mean; sum += d * d; }
        return Math.Sqrt(sum / values.Count);
    }

    private static double MaximumPositiveInterval(double[] times)
    {
        var maximum = 0d;
        for (var i = 1; i < times.Length; i++)
        {
            var interval = times[i] - times[i - 1];
            if (interval > maximum) maximum = interval;
        }

        return maximum;
    }

    private static double Percentile(IReadOnlyList<double> sorted, double q)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];
        var f = Math.Clamp(q, 0, 1);
        var pos = f * (sorted.Count - 1);
        var i0 = (int)Math.Floor(pos);
        var i1 = (int)Math.Ceiling(pos);
        if (i0 == i1) return sorted[i0];
        var p = pos - i0;
        return sorted[i0] + ((sorted[i1] - sorted[i0]) * p);
    }

    private static IReadOnlyList<NormalizedPoint> Reduce(IReadOnlyList<NormalizedPoint> source, int maxPoints)
    {
        var m = Math.Clamp(maxPoints, 100, 100000);
        if (source.Count <= m) return source.ToList();
        var step = (int)Math.Ceiling(source.Count / (double)m);
        var outp = new List<NormalizedPoint>(m + 2);
        for (var i = 0; i < source.Count; i += step) outp.Add(source[i]);
        if (outp.Count == 0 || outp[^1] != source[^1]) outp.Add(source[^1]);
        return outp;
    }

    private static DelayAnalysisResult CreateEmptyDelayResult(string commandLabel, string responseLabel, double searchRangeSeconds)
    {
        var emptyDelay = new DelayEventStatistics(0, null, null, null, null);
        return new DelayAnalysisResult(
            commandLabel,
            responseLabel,
            0,
            Math.Max(0.01, searchRangeSeconds),
            0,
            0,
            0,
            0,
            0,
            null,
            0,
            null,
            null,
            null,
            null,
            emptyDelay,
            emptyDelay,
            [],
            [],
            [],
            [],
            []);
    }

    private sealed record AlignedData(double[] Time, double[] Left, double[] Right, AlignmentReport Report);
    private sealed record PathData(double[] Time, double[] X, double[] Y, double CenterX, double CenterY, double Scale);
    private sealed record DelayMatchEvent(double DelaySeconds, int CommandDirection);
    private sealed record EdgeEvent(int Index, double Time, double Value, int Direction, double Magnitude);
}
