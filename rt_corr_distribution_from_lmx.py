#!/usr/bin/env python3
"""Generate distribution of real-time correlation estimates from LMX files."""

from __future__ import annotations

import argparse
import glob
import math
import os
import re
import warnings
from dataclasses import dataclass
from typing import Iterable, List, Optional, Sequence, Tuple

import numpy as np
import matplotlib.pyplot as plt

try:
    from scipy.stats import gaussian_kde
except Exception:  # pragma: no cover
    gaussian_kde = None


OKABE_ITO = {
    "blue": "#0072B2",
    "orange": "#E69F00",
    "sky": "#56B4E9",
    "vermillion": "#D55E00",
    "green": "#009E73",
    "yellow": "#F0E442",
    "purple": "#CC79A7",
    "gray": "#999999",
}


@dataclass
class LmxParseResult:
    timestamps_s: np.ndarray
    layout_description: str


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Compute distribution of real-time correlation Z-scores across runs."
        )
    )
    parser.add_argument("--lmx-dir", required=True, help="Directory containing LMX files")
    parser.add_argument("--pattern", default="*.lmx", help="Glob pattern for LMX files")
    parser.add_argument("--tg", type=float, default=1e-3, help="Gate width (seconds)")
    parser.add_argument(
        "--window", type=float, default=10.0, help="Analysis window length (seconds)"
    )
    parser.add_argument(
        "--step", type=float, default=1.0, help="Step between windows (seconds)"
    )
    parser.add_argument(
        "--warmup-drop",
        type=float,
        default=0.0,
        help="Seconds to drop at start of each run",
    )
    parser.add_argument(
        "--out", default="rt_corr_distribution.png", help="Output figure path"
    )
    parser.add_argument("--seed", type=int, default=12345, help="Random seed")
    parser.add_argument(
        "--min-events",
        type=int,
        default=100,
        help="Minimum events per window to compute stats",
    )
    parser.add_argument(
        "--show-points",
        action="store_true",
        help="Overlay jittered per-run median points",
    )
    parser.add_argument(
        "--debug-read-lmx",
        action="store_true",
        help="Print LMX parsing diagnostics and timestamp summary",
    )
    return parser.parse_args()


def read_lmx_timestamps(path: str, debug: bool = False) -> LmxParseResult:
    """Read LMX timestamps as float seconds.

    Mirrors converter_v6.py: parse ASCII header then binary 8-byte records.
    """

    header_max = 200
    t_step: Optional[int] = None
    nlines: Optional[int] = None
    total_counts: Optional[int] = None

    with open(path, "rb") as handle:
        for _ in range(header_max):
            line = handle.readline()
            if not line:
                break
            text = line.decode(errors="ignore").strip()
            if "BinaryDataClockTickLength" in text:
                t_step = _parse_first_int(text)
            elif "BinaryDataFollows" in text:
                nlines = _parse_first_int(text)
            elif "InternalScaler" in text:
                total_counts = _parse_first_int(text)
            if t_step is not None and nlines is not None and total_counts is not None:
                break

    if t_step is None or nlines is None or total_counts is None:
        raise ValueError(
            "Unable to parse LMX header. Expected ASCII header containing "
            "'BinaryDataClockTickLength', 'BinaryDataFollows', and 'InternalScaler'."
        )

    timestamps: List[float] = []
    add = 0.0
    read_events = 0
    channel_bitmask = list(range(1, 16))

    with open(path, "rb") as handle:
        for _ in range(nlines):
            if not handle.readline():
                break

        while True:
            chunk = handle.read(8)
            if len(chunk) < 8:
                break
            l_bytes = chunk[:4]
            l2 = int.from_bytes(chunk[4:], byteorder="little", signed=False)
            read_events += 1

            if l_bytes == b"\x00\x00\x00\x00":
                marker = handle.read(8)
                if len(marker) < 8:
                    break
                l_marker = marker[:4]
                l2_marker = int.from_bytes(marker[4:], byteorder="little", signed=False)
                read_events += 1
                t = l2_marker * t_step * 1e-9 + add
                if l_marker == b"\x01\x00\x00\x00":
                    add = float(t)
                elif l_marker == b"\xff\xff\xff\xff":
                    break
                if total_counts is not None and read_events > total_counts + 2:
                    break
                continue

            l_val = int.from_bytes(l_bytes, byteorder="little", signed=False)
            t = l2 * t_step * 1e-9 + add
            if _check_channels(_channels_present(l_val), channel_bitmask):
                timestamps.append(float(t))

            if total_counts is not None and read_events > total_counts + 2:
                break

    timestamps_arr = np.array(timestamps, dtype=np.float64)
    if timestamps_arr.size > 1 and not np.all(np.diff(timestamps_arr) > 0):
        warnings.warn(
            "LMX timestamps were not strictly increasing; sorting and uniquing.",
            RuntimeWarning,
        )
        timestamps_arr = np.unique(np.sort(timestamps_arr))

    if debug:
        print(f"LMX header: t_step={t_step}, nlines={nlines}, total_counts={total_counts}")
        print(f"Extracted timestamps: {timestamps_arr.size}")
        print(f"First 5: {timestamps_arr[:5]}")
        print(f"Last 5: {timestamps_arr[-5:]}")
        if timestamps_arr.size > 1:
            diffs = np.diff(timestamps_arr)
            print(f"min dt={float(diffs.min())} max dt={float(diffs.max())}")
        else:
            print("min dt=nan max dt=nan")

    return LmxParseResult(
        timestamps_arr,
        f"LMX binary (t_step={t_step}, header_lines={nlines})",
    )


def _looks_like_seconds(values: np.ndarray) -> bool:
    if values.size == 0:
        return False
    if not np.all(np.isfinite(values)):
        return False
    if np.any(values < 0):
        return False
    if values[-1] == 0:
        return False
    span = values.max() - values.min()
    if span <= 0:
        return False
    if span > 1e9:
        return False
    return True


def _prepare_timestamps(values: np.ndarray) -> np.ndarray:
    timestamps = np.asarray(values, dtype=np.float64)
    if timestamps.size == 0:
        return timestamps
    if not np.all(np.diff(timestamps) >= 0):
        timestamps = np.sort(timestamps)
    return timestamps


def _parse_first_int(text: str) -> int:
    match = re.search(r"(-?\d+)", text)
    if not match:
        raise ValueError(f"Unable to parse integer from header line: {text}")
    return int(match.group(1))


def _channels_present(mask: int) -> List[int]:
    return [idx + 1 for idx in range(32) if (mask >> idx) & 1]


def _check_channels(channels_present: Iterable[int], channel_bitmask: Sequence[int]) -> bool:
    return any(channel in channel_bitmask for channel in channels_present)


def compute_window_metrics(
    t: np.ndarray,
    window_start: float,
    window_len: float,
    tg: float,
    min_events: int,
) -> Optional[Tuple[float, float]]:
    window_end = window_start + window_len
    mask = (t >= window_start) & (t < window_end)
    t_window = t[mask]
    if t_window.size < min_events:
        return None

    n_gates = int(window_len / tg)
    if n_gates < 3:
        return None

    edges = window_start + np.arange(n_gates + 1) * tg
    counts, _ = np.histogram(t_window, bins=edges)

    if counts.sum() < min_events:
        return None

    mean = counts.mean()
    if mean <= 0:
        return None

    var = counts.var(ddof=1)
    y = (var - mean) / mean

    sigma_y = _delta_sigma_y(counts, mean, var)
    if sigma_y is None or not np.isfinite(sigma_y) or sigma_y <= 0:
        return None

    z = y / sigma_y
    return z, y


def _delta_sigma_y(counts: np.ndarray, mean: float, var: float) -> Optional[float]:
    n = counts.size
    if n < 3:
        return None

    centered = counts - mean
    m2 = np.mean(centered**2)
    m3 = np.mean(centered**3)
    m4 = np.mean(centered**4)

    var_mean = m2 / n
    var_var = max(m4 - m2**2, 0.0) / n
    cov_mean_var = m3 / n

    dfdm = -var / (mean**2)
    dfdv = 1.0 / mean

    var_y = (
        dfdm**2 * var_mean
        + dfdv**2 * var_var
        + 2 * dfdm * dfdv * cov_mean_var
    )

    if var_y <= 0 or not np.isfinite(var_y):
        return None

    return math.sqrt(var_y)


def summarize_counts(values: Sequence[int]) -> Tuple[float, int, int]:
    if not values:
        return 0.0, 0, 0
    return float(np.median(values)), int(np.min(values)), int(np.max(values))


def format_params(args: argparse.Namespace) -> str:
    return (
        f"Parameters: tg={args.tg:g}s, window={args.window:g}s, step={args.step:g}s, "
        f"warmup_drop={args.warmup_drop:g}s, min_events={args.min_events}, seed={args.seed}"
    )


def plot_distribution(
    medians: np.ndarray,
    args: argparse.Namespace,
    out_path: str,
    show_points: bool,
) -> None:
    fig, ax = plt.subplots(figsize=(7.5, 4.5), dpi=150)

    hist_color = OKABE_ITO["sky"]
    line_color = OKABE_ITO["blue"]
    stats_color = OKABE_ITO["vermillion"]

    ax.hist(
        medians,
        bins="auto",
        density=True,
        color=hist_color,
        edgecolor=OKABE_ITO["gray"],
        alpha=0.8,
        linewidth=0.8,
    )

    if gaussian_kde is not None and medians.size > 1:
        kde = gaussian_kde(medians)
        x_grid = np.linspace(medians.min(), medians.max(), 256)
        ax.plot(x_grid, kde(x_grid), color=line_color, linewidth=2)

    mean_val = float(np.mean(medians))
    median_val = float(np.median(medians))

    ax.axvline(mean_val, color=stats_color, linestyle="--", linewidth=1.5)
    ax.axvline(median_val, color=stats_color, linestyle=":", linewidth=1.5)

    ax.text(
        0.02,
        0.98,
        (
            f"N runs = {medians.size}\n"
            f"Tg = {args.tg:g}s\n"
            f"Window = {args.window:g}s\n"
            f"Step = {args.step:g}s"
        ),
        transform=ax.transAxes,
        ha="left",
        va="top",
        fontsize=9,
        bbox=dict(boxstyle="round", facecolor="white", edgecolor=OKABE_ITO["gray"], alpha=0.9),
    )

    ax.set_xlabel("Correlation Z-score (median across windows)")
    ax.set_ylabel("Density")
    ax.set_title("Distribution of real-time correlation estimates across runs")

    handles = [
        plt.Line2D([0], [0], color=stats_color, linestyle="--", linewidth=1.5, label=f"Mean = {mean_val:.2f}"),
        plt.Line2D([0], [0], color=stats_color, linestyle=":", linewidth=1.5, label=f"Median = {median_val:.2f}"),
    ]
    ax.legend(handles=handles, frameon=False, loc="upper right")

    if show_points:
        rng = np.random.default_rng(args.seed)
        jitter = rng.normal(scale=0.02, size=medians.size)
        ax.scatter(
            medians + jitter,
            np.zeros_like(medians),
            color=OKABE_ITO["orange"],
            alpha=0.7,
            s=25,
            zorder=3,
        )

    fig.tight_layout()
    fig.savefig(out_path, dpi=300)

    if out_path.lower().endswith(".png"):
        pdf_path = os.path.splitext(out_path)[0] + ".pdf"
        fig.savefig(pdf_path)

    plt.close(fig)


def main() -> None:
    args = parse_args()
    print(format_params(args))

    pattern = os.path.join(args.lmx_dir, args.pattern)
    files = sorted(glob.glob(pattern))
    if not files:
        raise SystemExit(f"No LMX files found with pattern: {pattern}")

    print(f"Found {len(files)} files")

    per_run_medians: List[float] = []
    windows_per_file: List[int] = []
    estimates_per_file: List[int] = []

    for path in files:
        result = read_lmx_timestamps(path, debug=args.debug_read_lmx)
        t = result.timestamps_s
        if t.size == 0:
            print(f"Skipping {os.path.basename(path)} (no timestamps)")
            continue

        start_time = t[0] + args.warmup_drop
        end_time = t[-1]
        if end_time <= start_time + args.window:
            print(f"Skipping {os.path.basename(path)} (not enough data after warmup)")
            continue

        z_values: List[float] = []
        window_starts = np.arange(start_time, end_time - args.window + 1e-12, args.step)
        windows_per_file.append(len(window_starts))

        for window_start in window_starts:
            metrics = compute_window_metrics(
                t,
                window_start,
                args.window,
                args.tg,
                args.min_events,
            )
            if metrics is None:
                continue
            z, y = metrics
            z_values.append(z)

        estimates_per_file.append(len(z_values))

        if not z_values:
            print(f"Skipping {os.path.basename(path)} (no valid windows)")
            continue

        median_z = float(np.median(z_values))
        per_run_medians.append(median_z)

        print(
            f"{os.path.basename(path)}: layout={result.layout_description}, "
            f"windows={len(window_starts)}, usable={len(z_values)}, median Z={median_z:.3f}"
        )

    if not per_run_medians:
        raise SystemExit("No valid runs found after processing windows")

    median_windows, min_windows, max_windows = summarize_counts(windows_per_file)
    print(
        "Summary: "
        f"files={len(files)}, windows_per_file median={median_windows:.1f} "
        f"min={min_windows} max={max_windows}"
    )
    print("Correlation estimates per file:")
    for file_path, count in zip(files, estimates_per_file):
        print(f"  {os.path.basename(file_path)}: {count}")

    medians_array = np.array(per_run_medians)
    plot_distribution(medians_array, args, args.out, args.show_points)


if __name__ == "__main__":
    main()
