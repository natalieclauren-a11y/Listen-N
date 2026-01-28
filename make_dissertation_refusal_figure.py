#!/usr/bin/env python3
"""
Generate the dissertation figure for a real refusal episode for LANL_2025_007.
"""

# Episode manager config JSON (verbatim, provided by the user):
# {
#   "<REPLACE_WITH_EPISODE_MANAGER_CONFIG_JSON>"
# }

from __future__ import annotations

import argparse
import csv
import datetime as dt
import glob
import json
import os
import re
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from typing import Dict, Iterable, Iterator, List, Optional, Sequence, Tuple

import matplotlib.pyplot as plt
import numpy as np

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
class LmxHeader:
    t_step: int
    nlines: int
    total_counts: int
    fifo_count: int


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Generate dissertation figure for a real refusal episode using "
            "the in-repo localization pipeline."
        )
    )
    parser.add_argument("--lmx", help="Explicit LMX file path")
    parser.add_argument("--lmx-dir", help="Directory with LMX files")
    parser.add_argument("--glob", default="*.lmx", help="LMX glob pattern")
    parser.add_argument("--out", required=True, help="Output figure path (.png or .pdf)")
    parser.add_argument("--run-id", default="LANL_2025_007", help="Run identifier")
    parser.add_argument("--model-dir", required=True, help="Model artifacts directory")
    parser.add_argument(
        "--episode-config",
        required=True,
        help="Episode manager config JSON (TriggerPolicyDocument)",
    )
    parser.add_argument(
        "--feature-config",
        help="Optional feature config (if pipeline requires external config)",
    )
    parser.add_argument(
        "--episode-max-duration",
        type=float,
        help="Override max episode duration (seconds)",
    )
    parser.add_argument(
        "--rate-bin-s",
        type=float,
        default=0.05,
        help="Rate bin size in seconds",
    )
    parser.add_argument(
        "--show-counts",
        action="store_true",
        help="Show accumulated counts in top panel",
    )
    parser.add_argument("--debug", action="store_true", help="Verbose logging")
    return parser.parse_args()


def find_lmx_files(lmx_path: Optional[str], lmx_dir: Optional[str], pattern: str) -> List[str]:
    if lmx_path:
        return [lmx_path]
    if not lmx_dir:
        raise ValueError("Either --lmx or --lmx-dir must be provided.")
    search_pattern = os.path.join(lmx_dir, pattern)
    return sorted(glob.glob(search_pattern))


def parse_lmx_header(path: str) -> LmxHeader:
    header_max = 200
    t_step: Optional[int] = None
    nlines: Optional[int] = None
    total_counts: Optional[int] = None
    fifo_count: Optional[int] = None
    header_lines: List[str] = []

    with open(path, "rb") as handle:
        for i in range(header_max):
            line = handle.readline()
            if not line:
                break
            decoded = line.decode(errors="ignore")
            header_lines.append(decoded.rstrip("\n"))
            if "BinaryDataClockTickLength" in decoded:
                t_step = int(decoded.split()[-2])
            elif "BinaryDataFollows" in decoded:
                nlines = i + 1
            elif "InternalScaler" in decoded:
                total_counts = int(decoded.split()[-1])
            elif "FifoLostCounts" in decoded:
                fifo_count = int(decoded.split()[-1])
            if (
                t_step is not None
                and nlines is not None
                and total_counts is not None
                and fifo_count is not None
            ):
                break

    missing_fields = []
    if t_step is None:
        missing_fields.append("BinaryDataClockTickLength")
    if nlines is None:
        missing_fields.append("BinaryDataFollows")
    if total_counts is None:
        missing_fields.append("InternalScaler")
    if fifo_count is None:
        missing_fields.append("FifoLostCounts")
    if missing_fields:
        recent_lines = "\n".join(header_lines[-10:])
        raise ValueError(
            "Unable to parse LMX header for "
            f"{path}. Missing fields: {', '.join(missing_fields)}. "
            f"Last header lines:\n{recent_lines}"
        )

    return LmxHeader(
        t_step=t_step,
        nlines=nlines,
        total_counts=total_counts,
        fifo_count=fifo_count,
    )


def channels_present(mask: int) -> List[int]:
    return [idx + 1 for idx in range(32) if (mask >> idx) & 1]


def check_channels(channels: Iterable[int], channel_bitmask: Sequence[int]) -> bool:
    return any(channel in channel_bitmask for channel in channels)


def iter_lmx_events(path: str, debug: bool = False) -> Iterator[Tuple[float, List[int]]]:
    header = parse_lmx_header(path)
    add = 0.0
    read_events = 0
    channel_bitmask = list(range(1, 16))

    with open(path, "rb") as handle:
        for _ in range(header.nlines):
            if not handle.readline():
                break

        while True:
            chunk = handle.read(8)
            if len(chunk) < 8:
                break
            l_bytes = chunk[:4]
            ticks = int.from_bytes(chunk[4:], byteorder=sys.byteorder, signed=False)
            read_events += 1

            if l_bytes == b"\x00\x00\x00\x00":
                marker = handle.read(8)
                if len(marker) < 8:
                    break
                marker_word = marker[:4]
                marker_ticks = int.from_bytes(
                    marker[4:], byteorder=sys.byteorder, signed=False
                )
                read_events += 1
                t = marker_ticks * header.t_step * 1e-9 + add
                marker_val = int.from_bytes(
                    marker_word, byteorder=sys.byteorder, signed=False
                )
                if marker_val == 0x01000000:
                    add = float(t)
                elif marker_val == 0xFFFFFFFF:
                    break

                if read_events > header.total_counts + 2:
                    break
                continue

            l_val = int.from_bytes(l_bytes, byteorder=sys.byteorder, signed=False)
            t = ticks * header.t_step * 1e-9 + add
            channels = channels_present(l_val)
            if check_channels(channels, channel_bitmask):
                filtered = [ch for ch in channels if ch in channel_bitmask]
                if filtered:
                    yield float(t), filtered

            if read_events > header.total_counts + 2:
                break

    if debug:
        print(f"Parsed LMX {path}: t_step={header.t_step}, total_counts={header.total_counts}")


def iter_lmx_files(paths: List[str], debug: bool = False) -> Iterator[Tuple[float, List[int]]]:
    last_t: Optional[float] = None
    file_offset = 0.0
    for path in paths:
        for t, channels in iter_lmx_events(path, debug=debug):
            if last_t is not None and t + file_offset < last_t:
                file_offset = last_t - t + 1e-9
            t_adj = t + file_offset
            last_t = t_adj
            yield t_adj, channels


def write_list_mode(paths: List[str], out_path: str, debug: bool = False) -> Tuple[float, LmxHeader]:
    first_event_time: Optional[float] = None
    header = parse_lmx_header(paths[0])

    with open(out_path, "w", encoding="utf-8") as handle:
        for t, channels in iter_lmx_files(paths, debug=debug):
            if first_event_time is None:
                first_event_time = t
            t_rel = t - first_event_time
            if t_rel < 0:
                continue
            t_us = int(round(t_rel * 1e6))
            for channel in channels:
                det_id = channel - 1
                if det_id < 0:
                    continue
                handle.write(f"{t_us},{det_id}\n")

    if first_event_time is None:
        raise RuntimeError("No events found in LMX files.")

    return first_event_time, header


def run_command(cmd: List[str], debug: bool = False) -> None:
    if debug:
        print("Running:", " ".join(cmd))
    result = subprocess.run(cmd, check=False, capture_output=not debug, text=True)
    if result.returncode != 0:
        stderr = result.stderr.strip() if result.stderr else ""
        stdout = result.stdout.strip() if result.stdout else ""
        raise RuntimeError(
            f"Command failed: {' '.join(cmd)}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}"
        )


def build_runtime_config(
    base_config: Dict[str, object],
    artifacts_dir: str,
    trigger_policy_path: str,
    output_path: str,
) -> None:
    base_config = dict(base_config)
    base_config["ArtifactsDirectory"] = artifacts_dir
    base_config["TriggerPolicyPath"] = trigger_policy_path
    with open(output_path, "w", encoding="utf-8") as handle:
        json.dump(base_config, handle, indent=2)


def load_runtime_config_template() -> Dict[str, object]:
    path = os.path.join(os.getcwd(), "localization_runtime_config.json")
    if not os.path.exists(path):
        return {
            "ArtifactsDirectory": "artifacts",
            "TriggerPolicyPath": "trigger_policy.json",
            "AllowedSchemaHashes": ["v1", "v1.1"],
            "SupportedTrainingDurationsSec": [30, 60],
            "MaxMlQueueDepth": 1,
            "MaxMlRequestsPerEpisode": 2,
            "DecisionLoggingEnabled": True,
            "FailFastOnStartupError": True,
        }
    with open(path, "r", encoding="utf-8") as handle:
        return json.load(handle)


def load_pipeline_config(model_dir: str) -> Dict[str, object]:
    path = os.path.join(model_dir, "pipeline_config.json")
    if not os.path.exists(path):
        raise FileNotFoundError(f"pipeline_config.json not found at {path}")
    with open(path, "r", encoding="utf-8") as handle:
        return json.load(handle)


def load_episode_config(path: str, episode_max_duration: Optional[float]) -> Dict[str, object]:
    with open(path, "r", encoding="utf-8") as handle:
        config = json.load(handle)
    if episode_max_duration is not None:
        config["MaxPublishDurationSeconds"] = episode_max_duration
    return config


def to_epoch_seconds(ts: str) -> float:
    parsed = dt.datetime.fromisoformat(ts.replace("Z", "+00:00"))
    return parsed.timestamp()


def parse_localization_events(path: str) -> List[Dict[str, object]]:
    events: List[Dict[str, object]] = []
    with open(path, "r", encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            events.append(json.loads(line))
    return events


def compute_thresholds(config: Dict[str, object], duration_s: float) -> int:
    nmin_30 = int(config.get("Nmin_15cm_30s", 0))
    nmin_60 = int(config.get("Nmin_15cm_60s", 0))
    min_duration = float(config.get("MinPublishDurationSeconds", 30.0))
    max_duration = float(config.get("MaxPublishDurationSeconds", 60.0))

    if max_duration <= min_duration:
        return nmin_60
    if duration_s <= min_duration:
        return nmin_30
    if duration_s >= max_duration:
        return nmin_60
    ratio = (duration_s - min_duration) / (max_duration - min_duration)
    threshold = nmin_30 + (nmin_60 - nmin_30) * ratio
    return int(round(threshold))


def infer_probe_outputs(
    request_events: List[Dict[str, object]],
    model_dir: str,
    debug: bool,
) -> List[Dict[str, object]]:
    outputs: List[Dict[str, object]] = []
    for idx, event in enumerate(request_events):
        counts = event.get("channel_counts")
        duration_s = event.get("duration_s")
        if counts is None or duration_s is None:
            raise RuntimeError(f"Missing counts or duration in request event {idx}.")

        row = {f"Channel{i+1}": float(counts[i]) for i in range(min(15, len(counts)))}
        row["duration_s"] = float(duration_s)

        with tempfile.TemporaryDirectory() as temp_dir:
            input_path = os.path.join(temp_dir, "row.json")
            with open(input_path, "w", encoding="utf-8") as handle:
                json.dump(row, handle)

            cmd = [
                "dotnet",
                "run",
                "--project",
                "Localization.RuntimeCheck/Localization.RuntimeCheck.csproj",
                "--",
                "--artifacts-dir",
                model_dir,
                "--input",
                input_path,
                "--format",
                "json",
                "--strict-schema",
                "true",
                "--print-features",
                "false",
            ]
            result = subprocess.run(cmd, check=False, capture_output=True, text=True)
            if result.returncode != 0:
                raise RuntimeError(
                    "Runtime check failed:\n"
                    f"STDOUT:\n{result.stdout}\nSTDERR:\n{result.stderr}"
                )

            output = result.stdout
            label_match = re.search(r"Outcome: (.+)", output)
            clf_match = re.search(r"Classifier: probability=([0-9.eE+-]+)", output)
            ood_match = re.search(r"OOD: distance=([0-9.eE+-]+), threshold=([0-9.eE+-]+), pass=(FAIL|PASS)", output)
            if not (label_match and clf_match and ood_match):
                raise RuntimeError(
                    f"Failed to parse runtime check output for probe {idx}. Output:\n{output}"
                )

            label = label_match.group(1).strip()
            probability = float(clf_match.group(1))
            mahal = float(ood_match.group(1))
            threshold = float(ood_match.group(2))
            ood_flag = ood_match.group(3) == "FAIL"

            outputs.append(
                {
                    "label": label,
                    "probability": probability,
                    "mahalanobis": mahal,
                    "threshold": threshold,
                    "ood": ood_flag,
                }
            )

    if debug:
        print(f"Computed ML outputs for {len(outputs)} probe requests.")

    return outputs


def collect_episode_events(
    paths: List[str],
    first_event_time: float,
    episode_start_s: float,
    episode_end_s: float,
    debug: bool,
) -> List[float]:
    event_times: List[float] = []
    for t, channels in iter_lmx_files(paths, debug=debug):
        t_rel = t - first_event_time
        if t_rel < episode_start_s:
            continue
        if t_rel > episode_end_s:
            break
        for _ in channels:
            event_times.append(t_rel - episode_start_s)
    return event_times


def build_counts_series(event_times: List[float], duration_s: float) -> Tuple[List[float], List[int]]:
    times = [0.0]
    counts = [0]
    if not event_times:
        return times + [duration_s], counts + [0]

    event_times_sorted = sorted(event_times)
    for idx, t in enumerate(event_times_sorted, start=1):
        times.append(t)
        counts.append(idx)
    if times[-1] < duration_s:
        times.append(duration_s)
        counts.append(counts[-1])
    return times, counts


def build_rate_series(event_times: List[float], duration_s: float, bin_s: float) -> Tuple[np.ndarray, np.ndarray]:
    if duration_s <= 0 or bin_s <= 0:
        return np.array([]), np.array([])
    bins = np.arange(0.0, duration_s + bin_s, bin_s)
    counts, edges = np.histogram(event_times, bins=bins)
    rates = counts / bin_s
    centers = edges[:-1] + bin_s / 2
    return centers, rates


def plot_figure(
    out_path: str,
    run_id: str,
    terminal_reason: str,
    duration_s: float,
    event_times: List[float],
    rate_bins_s: float,
    probe_times: List[float],
    mahalanobis: List[float],
    threshold: float,
    ood_flags: List[bool],
    show_counts: bool,
) -> None:
    fig, (ax_top, ax_bottom) = plt.subplots(
        2,
        1,
        figsize=(10, 6.5),
        sharex=True,
        gridspec_kw={"height_ratios": [1, 1.1]},
    )

    duration_line, = ax_top.plot(
        [0.0, duration_s],
        [0.0, duration_s],
        color=OKABE_ITO["orange"],
        label="Accumulated duration",
    )

    counts_line = None
    if show_counts:
        times, counts = build_counts_series(event_times, duration_s)
        ax_counts = ax_top.twinx()
        counts_line = ax_counts.step(
            times,
            counts,
            where="post",
            color=OKABE_ITO["blue"],
            label="Accumulated counts",
        )[0]
        ax_counts.set_ylabel("Accumulated counts")

    terminal_line = ax_top.axvline(
        duration_s,
        color=OKABE_ITO["gray"],
        linestyle="--",
        label="Terminal refusal",
    )
    ax_top.set_ylabel("Accumulated duration (s)")

    rate_centers, rate_values = build_rate_series(event_times, duration_s, rate_bins_s)
    rate_line = ax_bottom.step(
        rate_centers,
        rate_values,
        where="mid",
        color=OKABE_ITO["sky"],
        label="Rate (cps)",
    )[0]
    ax_bottom.set_ylabel("Rate (cps)")

    ax_maha = ax_bottom.twinx()
    maha_line = ax_maha.plot(
        probe_times,
        mahalanobis,
        color=OKABE_ITO["vermillion"],
        marker="o",
        markersize=4,
        label="Mahalanobis distance",
    )[0]
    threshold_line = ax_maha.axhline(
        threshold,
        color=OKABE_ITO["purple"],
        linestyle="--",
        label="OOD threshold",
    )

    if probe_times:
        ood_clear_times = [t for t, ood in zip(probe_times, ood_flags) if not ood]
        if ood_clear_times:
            ax_maha.scatter(
                ood_clear_times,
                [mahalanobis[probe_times.index(t)] for t in ood_clear_times],
                color=OKABE_ITO["green"],
                s=20,
                label="OOD cleared",
                zorder=3,
            )

    ax_maha.set_ylabel("Mahalanobis distance")
    ax_bottom.set_xlabel("Time since episode start (s)")

    title = f"{run_id} terminal reason: {terminal_reason}"
    fig.suptitle(title)
    fig.text(
        0.5,
        0.92,
        "Representative localization refusal at maximum supported duration. Persistent out-of-distribution behavior results in an explicit refusal, demonstrating conservative failure handling.",
        ha="center",
        va="center",
        fontsize=9,
    )

    handles = [duration_line, terminal_line, rate_line, maha_line, threshold_line]
    labels = [h.get_label() for h in handles]
    if counts_line is not None:
        handles.insert(1, counts_line)
        labels.insert(1, counts_line.get_label())
    ax_bottom.legend(handles, labels, loc="upper right")

    fig.tight_layout(rect=[0, 0.02, 1, 0.9])

    out_dir = os.path.dirname(os.path.abspath(out_path))
    if out_dir and not os.path.exists(out_dir):
        os.makedirs(out_dir, exist_ok=True)

    if out_path.lower().endswith(".png"):
        fig.savefig(out_path, dpi=300)
    else:
        fig.savefig(out_path)
    plt.close(fig)


def main() -> None:
    args = parse_args()
    lmx_files = find_lmx_files(args.lmx, args.lmx_dir, args.glob)
    if not lmx_files:
        raise FileNotFoundError("No LMX files found with provided inputs.")

    out_dir = os.path.dirname(os.path.abspath(args.out))
    run_dir = os.path.join(out_dir if out_dir else os.getcwd(), f"replay_{args.run_id}")
    os.makedirs(run_dir, exist_ok=True)

    list_mode_path = os.path.join(run_dir, "list_mode.txt")
    first_event_time, header = write_list_mode(lmx_files, list_mode_path, debug=args.debug)

    replay_output_root = os.path.join(run_dir, "rt_replay")
    os.makedirs(replay_output_root, exist_ok=True)

    run_command(
        [
            "dotnet",
            "run",
            "--project",
            "Listen-N.csproj",
            "--",
            "replay",
            "--input",
            list_mode_path,
            "--config",
            os.path.join("configs", "rt_replay_v1.json"),
            "--output",
            replay_output_root,
            "--run-id",
            args.run_id,
            "--start-utc",
            "1970-01-01T00:00:00Z",
        ],
        debug=args.debug,
    )

    rt_run_dir = os.path.join(replay_output_root, args.run_id)
    rt_windows_path = os.path.join(rt_run_dir, "rt_windows.ndjson")
    if not os.path.exists(rt_windows_path):
        raise FileNotFoundError(f"rt_windows.ndjson not found at {rt_windows_path}")

    runtime_template = load_runtime_config_template()
    episode_config = load_episode_config(args.episode_config, args.episode_max_duration)
    trigger_policy_path = os.path.abspath(args.episode_config)
    runtime_config_path = os.path.join(run_dir, "runtime_config.json")

    build_runtime_config(
        runtime_template,
        artifacts_dir=os.path.abspath(args.model_dir),
        trigger_policy_path=trigger_policy_path,
        output_path=runtime_config_path,
    )

    localize_output_root = os.path.join(run_dir, "localize")
    os.makedirs(localize_output_root, exist_ok=True)

    run_command(
        [
            "dotnet",
            "run",
            "--project",
            "Listen-N.csproj",
            "--",
            "localize-replay",
            "--input",
            rt_windows_path,
            "--output",
            localize_output_root,
            "--models",
            os.path.abspath(args.model_dir),
            "--config",
            runtime_config_path,
            "--run-id",
            args.run_id,
        ],
        debug=args.debug,
    )

    events_path = os.path.join(localize_output_root, "localization_events.ndjson")
    if not os.path.exists(events_path):
        raise FileNotFoundError(f"localization_events.ndjson not found at {events_path}")

    events = parse_localization_events(events_path)
    request_events = [e for e in events if e.get("event_type") == "localization_requested"]
    if not request_events:
        raise RuntimeError("No localization_requested events found.")

    terminal_events = [
        e
        for e in events
        if e.get("event_type") in {"localization_refused", "localization_result"}
    ]
    if not terminal_events:
        raise RuntimeError("No terminal localization events found.")
    terminal_event = terminal_events[-1]

    requests_by_id = {e.get("request_id"): e for e in request_events}
    terminal_request = requests_by_id.get(terminal_event.get("request_id"), request_events[-1])

    window_end_utc = terminal_request.get("window_end_utc")
    if window_end_utc is None:
        raise RuntimeError("Missing window_end_utc for terminal request.")

    terminal_duration = float(terminal_request.get("duration_s", 0.0))
    terminal_counts = int(sum(terminal_request.get("channel_counts", [])))

    episode_end_s = to_epoch_seconds(window_end_utc)
    episode_start_s = episode_end_s - terminal_duration

    if request_events:
        first_request = min(request_events, key=lambda e: to_epoch_seconds(e["window_end_utc"]))
        first_end_s = to_epoch_seconds(first_request["window_end_utc"])
        first_duration = float(first_request.get("duration_s", 0.0))
        episode_start_s = first_end_s - first_duration

    terminal_reason = "UNKNOWN"
    if terminal_event.get("event_type") == "localization_refused":
        codes = terminal_event.get("refusal_reason_codes") or []
        terminal_reason = "|".join(codes) if codes else "REFUSED"
    elif terminal_event.get("event_type") == "localization_result":
        terminal_reason = "PUBLISHED"

    if terminal_reason != "OOD_AT_MAX_DURATION":
        print(
            "ERROR: Expected terminal reason OOD_AT_MAX_DURATION but got "
            f"{terminal_reason} (event_type={terminal_event.get('event_type')}).",
            file=sys.stderr,
        )

    probe_request_events = [
        e for e in request_events if e.get("trigger_basis", {}).get("request_kind") == "probe"
    ]
    if not probe_request_events:
        probe_request_events = request_events

    probe_outputs = infer_probe_outputs(probe_request_events, args.model_dir, args.debug)

    probe_times = [float(e.get("duration_s", 0.0)) for e in probe_request_events]
    counts_accum = [int(sum(e.get("channel_counts", []))) for e in probe_request_events]

    clear_requires = int(
        episode_config.get("clear_requires_consecutive_probes")
        or episode_config.get("clearRequiresConsecutiveProbes")
        or episode_config.get("ClearRequiresConsecutiveProbes")
        or 1
    )
    consecutive_clear = 0
    ood_cleared_flags: List[bool] = []

    probe_rows: List[Dict[str, object]] = []
    for event, output in zip(probe_request_events, probe_outputs):
        duration_s = float(event.get("duration_s", 0.0))
        total_counts = int(sum(event.get("channel_counts", [])))
        threshold = compute_thresholds(episode_config, duration_s)
        statistically_sufficient = total_counts >= threshold

        ood_flag = bool(output["ood"])
        if not ood_flag:
            consecutive_clear += 1
        else:
            consecutive_clear = 0
        ood_cleared = consecutive_clear >= clear_requires
        ood_cleared_flags.append(ood_cleared)

        probe_rows.append(
            {
                "t_probe_s": duration_s,
                "counts_accum": total_counts,
                "duration_s": duration_s,
                "statistically_sufficient": statistically_sufficient,
                "classifier_label": output["label"],
                "classifier_confidence": output["probability"],
                "mahalanobis": output["mahalanobis"],
                "ood_flag": ood_flag,
                "ood_cleared_flag": ood_cleared,
            }
        )

    ood_cleared_any = any(ood_cleared_flags)
    mahal_values = [row["mahalanobis"] for row in probe_rows]
    min_mahal = min(mahal_values) if mahal_values else float("nan")
    max_mahal = max(mahal_values) if mahal_values else float("nan")
    ood_threshold = probe_outputs[-1]["threshold"] if probe_outputs else float("nan")

    event_times = collect_episode_events(
        lmx_files,
        first_event_time,
        episode_start_s,
        episode_end_s,
        debug=args.debug,
    )

    duration = episode_end_s - episode_start_s

    plot_figure(
        out_path=args.out,
        run_id=args.run_id,
        terminal_reason=terminal_reason,
        duration_s=duration,
        event_times=event_times,
        rate_bins_s=args.rate_bin_s,
        probe_times=probe_times,
        mahalanobis=mahal_values,
        threshold=ood_threshold,
        ood_flags=[row["ood_flag"] for row in probe_rows],
        show_counts=args.show_counts,
    )

    csv_path = os.path.splitext(args.out)[0] + "_probes.csv"
    with open(csv_path, "w", newline="", encoding="utf-8") as csvfile:
        writer = csv.DictWriter(
            csvfile,
            fieldnames=[
                "t_probe_s",
                "counts_accum",
                "duration_s",
                "statistically_sufficient",
                "classifier_label",
                "classifier_confidence",
                "mahalanobis",
                "ood_flag",
                "ood_cleared_flag",
            ],
        )
        writer.writeheader()
        for row in probe_rows:
            writer.writerow(row)

    summary = (
        "file={file}, t_step={t_step}, fifo_count={fifo_count}, "
        "episode_start_utc_or_s={start:.6f}, episode_end_s={end:.6f}, "
        "terminal_reason={reason}, probes_total={probes}, ood_cleared={ood_cleared}, "
        "min_mahal={min_mahal:.4f}, max_mahal={max_mahal:.4f}, threshold={threshold:.4f}, "
        "duration={duration:.4f}, counts_accumulated={counts}"
    ).format(
        file=os.path.basename(lmx_files[0]),
        t_step=header.t_step,
        fifo_count=header.fifo_count,
        start=episode_start_s,
        end=episode_end_s,
        reason=terminal_reason,
        probes=len(probe_rows),
        ood_cleared=str(ood_cleared_any).lower(),
        min_mahal=min_mahal,
        max_mahal=max_mahal,
        threshold=ood_threshold,
        duration=duration,
        counts=terminal_counts,
    )
    print(summary)


if __name__ == "__main__":
    main()
