#!/usr/bin/env python3
"""Generate accumulated counts/duration figure from LMX files."""

from __future__ import annotations

import argparse
import glob
import os
import sys
from dataclasses import dataclass
from typing import Iterable, Iterator, List, Optional, Sequence, Tuple

import matplotlib.pyplot as plt

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


@dataclass
class EpisodeBounds:
    start: float
    end: float
    reason: str


class LmxEventStream:
    def __init__(self, path: str, debug: bool = False) -> None:
        self.path = path
        self.debug = debug
        self.header = self._parse_header(path)
        self.gate_markers: List[Tuple[str, float]] = []
        self.last_marker_type: Optional[str] = None

    def _parse_header(self, path: str) -> LmxHeader:
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

    def __iter__(self) -> Iterator[Tuple[float, bool]]:
        add = 0.0
        read_events = 0
        channel_bitmask = list(range(1, 16))

        with open(self.path, "rb") as handle:
            for _ in range(self.header.nlines):
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
                    t = marker_ticks * self.header.t_step * 1e-9 + add
                    marker_val = int.from_bytes(
                        marker_word, byteorder=sys.byteorder, signed=False
                    )
                    if marker_val == 0x01000000:
                        self.last_marker_type = "segment"
                        add = float(t)
                        yield float(t), False
                    elif marker_val == 0xFFFFFFFF:
                        break
                    elif marker_val == 0x02000000:
                        self.last_marker_type = "gate-start"
                        self.gate_markers.append(("start", float(t)))
                        yield float(t), False
                    elif marker_val == 0x03000000:
                        self.last_marker_type = "gate-end"
                        self.gate_markers.append(("end", float(t)))
                        yield float(t), False
                    else:
                        self.last_marker_type = "other"
                        yield float(t), False
                    if read_events > self.header.total_counts + 2:
                        break
                    continue

                l_val = int.from_bytes(l_bytes, byteorder=sys.byteorder, signed=False)
                t = ticks * self.header.t_step * 1e-9 + add
                if check_channels(channels_present(l_val), channel_bitmask):
                    self.last_marker_type = None
                    yield float(t), True

                if read_events > self.header.total_counts + 2:
                    break


def channels_present(mask: int) -> List[int]:
    return [idx + 1 for idx in range(32) if (mask >> idx) & 1]


def check_channels(channels: Iterable[int], channel_bitmask: Sequence[int]) -> bool:
    return any(channel in channel_bitmask for channel in channels)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Generate accumulated counts and duration figure from LMX data."
        )
    )
    parser.add_argument("--lmx-dir", required=True, help="Directory with LMX files")
    parser.add_argument("--glob", default="*.lmx", help="Glob pattern")
    parser.add_argument("--out", required=True, help="Output figure path")
    parser.add_argument("--run-id", required=True, help="Run identifier")
    parser.add_argument(
        "--count-thresh",
        type=int,
        default=50000,
        help="Count threshold for publish",
    )
    parser.add_argument(
        "--max-duration",
        type=float,
        default=30.0,
        help="Maximum episode duration in seconds",
    )
    parser.add_argument(
        "--use-gate-markers",
        action=argparse.BooleanOptionalAction,
        default=True,
        help="Use gate start/end markers when present",
    )
    parser.add_argument("--debug", action="store_true", help="Verbose logging")
    return parser.parse_args()


def find_lmx_files(lmx_dir: str, pattern: str) -> List[str]:
    search_pattern = os.path.join(lmx_dir, pattern)
    return sorted(glob.glob(search_pattern))


def select_episode_bounds(
    stream: LmxEventStream,
    max_duration: float,
    use_gate_markers: bool,
    debug: bool,
) -> EpisodeBounds:
    if use_gate_markers:
        gate_pairs = None
        for _, _ in stream:
            gate_pairs = _first_gate_pair(stream.gate_markers)
            if gate_pairs is not None:
                break
        if gate_pairs is not None:
            start, end = gate_pairs
            if debug:
                print(
                    "Episode bounds from gate markers: "
                    f"start={start:.6f}, end={end:.6f}"
                )
            return EpisodeBounds(start=start, end=end, reason="gate markers")
        stream = LmxEventStream(stream.path, debug=stream.debug)

    segment_start: Optional[float] = None
    segment_last: Optional[float] = None
    selected_start: Optional[float] = None
    selected_end: Optional[float] = None

    for t, is_event in stream:
        if is_event:
            if segment_start is None:
                segment_start = t
            segment_last = t
        else:
            if stream.last_marker_type == "segment":
                if segment_start is not None and segment_last is not None:
                    if segment_last - segment_start >= max_duration:
                        selected_start = segment_start
                        selected_end = segment_start + max_duration
                        break
                segment_start = None
                segment_last = None

    if selected_start is None:
        if segment_start is not None and segment_last is not None:
            if segment_last - segment_start >= max_duration:
                selected_start = segment_start
                selected_end = segment_start + max_duration

    if selected_start is None or selected_end is None:
        raise RuntimeError(
            "Unable to find contiguous segment of length >= max_duration."
        )

    if debug:
        print(
            "Episode bounds from segment scan: "
            f"start={selected_start:.6f}, end={selected_end:.6f}"
        )

    return EpisodeBounds(
        start=selected_start,
        end=selected_end,
        reason="segment scan",
    )


def _first_gate_pair(markers: List[Tuple[str, float]]) -> Optional[Tuple[float, float]]:
    start_time: Optional[float] = None
    for marker_type, t in markers:
        if marker_type == "start" and start_time is None:
            start_time = t
        elif marker_type == "end" and start_time is not None:
            return start_time, t
    return None


def build_episode_accumulation(
    stream: LmxEventStream,
    bounds: EpisodeBounds,
    count_thresh: int,
    max_duration: float,
) -> Tuple[List[float], List[int], float, int, float]:
    times: List[float] = [0.0]
    counts: List[int] = [0]

    t0 = bounds.start
    duration_limit = max_duration
    if bounds.end > bounds.start:
        duration_limit = min(max_duration, bounds.end - bounds.start)
    publish_time = bounds.start + duration_limit
    event_count = 0
    for t, is_event in stream:
        if t < t0:
            continue
        if t > t0 + duration_limit:
            break
        if is_event:
            event_count += 1
            times.append(t - t0)
            counts.append(event_count)
            if event_count >= count_thresh:
                publish_time = t
                break

    if times[-1] < publish_time - t0:
        times.append(publish_time - t0)
        counts.append(event_count)

    duration_at_publish = publish_time - t0

    return times, counts, publish_time, event_count, duration_at_publish


def plot_episode(
    times: List[float],
    counts: List[int],
    publish_time: float,
    t0: float,
    run_id: str,
    out_path: str,
) -> None:
    fig, ax_counts = plt.subplots(figsize=(8, 4.5))
    color_counts = OKABE_ITO["blue"]
    color_duration = OKABE_ITO["orange"]
    ax_counts.step(times, counts, where="post", label="Accumulated counts", color=color_counts)
    ax_counts.set_xlabel("Time since episode start (s)")
    ax_counts.set_ylabel("Accumulated counts")

    ax_duration = ax_counts.twinx()
    duration_end = publish_time - t0
    ax_duration.plot(
        [0.0, duration_end],
        [0.0, duration_end],
        label="Accumulated duration (s)",
        color=color_duration,
    )
    ax_duration.set_ylabel("Accumulated duration (s)")

    ax_counts.axvline(duration_end, color=OKABE_ITO["gray"], linestyle="--", label="Publish")

    lines, labels = ax_counts.get_legend_handles_labels()
    lines2, labels2 = ax_duration.get_legend_handles_labels()
    ax_counts.legend(lines + lines2, labels + labels2, loc="upper left")

    ax_counts.set_title(
        f"Accumulated counts and integration duration for run {run_id}"
    )

    fig.tight_layout()

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
    lmx_files = find_lmx_files(args.lmx_dir, args.glob)
    if not lmx_files:
        raise FileNotFoundError(
            f"No LMX files found in {args.lmx_dir} with pattern {args.glob}"
        )

    lmx_path = lmx_files[0]
    stream = LmxEventStream(lmx_path, debug=args.debug)

    if args.debug:
        header = stream.header
        print(
            "Parsed header: "
            f"t_step={header.t_step}, nlines={header.nlines}, "
            f"total_counts={header.total_counts}, fifo_count={header.fifo_count}"
        )

    bounds = select_episode_bounds(
        stream=LmxEventStream(lmx_path, debug=args.debug),
        max_duration=args.max_duration,
        use_gate_markers=args.use_gate_markers,
        debug=args.debug,
    )

    times, counts, publish_time, count_at_publish, duration_at_publish = (
        build_episode_accumulation(
            stream=LmxEventStream(lmx_path, debug=args.debug),
            bounds=bounds,
            count_thresh=args.count_thresh,
            max_duration=args.max_duration,
        )
    )

    plot_episode(
        times=times,
        counts=counts,
        publish_time=publish_time,
        t0=bounds.start,
        run_id=args.run_id,
        out_path=args.out,
    )

    print(
        "Summary: "
        f"file={os.path.basename(lmx_path)}, "
        f"t_step={stream.header.t_step}, fifo_count={stream.header.fifo_count}, "
        f"total_counts={stream.header.total_counts}, t0={bounds.start:.6f}, "
        f"t_publish={publish_time:.6f}, counts_at_publish={count_at_publish}, "
        f"duration_at_publish={duration_at_publish:.6f}"
    )


if __name__ == "__main__":
    main()
