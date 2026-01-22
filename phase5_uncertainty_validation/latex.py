"""LaTeX snippet generation for Phase 5 uncertainty validation."""

from __future__ import annotations

import pathlib

import pandas as pd


def write_group_summary_table(df: pd.DataFrame, outpath: pathlib.Path) -> None:
    """Write a LaTeX table for group summary."""
    table_df = df[[
        "group_id",
        "n_runs",
        "n_windows_median",
        "frobenius_rel_error",
        "coverage_full",
        "coverage_diag",
    ]].copy()
    table_df = table_df.rename(columns={
        "group_id": "Group",
        "n_runs": "Runs",
        "n_windows_median": "Median windows",
        "frobenius_rel_error": "Frobenius rel error",
        "coverage_full": "Coverage full",
        "coverage_diag": "Coverage diag",
    })
    latex = table_df.to_latex(index=False, float_format="%.3f")
    outpath.write_text(latex, encoding="utf-8")


def write_subsection_snippet(
    outpath: pathlib.Path,
    table_path: pathlib.Path,
    figures: dict[str, str],
) -> None:
    """Write LaTeX subsection snippet."""
    snippet = f"""\\subsection{{Phase 5: Empirical covariance and uncertainty validation}}
This subsection validates reported uncertainty for per window factorial moments and derived quantities using repeated static runs grouped by covariance configuration. Empirical repeatability across runs is compared against system covariance aggregations, and coverage is measured against group mean reference values. Reduced count factors are evaluated to find calibration breakdown points and motivate exclusion thresholds.

\\begin{{figure}}[ht]
\\centering
\\includegraphics[width=0.7\\linewidth]{{{figures['cov_ratio']}}}
\\caption{{Elementwise ratio of system to empirical covariance for m vector.}}
\\end{{figure}}

\\begin{{figure}}[ht]
\\centering
\\includegraphics[width=0.7\\linewidth]{{{figures['coverage']}}}
\\caption{{Coverage versus reduced count factor for full and diagonal covariance.}}
\\end{{figure}}

\\begin{{figure}}[ht]
\\centering
\\includegraphics[width=0.7\\linewidth]{{{figures['qq']}}}
\\caption{{QQ plot of whitened residuals for m vector in a representative group.}}
\\end{{figure}}

\\begin{{figure}}[ht]
\\centering
\\includegraphics[width=0.7\\linewidth]{{{figures['residual_std']}}}
\\caption{{Residual standard deviation versus reduced count factor.}}
\\end{{figure}}

\\begin{{table}}[ht]
\\centering
\\input{{{table_path.as_posix()}}}
\\caption{{Group summary of covariance mismatch and coverage.}}
\\end{{table}}
"""
    outpath.write_text(snippet, encoding="utf-8")
