"""I/O utilities for Phase 5 uncertainty validation."""

from __future__ import annotations

import json
import pathlib
from dataclasses import dataclass
from typing import Iterable

import numpy as np
import pandas as pd


@dataclass(frozen=True)
class ManifestRecord:
    run_id: str
    covariance_group_id: str
    usable_for_covariance: bool
    data_path: str | None = None


@dataclass
class LoadedRun:
    run_id: str
    covariance_group_id: str
    windows: pd.DataFrame
    cov_mats: np.ndarray
    derived: dict[str, np.ndarray]
    warnings: dict[str, float]


def read_manifest(path: str | pathlib.Path) -> pd.DataFrame:
    """Read manifest CSV or JSON into a DataFrame."""
    path = pathlib.Path(path)
    if not path.exists():
        raise FileNotFoundError(f"Manifest not found: {path}")
    if path.suffix.lower() in {".json", ".jsonl"}:
        if path.suffix.lower() == ".jsonl":
            df = pd.read_json(path, lines=True)
        else:
            with path.open("r", encoding="utf-8") as handle:
                data = json.load(handle)
            df = pd.DataFrame(data)
    else:
        df = pd.read_csv(path)
    return df


def validate_manifest(df: pd.DataFrame) -> None:
    """Validate manifest schema."""
    required = {"run_id", "covariance_group_id", "usable_for_covariance"}
    missing = required - set(df.columns)
    if missing:
        raise ValueError(f"Manifest missing columns: {sorted(missing)}")


def filtered_manifest(df: pd.DataFrame) -> pd.DataFrame:
    """Filter manifest to usable rows."""
    return df[df["usable_for_covariance"].astype(bool)].copy()


def discover_run_file(data_root: str | pathlib.Path, run_id: str) -> pathlib.Path:
    """Discover a run file in the data root."""
    data_root = pathlib.Path(data_root)
    run_dir = data_root / run_id
    candidate_names = [
        "rt_windows.ndjson",
        "windows.ndjson",
        "rt_windows_selected.ndjson",
        "windows.jsonl",
        "rt_windows.jsonl",
        "windows.csv",
        "rt_windows.csv",
    ]
    allowed_exts = {".ndjson", ".jsonl", ".csv"}

    def collect_candidates(paths: Iterable[pathlib.Path]) -> list[pathlib.Path]:
        matches: list[pathlib.Path] = []
        for candidate in paths:
            if not candidate.is_file():
                continue
            name = candidate.name.lower()
            ext = candidate.suffix.lower()
            if ext not in allowed_exts:
                continue
            if name in candidate_names or ("window" in name):
                matches.append(candidate)
        return matches

    search_dirs = [run_dir] if run_dir.is_dir() else [data_root]
    candidates = []
    for search_dir in search_dirs:
        direct = collect_candidates(search_dir.iterdir())
        if direct:
            candidates.extend(direct)

    if not candidates and run_dir.is_dir():
        candidates = collect_candidates(run_dir.rglob("*"))
    elif not candidates:
        run_id_lower = run_id.lower()
        def run_id_match(path: pathlib.Path) -> bool:
            if run_id_lower in path.name.lower():
                return True
            return any(run_id_lower == part.lower() for part in path.parts)
        candidates = [
            candidate
            for candidate in collect_candidates(data_root.rglob("*"))
            if run_id_match(candidate)
        ]

    if not candidates:
        raise FileNotFoundError(f"No run data file found for run_id={run_id} under {data_root}")

    def candidate_key(path: pathlib.Path) -> tuple[int, int, int, str]:
        name = path.name.lower()
        ext = path.suffix.lower()
        ext_priority = 0 if ext in {".ndjson", ".jsonl"} else 1
        exact_priority = 0 if name in candidate_names else 1
        size = path.stat().st_size
        return (ext_priority, exact_priority, -size, path.as_posix())

    return sorted(candidates, key=candidate_key)[0]


def load_window_data(path: str | pathlib.Path) -> pd.DataFrame:
    """Load window data from CSV or JSON variants."""
    path = pathlib.Path(path)
    ext = path.suffix.lower()
    if not path.exists():
        raise FileNotFoundError(f"Window data file not found: {path}")
    size = path.stat().st_size
    if size == 0:
        raise ValueError(f"Window data file is empty: {path}")

    def preview_lines(limit: int = 5, max_chars: int = 200) -> list[str]:
        text = path.read_text(encoding="utf-8-sig", errors="replace")
        lines = [line.strip() for line in text.splitlines() if line.strip()]
        return [line[:max_chars] for line in lines[:limit]]

    def json_error_message(exc: Exception) -> ValueError:
        preview = preview_lines()
        preview_text = "\n".join(preview) if preview else "<no non-empty lines>"
        message = (
            f"Failed to parse window data JSON file: {path} (size {size} bytes). "
            f"{exc.__class__.__name__}: {exc}. Preview:\n{preview_text}"
        )
        return ValueError(message)

    if ext in {".ndjson", ".jsonl"}:
        text = path.read_text(encoding="utf-8-sig", errors="replace")
        lines = [line for line in text.splitlines() if line.strip()]
        if not lines:
            raise ValueError(f"Window data file is empty: {path}")
        try:
            return pd.read_json("\n".join(lines), lines=True)
        except ValueError as exc:
            raise json_error_message(exc) from exc
    if ext == ".json":
        text = path.read_text(encoding="utf-8-sig", errors="replace")
        if not text.strip():
            raise ValueError(f"Window data file is empty: {path}")
        try:
            filtered_lines = "\n".join([line for line in text.splitlines() if line.strip()])
            return pd.read_json(filtered_lines, lines=True)
        except ValueError:
            try:
                return pd.read_json(text, lines=False)
            except ValueError as exc:
                raise json_error_message(exc) from exc
    if ext == ".csv":
        return pd.read_csv(path)
    raise ValueError(f"Unsupported window data file extension: {path.suffix}")


def select_gate(df: pd.DataFrame) -> pd.DataFrame:
    """Apply gate selection if gate columns are present."""
    if "selected_gate" in df.columns and "gate" in df.columns:
        return df[df["gate"] == df["selected_gate"]].copy()
    if "selected_gate_index" in df.columns and "gate_index" in df.columns:
        return df[df["gate_index"] == df["selected_gate_index"]].copy()
    return df


def cov_columns(df: pd.DataFrame) -> list[str]:
    """Identify covariance columns."""
    cov_cols = [col for col in df.columns if col.lower().startswith("cov_m")]
    return cov_cols


def parse_covariance(df: pd.DataFrame) -> tuple[np.ndarray, dict[str, float], np.ndarray]:
    """Parse covariance matrices and return valid mask."""
    warnings: dict[str, float] = {"symmetrized": 0, "nonfinite": 0, "regularized": 0, "regularization_shift_total": 0.0}
    cols = cov_columns(df)
    if not cols:
        raise ValueError("No covariance columns found for m1,m2,m3")
    lower_cols = [c.lower() for c in cols]
    if {"cov_m_00", "cov_m_01", "cov_m_02", "cov_m_10", "cov_m_11", "cov_m_12", "cov_m_20", "cov_m_21", "cov_m_22"}.issubset(lower_cols):
        data = df[[cols[lower_cols.index(name)] for name in [
            "cov_m_00", "cov_m_01", "cov_m_02",
            "cov_m_10", "cov_m_11", "cov_m_12",
            "cov_m_20", "cov_m_21", "cov_m_22",
        ]]].to_numpy()
        cov = data.reshape(-1, 3, 3)
    elif {"cov_m_00", "cov_m_01", "cov_m_02", "cov_m_11", "cov_m_12", "cov_m_22"}.issubset(lower_cols):
        data = df[[cols[lower_cols.index(name)] for name in [
            "cov_m_00", "cov_m_01", "cov_m_02", "cov_m_11", "cov_m_12", "cov_m_22",
        ]]].to_numpy()
        cov = np.zeros((data.shape[0], 3, 3))
        cov[:, 0, 0] = data[:, 0]
        cov[:, 0, 1] = data[:, 1]
        cov[:, 0, 2] = data[:, 2]
        cov[:, 1, 0] = data[:, 1]
        cov[:, 1, 1] = data[:, 3]
        cov[:, 1, 2] = data[:, 4]
        cov[:, 2, 0] = data[:, 2]
        cov[:, 2, 1] = data[:, 4]
        cov[:, 2, 2] = data[:, 5]
    else:
        raise ValueError("Unrecognized covariance column set for m1,m2,m3")

    sym = 0.5 * (cov + np.transpose(cov, (0, 2, 1)))
    if not np.allclose(sym, cov, equal_nan=True):
        warnings["symmetrized"] += int(np.sum(~np.isclose(sym, cov, equal_nan=True)))
    cov = sym
    finite_mask = np.isfinite(cov).all(axis=(1, 2))
    if not finite_mask.all():
        warnings["nonfinite"] += int(np.sum(~finite_mask))
    return cov, warnings, finite_mask


def regularize_covariances(cov: np.ndarray, eps: float = 1e-12) -> tuple[np.ndarray, int, float]:
    """Ensure per-window covariance matrices are PSD via diagonal shifts."""
    regularized = 0
    total_shift = 0.0
    cov = cov.copy()
    for idx in range(cov.shape[0]):
        eigvals = np.linalg.eigvalsh(cov[idx])
        min_eig = float(np.min(eigvals))
        if min_eig < 0:
            shift = max(eps, -min_eig + eps)
            cov[idx] += np.eye(3) * shift
            regularized += 1
            total_shift += shift
    return cov, regularized, total_shift


def derived_columns(df: pd.DataFrame) -> list[str]:
    """Identify derived quantity columns."""
    candidates = []
    for name in df.columns:
        if name in {"Y", "FeynmanY", "feynman_y"}:
            candidates.append(name)
        elif name.lower().startswith("derived_"):
            candidates.append(name)
    return candidates


def load_runs(
    manifest: pd.DataFrame,
    data_root: str | pathlib.Path,
    warmup_drop: int,
    min_windows_per_run: int,
) -> tuple[list[LoadedRun], dict[str, int]]:
    """Load and filter run data."""
    logs: dict[str, int] = {
        "runs_total": 0,
        "runs_loaded": 0,
        "runs_excluded_windows": 0,
        "windows_excluded_missing_cov": 0,
    }
    runs: list[LoadedRun] = []
    logs["runs_total"] = len(manifest)

    for _, row in manifest.iterrows():
        run_id = str(row["run_id"])
        group_id = str(row["covariance_group_id"])
        data_path = row["data_path"] if "data_path" in row else None
        path = pathlib.Path(data_path) if isinstance(data_path, str) and data_path else None
        if path is None:
            path = discover_run_file(data_root, run_id)
        df = load_window_data(path)
        df = select_gate(df)
        if warmup_drop > 0:
            df = df.iloc[warmup_drop:].copy()
        if len(df) < min_windows_per_run:
            logs["runs_excluded_windows"] += 1
            continue
        if not {"m1", "m2", "m3"}.issubset(df.columns):
            raise ValueError(f"Run {run_id} missing m1,m2,m3 columns")
        cov, warnings, finite_mask = parse_covariance(df)
        if not finite_mask.all():
            logs["windows_excluded_missing_cov"] += int(np.sum(~finite_mask))
            df = df[finite_mask].copy()
            cov = cov[finite_mask]
        cov, regularized_count, total_shift = regularize_covariances(cov)
        if regularized_count > 0:
            warnings["regularized"] += regularized_count
            warnings["regularization_shift_total"] += total_shift
        if len(df) < min_windows_per_run:
            logs["runs_excluded_windows"] += 1
            continue
        derived: dict[str, np.ndarray] = {}
        for name in derived_columns(df):
            derived[name] = df[name].to_numpy()
        runs.append(
            LoadedRun(
                run_id=run_id,
                covariance_group_id=group_id,
                windows=df.reset_index(drop=True),
                cov_mats=cov,
                derived=derived,
                warnings=warnings,
            )
        )
        logs["runs_loaded"] += 1
    return runs, logs


def group_runs(runs: Iterable[LoadedRun]) -> dict[str, list[LoadedRun]]:
    """Group runs by covariance_group_id."""
    grouped: dict[str, list[LoadedRun]] = {}
    for run in runs:
        grouped.setdefault(run.covariance_group_id, []).append(run)
    return grouped
