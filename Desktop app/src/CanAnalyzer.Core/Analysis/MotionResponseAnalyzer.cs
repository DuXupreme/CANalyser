using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Analysis;

/// <summary>
/// Detects meaningful command episodes and measures sustained feedback departures on source
/// samples. Continuous movement already in the requested direction is deliberately not timed.
/// </summary>
public static class MotionResponseAnalyzer
{
    public static MotionResponseResult Analyze(
        SignalSeries command,
        SignalSeries response,
        MotionResponseOptions? options = null,
        IReadOnlyList<MeasurementGap>? gaps = null, CancellationToken cancellationToken = default,
        double? startSeconds = null, double? endSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(response);
        options ??= new MotionResponseOptions();
        cancellationToken.ThrowIfCancellationRequested();
        command.GetData(cancellationToken); response.GetData(cancellationToken);
        if (startSeconds.HasValue && !double.IsFinite(startSeconds.Value) || endSeconds.HasValue && !double.IsFinite(endSeconds.Value) ||
            startSeconds.HasValue && endSeconds.HasValue && endSeconds <= startSeconds)
            throw new ArgumentException("Het reactietijdvenster moet eindig zijn en een positief bereik hebben.");
        // Keep prehistory and original source indices, but never confirm a response after the requested end.
        command = Through(command, endSeconds); response = Through(response, endSeconds);
        Validate(command, cancellationToken);
        Validate(response, cancellationToken);
        if (!double.IsFinite(options.MaxDelaySeconds) || options.MaxDelaySeconds <= 0 ||
            !double.IsFinite(options.MinimumQuietSeconds) || options.MinimumQuietSeconds <= 0 ||
            !double.IsFinite(options.SustainedSeconds) || options.SustainedSeconds < 0 ||
            options.ResponseThresholdAbsolute is { } absolute && (!double.IsFinite(absolute) || absolute <= 0) ||
            options.PositionToleranceAbsolute is { } tolerance && (!double.IsFinite(tolerance) || tolerance <= 0) ||
            !Enum.IsDefined(options.Reference))
            throw new ArgumentException("Reactietijdinstellingen moeten eindig en positief zijn; bevestigingsduur mag nul zijn.", nameof(options));

        var commandDt = MedianInterval(command.Time);
        var responseDt = MedianInterval(response.Time);
        var commandThreshold = AutomaticThreshold(command, commandDt, commandSignal: true, cancellationToken);
        var automaticResponseThreshold = AutomaticThreshold(response, responseDt, commandSignal: false, cancellationToken);
        var responseThreshold = options.ResponseThresholdAbsolute ?? automaticResponseThreshold;
        var allGaps = MergeGaps(FindGaps(command.Time, commandDt).Concat(FindGaps(response.Time, responseDt)).Concat(gaps ?? []));
        double? positionTolerance = options.Reference == MotionResponseReference.PositionRequest
            ? options.PositionToleranceAbsolute ?? Math.Max(commandThreshold, AutomaticThreshold(response, responseDt, commandSignal: true, cancellationToken))
            : null;
        var commandEpisodes = DetectCommands(command, commandThreshold, commandDt, options, cancellationToken);
        var candidates = options.Reference == MotionResponseReference.PositionRequest
            ? DetectPositionRequests(command, response, commandEpisodes, positionTolerance!.Value, commandDt, responseDt, allGaps, options, cancellationToken)
            : commandEpisodes;
        var events = new List<MotionResponseEvent>(candidates.Count);
        var nextRequestTime = new double[candidates.Count];
        var nextTime = double.PositiveInfinity;
        for (var i = candidates.Count - 1; i >= 0; i--)
        {
            nextRequestTime[i] = nextTime;
            if (candidates[i].ForcedOutcome is null) nextTime = command.Time[candidates[i].Index];
        }
        for (var i = 0; i < candidates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = candidates[i];
            if (startSeconds.HasValue && command.Time[candidate.Index] < startSeconds.Value) continue;
            var measurement = candidate.ForcedOutcome is { } excluded
                ? ExcludedPositionRequest(command, response, candidate, excluded, responseThreshold)
                : Measure(command, response, candidate, Math.Min(nextRequestTime[i], candidate.StopTime ?? double.PositiveInfinity), responseThreshold, automaticResponseThreshold, responseDt,
                    allGaps, options);
            var origin = options.Reference == MotionResponseReference.SetpointChange ? candidate.Index : candidate.OriginIndex;
            events.Add(measurement with
            {
                OriginCommandSourceIndex = origin,
                OriginCommandTimeSeconds = origin is { } index ? command.Time[index] : null
            });
        }

        return new MotionResponseResult(command.Label, response.Label, events,
            Statistics(events.Where(e => e.Kind == MotionResponseKind.Start)),
            Statistics(events.Where(e => e.Kind == MotionResponseKind.Correction)),
            Statistics(events.Where(e => e.Kind == MotionResponseKind.Reversal)),
            Statistics(events), commandThreshold, responseThreshold,
            options.ResponseThresholdAbsolute is null, commandDt, responseDt, Math.Max(commandDt, responseDt))
        { Reference = options.Reference, PositionTolerance = positionTolerance };
    }

    private static List<CommandCandidate> DetectPositionRequests(
        SignalSeries command, SignalSeries response, IReadOnlyList<CommandCandidate> episodes,
        double tolerance, double commandDt, double responseDt, IReadOnlyList<MeasurementGap> gaps,
        MotionResponseOptions options, CancellationToken cancellationToken)
    {
        var requests = new List<CommandCandidate>();
        if (command.Time.Length < 2 || response.Time.Length == 0) return requests;
        var linkedOrigins = new HashSet<int>();
        var requestedDirection = 0;
        var episodeCursor = -1;
        var responseCursor = -1;
        var segmentStart = command.Time[0];
        var activeRequest = -1;
        var lastCommandChangeTime = command.Time[0];
        var lastQuietTime = double.NegativeInfinity;
        var commandNumericFloor = Math.Max(1e-12, command.Value.Max(v => Math.Abs(v)) * 1e-12);
        for (var i = 1; i < command.Time.Length; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var time = command.Time[i];
            while (episodeCursor + 1 < episodes.Count && episodes[episodeCursor + 1].Index <= i) episodeCursor++;
            // Same-timestamp feedback may already contain the response to this command.
            // Use strictly preceding source feedback for both the before/after comparisons.
            while (responseCursor + 1 < response.Time.Length && response.Time[responseCursor + 1] < time) responseCursor++;
            if (responseCursor < 0) continue;
            var position = response.Value[responseCursor];
            var previousCommand = command.Value[i - 1];
            var newCommand = command.Value[i];
            var commandChanged = Math.Abs(newCommand - previousCommand) > commandNumericFloor;
            if (commandChanged) lastCommandChangeTime = time;
            else if (time - lastCommandChangeTime >= options.MinimumQuietSeconds - 1e-9) lastQuietTime = time;
            var missingHistory = commandDt > 0 && time - command.Time[i - 1] > commandDt * 5 + 1e-9 ||
                responseDt > 0 && time - response.Time[responseCursor] > responseDt * 5 + 1e-9 ||
                CrossesGap(gaps, Math.Min(command.Time[i - 1], response.Time[responseCursor]), time);
            if (missingHistory)
            {
                requestedDirection = RequestDirection(newCommand - position, 0, tolerance);
                segmentStart = time;
                continue;
            }

            // Feedback may change/release an outstanding request, but that transition alone
            // never emits a command event. Only an updated target can create the new request.
            var beforeDirection = RequestDirection(previousCommand - position, requestedDirection, tolerance);
            var afterDirection = RequestDirection(newCommand - position, beforeDirection, tolerance);
            requestedDirection = afterDirection;
            if (commandChanged && activeRequest >= 0 && requests[activeRequest].Direction != afterDirection)
            {
                // A withdrawn or opposite demand ends attribution even if it does not create
                // another timed request. Same-direction reductions/extensions keep this clock.
                requests[activeRequest] = requests[activeRequest] with { StopTime = time };
                activeRequest = -1;
            }
            if (!commandChanged || afterDirection == 0 || afterDirection == beforeDirection) continue;

            var relatedEpisode = episodeCursor >= 0 ? episodes[episodeCursor] : null;
            var origin = relatedEpisode is not null && relatedEpisode.Direction == afterDirection &&
                command.Time[relatedEpisode.Index] >= lastQuietTime - 1e-9 &&
                !CrossesGap(gaps, command.Time[relatedEpisode.Index], time)
                ? relatedEpisode.Index : (int?)null;
            var kind = beforeDirection != 0 && afterDirection == -beforeDirection
                ? MotionResponseKind.Reversal : MotionResponseKind.Start;
            var outcome = ConfirmPositionRequest(command, i, position, afterDirection, tolerance, commandDt, options);
            requests.Add(new CommandCandidate(i, afterDirection, kind,
                time - segmentStart >= options.MinimumQuietSeconds - 1e-9)
            { OriginIndex = origin, ForcedOutcome = outcome });
            if (outcome is null) activeRequest = requests.Count - 1;
            if (origin is { } linked) linkedOrigins.Add(linked);
        }

        foreach (var episode in episodes)
        {
            if (linkedOrigins.Contains(episode.Index)) continue;
            var time = command.Time[episode.Index];
            var feedback = LowerBound(response.Time, time) - 1;
            var outcome = feedback < 0 ? MotionResponseOutcome.NoPrehistory :
                CrossesGap(gaps, Math.Min(command.Time[episode.Index - 1], response.Time[Math.Max(0, feedback)]), time) ||
                responseDt > 0 && time - response.Time[Math.Max(0, feedback)] > responseDt * 5 + 1e-9
                    ? MotionResponseOutcome.MeasurementGap : MotionResponseOutcome.NoNewDirectionRequest;
            var direction = feedback < 0 ? 0 : RequestDirection(command.Value[episode.Index] - response.Value[feedback], 0, tolerance);
            requests.Add(episode with { Direction = direction, Kind = MotionResponseKind.Correction, OriginIndex = episode.Index, ForcedOutcome = outcome });
        }
        requests.Sort(static (left, right) => left.Index.CompareTo(right.Index));
        return requests;
    }

    private static int RequestDirection(double error, int previousDirection, double tolerance)
    {
        var epsilon = Math.Max(1e-12, tolerance * 1e-12);
        if (error >= tolerance - epsilon) return 1;
        if (error <= -tolerance + epsilon) return -1;
        if (previousDirection == 1 && error > tolerance * .5) return 1;
        if (previousDirection == -1 && error < -tolerance * .5) return -1;
        return 0;
    }

    private static MotionResponseOutcome? ConfirmPositionRequest(SignalSeries command, int index, double position,
        int direction, double tolerance, double dt, MotionResponseOptions options)
    {
        var required = Math.Max(options.SustainedSeconds, dt);
        for (var j = index + 1; j < command.Time.Length; j++)
        {
            if (dt > 0 && command.Time[j] - command.Time[j - 1] > dt * 5 + 1e-9)
                return MotionResponseOutcome.MeasurementGap;
            // Keep the original pre-command position fixed: a fast genuine response reaching
            // its target must not cancel evidence that the command requested movement.
            if (direction * (command.Value[j] - position) < tolerance * .5)
                return MotionResponseOutcome.NoNewDirectionRequest;
            if (command.Time[j] - command.Time[index] + 1e-9 >= required) return null;
        }
        return MotionResponseOutcome.InsufficientObservation;
    }

    private static MotionResponseEvent ExcludedPositionRequest(SignalSeries command, SignalSeries response,
        CommandCandidate candidate, MotionResponseOutcome outcome, double responseThreshold)
    {
        var time = command.Time[candidate.Index];
        var feedbackIndex = LowerBound(response.Time, time) - 1;
        return new MotionResponseEvent(candidate.Index, time, command.Time[candidate.Index - 1], candidate.Direction,
            candidate.Kind, outcome, null, null, null, null, null, time, responseThreshold,
            feedbackIndex >= 0 ? response.Value[feedbackIndex] : null, null);
    }

    private static MotionResponseEvent Measure(
        SignalSeries command, SignalSeries response, CommandCandidate candidate, double nextCommand,
        double threshold, double restingThreshold, double responseDt, IReadOnlyList<MeasurementGap> allGaps,
        MotionResponseOptions options)
    {
        var edgeTime = command.Time[candidate.Index];
        var previousTime = command.Time[candidate.Index - 1];
        var kind = candidate.Kind;
        var deadline = edgeTime + options.MaxDelaySeconds;
        var searchEnd = Math.Min(deadline, nextCommand);
        double? baseline = null;
        double? target = null;

        MotionResponseEvent Excluded(MotionResponseOutcome outcome) => new(candidate.Index, edgeTime, previousTime,
            candidate.Direction, kind, outcome, null, null, null, null, null,
            searchEnd, threshold, baseline, target);

        // A slow logger needs a longer history to supply at least three pre-command samples.
        // Requiring three samples in a fixed 100 ms window would reject valid 20/10 Hz feedback.
        var historyStart = edgeTime - Math.Max(options.MinimumQuietSeconds, responseDt * 3);
        if (CrossesGap(allGaps, historyStart, edgeTime))
            return Excluded(MotionResponseOutcome.MeasurementGap);
        var firstHistory = LowerBound(response.Time, historyStart);
        var firstAfter = LowerBound(response.Time, edgeTime);
        var historyCount = firstAfter - firstHistory;
        if (!candidate.HasPrehistory || response.Time.Length == 0 || command.Time[0] > historyStart + 1e-9 ||
            response.Time[0] > historyStart + 1e-9 || historyCount < 3)
            return Excluded(MotionResponseOutcome.NoPrehistory);

        var history = response.Value.AsSpan(firstHistory, historyCount).ToArray();
        var middle = history.Length / 2;
        var before = Median(history.Take(middle));
        var after = Median(history.Skip(middle));
        var medianTrend = after - before;
        var trend = medianTrend;
        var recentDeparture = history[^1] - before;
        if (Math.Abs(trend) < restingThreshold * 0.75 && Math.Abs(recentDeparture) >= restingThreshold * 0.75)
            trend = recentDeparture;
        var alreadyMoving = Math.Abs(trend) >= restingThreshold * 0.75 || history.Max() - history.Min() > restingThreshold * 2;
        // The most recent pre-command sample is the actual starting level. A median alone
        // can lag a feedback step just before the command and fabricate a zero-delay reaction.
        baseline = history[^1];
        target = baseline + candidate.Direction * threshold;
        if (alreadyMoving)
        {
            // A continuing response cannot identify the delay of a speed/amplitude correction.
            // An opposite pre-existing direction permits a separately reported reversal measurement.
            if (Math.Abs(trend) < restingThreshold * 0.75 || Math.Sign(trend) == candidate.Direction ||
                Math.Abs(medianTrend) < restingThreshold * 0.75)
            {
                // A latest-sample departure with otherwise quiet history cannot establish an
                // opposite movement. Its return may be sensor noise, not a commanded reversal.
                if (kind == MotionResponseKind.Start) kind = MotionResponseKind.Correction;
                return Excluded(MotionResponseOutcome.AlreadyMoving);
            }
            kind = MotionResponseKind.Reversal;
            baseline = response.Value[firstAfter - 1];
            target = baseline + candidate.Direction * threshold;
        }

        var confirmationSeconds = Math.Max(options.SustainedSeconds, responseDt);
        // A reversed command alone does not prove that feedback was moving in the old
        // direction. Both time references require observed motion before moving the baseline.
        var followTurningPoint = alreadyMoving;
        var lackedConfirmation = false;
        for (var j = firstAfter; j < response.Time.Length; j++)
        {
            var time = response.Time[j];
            if (time >= nextCommand || time > deadline + 1e-9 || time > command.Time[^1] + 1e-9) break;
            if (CrossesGap(allGaps, edgeTime, time))
                return Excluded(MotionResponseOutcome.MeasurementGap);
            var value = response.Value[j];
            if (!followTurningPoint &&
                candidate.Direction * (value - baseline!.Value) <= -restingThreshold + 1e-12 &&
                SustainedOppositeExcursion(response, j, baseline.Value, candidate.Direction, restingThreshold,
                    confirmationSeconds, Math.Min(nextCommand, Math.Min(deadline, command.Time[^1])), allGaps))
            {
                // A cancelled earlier request may start moving only after this new request.
                // Follow that confirmed old-direction excursion to its turning point. A lone
                // opposite sample must not move the baseline and fabricate a reversal on return.
                kind = MotionResponseKind.Reversal;
                followTurningPoint = true;
            }
            if (followTurningPoint && candidate.Direction * (value - baseline!.Value) < 0)
            {
                // Follow the old direction to its turning point, then measure departure from it.
                baseline = value;
                target = baseline + candidate.Direction * threshold;
            }
            if (candidate.Direction * (value - baseline!.Value) + 1e-12 < threshold) continue;

            var confirmed = false;
            var returnedBelowThreshold = false;
            for (var k = j + 1; k < response.Time.Length; k++)
            {
                var confirmationTime = response.Time[k];
                if (confirmationTime >= nextCommand || confirmationTime > deadline + 1e-9 || confirmationTime > command.Time[^1] + 1e-9)
                { lackedConfirmation = true; break; }
                if (CrossesGap(allGaps, edgeTime, confirmationTime))
                    return Excluded(MotionResponseOutcome.MeasurementGap);
                if (candidate.Direction * (response.Value[k] - baseline.Value) < threshold * 0.8)
                { returnedBelowThreshold = true; break; }
                if (confirmationTime - time + 1e-9 < confirmationSeconds) continue;
                confirmed = true;
                break;
            }
            if (!confirmed) { lackedConfirmation |= !returnedBelowThreshold; continue; }

            var responsePrevious = j > 0 ? response.Time[j - 1] : time;
            return new MotionResponseEvent(candidate.Index, edgeTime, previousTime, candidate.Direction,
                kind, MotionResponseOutcome.Accepted, j, time, time - edgeTime,
                Math.Max(0, responsePrevious - edgeTime), Math.Max(0, time - previousTime),
                searchEnd, threshold, baseline, target);
        }

        var observedEnd = response.Time.Length == 0 ? edgeTime : Math.Min(response.Time[^1], command.Time[^1]);
        if (CrossesGap(allGaps, edgeTime, Math.Min(searchEnd, observedEnd)))
            return Excluded(MotionResponseOutcome.MeasurementGap);
        if (nextCommand <= deadline && nextCommand <= observedEnd)
            return Excluded(MotionResponseOutcome.InterruptedByCommand);
        if (observedEnd + 1e-9 < deadline || lackedConfirmation)
            return Excluded(MotionResponseOutcome.InsufficientObservation);
        return Excluded(MotionResponseOutcome.NoResponseWithinWindow);
    }

    private static bool SustainedOppositeExcursion(SignalSeries response, int index, double baseline,
        int requestedDirection, double threshold, double confirmationSeconds, double end,
        IReadOnlyList<MeasurementGap> gaps)
    {
        var start = response.Time[index];
        for (var i = index + 1; i < response.Time.Length; i++)
        {
            if (response.Time[i] >= end || CrossesGap(gaps, start, response.Time[i])) return false;
            if (-requestedDirection * (response.Value[i] - baseline) < threshold * .8) return false;
            if (response.Time[i] - start + 1e-9 >= confirmationSeconds) return true;
        }
        return false;
    }

    private static List<CommandCandidate> DetectCommands(SignalSeries command, double threshold, double dt, MotionResponseOptions options,
        CancellationToken cancellationToken)
    {
        var result = new List<CommandCandidate>();
        if (command.Time.Length < 2) return result;
        var direction = 0;
        var anchor = command.Value[0];
        var extreme = anchor;
        var lastMotionValue = anchor;
        var lastMotionTime = command.Time[0];
        var segmentStart = command.Time[0];
        var lastEventTime = double.NegativeInfinity;
        var lastMotionIncrement = 0d;
        var previousEventDirection = 0;
        var lastAdjacentChange = -1;
        var previousAdjacentChange = -1;
        for (var i = 1; i < command.Time.Length; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var time = command.Time[i];
            var value = command.Value[i];
            if (dt > 0 && time - command.Time[i - 1] > dt * 5 + 1e-9)
            {
                direction = 0; anchor = extreme = lastMotionValue = value;
                segmentStart = lastMotionTime = time; lastMotionIncrement = 0; previousEventDirection = 0;
                lastAdjacentChange = previousAdjacentChange = -1;
                continue;
            }
            if (direction != 0 && time - lastMotionTime >= options.MinimumQuietSeconds && Math.Abs(value - lastMotionValue) < threshold &&
                direction * (value - extreme) <= 0)
            {
                direction = 0;
                anchor = lastMotionValue;
            }

            var candidateDirection = 0;
            var kind = MotionResponseKind.Start;
            var candidateBaseline = anchor;
            if (direction == 0)
            {
                var departure = value - anchor;
                if (Math.Abs(departure) + 1e-12 >= threshold) candidateDirection = Math.Sign(departure);
            }
            else if (direction * (value - extreme) <= -threshold)
            {
                candidateDirection = -direction;
                candidateBaseline = extreme;
                kind = MotionResponseKind.Reversal;
            }
            else
            {
                var delta = value - command.Value[i - 1];
                var followsPlateau = i >= 2 && Math.Abs(command.Value[i - 1] - command.Value[i - 2]) < threshold * .2;
                var regularRamp = followsPlateau && IsRegularQuantizedRamp(command, i, lastAdjacentChange,
                    previousAdjacentChange, threshold, dt);
                if (Math.Sign(delta) == direction && Math.Abs(delta) >= threshold * 1.5 &&
                    (Math.Abs(delta) > lastMotionIncrement * 3 || followsPlateau && !regularRamp) &&
                    time - lastEventTime >= Math.Max(options.SustainedSeconds, dt) - 1e-9)
                {
                    candidateDirection = direction;
                    candidateBaseline = command.Value[i - 1];
                    kind = MotionResponseKind.Correction;
                }
            }

            if (candidateDirection != 0 && CommandPersists(command, i, candidateBaseline, candidateDirection, threshold, dt, options.SustainedSeconds))
            {
                // A slow noisy drift must not become a new start every time it accumulates
                // another deadband. Without a distinct renewed command its onset is ambiguous.
                var gentleContinuation = kind == MotionResponseKind.Start && previousEventDirection == candidateDirection &&
                    Math.Abs(value - command.Value[i - 1]) < threshold * 0.5;
                if (!gentleContinuation)
                {
                    result.Add(new CommandCandidate(i, candidateDirection, kind, time - segmentStart >= options.MinimumQuietSeconds - 1e-9));
                    previousEventDirection = candidateDirection;
                }
                direction = candidateDirection;
                extreme = lastMotionValue = value;
                lastMotionTime = lastEventTime = time;
                lastMotionIncrement = Math.Abs(value - command.Value[i - 1]);
            }
            if (Math.Abs(value - command.Value[i - 1]) >= threshold)
            {
                previousAdjacentChange = lastAdjacentChange;
                lastAdjacentChange = i;
            }
            if (direction == 0) continue;
            if (direction * (value - extreme) > 0)
            {
                extreme = value;
                lastMotionTime = time;
            }
            if (Math.Abs(value - lastMotionValue) >= threshold)
            {
                lastMotionIncrement = Math.Abs(value - lastMotionValue);
                lastMotionValue = value;
                lastMotionTime = time;
            }
        }
        return result;
    }

    private static bool IsRegularQuantizedRamp(SignalSeries series, int index, int previous, int beforePrevious, double threshold, double dt)
    {
        if (previous < 1) return false;
        var currentDelta = series.Value[index] - series.Value[index - 1];
        var previousDelta = series.Value[previous] - series.Value[previous - 1];
        var interval = series.Time[index] - series.Time[previous];
        bool Similar(double delta, double gap) => Math.Sign(delta) == Math.Sign(currentDelta) &&
            Math.Abs(delta - currentDelta) <= Math.Max(threshold, Math.Abs(currentDelta) * .2) &&
            Math.Abs(gap - interval) <= Math.Max(dt * 1.1, interval * .2);
        if (!Similar(previousDelta, interval)) return false;
        if (beforePrevious >= 1 && Similar(series.Value[beforePrevious] - series.Value[beforePrevious - 1],
            series.Time[previous] - series.Time[beforePrevious])) return true;
        // The second tick needs one look-ahead tick to distinguish a quantized ramp from
        // a separate renewed command. Later ticks are recognized from their source history.
        for (var j = index + 1; j < series.Time.Length && series.Time[j] - series.Time[index] <= interval + Math.Max(dt * 1.1, interval * .2); j++)
        {
            var delta = series.Value[j] - series.Value[j - 1];
            if (Math.Abs(delta) < threshold) continue;
            return Similar(delta, series.Time[j] - series.Time[index]);
        }
        return false;
    }

    private static bool CommandPersists(SignalSeries series, int index, double baseline, int direction, double threshold, double dt, double sustainedSeconds)
    {
        var required = Math.Max(sustainedSeconds, dt);
        for (var j = index + 1; j < series.Time.Length; j++)
        {
            if (dt > 0 && series.Time[j] - series.Time[j - 1] > dt * 5 + 1e-9) return false;
            if (direction * (series.Value[j] - baseline) < threshold * 0.8) return false;
            if (series.Time[j] - series.Time[index] + 1e-9 >= required) return true;
        }
        // Retain an end-of-log command so its missing observation is visible to the user.
        return true;
    }

    private static double AutomaticThreshold(SignalSeries series, double dt, bool commandSignal, CancellationToken cancellationToken)
    {
        if (series.Value.Length == 0) return 1e-9;
        var numericFloor = Math.Max(1e-9, series.Value.Max(v => Math.Abs(v)) * 1e-10);
        var minimumIncrement = double.PositiveInfinity;
        var reversals = new List<double>();
        var validTriples = 0;
        for (var i = 1; i < series.Value.Length; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (dt > 0 && series.Time[i] - series.Time[i - 1] > dt * 5 + 1e-9) continue;
            var delta = series.Value[i] - series.Value[i - 1];
            if (Math.Abs(delta) > numericFloor) minimumIncrement = Math.Min(minimumIncrement, Math.Abs(delta));
            if (i < 2 || dt > 0 && series.Time[i - 1] - series.Time[i - 2] > dt * 5 + 1e-9) continue;
            validTriples++;
            var previousDelta = series.Value[i - 1] - series.Value[i - 2];
            if (delta * previousDelta < 0 && Math.Abs(delta) > numericFloor && Math.Abs(previousDelta) > numericFloor)
            {
                // Smooth real reversals have continuing direction on both sides of the turn.
                // They are not stationary noise, even when a log contains many small corrections.
                var smoothTurn = i >= 3 && i + 1 < series.Value.Length &&
                    Math.Sign(series.Value[i - 2] - series.Value[i - 3]) == Math.Sign(previousDelta) &&
                    Math.Sign(series.Value[i + 1] - series.Value[i]) == Math.Sign(delta);
                if (!smoothTurn) reversals.Add(Math.Abs(delta - previousDelta));
            }
        }
        // Monotonic quantized ramps are signal, not noise. Repeated alternating local changes
        // provide a conservative noise estimate; isolated turning points do not establish noise.
        var noiseSigma = reversals.Count >= Math.Max(3, validTriples * 0.05)
            ? Median(reversals) / (0.67448975 * Math.Sqrt(6)) : 0;
        var observedIncrement = double.IsFinite(minimumIncrement) ? minimumIncrement : 0;
        return Math.Max(numericFloor, Math.Max(noiseSigma * 5, observedIncrement * (commandSignal ? 0.5 : 1)));
    }

    private static IReadOnlyList<MeasurementGap> FindGaps(double[] times, double dt)
    {
        var result = new List<MeasurementGap>();
        if (dt <= 0) return result;
        for (var i = 1; i < times.Length; i++)
            if (times[i] - times[i - 1] > dt * 5 + 1e-9) result.Add(new(times[i - 1], times[i]));
        return result;
    }

    private static IReadOnlyList<MeasurementGap> MergeGaps(IEnumerable<MeasurementGap> gaps)
    {
        var merged = new List<MeasurementGap>();
        foreach (var gap in gaps.OrderBy(g => g.StartSeconds))
        {
            if (!double.IsFinite(gap.StartSeconds) || !double.IsFinite(gap.EndSeconds) || gap.EndSeconds < gap.StartSeconds)
                throw new ArgumentException("Een meetgat heeft ongeldige begin- of eindtijden.", nameof(gaps));
            if (merged.Count > 0 && gap.StartSeconds <= merged[^1].EndSeconds)
                merged[^1] = new(merged[^1].StartSeconds, Math.Max(merged[^1].EndSeconds, gap.EndSeconds));
            else merged.Add(gap);
        }
        return merged;
    }

    private static bool CrossesGap(IReadOnlyList<MeasurementGap> gaps, double start, double end)
    {
        if (end <= start) return false;
        var low = 0; var high = gaps.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (gaps[middle].EndSeconds <= start) low = middle + 1; else high = middle;
        }
        return low < gaps.Count && gaps[low].StartSeconds < end;
    }

    private static DelayEventStatistics Statistics(IEnumerable<MotionResponseEvent> events)
    {
        var values = events.Where(e => e.Outcome == MotionResponseOutcome.Accepted && e.DelaySeconds.HasValue)
            .Select(e => e.DelaySeconds!.Value).Order().ToArray();
        return values.Length == 0 ? new(0, null, null, null, null)
            : new(values.Length, values.Average(), values[0], values[^1], Quantile(values, 0.95));
    }

    private static double MedianInterval(double[] time) => time.Length < 2 ? 0 :
        Median(Enumerable.Range(1, time.Length - 1).Select(i => time[i] - time[i - 1]));

    private static double Median(IEnumerable<double> source)
    {
        var sorted = source.Order().ToArray();
        return sorted.Length == 0 ? 0 : Quantile(sorted, 0.5);
    }

    private static double Quantile(double[] sorted, double fraction)
    {
        var position = (sorted.Length - 1) * fraction;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static int LowerBound(double[] values, double target)
    {
        var low = 0; var high = values.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (values[middle] < target) low = middle + 1; else high = middle;
        }
        return low;
    }

    private static SignalSeries Through(SignalSeries series, double? endSeconds)
    {
        if (!endSeconds.HasValue || series.Time.Length == 0 || series.Time[^1] <= endSeconds.Value) return series;
        var index = LowerBound(series.Time, endSeconds.Value);
        while (index < series.Time.Length && series.Time[index] <= endSeconds.Value) index++;
        return new(series.Identity, series.TimestampNanoseconds[..index], series.Value[..index], series.Label);
    }

    private static void Validate(SignalSeries series, CancellationToken cancellationToken)
    {
        for (var i = 0; i < series.Time.Length; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (!double.IsFinite(series.Time[i])) throw DelayInputFailure.Create(DelayInputFailureReason.NonFiniteTime);
            if (!double.IsFinite(series.Value[i])) throw DelayInputFailure.Create(DelayInputFailureReason.NonFiniteValue);
            if (i > 0 && series.Time[i] == series.Time[i - 1]) throw DelayInputFailure.Create(DelayInputFailureReason.DuplicateTimestamp);
            if (i > 0 && series.Time[i] < series.Time[i - 1]) throw DelayInputFailure.Create(DelayInputFailureReason.NonMonotonicTimestamp);
        }
    }

    private sealed record CommandCandidate(int Index, int Direction, MotionResponseKind Kind, bool HasPrehistory)
    {
        public int? OriginIndex { get; init; }
        public MotionResponseOutcome? ForcedOutcome { get; init; }
        public double? StopTime { get; init; }
    }
}
