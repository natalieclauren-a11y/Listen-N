"""Metrics and aggregation logic for Phase 5 uncertainty validation."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Iterable

import numpy as np
import pandas as pd
from scipy import stats

from .io import LoadedRun


@dataclass
class GroupSummary:
    group_id: str
    n_runs: int
    n_windows_median: float
    cov_emp: np.ndarray
    cov_sys_mean: np.ndarray
    cov_sys_mean_of_mean: np.ndarray
    frobenius_rel_error: float
    logdet_diff: float | None
    corr_diff_fro: float


@dataclass
class CoverageResult:
    group_id: str
    target: str
    coverage_full: float
    coverage_diag: float
    ci_width_full_mean: float
    ci_width_diag_mean: float


@dataclass
class ResidualStats:
    group_id: str
    target: str
    residual_mean: float
    residual_std: float
    residual_skew: float
    residual_kurtosis: float


def feynman_y(m1: np.ndarray, m2: np.ndarray) -> np.ndarray:
    """Compute Feynman-Y from m1 and m2."""
    y = np.zeros_like(m1, dtype=float)
    mask = m1 > 0
    y[mask] = (m2[mask] - m1[mask] * m1[mask]) / m1[mask]
    return y


def grad_feynman_y(m1: float, m2: float) -> np.ndarray:
    """Gradient of Feynman-Y with respect to m1, m2, m3."""
    if m1 <= 0:
        return np.array([0.0, 0.0, 0.0])
    d_dm2 = 1.0 / m1
    d_dm1 = -m2 / (m1 * m1) - 1.0
    return np.array([d_dm1, d_dm2, 0.0])


def ensure_psd(cov: np.ndarray, eps: float = 1e-12) -> tuple[np.ndarray, float]:
    """Ensure covariance matrix is positive semidefinite by diagonal shift."""
    eigvals = np.linalg.eigvalsh(cov)
    min_eig = float(np.min(eigvals))
    if min_eig >= 0:
        return cov, 0.0
    shift = max(eps, -min_eig + eps)
    return cov + np.eye(cov.shape[0]) * shift, shift


def run_means(run: LoadedRun) -> dict[str, np.ndarray]:
    """Compute run-level means for moments and derived values."""
    means = {
        "m": run.windows[["m1", "m2", "m3"]].mean(axis=0).to_numpy(),
    }
    for name, values in run.derived.items():
        means[name] = np.array([float(np.mean(values))])
    if "Y" in run.derived:
        means["Y"] = np.array([float(np.mean(run.derived["Y"]))])
    elif "FeynmanY" in run.derived:
        means["Y"] = np.array([float(np.mean(run.derived["FeynmanY"]))])
    else:
        y_values = feynman_y(run.windows["m1"].to_numpy(), run.windows["m2"].to_numpy())
        means["Y"] = np.array([float(np.mean(y_values))])
    return means


def aggregate_covariances(run: LoadedRun) -> tuple[np.ndarray, np.ndarray]:
    """Aggregate per-window covariance estimates for a run."""
    cov_mean = np.mean(run.cov_mats, axis=0)
    cov_mean_of_mean = np.sum(run.cov_mats, axis=0) / (len(run.cov_mats) ** 2)
    return cov_mean, cov_mean_of_mean


def empirical_group_stats(group_id: str, runs: list[LoadedRun]) -> GroupSummary:
    """Compute empirical and system covariance summaries for a group."""
    run_mean_vecs = np.vstack([run_means(run)["m"] for run in runs])
    cov_emp = np.cov(run_mean_vecs.T, bias=False)
    cov_sys_mean = np.mean([aggregate_covariances(run)[0] for run in runs], axis=0)
    cov_sys_mean_of_mean = np.mean([aggregate_covariances(run)[1] for run in runs], axis=0)
    frob = np.linalg.norm(cov_sys_mean - cov_emp, ord="fro") / np.linalg.norm(cov_emp, ord="fro")

    logdet_diff = None
    if np.all(np.linalg.eigvalsh(cov_emp) > 0) and np.all(np.linalg.eigvalsh(cov_sys_mean) > 0):
        logdet_diff = float(np.linalg.slogdet(cov_sys_mean)[1] - np.linalg.slogdet(cov_emp)[1])

    corr_emp = cov_to_corr(cov_emp)
    corr_sys = cov_to_corr(cov_sys_mean)
    corr_diff_fro = float(np.linalg.norm(corr_sys - corr_emp, ord="fro"))

    windows_per_run = [len(run.windows) for run in runs]
    return GroupSummary(
        group_id=group_id,
        n_runs=len(runs),
        n_windows_median=float(np.median(windows_per_run)),
        cov_emp=cov_emp,
        cov_sys_mean=cov_sys_mean,
        cov_sys_mean_of_mean=cov_sys_mean_of_mean,
        frobenius_rel_error=float(frob),
        logdet_diff=logdet_diff,
        corr_diff_fro=corr_diff_fro,
    )


def cov_to_corr(cov: np.ndarray) -> np.ndarray:
    """Convert covariance matrix to correlation matrix."""
    std = np.sqrt(np.diag(cov))
    denom = np.outer(std, std)
    with np.errstate(divide="ignore", invalid="ignore"):
        corr = np.where(denom > 0, cov / denom, 0.0)
    return corr


def elementwise_covariance_table(cov_emp: np.ndarray, cov_sys: np.ndarray) -> pd.DataFrame:
    """Create elementwise comparison table for covariance matrices."""
    rows = []
    for i in range(3):
        for j in range(i, 3):
            emp = cov_emp[i, j]
            sys = cov_sys[i, j]
            ratio = sys / emp if emp != 0 else np.nan
            rows.append({
                "i": i,
                "j": j,
                "cov_emp": emp,
                "cov_sys": sys,
                "ratio": ratio,
                "diff": sys - emp,
            })
    return pd.DataFrame(rows)


def propagate_y_variance(m_mean: np.ndarray, cov: np.ndarray) -> float:
    """Propagate Y variance using delta method."""
    grad = grad_feynman_y(m_mean[0], m_mean[1])
    return float(grad.T @ cov @ grad)


def coverage_for_group(
    group_id: str,
    runs: list[LoadedRun],
    cov_pred: list[np.ndarray],
    ci_level: float,
    target: str,
) -> CoverageResult:
    """Compute coverage for scalar targets across runs."""
    z = stats.norm.ppf((1 + ci_level) / 2)
    run_mean_vals = []
    sigma_full = []
    sigma_diag = []

    for run, cov in zip(runs, cov_pred):
        means = run_means_for_target(run, target)
        run_mean_vals.append(means)
        if target in {"m1", "m2", "m3"}:
            idx = {"m1": 0, "m2": 1, "m3": 2}[target]
            var_full = cov[idx, idx]
            var_diag = cov[idx, idx]
        elif target == "Y":
            m_mean = run_means(run)["m"]
            var_full = propagate_y_variance(m_mean, cov)
            var_diag = propagate_y_variance(m_mean, np.diag(np.diag(cov)))
        else:
            var_full = float(cov[0, 0])
            var_diag = float(cov[0, 0])
        sigma_full.append(np.sqrt(max(var_full, 0.0)))
        sigma_diag.append(np.sqrt(max(var_diag, 0.0)))

    run_mean_vals = np.array(run_mean_vals)
    mu_emp = float(np.mean(run_mean_vals))
    sigma_full = np.array(sigma_full)
    sigma_diag = np.array(sigma_diag)
    ci_full = np.vstack((run_mean_vals - z * sigma_full, run_mean_vals + z * sigma_full)).T
    ci_diag = np.vstack((run_mean_vals - z * sigma_diag, run_mean_vals + z * sigma_diag)).T

    coverage_full = float(np.mean((mu_emp >= ci_full[:, 0]) & (mu_emp <= ci_full[:, 1])))
    coverage_diag = float(np.mean((mu_emp >= ci_diag[:, 0]) & (mu_emp <= ci_diag[:, 1])))
    return CoverageResult(
        group_id=group_id,
        target=target,
        coverage_full=coverage_full,
        coverage_diag=coverage_diag,
        ci_width_full_mean=float(np.mean(ci_full[:, 1] - ci_full[:, 0])),
        ci_width_diag_mean=float(np.mean(ci_diag[:, 1] - ci_diag[:, 0])),
    )


def run_means_for_target(run: LoadedRun, target: str) -> float:
    """Compute run mean for a scalar target."""
    if target in {"m1", "m2", "m3"}:
        idx = {"m1": 0, "m2": 1, "m3": 2}[target]
        return float(run_means(run)["m"][idx])
    if target == "Y":
        return float(run_means(run).get("Y", np.array([np.nan]))[0])
    if target == "FeynmanY":
        return float(run_means(run).get("Y", np.array([np.nan]))[0])
    if target in run.derived:
        return float(np.mean(run.derived[target]))
    raise ValueError(f"Unknown target {target}")


def residuals_for_target(
    group_id: str,
    runs: list[LoadedRun],
    cov_pred: list[np.ndarray],
    target: str,
    diag_only: bool = False,
) -> ResidualStats:
    """Compute residual stats for scalar targets."""
    run_means_values = np.array([run_means_for_target(run, target) for run in runs])
    mu_emp = float(np.mean(run_means_values))
    sigma = []
    for run, cov in zip(runs, cov_pred):
        if target in {"m1", "m2", "m3"}:
            idx = {"m1": 0, "m2": 1, "m3": 2}[target]
            var = cov[idx, idx]
        elif target == "Y":
            m_mean = run_means(run)["m"]
            if diag_only:
                var = propagate_y_variance(m_mean, np.diag(np.diag(cov)))
            else:
                var = propagate_y_variance(m_mean, cov)
        else:
            var = float(cov[0, 0])
        sigma.append(np.sqrt(max(var, 1e-12)))
    residuals = (run_means_values - mu_emp) / np.array(sigma)
    return ResidualStats(
        group_id=group_id,
        target=target,
        residual_mean=float(np.mean(residuals)),
        residual_std=float(np.std(residuals, ddof=1)) if len(residuals) > 1 else float(np.std(residuals)),
        residual_skew=float(stats.skew(residuals, bias=False)) if len(residuals) > 2 else 0.0,
        residual_kurtosis=float(stats.kurtosis(residuals, fisher=True, bias=False)) if len(residuals) > 3 else 0.0,
    )


def whitened_residuals(
    runs: list[LoadedRun],
    cov_pred: list[np.ndarray],
    mu_emp: np.ndarray,
) -> np.ndarray:
    """Compute whitened residuals for vector m."""
    residuals = []
    for run, cov in zip(runs, cov_pred):
        run_mean_vec = run_means(run)["m"]
        cov_reg, _ = ensure_psd(cov)
        chol = np.linalg.cholesky(cov_reg)
        resid = np.linalg.solve(chol, run_mean_vec - mu_emp)
        residuals.append(resid)
    return np.vstack(residuals)


def mahalanobis_distances(
    runs: list[LoadedRun],
    cov_pred: list[np.ndarray],
    mu_emp: np.ndarray,
) -> np.ndarray:
    """Compute Mahalanobis distances for vector m."""
    distances = []
    for run, cov in zip(runs, cov_pred):
        cov_reg, _ = ensure_psd(cov)
        inv = np.linalg.inv(cov_reg)
        diff = run_means(run)["m"] - mu_emp
        distances.append(float(diff.T @ inv @ diff))
    return np.array(distances)


def reduced_count_runs(
    runs: Iterable[LoadedRun],
    factor: float,
    rng: np.random.Generator,
) -> list[LoadedRun]:
    """Reduce effective counts by subsampling windows."""
    reduced_runs: list[LoadedRun] = []
    for run in runs:
        total = len(run.windows)
        keep = max(1, int(np.floor(factor * total)))
        indices = np.sort(rng.choice(total, size=keep, replace=False))
        reduced_runs.append(
            LoadedRun(
                run_id=run.run_id,
                covariance_group_id=run.covariance_group_id,
                windows=run.windows.iloc[indices].reset_index(drop=True),
                cov_mats=run.cov_mats[indices],
                derived={k: v[indices] for k, v in run.derived.items()},
                warnings=run.warnings.copy(),
            )
        )
    return reduced_runs
