# Deterministic Real-Time Replay

## Overview
The deterministic replay runner executes the real-time adaptive window analysis over list-mode timestamps using a fixed analysis cadence. Parameters are sourced exclusively from `configs/rt_replay_v1.json`, which is hashed and recorded in every replay summary.

## Running a Replay
From the repository root:

```bash
# Run replay mode with the versioned config
# list-mode format: one timestamp per line in microseconds (optionally `timestamp_us,detector_id`)
dotnet run --project Listen-N.csproj -- replay \
  --input path/to/list_mode.txt \
  --config configs/rt_replay_v1.json \
  --output artifacts \
  --start-utc 2024-01-01T00:00:00Z

# Or run the built executable
./bin/Debug/net8.0-windows/Listen-N.exe replay \
  --input path/to/list_mode.txt \
  --config configs/rt_replay_v1.json \
  --output artifacts \
  --start-utc 2024-01-01T00:00:00Z
```

Notes:
- `--run-id` is optional. If omitted, a deterministic run id is derived from the input and config hash.
- `--start-utc` defaults to `1970-01-01T00:00:00Z` if omitted.

## Artifacts
Each replay writes to a run-scoped directory:

```
artifacts/<run_id>/
  rt_windows.ndjson
  replay_summary.json
```

### `rt_windows.ndjson`
Each line is a structured JSON record with:
- Window boundaries and duration.
- Selected gate index/width.
- Per-gate factorial moments (`m1`, `m2`, `m3`) and covariance entries.
- Per-gate `Y(Tg)` and `sigmaY(Tg)`.
- Gate stability indicators and Page–Hinkley change status.
- FSM state, hold flag, and correlation fit diagnostics.

### `replay_summary.json`
Includes:
- Config hash and version.
- Counts processed and steps emitted.
- Gate exclusion statistics.
- Hold enter/exit timestamps.
