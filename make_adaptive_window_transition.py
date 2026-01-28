#!/usr/bin/env python3
"""Generate adaptive window transition figure from sequential LMX files."""

from __future__ import annotations

import argparse
import bisect
import csv
import os
from dataclasses import dataclass
from typing import List, Optional, Sequence, Tuple

import numpy as np
import matplotlib.pyplot as plt


OKABE_ITO = {
    "black": "#000000",
    "orange": "#E69F00",
    "skyblue": "#56B4E9",
    "bluishgreen": "#009E73",
    "yellow": "#F0E442",
    "blue": "#0072B2",
    "vermillion": "#D55E00",
    "reddishpurple": "#CC79A7",
    "grey": "#B0B0B0",
}


@dataclass
class ControllerResult:
    t_end_s: np.ndarray
    t_mid_s: np.ndarray
    w_s: np.ndarray
    state: List[str]
    rate_cps: np.ndarray
    rate_smooth_cps: np.ndarray
    w_des_s: np.ndarray
    change_stat: np.ndarray
    ph_alarm: np.ndarray
    n_events: np.ndarray


@dataclass(frozen=True)
class ScheduleRow:
    t_start_s: float
    t_end_s: float
    state: str
    w_s: float


def parse_args() -> argparse.Namespace:
    description = (
        "Generate adaptive window transition figure from sequential LMX files."
    )
    epilog = (
        "PowerShell examples:\n"
        "  python .\\make_adaptive_window_transition.py --lmx-dir "
        '"C:\\...\\CfBeRP_motion" --out ".\\figures\\adaptive_cfberp"\n'
        "  python .\\make_adaptive_window_transition.py --lmx-files "
        '"C:\\...\\seg1.lmx" "C:\\...\\seg2.lmx" "C:\\...\\seg3.lmx" '
        '--out ".\\figures\\adaptive_pu" --gap-s 0\n'
        "  python .\\make_adaptive_window_transition.py --lmx-dir "
        '"C:\\...\\CfBeRP_motion" --out ".\\figures\\adaptive_forced" '
        '--schedule-csv ".\\schedule.csv"'
    )
    parser = argparse.ArgumentParser(
        description=description,
        epilog=epilog,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--lmx-dir", help="Directory containing *.lmx files")
    group.add_argument(
        "--lmx-files",
        nargs="+",
        help="Explicit ordered list of LMX files",
    )
    parser.add_argument(
        "--gap-s", type=float, default=0.0, help="Gap between files in seconds"
    )
    parser.add_argument(
        "--out",
        required=True,
        help="Output path without extension or with .png or .pdf",
    )
    parser.add_argument("--title", default=None, help="Optional figure title")
    parser.add_argument(
        "--tg-ms",
        type=float,
        default=1.0,
        help="Gate width in milliseconds for occupancy stats",
    )
    parser.add_argument(
        "--step-s",
        type=float,
        default=1.0,
        help="Controller update cadence in seconds",
    )
    parser.add_argument(
        "--rate-smooth-tau-s",
        type=float,
        default=2.0,
        help="Exponential smoothing time constant for rate proxy",
    )
    parser.add_argument(
        "--w-min-s", type=float, default=1.0, help="Minimum window length"
    )
    parser.add_argument(
        "--w-max-s", type=float, default=30.0, help="Maximum window length"
    )
    parser.add_argument(
        "--w-contract-max-per-step",
        type=float,
        default=1.0,
        help="Maximum window contraction per controller step in seconds",
    )
    parser.add_argument(
        "--w-expand-max-per-step",
        type=float,
        default=2.0,
        help="Maximum window expansion per controller step in seconds",
    )
    parser.add_argument(
        "--warmup-s", type=float, default=20.0, help="Warmup duration in seconds"
    )
    parser.add_argument(
        "--debounce-s",
        type=float,
        default=5.0,
        help="Debounce interval for conservative state entry",
    )
    parser.add_argument(
        "--ph-threshold",
        type=float,
        default=30.0,
        help="Page-Hinkley threshold",
    )
    parser.add_argument(
        "--ph-delta",
        type=float,
        default=0.005,
        help="Page-Hinkley delta",
    )
    parser.add_argument(
        "--target-rel-unc",
        type=float,
        default=0.03,
        help="Target relative uncertainty on rate proxy",
    )
    parser.add_argument(
        "--min-events",
        type=int,
        default=200,
        help="Minimum events required to compute stats",
    )
    parser.add_argument(
        "--show-ph-markers",
        action="store_true",
        default=False,
        help="Show Page-Hinkley alarm markers",
    )
    parser.add_argument(
        "--schedule-csv",
        default=None,
        help="Optional CSV schedule for forced window and state changes",
    )
    return parser.parse_args()


def read_lmx_timestamps(path: str) -> np.ndarray:
    header_max = 200
    t_step_ns: Optional[int] = None
    nlines: Optional[int] = None
    total_counts: Optional[int] = None
    header_lines: List[str] = []

    with open(path, "rb") as handle:
        for i in range(header_max):
            line = handle.readline()
            if not line:
                break
            decoded = line.decode(errors="ignore")
            header_lines.append(decoded.rstrip("\n"))
            if "BinaryDataClockTickLength" in decoded:
                t_step_ns = int(decoded.split()[-2])
            elif "BinaryDataFollows" in decoded:
                nlines = i + 1
            elif "InternalScaler" in decoded:
                total_counts = int(decoded.split()[-1])
            if t_step_ns is not None and nlines is not None and total_counts is not None:
                break

    if t_step_ns is None or nlines is None or total_counts is None:
        missing_fields = []
        if t_step_ns is None:
            missing_fields.append("BinaryDataClockTickLength")
        if nlines is None:
            missing_fields.append("BinaryDataFollows")
        if total_counts is None:
            missing_fields.append("InternalScaler")
        recent_lines = "\n".join(header_lines[-10:])
        raise ValueError(
            "Unable to parse LMX header. Missing fields: "
            f"{', '.join(missing_fields)}. "
            "Last header lines:\n"
            f"{recent_lines}"
        )

    timestamps: List[float] = []
    add = 0.0
    read_events = 0

    with open(path, "rb") as handle:
        for _ in range(nlines):
            if not handle.readline():
                break
        while True:
            chunk = handle.read(8)
            if len(chunk) < 8:
                break
            word = int.from_bytes(chunk[:4], byteorder="little", signed=False)
            ticks = int.from_bytes(chunk[4:], byteorder="little", signed=False)
            read_events += 1

            if word == 0x00000000:
                marker = handle.read(8)
                if len(marker) < 8:
                    break
                word2 = int.from_bytes(marker[:4], byteorder="little", signed=False)
                ticks2 = int.from_bytes(marker[4:], byteorder="little", signed=False)
                read_events += 1
                timestamp = ticks2 * (t_step_ns * 1e-9) + add
                if word2 == 0x00000001:
                    add = float(timestamp)
                elif word2 == 0xFFFFFFFF:
                    break
                if total_counts is not None and read_events > total_counts + 2:
                    break
                continue

            timestamp = ticks * (t_step_ns * 1e-9) + add
            timestamps.append(float(timestamp))

            if total_counts is not None and read_events > total_counts + 2:
                break

    timestamps_arr = np.array(timestamps, dtype=np.float64)
    if timestamps_arr.size > 1 and not np.all(np.diff(timestamps_arr) > 0):
        timestamps_arr = np.unique(np.sort(timestamps_arr))
    return timestamps_arr


def stitch_sessions(
    sessions: Sequence[np.ndarray], gap_s: float
) -> np.ndarray:
    stitched: List[np.ndarray] = []
    offset = 0.0
    for idx, timestamps in enumerate(sessions):
        if timestamps.size == 0:
            continue
        normalized = timestamps - timestamps[0]
        stitched.append(normalized + offset)
        offset = float(stitched[-1][-1]) + gap_s
    if not stitched:
        return np.array([], dtype=np.float64)
    return np.concatenate(stitched)


def _load_schedule_csv(
    path: str,
    w_min_s: float,
    w_max_s: float,
) -> List[ScheduleRow]:
    required_fields = ["t_start_s", "t_end_s", "state", "w_s"]
    allowed_states = {"Warmup", "Track", "Hold", "Degraded", "LowRate"}
    rows: List[ScheduleRow] = []

    with open(path, "r", newline="") as handle:
        reader = csv.DictReader(handle)
        if reader.fieldnames is None:
            raise ValueError(
                "Schedule CSV must include header: t_start_s,t_end_s,state,w_s"
            )
        missing = [field for field in required_fields if field not in reader.fieldnames]
        if missing:
            raise ValueError(
                "Schedule CSV must include header: t_start_s,t_end_s,state,w_s"
            )
        prev_end: Optional[float] = None
        for row_num, row in enumerate(reader, start=2):
            try:
                t_start = float(row["t_start_s"])
                t_end = float(row["t_end_s"])
                state = str(row["state"]).strip()
                w_s = float(row["w_s"])
            except (TypeError, ValueError) as exc:
                raise ValueError(
                    f"Invalid value in schedule CSV row {row_num}: {exc}"
                ) from exc

            if t_start >= t_end:
                raise ValueError(
                    f"Schedule row {row_num} has t_start_s >= t_end_s"
                )
            if state not in allowed_states:
                raise ValueError(
                    f"Schedule row {row_num} has invalid state: {state}"
                )
            if not (w_min_s <= w_s <= w_max_s):
                raise ValueError(
                    f"Schedule row {row_num} has w_s outside [{w_min_s}, {w_max_s}]"
                )
            if prev_end is not None and t_start < prev_end:
                raise ValueError("Schedule rows must be sorted and non-overlapping")
            prev_end = t_end
            rows.append(
                ScheduleRow(
                    t_start_s=t_start,
                    t_end_s=t_end,
                    state=state,
                    w_s=w_s,
                )
            )

    if not rows:
        raise ValueError("Schedule CSV must include at least one row")

    return rows


def page_hinkley_update(
    x: float,
    mean: float,
    n: int,
    cumulative: float,
    min_cumulative: float,
    delta: float,
    threshold: float,
) -> Tuple[float, float, float, float, bool, int]:
    n += 1
    mean = mean + (x - mean) / n
    cumulative = cumulative + (x - mean - delta)
    min_cumulative = min(min_cumulative, cumulative)
    stat = cumulative - min_cumulative
    alarm = stat > threshold
    return mean, cumulative, min_cumulative, stat, alarm, n


def run_controller(
    timestamps: np.ndarray,
    step_s: float,
    w_min_s: float,
    w_max_s: float,
    rate_smooth_tau_s: float,
    w_contract_max_per_step: float,
    w_expand_max_per_step: float,
    warmup_s: float,
    debounce_s: float,
    ph_threshold: float,
    ph_delta: float,
    target_rel_unc: float,
    min_events: int,
    tg_ms: float,
    schedule: Optional[List[ScheduleRow]] = None,
) -> ControllerResult:
    if timestamps.size == 0:
        return ControllerResult(
            t_end_s=np.array([]),
            t_mid_s=np.array([]),
            w_s=np.array([]),
            state=[],
            rate_cps=np.array([]),
            rate_smooth_cps=np.array([]),
            w_des_s=np.array([]),
            change_stat=np.array([]),
            ph_alarm=np.array([], dtype=bool),
            n_events=np.array([], dtype=int),
        )

    duration = float(timestamps[-1])
    step_times = np.arange(step_s, duration + 1e-9, step_s)
    if step_times.size == 0:
        step_times = np.array([duration])

    w_s = np.zeros(step_times.size, dtype=float)
    rate_cps = np.zeros(step_times.size, dtype=float)
    rate_smooth_cps = np.zeros(step_times.size, dtype=float)
    w_des_s = np.zeros(step_times.size, dtype=float)
    change_stat = np.zeros(step_times.size, dtype=float)
    ph_alarm = np.zeros(step_times.size, dtype=bool)
    n_events = np.zeros(step_times.size, dtype=int)
    states: List[str] = []

    mean = 0.0
    cumulative = 0.0
    min_cumulative = 0.0
    n_ph = 0
    alarm_start: Optional[float] = None
    alarm_clear_start: Optional[float] = None

    current_w = min(max(5.0, w_min_s), w_max_s)
    current_state = "Warmup"
    rate_smooth_prev = 0.0
    n_target = (1.0 / target_rel_unc) ** 2

    schedule_starts: Optional[List[float]] = None
    if schedule is not None:
        schedule_starts = [row.t_start_s for row in schedule]

    for idx, t_end in enumerate(step_times):
        if schedule is not None:
            if t_end < schedule[0].t_start_s:
                row = schedule[0]
            elif t_end >= schedule[-1].t_end_s:
                row = schedule[-1]
            else:
                assert schedule_starts is not None
                row_idx = bisect.bisect_right(schedule_starts, t_end) - 1
                row = schedule[row_idx]
                if t_end >= row.t_end_s:
                    raise ValueError(
                        f"Schedule does not cover t_end_s={t_end:.6f}"
                    )
            current_state = row.state
            current_w = row.w_s
        t_start = max(0.0, t_end - current_w)
        left = np.searchsorted(timestamps, t_start, side="left")
        right = np.searchsorted(timestamps, t_end, side="left")
        count = int(right - left)
        n_events[idx] = count
        rate = count / current_w if current_w > 0 else 0.0
        rate_cps[idx] = rate
        if idx == 0:
            rate_smooth = rate
        else:
            alpha = 1.0 - np.exp(-step_s / rate_smooth_tau_s)
            rate_smooth = (1.0 - alpha) * rate_smooth_prev + alpha * rate
        rate_smooth_prev = rate_smooth
        rate_smooth_cps[idx] = rate_smooth

        w_des = n_target / max(rate_smooth, 1e-9)
        w_des = float(np.clip(w_des, w_min_s, w_max_s))
        if schedule is not None:
            w_des = current_w
        w_des_s[idx] = w_des

        if schedule is None:
            log_rate = np.log(rate + 1e-6)
            mean, cumulative, min_cumulative, stat, alarm, n_ph = page_hinkley_update(
                log_rate, mean, n_ph, cumulative, min_cumulative, ph_delta, ph_threshold
            )
            change_stat[idx] = stat
            ph_alarm[idx] = alarm

            if alarm:
                if alarm_start is None:
                    alarm_start = t_end
                alarm_clear_start = None
            else:
                alarm_start = None
                if alarm_clear_start is None:
                    alarm_clear_start = t_end

            alarm_sustained = alarm and alarm_start is not None and (
                t_end - alarm_start >= debounce_s
            )
            alarm_cleared = (not alarm) and alarm_clear_start is not None and (
                t_end - alarm_clear_start >= debounce_s
            )
            lowrate_condition = count < min_events or rate < (min_events / w_max_s)

            if t_end < warmup_s:
                current_state = "Warmup"
            else:
                if alarm_sustained:
                    if count >= min_events:
                        current_state = "Hold"
                    else:
                        current_state = "Degraded"
                else:
                    if current_state in {"Hold", "Degraded"}:
                        if alarm_cleared:
                            current_state = "Track"
                    elif current_state == "LowRate":
                        if count >= min_events and alarm_cleared:
                            current_state = "Track"
                    else:
                        if lowrate_condition:
                            current_state = "LowRate"
                        else:
                            current_state = "Track"

            # Desired window controller with asymmetric rate limits.
            if current_state == "Track":
                dw = w_des - current_w
                dw = float(np.clip(dw, -w_contract_max_per_step, w_expand_max_per_step))
                current_w = float(np.clip(current_w + dw, w_min_s, w_max_s))
            elif current_state in {"Degraded", "LowRate"}:
                current_w = min(w_max_s, current_w + w_expand_max_per_step)
            elif current_state == "Hold":
                current_w = current_w
            elif current_state == "Warmup":
                current_w = current_w
        else:
            change_stat[idx] = 0.0
            ph_alarm[idx] = False

        w_s[idx] = current_w
        states.append(current_state)

    t_mid = np.maximum(0.0, step_times - 0.5 * w_s)
    return ControllerResult(
        t_end_s=step_times,
        t_mid_s=t_mid,
        w_s=w_s,
        state=states,
        rate_cps=rate_cps,
        rate_smooth_cps=rate_smooth_cps,
        w_des_s=w_des_s,
        change_stat=change_stat,
        ph_alarm=ph_alarm,
        n_events=n_events,
    )


def _state_segments(times: np.ndarray, states: Sequence[str]) -> List[Tuple[float, float, str]]:
    if times.size == 0:
        return []
    segments: List[Tuple[float, float, str]] = []
    start = times[0]
    current = states[0]
    for t, s in zip(times[1:], states[1:]):
        if s != current:
            segments.append((start, t, current))
            start = t
            current = s
    segments.append((start, times[-1], current))
    return segments


def plot_figure(
    result: ControllerResult,
    out_png: str,
    out_pdf: Optional[str],
    title: Optional[str],
    show_ph_markers: bool,
) -> None:
    if result.t_end_s.size == 0:
        raise ValueError("No events available for plotting")

    total_duration = float(result.t_end_s[-1])
    use_minutes = total_duration >= 300.0
    scale = 60.0 if use_minutes else 1.0
    x_label = "Time (min)" if use_minutes else "Time (s)"

    t_plot = result.t_end_s / scale
    t_end_plot = result.t_end_s / scale

    fig, (ax_w, ax_r) = plt.subplots(
        2, 1, figsize=(10.5, 6.5), sharex=True, gridspec_kw={"height_ratios": [1, 1]}
    )

    state_colors = {
        "Warmup": OKABE_ITO["grey"],
        "Track": OKABE_ITO["skyblue"],
        "Hold": OKABE_ITO["vermillion"],
        "Degraded": OKABE_ITO["reddishpurple"],
        "LowRate": OKABE_ITO["orange"],
    }

    for start, end, state in _state_segments(t_end_plot, result.state):
        color = state_colors.get(state, OKABE_ITO["grey"])
        ax_w.axvspan(start, end, color=color, alpha=0.18, lw=0)

    ax_w.plot(t_plot, result.w_s, color=OKABE_ITO["blue"], lw=2)
    ax_w.set_ylabel("Window length (s)")

    ax_r.plot(t_plot, result.rate_cps, color=OKABE_ITO["black"], lw=1.5)
    if show_ph_markers and np.any(result.ph_alarm):
        ax_r.scatter(
            t_plot[result.ph_alarm],
            result.rate_cps[result.ph_alarm],
            color=OKABE_ITO["vermillion"],
            s=18,
            marker="o",
            zorder=3,
            label="PH alarm",
        )
    ax_r.set_ylabel("Singles rate (cps)")
    ax_r.set_xlabel(x_label)

    legend_handles = []
    for state, color in state_colors.items():
        legend_handles.append(
            plt.Line2D([0], [0], color=color, lw=6, alpha=0.3, label=state)
        )
    ax_w.legend(
        handles=legend_handles,
        loc="upper right",
        frameon=True,
        framealpha=0.9,
        title="State",
    )

    if title:
        fig.suptitle(title, fontsize=12)

    for ax in (ax_w, ax_r):
        ax.grid(alpha=0.2, linestyle=":")

    fig.tight_layout()
    fig.savefig(out_png, dpi=300)
    if out_pdf is not None:
        fig.savefig(out_pdf)
    plt.close(fig)


def write_csv(result: ControllerResult, out_csv: str) -> None:
    with open(out_csv, "w", newline="") as handle:
        writer = csv.writer(handle)
        writer.writerow(
            [
                "t_end_s",
                "t_mid_s",
                "w_s",
                "state",
                "rate_cps",
                "rate_smooth_cps",
                "w_des_s",
                "change_stat",
                "ph_alarm",
                "n_events",
            ]
        )
        for idx, state in enumerate(result.state):
            writer.writerow(
                [
                    f"{result.t_end_s[idx]:.6f}",
                    f"{result.t_mid_s[idx]:.6f}",
                    f"{result.w_s[idx]:.6f}",
                    state,
                    f"{result.rate_cps[idx]:.6f}",
                    f"{result.rate_smooth_cps[idx]:.6f}",
                    f"{result.w_des_s[idx]:.6f}",
                    f"{result.change_stat[idx]:.6f}",
                    str(bool(result.ph_alarm[idx])),
                    str(int(result.n_events[idx])),
                ]
            )


def _resolve_outputs(out_arg: str) -> Tuple[str, Optional[str], str]:
    base, ext = os.path.splitext(out_arg)
    if ext.lower() == ".png":
        out_png = out_arg
        out_pdf = None
        csv_base = base
    elif ext.lower() == ".pdf":
        out_png = f"{base}.png"
        out_pdf = out_arg
        csv_base = base
    else:
        out_png = f"{out_arg}.png"
        out_pdf = f"{out_arg}.pdf"
        csv_base = out_arg
    out_csv = f"{csv_base}_windows.csv"
    return out_png, out_pdf, out_csv


def _load_lmx_files(args: argparse.Namespace) -> List[str]:
    if args.lmx_dir:
        entries = [
            os.path.join(args.lmx_dir, name)
            for name in os.listdir(args.lmx_dir)
            if name.lower().endswith(".lmx")
        ]
        entries.sort()
        return entries
    return list(args.lmx_files or [])


def main() -> None:
    args = parse_args()

    lmx_files = _load_lmx_files(args)
    if not lmx_files:
        raise SystemExit("No LMX files provided")

    sessions = [read_lmx_timestamps(path) for path in lmx_files]
    timestamps = stitch_sessions(sessions, args.gap_s)
    schedule_rows = None
    if args.schedule_csv:
        schedule_rows = _load_schedule_csv(
            args.schedule_csv,
            w_min_s=args.w_min_s,
            w_max_s=args.w_max_s,
        )

    result = run_controller(
        timestamps=timestamps,
        step_s=args.step_s,
        w_min_s=args.w_min_s,
        w_max_s=args.w_max_s,
        rate_smooth_tau_s=args.rate_smooth_tau_s,
        w_contract_max_per_step=args.w_contract_max_per_step,
        w_expand_max_per_step=args.w_expand_max_per_step,
        warmup_s=args.warmup_s,
        debounce_s=args.debounce_s,
        ph_threshold=args.ph_threshold,
        ph_delta=args.ph_delta,
        target_rel_unc=args.target_rel_unc,
        min_events=args.min_events,
        tg_ms=args.tg_ms,
        schedule=schedule_rows,
    )

    out_png, out_pdf, out_csv = _resolve_outputs(args.out)
    plot_figure(result, out_png, out_pdf, args.title, args.show_ph_markers)
    write_csv(result, out_csv)

    print(f"Saved figure: {out_png}")
    if out_pdf is not None:
        print(f"Saved figure: {out_pdf}")
    print(f"Saved CSV: {out_csv}")


if __name__ == "__main__":
    main()
