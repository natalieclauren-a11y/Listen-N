#!/usr/bin/env python3
"""Plot distributions of run-level correlation Z-scores for two groups."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Optional, Sequence, Tuple

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


@dataclass(frozen=True)
class PlotMeta:
    tg_s: float
    window_s: float
    step_s: float


@dataclass(frozen=True)
class PlotConfig:
    hist_color: str = OKABE_ITO["sky"]
    kde_color: str = OKABE_ITO["blue"]
    stats_color: str = OKABE_ITO["orange"]
    edge_color: str = OKABE_ITO["gray"]
    hist_alpha: float = 0.6
    kde_linewidth: float = 2.5
    stats_linewidth: float = 2.0


def _validate_scores(values: Sequence[float], name: str) -> np.ndarray:
    data = np.asarray(values, dtype=float)
    if data.size == 0:
        raise ValueError(f"{name} must contain at least one value")
    if not np.all(np.isfinite(data)):
        raise ValueError(f"{name} must contain only finite values")
    return data


def _shared_bins(z_cf: np.ndarray, z_berp: np.ndarray) -> np.ndarray:
    combined = np.concatenate([z_cf, z_berp])
    data_min = float(np.min(combined))
    data_max = float(np.max(combined))
    span = data_max - data_min
    if span <= 0:
        span = 1.0
        data_min -= 0.5
        data_max += 0.5
    bins = np.histogram_bin_edges(combined, bins="auto")
    if bins.size < 2:
        bins = np.linspace(data_min, data_max, num=4)
    return bins


def _shared_xlim(z_cf: np.ndarray, z_berp: np.ndarray, pad_frac: float = 0.05) -> Tuple[float, float]:
    combined = np.concatenate([z_cf, z_berp])
    data_min = float(np.min(combined))
    data_max = float(np.max(combined))
    span = data_max - data_min
    if span <= 0:
        span = 1.0
    pad = span * pad_frac
    return data_min - pad, data_max + pad


def _add_kde(ax: plt.Axes, data: np.ndarray, color: str, linewidth: float, label: Optional[str]) -> None:
    if gaussian_kde is None or data.size < 2:
        return
    kde = gaussian_kde(data)
    x_grid = np.linspace(data.min(), data.max(), 256)
    ax.plot(x_grid, kde(x_grid), color=color, linewidth=linewidth, label=label)


def _add_stats_lines(
    ax: plt.Axes,
    data: np.ndarray,
    color: str,
    linewidth: float,
    label_prefix: str,
) -> Tuple[plt.Line2D, plt.Line2D]:
    mean_val = float(np.mean(data))
    median_val = float(np.median(data))
    mean_line = ax.axvline(
        mean_val,
        color=color,
        linestyle="--",
        linewidth=linewidth,
        label=f"{label_prefix} mean = {mean_val:.2f}",
    )
    median_line = ax.axvline(
        median_val,
        color=color,
        linestyle=":",
        linewidth=linewidth,
        label=f"{label_prefix} median = {median_val:.2f}",
    )
    return mean_line, median_line


def plot_group(
    ax: plt.Axes,
    data: np.ndarray,
    label: str,
    bins: np.ndarray,
    xlim: Tuple[float, float],
    meta: PlotMeta,
    config: PlotConfig,
) -> None:
    ax.hist(
        data,
        bins=bins,
        density=True,
        color=config.hist_color,
        edgecolor=config.edge_color,
        alpha=config.hist_alpha,
        linewidth=0.8,
        label=f"{label} histogram",
    )

    _add_kde(ax, data, config.kde_color, config.kde_linewidth, label=f"{label} KDE")
    mean_line, median_line = _add_stats_lines(
        ax,
        data,
        config.stats_color,
        config.stats_linewidth,
        label_prefix=label,
    )

    ax.set_xlim(xlim)
    ax.set_title(label)

    ax.text(
        0.02,
        0.98,
        (
            f"N runs = {data.size}\n"
            f"Tg = {meta.tg_s:.3f}s\n"
            f"Window = {meta.window_s:.0f}s\n"
            f"Step = {meta.step_s:.0f}s"
        ),
        transform=ax.transAxes,
        ha="left",
        va="top",
        fontsize=9,
        bbox=dict(boxstyle="round", facecolor="white", edgecolor=config.edge_color, alpha=0.9),
    )

    handles = [mean_line, median_line]
    if gaussian_kde is not None and data.size > 1:
        handles.append(plt.Line2D([0], [0], color=config.kde_color, linewidth=config.kde_linewidth, label=f"{label} KDE"))
    ax.legend(handles=handles, frameon=False, loc="upper right")


def plot_two_panel_distribution(
    z_cf: Sequence[float],
    z_berp: Sequence[float],
    out_path: str,
    meta: PlotMeta,
) -> None:
    z_cf_arr = _validate_scores(z_cf, "z_cf")
    z_berp_arr = _validate_scores(z_berp, "z_berp")

    bins = _shared_bins(z_cf_arr, z_berp_arr)
    xlim = _shared_xlim(z_cf_arr, z_berp_arr)
    config = PlotConfig()

    fig, axes = plt.subplots(1, 2, figsize=(12, 4), sharey=True)
    plot_group(axes[0], z_cf_arr, "Cf-only", bins, xlim, meta, config)
    plot_group(axes[1], z_berp_arr, "BeRP-only", bins, xlim, meta, config)

    fig.suptitle("Distribution of real-time correlation estimates across runs")
    fig.supxlabel("Correlation Z-score (median across windows)")
    axes[0].set_ylabel("Density")

    fig.tight_layout(rect=(0, 0, 1, 0.92))
    fig.savefig(out_path, dpi=300)
    plt.close(fig)


def plot_overlay_distribution(
    z_cf: Sequence[float],
    z_berp: Sequence[float],
    out_path: str,
    meta: PlotMeta,
) -> None:
    z_cf_arr = _validate_scores(z_cf, "z_cf")
    z_berp_arr = _validate_scores(z_berp, "z_berp")

    bins = _shared_bins(z_cf_arr, z_berp_arr)
    xlim = _shared_xlim(z_cf_arr, z_berp_arr)
    config = PlotConfig()

    fig, ax = plt.subplots(figsize=(7.5, 4.5), dpi=150)
    ax.hist(
        z_cf_arr,
        bins=bins,
        density=True,
        color=config.hist_color,
        edgecolor=config.edge_color,
        alpha=config.hist_alpha,
        linewidth=0.8,
        label="Cf-only histogram",
    )
    ax.hist(
        z_berp_arr,
        bins=bins,
        density=True,
        color=config.hist_color,
        edgecolor=config.edge_color,
        alpha=0.35,
        linewidth=0.8,
        label="BeRP-only histogram",
    )

    _add_kde(ax, z_cf_arr, config.kde_color, config.kde_linewidth, label="Cf-only KDE")
    _add_kde(ax, z_berp_arr, config.kde_color, config.kde_linewidth, label="BeRP-only KDE")

    _add_stats_lines(ax, z_cf_arr, config.stats_color, config.stats_linewidth, label_prefix="Cf-only")
    _add_stats_lines(ax, z_berp_arr, config.stats_color, config.stats_linewidth, label_prefix="BeRP-only")

    ax.set_xlim(xlim)
    ax.set_xlabel("Correlation Z-score (median across windows)")
    ax.set_ylabel("Density")
    ax.set_title("Distribution of real-time correlation estimates across runs")

    ax.text(
        0.02,
        0.98,
        (
            f"Cf-only N runs = {z_cf_arr.size}\n"
            f"Tg = {meta.tg_s:.3f}s\n"
            f"Window = {meta.window_s:.0f}s\n"
            f"Step = {meta.step_s:.0f}s"
        ),
        transform=ax.transAxes,
        ha="left",
        va="top",
        fontsize=9,
        bbox=dict(boxstyle="round", facecolor="white", edgecolor=config.edge_color, alpha=0.9),
    )
    ax.text(
        0.98,
        0.98,
        (
            f"BeRP-only N runs = {z_berp_arr.size}\n"
            f"Tg = {meta.tg_s:.3f}s\n"
            f"Window = {meta.window_s:.0f}s\n"
            f"Step = {meta.step_s:.0f}s"
        ),
        transform=ax.transAxes,
        ha="right",
        va="top",
        fontsize=9,
        bbox=dict(boxstyle="round", facecolor="white", edgecolor=config.edge_color, alpha=0.9),
    )

    ax.legend(frameon=False, loc="upper right")

    fig.tight_layout()
    fig.savefig(out_path, dpi=300)
    plt.close(fig)


def plot_distribution(
    z_cf: Sequence[float],
    z_berp: Sequence[float],
    out_path: str,
    tg_s: float,
    window_s: float,
    step_s: float,
    mode: str = "panel",
) -> None:
    meta = PlotMeta(tg_s=tg_s, window_s=window_s, step_s=step_s)
    if mode == "panel":
        plot_two_panel_distribution(z_cf, z_berp, out_path, meta)
    elif mode == "overlay":
        plot_overlay_distribution(z_cf, z_berp, out_path, meta)
    else:
        raise ValueError("mode must be 'panel' or 'overlay'")


if __name__ == "__main__":
    rng = np.random.default_rng(123)
    z_cf_example = rng.normal(loc=0.2, scale=0.5, size=5)
    z_berp_example = rng.normal(loc=-0.1, scale=0.6, size=5)

    plot_distribution(
        z_cf_example,
        z_berp_example,
        out_path="rt_corr_distribution_two_groups.png",
        tg_s=0.001,
        window_s=10.0,
        step_s=1.0,
        mode="panel",
    )
