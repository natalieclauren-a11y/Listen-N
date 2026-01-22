"""Plotting utilities for Phase 5 uncertainty validation."""

from __future__ import annotations

import pathlib

import matplotlib.pyplot as plt
import numpy as np
from scipy import stats


def save_cov_ratio_heatmap(
    outpath: pathlib.Path,
    ratio: np.ndarray,
    title: str,
) -> None:
    """Save heatmap of covariance ratio."""
    fig, ax = plt.subplots(figsize=(4.5, 3.8))
    im = ax.imshow(ratio, cmap="coolwarm", vmin=0.5, vmax=1.5)
    ax.set_title(title)
    ax.set_xticks([0, 1, 2])
    ax.set_yticks([0, 1, 2])
    ax.set_xticklabels(["m1", "m2", "m3"])
    ax.set_yticklabels(["m1", "m2", "m3"])
    for i in range(3):
        for j in range(3):
            ax.text(j, i, f"{ratio[i, j]:.2f}", ha="center", va="center", color="black")
    fig.colorbar(im, ax=ax, fraction=0.046, pad=0.04)
    fig.tight_layout()
    fig.savefig(outpath, dpi=200)
    plt.close(fig)


def save_coverage_plot(
    outpath: pathlib.Path,
    factors: np.ndarray,
    coverage_full: np.ndarray,
    coverage_diag: np.ndarray,
    title: str,
    target_label: str,
) -> None:
    """Save coverage vs reduced-count factor plot."""
    fig, ax = plt.subplots(figsize=(5.5, 3.8))
    ax.plot(factors, coverage_full, marker="o", label="Full covariance")
    ax.plot(factors, coverage_diag, marker="s", label="Diagonal only")
    ax.axhline(0.95, color="black", linestyle="--", linewidth=1, label="Nominal 0.95")
    ax.set_xlabel("Reduced-count factor")
    ax.set_ylabel("Empirical coverage")
    ax.set_title(f"Coverage vs reduced-count factor ({target_label})")
    ax.set_ylim(0.0, 1.0)
    ax.legend()
    fig.tight_layout()
    fig.savefig(outpath, dpi=200)
    plt.close(fig)


def save_qq_plot(
    outpath: pathlib.Path,
    residuals: np.ndarray,
    title: str,
) -> None:
    """Save QQ plot for whitened residuals."""
    fig, ax = plt.subplots(figsize=(4.5, 4.5))
    stats.probplot(residuals.flatten(), dist="norm", plot=ax)
    ax.set_title(title)
    fig.tight_layout()
    fig.savefig(outpath, dpi=200)
    plt.close(fig)


def save_residual_std_plot(
    outpath: pathlib.Path,
    factors: np.ndarray,
    residual_std_full: np.ndarray,
    residual_std_diag: np.ndarray,
    title: str,
) -> None:
    """Save residual standard deviation vs reduced-count factor plot."""
    fig, ax = plt.subplots(figsize=(5.5, 3.8))
    ax.plot(factors, residual_std_full, marker="o", label="Full covariance")
    ax.plot(factors, residual_std_diag, marker="s", label="Diagonal only")
    ax.axhline(1.0, color="black", linestyle="--", linewidth=1, label="Ideal")
    ax.set_xlabel("Reduced-count factor")
    ax.set_ylabel("Residual standard deviation")
    ax.set_title(title)
    ax.legend()
    fig.tight_layout()
    fig.savefig(outpath, dpi=200)
    plt.close(fig)
