namespace Localization.Metrics;

public sealed class Phase4Metrics
{
    public Phase4Report ComputeAll(RunTruth runTruth, IReadOnlyList<WindowRecord> windowRecords, Phase4Options options)
    {
        ArgumentNullException.ThrowIfNull(runTruth);
        ArgumentNullException.ThrowIfNull(windowRecords);
        ArgumentNullException.ThrowIfNull(options);

        var ordered = windowRecords.OrderBy(w => w.WindowIndex).ThenBy(w => w.TStart).ToList();
        if (ordered.Count == 0)
        {
            return new Phase4Report
            {
                RunId = runTruth.RunId,
                WindowMetrics = Array.Empty<WindowMetricsRow>(),
                ErrorVsCounts = new ErrorCurveSummary(),
                ErrorVsTime = new ErrorCurveSummary(),
                ErrorVsDistanceRegion = Array.Empty<DistanceRegionSummary>(),
                MotionLatency = runTruth.ScenarioType == ScenarioType.Motion ? new MotionLatencyReport() : null,
                StaticStability = runTruth.ScenarioType == ScenarioType.Static ? new StaticStabilityReport() : null,
                MultiplicitySummary = null,
                Metadata = BuildMetadata(runTruth, options)
            };
        }

        var scaleToCm = ResolveScaleToCm(runTruth, options);
        var detectorOrigin = ResolveDetectorOrigin(runTruth, scaleToCm);
        var t0 = ordered[0].TStart;

        var perWindow = new List<WindowMetricsRow>(ordered.Count);
        double cumulativeCounts = 0.0;

        foreach (var record in ordered)
        {
            var totalCounts = record.TotalCounts ?? (record.ChannelCounts?.Sum() ?? 0);
            cumulativeCounts += totalCounts;
            var cumulativeTime = record.TEnd - t0;

            var truth = ResolveTruthForTime(runTruth, record.TStart, scaleToCm);
            var metrics = ComputeWindowMetrics(record, truth, detectorOrigin, cumulativeCounts, cumulativeTime, scaleToCm, options);
            perWindow.Add(metrics);
        }

        var errorVsCounts = BuildErrorCurve(
            perWindow,
            options.CountThresholds,
            row => row.CumulativeCounts);

        var timeThresholds = options.TimeThresholdsSeconds;

        var errorVsTime = BuildErrorCurve(
            perWindow,
            timeThresholds,
            row => row.CumulativeTimeSeconds);

        var distanceRegion = BuildDistanceRegionSummary(perWindow, options.FieldOfView);
        var motionLatency = runTruth.ScenarioType == ScenarioType.Motion
            ? BuildMotionLatency(runTruth, ordered, scaleToCm, options)
            : null;

        var stabilityReport = runTruth.ScenarioType == ScenarioType.Static
            ? BuildStaticStability(runTruth, ordered, scaleToCm, options)
            : null;

        var multiplicitySummary = BuildMultiplicitySummary(runTruth, ordered, options);

        return new Phase4Report
        {
            RunId = runTruth.RunId,
            WindowMetrics = perWindow,
            ErrorVsCounts = errorVsCounts,
            ErrorVsTime = errorVsTime,
            ErrorVsDistanceRegion = distanceRegion,
            MotionLatency = motionLatency,
            StaticStability = stabilityReport,
            MultiplicitySummary = multiplicitySummary,
            Metadata = BuildMetadata(runTruth, options)
        };
    }

    private static Phase4Metadata BuildMetadata(RunTruth runTruth, Phase4Options options)
    {
        return new Phase4Metadata
        {
            CoordinateFrame = runTruth.CoordinateFrame,
            CoordinateUnits = runTruth.CoordinateUnits,
            DetectorOrigin = runTruth.DetectorOrigin ?? new double[] { 0.0, 0.0, 0.0 },
            Options = options
        };
    }

    private static double ResolveScaleToCm(RunTruth runTruth, Phase4Options options)
    {
        if (options.CoordinateScaleToCmOverride.HasValue)
        {
            return options.CoordinateScaleToCmOverride.Value;
        }

        return runTruth.CoordinateUnits.ToLowerInvariant() switch
        {
            "cm" => 1.0,
            "m" => 100.0,
            "mm" => 0.1,
            _ => 1.0
        };
    }

    private static Vector3 ResolveDetectorOrigin(RunTruth runTruth, double scaleToCm)
    {
        if (runTruth.DetectorOrigin is { Length: >= 3 })
        {
            return Vector3.From(runTruth.DetectorOrigin, 0, scaleToCm);
        }

        return new Vector3(0.0, 0.0, 0.0);
    }

    private static TruthState ResolveTruthForTime(RunTruth runTruth, double tStart, double scaleToCm)
    {
        if (runTruth.ScenarioType != ScenarioType.Motion || runTruth.ChangePoints.Count == 0)
        {
            return new TruthState(runTruth.TrueLabel, ScaleCoords(runTruth.CoordsTrue, scaleToCm));
        }

        MotionChangePoint? selected = null;
        foreach (var change in runTruth.ChangePoints.OrderBy(cp => cp.TimeSeconds))
        {
            if (tStart >= change.TimeSeconds)
            {
                selected = change;
            }
        }

        if (selected is null)
        {
            if (runTruth.CoordsTrue is { Length: > 0 })
            {
                return new TruthState(runTruth.TrueLabel, ScaleCoords(runTruth.CoordsTrue, scaleToCm));
            }

            var first = runTruth.ChangePoints.OrderBy(cp => cp.TimeSeconds).First();
            var before = first.CoordsTrueBefore ?? first.CoordsTrueAfter;
            return new TruthState(runTruth.TrueLabel, ScaleCoords(before, scaleToCm));
        }

        return new TruthState(selected.LabelAfter, ScaleCoords(selected.CoordsTrueAfter, scaleToCm));
    }

    private static double[] ScaleCoords(double[]? coords, double scale)
    {
        if (coords is null)
        {
            return Array.Empty<double>();
        }

        if (Math.Abs(scale - 1.0) < 1e-9)
        {
            return coords.ToArray();
        }

        var scaled = new double[coords.Length];
        for (var i = 0; i < coords.Length; i++)
        {
            scaled[i] = coords[i] * scale;
        }

        return scaled;
    }

    private static WindowMetricsRow ComputeWindowMetrics(
        WindowRecord record,
        TruthState truth,
        Vector3 detectorOrigin,
        double cumulativeCounts,
        double cumulativeTime,
        double scaleToCm,
        Phase4Options options)
    {
        var predLabel = record.PredictedLabel;
        var predCoords = ScaleCoords(record.CoordsPred, scaleToCm);
        var isUnknown = string.Equals(predLabel, "Unknown", StringComparison.OrdinalIgnoreCase) || record.IsOod;
        var trueCoords = truth.Coords;
        var isDualTruth = truth.IsDual;

        double? error = null;
        double? meanError = null;
        double? e1 = null;
        double? e2 = null;
        double? centroidError = null;
        Vector3? bias = null;
        Vector3? bias1 = null;
        Vector3? bias2 = null;
        Vector3? centroidBias = null;

        if (!isUnknown && predCoords.Length >= (isDualTruth ? 6 : 3) && trueCoords.Length >= (isDualTruth ? 6 : 3))
        {
            if (!isDualTruth)
            {
                var pred = Vector3.From(predCoords, 0, 1.0);
                var truthVec = Vector3.From(trueCoords, 0, 1.0);
                error = Vector3.Distance(pred, truthVec);
                bias = pred - truthVec;
            }
            else
            {
                var pred1 = Vector3.From(predCoords, 0, 1.0);
                var pred2 = Vector3.From(predCoords, 3, 1.0);
                var true1 = Vector3.From(trueCoords, 0, 1.0);
                var true2 = Vector3.From(trueCoords, 3, 1.0);

                var d11 = Vector3.Distance(pred1, true1);
                var d22 = Vector3.Distance(pred2, true2);
                var d12 = Vector3.Distance(pred1, true2);
                var d21 = Vector3.Distance(pred2, true1);

                if (d11 + d22 <= d12 + d21)
                {
                    e1 = d11;
                    e2 = d22;
                    bias1 = pred1 - true1;
                    bias2 = pred2 - true2;
                }
                else
                {
                    e1 = d12;
                    e2 = d21;
                    bias1 = pred1 - true2;
                    bias2 = pred2 - true1;
                }

                meanError = (e1 + e2) / 2.0;
                var centroidPred = (pred1 + pred2) / 2.0;
                var centroidTrue = (true1 + true2) / 2.0;
                centroidError = Vector3.Distance(centroidPred, centroidTrue);
                centroidBias = centroidPred - centroidTrue;
            }
        }

        var trueDistance = ComputeTrueDistance(truth, detectorOrigin);
        var stableFlag = ComputeStableFlag(truth, options, error, meanError, e1, e2);

        return new WindowMetricsRow
        {
            RunId = record.RunId,
            WindowIndex = record.WindowIndex,
            TStart = record.TStart,
            TEnd = record.TEnd,
            DurationSeconds = record.DurationSeconds,
            TotalCounts = record.TotalCounts ?? (record.ChannelCounts?.Sum() ?? 0),
            CumulativeCounts = cumulativeCounts,
            CumulativeTimeSeconds = cumulativeTime,
            PredictedLabel = predLabel,
            IsOod = record.IsOod,
            Probability = record.Probability,
            TrueLabel = truth.Label,
            TrueCoords = trueCoords,
            ErrorCm = error,
            MeanErrorCm = meanError,
            Error1Cm = e1,
            Error2Cm = e2,
            CentroidErrorCm = centroidError,
            BiasX = bias?.X,
            BiasY = bias?.Y,
            BiasZ = bias?.Z,
            Bias1X = bias1?.X,
            Bias1Y = bias1?.Y,
            Bias1Z = bias1?.Z,
            Bias2X = bias2?.X,
            Bias2Y = bias2?.Y,
            Bias2Z = bias2?.Z,
            CentroidBiasX = centroidBias?.X,
            CentroidBiasY = centroidBias?.Y,
            CentroidBiasZ = centroidBias?.Z,
            TrueDistanceCm = trueDistance,
            StableFlag = stableFlag,
            MahalanobisDistance = record.MahalanobisDistance,
            RtMetrics = record.RtMetrics
        };
    }

    private static double? ComputeTrueDistance(TruthState truth, Vector3 origin)
    {
        if (truth.Coords.Length < 3)
        {
            return null;
        }

        if (!truth.IsDual)
        {
            var point = Vector3.From(truth.Coords, 0, 1.0);
            return Vector3.Distance(point, origin);
        }

        if (truth.Coords.Length < 6)
        {
            return null;
        }

        var p1 = Vector3.From(truth.Coords, 0, 1.0);
        var p2 = Vector3.From(truth.Coords, 3, 1.0);
        var centroid = (p1 + p2) / 2.0;
        return Vector3.Distance(centroid, origin);
    }

    private static bool ComputeStableFlag(TruthState truth, Phase4Options options, double? error, double? meanError, double? e1, double? e2)
    {
        if (truth.IsDual)
        {
            return e1.HasValue && e2.HasValue && e1.Value <= options.StabilityRadiusCm && e2.Value <= options.StabilityRadiusCm;
        }

        return error.HasValue && error.Value <= options.StabilityRadiusCm;
    }

    private static ErrorCurveSummary BuildErrorCurve(
        IReadOnlyList<WindowMetricsRow> rows,
        IReadOnlyList<double> thresholds,
        Func<WindowMetricsRow, double> selector)
    {
        var curve = rows.Select(row => new ErrorCurvePoint
        {
            CumulativeValue = selector(row),
            ErrorCm = GetPrimaryError(row),
            IsUnknownOrOod = IsUnknown(row)
        }).ToList();

        var summaries = new List<ThresholdErrorSummary>(thresholds.Count);
        foreach (var threshold in thresholds)
        {
            var subset = rows.Where(row => selector(row) >= threshold).ToList();
            var total = subset.Count;
            var unknown = subset.Count(IsUnknown);
            var errors = subset.Select(GetPrimaryError).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            errors.Sort();

            summaries.Add(new ThresholdErrorSummary
            {
                Threshold = threshold,
                MedianErrorCm = errors.Count > 0 ? Statistics.MedianSorted(errors) : null,
                P90ErrorCm = errors.Count > 0 ? Statistics.PercentileSorted(errors, 0.9) : null,
                UnknownFraction = total > 0 ? (double)unknown / total : 0.0,
                TotalWindows = total,
                ErrorWindows = errors.Count
            });
        }

        return new ErrorCurveSummary
        {
            Curve = curve,
            ThresholdSummaries = summaries
        };
    }

    private static IReadOnlyList<DistanceRegionSummary> BuildDistanceRegionSummary(
        IReadOnlyList<WindowMetricsRow> rows,
        FieldOfViewConfig config)
    {
        var groups = new Dictionary<string, List<WindowMetricsRow>>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var regionKey = BuildRegionKey(row, config);
            if (!groups.TryGetValue(regionKey, out var list))
            {
                list = new List<WindowMetricsRow>();
                groups[regionKey] = list;
            }

            list.Add(row);
        }

        var summaries = new List<DistanceRegionSummary>();
        foreach (var (key, list) in groups)
        {
            var errors = list.Select(GetPrimaryError).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            errors.Sort();
            var unknown = list.Count(IsUnknown);
            var biases = list.Select(GetPrimaryBias).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            var meanBias = biases.Count > 0 ? Statistics.MeanBias(biases) : new BiasVector(0.0, 0.0, 0.0);

            summaries.Add(new DistanceRegionSummary
            {
                RegionKey = key,
                MedianErrorCm = errors.Count > 0 ? Statistics.MedianSorted(errors) : null,
                MeanBias = meanBias,
                UnknownFraction = list.Count > 0 ? (double)unknown / list.Count : 0.0,
                TotalWindows = list.Count,
                ErrorWindows = errors.Count
            });
        }

        return summaries;
    }

    private static string BuildRegionKey(WindowMetricsRow row, FieldOfViewConfig config)
    {
        var distance = row.TrueDistanceCm ?? double.NaN;
        var radialLabel = "radial:all";
        if (config.RadialBinsCm.Length > 1 && !double.IsNaN(distance))
        {
            radialLabel = "radial:" + Statistics.FormatRadialBin(distance, config.RadialBinsCm);
        }

        var edgeFlag = ComputeEdgeFlag(row, config.EdgeThresholds);
        return FormattableString.Invariant($"{radialLabel}|edge:{edgeFlag.ToString().ToLowerInvariant()}");
    }

    private static bool ComputeEdgeFlag(WindowMetricsRow row, EdgeThresholds thresholds)
    {
        if (row.TrueCoords.Length < 3)
        {
            return false;
        }

        double x;
        double y;
        double z;
        if (row.TrueCoords.Length >= 6)
        {
            x = (row.TrueCoords[0] + row.TrueCoords[3]) / 2.0;
            y = (row.TrueCoords[1] + row.TrueCoords[4]) / 2.0;
            z = (row.TrueCoords[2] + row.TrueCoords[5]) / 2.0;
        }
        else
        {
            x = row.TrueCoords[0];
            y = row.TrueCoords[1];
            z = row.TrueCoords[2];
        }

        var edgeX = thresholds.X.HasValue && Math.Abs(x) >= thresholds.X.Value;
        var edgeY = thresholds.Y.HasValue && Math.Abs(y) >= thresholds.Y.Value;
        var edgeZ = thresholds.Z.HasValue && Math.Abs(z) >= thresholds.Z.Value;

        return edgeX || edgeY || edgeZ;
    }

    private static MotionLatencyReport BuildMotionLatency(
        RunTruth runTruth,
        IReadOnlyList<WindowRecord> windowRecords,
        double scaleToCm,
        Phase4Options options)
    {
        var ordered = windowRecords.OrderBy(w => w.WindowIndex).ThenBy(w => w.TStart).ToList();
        var results = new List<MotionLatencyResult>();

        foreach (var change in runTruth.ChangePoints.OrderBy(cp => cp.TimeSeconds))
        {
            var truth = new TruthState(change.LabelAfter, ScaleCoords(change.CoordsTrueAfter, scaleToCm));
            var startIndex = ordered.FindIndex(w => w.TStart >= change.TimeSeconds);
            if (startIndex < 0)
            {
                results.Add(new MotionLatencyResult
                {
                    ChangePointTime = change.TimeSeconds,
                    LabelAfter = change.LabelAfter,
                    LatencySeconds = null,
                    FirstOutputLatencySeconds = null,
                    Success = false
                });
                continue;
            }

            double? firstOutputLatency = null;
            for (var i = startIndex; i < ordered.Count; i++)
            {
                if (IsStableWindow(ordered[i], truth, scaleToCm, options, requireConsecutive: false))
                {
                    firstOutputLatency = ordered[i].TEnd - change.TimeSeconds;
                    break;
                }
            }

            double? latency = null;
            var consecutive = Math.Max(1, options.StabilityConsecutiveWindows);
            for (var i = startIndex; i <= ordered.Count - consecutive; i++)
            {
                var stable = true;
                for (var j = 0; j < consecutive; j++)
                {
                    if (!IsStableWindow(ordered[i + j], truth, scaleToCm, options, requireConsecutive: true))
                    {
                        stable = false;
                        break;
                    }
                }

                if (stable)
                {
                    latency = ordered[i].TEnd - change.TimeSeconds;
                    break;
                }
            }

            results.Add(new MotionLatencyResult
            {
                ChangePointTime = change.TimeSeconds,
                LabelAfter = change.LabelAfter,
                LatencySeconds = latency,
                FirstOutputLatencySeconds = firstOutputLatency,
                Success = latency.HasValue
            });
        }

        return new MotionLatencyReport { ChangePoints = results };
    }

    private static bool IsStableWindow(
        WindowRecord record,
        TruthState truth,
        double scaleToCm,
        Phase4Options options,
        bool requireConsecutive)
    {
        if (string.Equals(record.PredictedLabel, "Unknown", StringComparison.OrdinalIgnoreCase) || record.IsOod)
        {
            return false;
        }

        if (record.CoordsPred is not { Length: >= 3 })
        {
            return false;
        }

        var predCoords = ScaleCoords(record.CoordsPred, scaleToCm);
        if (!truth.IsDual)
        {
            var pred = Vector3.From(predCoords, 0, 1.0);
            var trueVec = Vector3.From(truth.Coords, 0, 1.0);
            var distance = Vector3.Distance(pred, trueVec);
            return distance <= options.StabilityRadiusCm;
        }

        if (predCoords.Length < 6 || truth.Coords.Length < 6)
        {
            return false;
        }

        var pred1 = Vector3.From(predCoords, 0, 1.0);
        var pred2 = Vector3.From(predCoords, 3, 1.0);
        var true1 = Vector3.From(truth.Coords, 0, 1.0);
        var true2 = Vector3.From(truth.Coords, 3, 1.0);

        var d11 = Vector3.Distance(pred1, true1);
        var d22 = Vector3.Distance(pred2, true2);
        var d12 = Vector3.Distance(pred1, true2);
        var d21 = Vector3.Distance(pred2, true1);

        if (d11 + d22 <= d12 + d21)
        {
            return d11 <= options.StabilityRadiusCm && d22 <= options.StabilityRadiusCm;
        }

        return d12 <= options.StabilityRadiusCm && d21 <= options.StabilityRadiusCm;
    }

    private static StaticStabilityReport BuildStaticStability(
        RunTruth runTruth,
        IReadOnlyList<WindowRecord> records,
        double scaleToCm,
        Phase4Options options)
    {
        var ordered = records.OrderBy(w => w.WindowIndex).ThenBy(w => w.TStart).ToList();
        var stable = SelectStableInterval(ordered, options);
        if (stable.Count == 0)
        {
            return new StaticStabilityReport
            {
                Single = null,
                Dual = null,
                LabelTransitionsPerMinute = null,
                UnknownFraction = 0.0
            };
        }

        var unknownCount = stable.Count(r => string.Equals(r.PredictedLabel, "Unknown", StringComparison.OrdinalIgnoreCase) || r.IsOod);
        var transitions = CountLabelTransitions(stable);
        var totalSeconds = stable.Sum(r => r.DurationSeconds);
        var transitionsPerMinute = totalSeconds > 0 ? transitions / (totalSeconds / 60.0) : null;

        DispersionStats? singleStats = null;
        DualDispersionStats? dualStats = null;

        if (string.Equals(runTruth.TrueLabel, "Dual", StringComparison.OrdinalIgnoreCase))
        {
            dualStats = BuildDualDispersion(runTruth, stable, scaleToCm);
        }
        else
        {
            singleStats = BuildSingleDispersion(stable, scaleToCm);
        }

        return new StaticStabilityReport
        {
            Single = singleStats,
            Dual = dualStats,
            LabelTransitionsPerMinute = transitionsPerMinute,
            UnknownFraction = stable.Count > 0 ? (double)unknownCount / stable.Count : 0.0
        };
    }

    private static List<WindowRecord> SelectStableInterval(IReadOnlyList<WindowRecord> records, Phase4Options options)
    {
        if (options.UseRtStateWarmup && options.WarmupStateLabels.Length > 0 && records.Any(r => !string.IsNullOrWhiteSpace(r.RtStateLabel)))
        {
            var warmupLabels = new HashSet<string>(options.WarmupStateLabels, StringComparer.OrdinalIgnoreCase);
            return records.Where(r => r.RtStateLabel is null || !warmupLabels.Contains(r.RtStateLabel)).ToList();
        }

        if (options.WarmupWindowCount <= 0)
        {
            return records.ToList();
        }

        return records.Skip(options.WarmupWindowCount).ToList();
    }

    private static int CountLabelTransitions(IReadOnlyList<WindowRecord> records)
    {
        if (records.Count < 2)
        {
            return 0;
        }

        var transitions = 0;
        var previous = records[0].PredictedLabel;
        for (var i = 1; i < records.Count; i++)
        {
            var current = records[i].PredictedLabel;
            if (!string.Equals(previous, current, StringComparison.OrdinalIgnoreCase))
            {
                transitions++;
            }

            previous = current;
        }

        return transitions;
    }

    private static DispersionStats BuildSingleDispersion(IReadOnlyList<WindowRecord> records, double scaleToCm)
    {
        var coords = records
            .Where(r => r.CoordsPred is { Length: >= 3 }
                        && !string.Equals(r.PredictedLabel, "Unknown", StringComparison.OrdinalIgnoreCase)
                        && !r.IsOod)
            .Select(r => Vector3.From(ScaleCoords(r.CoordsPred, scaleToCm), 0, 1.0))
            .ToList();

        if (coords.Count == 0)
        {
            return new DispersionStats();
        }

        var median = Statistics.MedianVector(coords);
        var rms = Statistics.RmsDistance(coords, median);
        var stdDev = Statistics.StdDev(coords);

        return new DispersionStats
        {
            RmsDistanceToMedianCm = rms,
            AxisStdDevCm = new AxisStats { X = stdDev.X, Y = stdDev.Y, Z = stdDev.Z }
        };
    }

    private static DualDispersionStats BuildDualDispersion(RunTruth runTruth, IReadOnlyList<WindowRecord> records, double scaleToCm)
    {
        var truthCoords = ScaleCoords(runTruth.CoordsTrue, scaleToCm);
        if (truthCoords.Length < 6)
        {
            return new DualDispersionStats();
        }

        var true1 = Vector3.From(truthCoords, 0, 1.0);
        var true2 = Vector3.From(truthCoords, 3, 1.0);

        var source1 = new List<Vector3>();
        var source2 = new List<Vector3>();
        var centroids = new List<Vector3>();

        foreach (var record in records)
        {
            if (record.CoordsPred is not { Length: >= 6 })
            {
                continue;
            }

            if (string.Equals(record.PredictedLabel, "Unknown", StringComparison.OrdinalIgnoreCase) || record.IsOod)
            {
                continue;
            }

            var predCoords = ScaleCoords(record.CoordsPred, scaleToCm);
            var pred1 = Vector3.From(predCoords, 0, 1.0);
            var pred2 = Vector3.From(predCoords, 3, 1.0);

            var d11 = Vector3.Distance(pred1, true1);
            var d22 = Vector3.Distance(pred2, true2);
            var d12 = Vector3.Distance(pred1, true2);
            var d21 = Vector3.Distance(pred2, true1);

            if (d11 + d22 <= d12 + d21)
            {
                source1.Add(pred1);
                source2.Add(pred2);
            }
            else
            {
                source1.Add(pred2);
                source2.Add(pred1);
            }

            centroids.Add((pred1 + pred2) / 2.0);
        }

        return new DualDispersionStats
        {
            Source1 = BuildDispersion(source1),
            Source2 = BuildDispersion(source2),
            Centroid = BuildDispersion(centroids)
        };
    }

    private static DispersionStats BuildDispersion(IReadOnlyList<Vector3> coords)
    {
        if (coords.Count == 0)
        {
            return new DispersionStats();
        }

        var median = Statistics.MedianVector(coords);
        var rms = Statistics.RmsDistance(coords, median);
        var stdDev = Statistics.StdDev(coords);

        return new DispersionStats
        {
            RmsDistanceToMedianCm = rms,
            AxisStdDevCm = new AxisStats { X = stdDev.X, Y = stdDev.Y, Z = stdDev.Z }
        };
    }

    private static MultiplicitySummary? BuildMultiplicitySummary(RunTruth runTruth, IReadOnlyList<WindowRecord> records, Phase4Options options)
    {
        var stable = SelectStableInterval(records, options);
        var metrics = stable.Select(r => r.RtMetrics).Where(r => r is not null).ToList();
        if (metrics.Count == 0)
        {
            return null;
        }

        var summary = new MultiplicitySummary
        {
            Grouping = BuildGrouping(runTruth)
        };

        AddMetricSummary(summary.Metrics, "Y", metrics.Select(m => m!.Y));
        AddMetricSummary(summary.Metrics, "SigmaY", metrics.Select(m => m!.SigmaY));
        AddMetricSummary(summary.Metrics, "SelectedGate", metrics.Select(m => m!.SelectedGate));
        AddMetricSummary(summary.Metrics, "CorrelationTimeEstimate", metrics.Select(m => m!.CorrelationTimeEstimate));

        return summary;
    }

    private static Dictionary<string, string> BuildGrouping(RunTruth runTruth)
    {
        var grouping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "shielding", "shielding_label", "background", "background_label" })
        {
            if (runTruth.Metadata.TryGetValue(key, out var value))
            {
                grouping[key] = value;
            }
        }

        return grouping;
    }

    private static void AddMetricSummary(Dictionary<string, MetricSummary> target, string name, IEnumerable<double?> values)
    {
        var list = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (list.Count == 0)
        {
            target[name] = new MetricSummary();
            return;
        }

        list.Sort();
        var mean = list.Average();
        var median = Statistics.MedianSorted(list);
        var stdDev = Statistics.StdDev(list, mean);
        var mad = Statistics.MedianAbsoluteDeviation(list, median);

        target[name] = new MetricSummary
        {
            Mean = mean,
            Median = median,
            StdDev = stdDev,
            MedianAbsoluteDeviation = mad
        };
    }

    private static double? GetPrimaryError(WindowMetricsRow row)
    {
        return row.ErrorCm ?? row.MeanErrorCm;
    }

    private static BiasVector? GetPrimaryBias(WindowMetricsRow row)
    {
        if (row.BiasX.HasValue && row.BiasY.HasValue && row.BiasZ.HasValue)
        {
            return new BiasVector(row.BiasX.Value, row.BiasY.Value, row.BiasZ.Value);
        }

        if (row.CentroidBiasX.HasValue && row.CentroidBiasY.HasValue && row.CentroidBiasZ.HasValue)
        {
            return new BiasVector(row.CentroidBiasX.Value, row.CentroidBiasY.Value, row.CentroidBiasZ.Value);
        }

        return null;
    }

    private static bool IsUnknown(WindowMetricsRow row)
    {
        return string.Equals(row.PredictedLabel, "Unknown", StringComparison.OrdinalIgnoreCase) || row.IsOod;
    }

    private readonly record struct TruthState(string Label, double[] Coords)
    {
        public bool IsDual => string.Equals(Label, "Dual", StringComparison.OrdinalIgnoreCase) || Coords.Length >= 6;
    }

}

internal readonly struct Vector3
{
    public Vector3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public double X { get; }
    public double Y { get; }
    public double Z { get; }

    public static Vector3 From(double[] coords, int offset, double scale)
    {
        return new Vector3(coords[offset] * scale, coords[offset + 1] * scale, coords[offset + 2] * scale);
    }

    public static Vector3 operator +(Vector3 left, Vector3 right)
    {
        return new Vector3(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
    }

    public static Vector3 operator -(Vector3 left, Vector3 right)
    {
        return new Vector3(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
    }

    public static Vector3 operator /(Vector3 vector, double divisor)
    {
        return new Vector3(vector.X / divisor, vector.Y / divisor, vector.Z / divisor);
    }

    public static double Distance(Vector3 left, Vector3 right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        var dz = left.Z - right.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

internal static class Statistics
{
    public static double MedianSorted(IReadOnlyList<double> sorted)
    {
        var count = sorted.Count;
        if (count == 0)
        {
            return double.NaN;
        }

        var mid = count / 2;
        if (count % 2 == 1)
        {
            return sorted[mid];
        }

        return (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    public static double PercentileSorted(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
        {
            return double.NaN;
        }

        var clamped = Math.Clamp(percentile, 0.0, 1.0);
        var index = clamped * (sorted.Count - 1);
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper)
        {
            return sorted[lower];
        }

        var weight = index - lower;
        return sorted[lower] * (1 - weight) + sorted[upper] * weight;
    }

    public static BiasVector MeanBias(IReadOnlyList<BiasVector> biases)
    {
        var sumX = 0.0;
        var sumY = 0.0;
        var sumZ = 0.0;
        foreach (var bias in biases)
        {
            sumX += bias.X;
            sumY += bias.Y;
            sumZ += bias.Z;
        }

        var count = biases.Count;
        return count > 0
            ? new BiasVector(sumX / count, sumY / count, sumZ / count)
            : new BiasVector(0.0, 0.0, 0.0);
    }

    public static string FormatRadialBin(double value, IReadOnlyList<double> bins)
    {
        for (var i = 0; i < bins.Count - 1; i++)
        {
            if (value >= bins[i] && value < bins[i + 1])
            {
                return FormattableString.Invariant($"{bins[i]}-{bins[i + 1]}cm");
            }
        }

        if (bins.Count > 0 && value >= bins[^1])
        {
            return FormattableString.Invariant($">={bins[^1]}cm");
        }

        return "unknown";
    }

    public static Vector3 MedianVector(IReadOnlyList<Vector3> values)
    {
        var xs = values.Select(v => v.X).OrderBy(v => v).ToArray();
        var ys = values.Select(v => v.Y).OrderBy(v => v).ToArray();
        var zs = values.Select(v => v.Z).OrderBy(v => v).ToArray();

        return new Vector3(MedianSorted(xs), MedianSorted(ys), MedianSorted(zs));
    }

    public static double RmsDistance(IReadOnlyList<Vector3> values, Vector3 median)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }

        var sum = 0.0;
        foreach (var v in values)
        {
            var dx = v.X - median.X;
            var dy = v.Y - median.Y;
            var dz = v.Z - median.Z;
            sum += dx * dx + dy * dy + dz * dz;
        }

        return Math.Sqrt(sum / values.Count);
    }

    public static Vector3 StdDev(IReadOnlyList<Vector3> values)
    {
        if (values.Count == 0)
        {
            return new Vector3(double.NaN, double.NaN, double.NaN);
        }

        var meanX = values.Average(v => v.X);
        var meanY = values.Average(v => v.Y);
        var meanZ = values.Average(v => v.Z);
        var sumX = 0.0;
        var sumY = 0.0;
        var sumZ = 0.0;

        foreach (var v in values)
        {
            var dx = v.X - meanX;
            var dy = v.Y - meanY;
            var dz = v.Z - meanZ;
            sumX += dx * dx;
            sumY += dy * dy;
            sumZ += dz * dz;
        }

        var count = values.Count;
        return new Vector3(Math.Sqrt(sumX / count), Math.Sqrt(sumY / count), Math.Sqrt(sumZ / count));
    }

    public static double StdDev(IReadOnlyList<double> values, double mean)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }

        var sum = 0.0;
        foreach (var value in values)
        {
            var diff = value - mean;
            sum += diff * diff;
        }

        return Math.Sqrt(sum / values.Count);
    }

    public static double MedianAbsoluteDeviation(IReadOnlyList<double> sorted, double median)
    {
        var deviations = sorted.Select(v => Math.Abs(v - median)).OrderBy(v => v).ToArray();
        return MedianSorted(deviations);
    }
}
