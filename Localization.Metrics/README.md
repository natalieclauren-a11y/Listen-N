# Phase 4 Metrics

This module computes deterministic localization, motion, stability, and multiplicity metrics from per-window ML outputs and ground-truth scenario annotations. It **does not** perform inference or plotting; all outputs are numeric and JSON/CSV-ready.

## Inputs

- **Run truth (`RunTruth`)**
  - `ScenarioType`: `Static` or `Motion`.
  - `TrueLabel`: `Single` or `Dual`.
  - `CoordsTrue`: length 3 (single) or 6 (dual), in `CoordinateUnits`.
  - `CoordinateFrame`: frame name for metadata.
  - `DetectorOrigin`: optional origin for radial distance (defaults to `[0,0,0]`).
  - Motion runs include `ChangePoints` with `TimeSeconds`, `CoordsTrueAfter`, and `LabelAfter`.

- **Window records (`WindowRecord`)**
  - Ordered windows with timing, counts, ML outputs, optional RT metrics, and optional OOD flags.

- **Options (`Phase4Options`)**
  - Count/time thresholds for binning curves.
  - Stability radius and K-consecutive window definitions.
  - Field-of-view bins and edge thresholds.
  - Warmup handling for the stable interval.

## Metric Definitions

### A. Localization error
- **Single-source**: Euclidean distance between predicted and true position.
- **Dual-source**: choose the pairing that minimizes total error. Report per-source errors, mean error, and centroid error. Bias vectors are `pred - true` for the paired mapping.
- If `PredictedLabel` is `Unknown` **or** `IsOod` is true, errors are `null`.

### B. Error vs counts / time
- Cumulative counts and cumulative time are computed per window.
- For each threshold bin, report median error, 90th percentile error, and fraction Unknown/OOD.

### C. Error vs distance / field of view
- True distance `r_true` is computed from the detector origin.
- Regions are determined by configurable radial bins and edge thresholds. Aggregates include median error, mean bias vector, and Unknown/OOD fraction.

### D. Motion scenarios: detection latency
- Latency is time from change point to the first window that **starts** a stable streak.
- Stable requires: not Unknown, not OOD, and within `StabilityRadiusCm` of the post-change truth for `K` consecutive windows.
- A “first output” latency is also computed without the consecutive window requirement.

### E. Static scenarios: stability / dispersion
- Stable interval is the run excluding warmup windows (by RT state label if provided, else first `N` windows).
- Dispersion includes RMS distance to the median predicted position and per-axis standard deviation.
- Report label transition rate (transitions per minute) and Unknown/OOD fraction.
- Dual-source dispersion is computed per paired source and centroid.

### F. Multiplicity and correlation summaries
- If RT metrics exist, compute mean, median, standard deviation, and median absolute deviation for each metric over the stable interval.
- Grouping labels (e.g., shielding/background) are included when provided in `RunTruth.Metadata`.

## Outputs

`Phase4Metrics.ComputeAll` returns a `Phase4Report` containing:
- Per-window metrics rows.
- Aggregated summaries:
  - `error_vs_counts`
  - `error_vs_time`
  - `error_vs_distance_region`
  - `motion_latency`
  - `static_stability`
  - `multiplicity_summary`
- Metadata (coordinate frame, units, detector origin, and options).

## CLI

Use `Localization.Metrics.Cli` to compute metrics from JSON inputs:

```
--truth runTruth.json --windows windowRecords.json --output report.json [--options options.json] [--table table.csv]
```

The CLI emits a JSON report and optional CSV per-window table.

## Units

Coordinates are assumed to be in **centimeters** by default. If `RunTruth.CoordinateUnits` is set to `m` or `mm`, inputs are scaled to centimeters before computing metrics.
