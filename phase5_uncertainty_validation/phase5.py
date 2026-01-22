"""CLI for Phase 5 uncertainty validation."""

from __future__ import annotations

import argparse
import json
import pathlib
from dataclasses import asdict

import numpy as np
import pandas as pd

from . import io
from . import latex
from . import metrics
from . import plots


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Phase 5 uncertainty validation")
    subparsers = parser.add_subparsers(dest="command", required=True)

    run_parser = subparsers.add_parser("run", help="Run validation analysis")
    run_parser.add_argument("--manifest", required=True)
    run_parser.add_argument("--data-root", required=True)
    run_parser.add_argument("--outdir", required=True)
    run_parser.add_argument("--min-windows-per-run", type=int, default=30)
    run_parser.add_argument("--min-runs-per-group", type=int, default=3)
    run_parser.add_argument("--warmup-drop", type=int, default=0)
    run_parser.add_argument("--reduced-count-factors", default="1.0,0.75,0.5,0.25,0.1")
    run_parser.add_argument("--ci-level", type=float, default=0.95)
    run_parser.add_argument("--seed", type=int, default=123)

    validate_parser = subparsers.add_parser("validate-inputs", help="Validate input files")
    validate_parser.add_argument("--manifest", required=True)
    validate_parser.add_argument("--data-root", required=True)

    return parser.parse_args()


def ensure_outdirs(outdir: pathlib.Path) -> dict[str, pathlib.Path]:
    outdir.mkdir(parents=True, exist_ok=True)
    paths = {
        "figures": outdir / "figures",
        "tables": outdir / "tables",
        "latex": outdir / "latex",
        "logs": outdir / "logs",
    }
    for path in paths.values():
        path.mkdir(parents=True, exist_ok=True)
    return paths


def validate_inputs(manifest_path: str, data_root: str) -> None:
    df = io.read_manifest(manifest_path)
    io.validate_manifest(df)
    df = io.filtered_manifest(df)
    for _, row in df.iterrows():
        run_id = str(row["run_id"])
        data_path = row["data_path"] if "data_path" in row else None
        path = pathlib.Path(data_path) if isinstance(data_path, str) and data_path else None
        if path is None:
            path = io.discover_run_file(data_root, run_id)
        data = io.load_window_data(path)
        data = io.select_gate(data)
        required = {"m1", "m2", "m3"}
        missing = required - set(data.columns)
        if missing:
            raise ValueError(f"Run {run_id} missing columns: {sorted(missing)}")
        _ = io.cov_columns(data)
    print("Input validation succeeded")


def run_analysis(args: argparse.Namespace) -> None:
    outdir = pathlib.Path(args.outdir)
    paths = ensure_outdirs(outdir)

    df = io.read_manifest(args.manifest)
    io.validate_manifest(df)
    df = io.filtered_manifest(df)

    runs, load_logs = io.load_runs(
        df,
        data_root=args.data_root,
        warmup_drop=args.warmup_drop,
        min_windows_per_run=args.min_windows_per_run,
    )
    grouped = io.group_runs(runs)

    group_summaries: list[metrics.GroupSummary] = []
    coverage_rows: list[dict[str, float]] = []
    residual_rows: list[dict[str, float]] = []
    cov_table_rows: list[pd.DataFrame] = []
    regularization_logs: list[dict[str, float]] = []

    for group_id, group_runs in grouped.items():
        if len(group_runs) < args.min_runs_per_group:
            continue
        summary = metrics.empirical_group_stats(group_id, group_runs)
        group_summaries.append(summary)

        cov_table = metrics.elementwise_covariance_table(summary.cov_emp, summary.cov_sys_mean)
        cov_table.insert(0, "group_id", group_id)
        cov_table_rows.append(cov_table)

        cov_pred = [metrics.aggregate_covariances(run)[1] for run in group_runs]
        for target in ["m1", "Y"]:
            cov_result = metrics.coverage_for_group(group_id, group_runs, cov_pred, args.ci_level, target)
            coverage_rows.append(asdict(cov_result))
            residual_stats = metrics.residuals_for_target(group_id, group_runs, cov_pred, target)
            residual_rows.append(asdict(residual_stats))

        for run, cov in zip(group_runs, cov_pred):
            cov_reg, shift = metrics.ensure_psd(cov)
            if shift > 0:
                regularization_logs.append({"run_id": run.run_id, "group_id": group_id, "shift": shift})

    if not group_summaries:
        raise ValueError("No groups met the minimum run criteria")

    group_summary_df = pd.DataFrame([
        {
            "group_id": gs.group_id,
            "n_runs": gs.n_runs,
            "n_windows_median": gs.n_windows_median,
            "frobenius_rel_error": gs.frobenius_rel_error,
            "logdet_diff": gs.logdet_diff,
            "corr_diff_fro": gs.corr_diff_fro,
        }
        for gs in group_summaries
    ])

    coverage_df = pd.DataFrame(coverage_rows)
    residual_df = pd.DataFrame(residual_rows)

    summary_join = group_summary_df.merge(
        coverage_df[coverage_df["target"] == "m1"][
            ["group_id", "coverage_full", "coverage_diag"]
        ],
        on="group_id",
        how="left",
    )

    tables_dir = paths["tables"]
    group_summary_path = tables_dir / "group_summary.csv"
    summary_join.to_csv(group_summary_path, index=False)

    cov_table_path = tables_dir / "per_group_covariance_tables.csv"
    pd.concat(cov_table_rows, ignore_index=True).to_csv(cov_table_path, index=False)

    coverage_df.to_csv(tables_dir / "coverage_summary.csv", index=False)
    residual_df.to_csv(tables_dir / "residual_summary.csv", index=False)

    factors = np.array([float(x) for x in args.reduced_count_factors.split(",")])
    rng = np.random.default_rng(args.seed)
    breakdown_rows = []
    coverage_full_series_m1 = []
    coverage_diag_series_m1 = []
    coverage_full_series_y = []
    coverage_diag_series_y = []
    residual_std_full_series = []
    residual_std_diag_series = []

    for factor in factors:
        coverage_full_vals_m1 = []
        coverage_diag_vals_m1 = []
        coverage_full_vals_y = []
        coverage_diag_vals_y = []
        residual_std_full_vals = []
        residual_std_diag_vals = []

        for group_id, group_runs in grouped.items():
            if len(group_runs) < args.min_runs_per_group:
                continue
            reduced_runs = metrics.reduced_count_runs(group_runs, factor, rng)
            cov_pred = [metrics.aggregate_covariances(run)[1] for run in reduced_runs]
            cov_m1 = metrics.coverage_for_group(group_id, reduced_runs, cov_pred, args.ci_level, "m1")
            coverage_full_vals_m1.append(cov_m1.coverage_full)
            coverage_diag_vals_m1.append(cov_m1.coverage_diag)
            cov_y = metrics.coverage_for_group(group_id, reduced_runs, cov_pred, args.ci_level, "Y")
            coverage_full_vals_y.append(cov_y.coverage_full)
            coverage_diag_vals_y.append(cov_y.coverage_diag)
            residual_full = metrics.residuals_for_target(group_id, reduced_runs, cov_pred, "m1")
            residual_std_full_vals.append(residual_full.residual_std)
            residual_diag = metrics.residuals_for_target(group_id, reduced_runs, cov_pred, "m1", diag_only=True)
            residual_std_diag_vals.append(residual_diag.residual_std)

        coverage_full_m1 = float(np.mean(coverage_full_vals_m1))
        coverage_diag_m1 = float(np.mean(coverage_diag_vals_m1))
        coverage_full_y = float(np.mean(coverage_full_vals_y))
        coverage_diag_y = float(np.mean(coverage_diag_vals_y))
        residual_std_full = float(np.mean(residual_std_full_vals))
        residual_std_diag = float(np.mean(residual_std_diag_vals))
        suggested_ok = 0.90 <= coverage_full_y <= 0.99
        breakdown_rows.append({
            "factor": factor,
            "coverage_full_m1": coverage_full_m1,
            "coverage_diag_m1": coverage_diag_m1,
            "coverage_full_y": coverage_full_y,
            "coverage_diag_y": coverage_diag_y,
            "residual_std_full": residual_std_full,
            "residual_std_diag": residual_std_diag,
            "suggested_ok": suggested_ok,
        })
        coverage_full_series_m1.append(coverage_full_m1)
        coverage_diag_series_m1.append(coverage_diag_m1)
        coverage_full_series_y.append(coverage_full_y)
        coverage_diag_series_y.append(coverage_diag_y)
        residual_std_full_series.append(residual_std_full)
        residual_std_diag_series.append(residual_std_diag)

    breakdown_df = pd.DataFrame(breakdown_rows)
    breakdown_df.to_csv(tables_dir / "breakdown_summary.csv", index=False)

    figures_dir = paths["figures"]
    representative = group_summaries[0]
    ratio = representative.cov_sys_mean / representative.cov_emp
    cov_ratio_path = figures_dir / "cov_ratio_heatmap.png"
    plots.save_cov_ratio_heatmap(cov_ratio_path, ratio, f"Covariance ratio {representative.group_id}")

    coverage_plot_path = figures_dir / "coverage_vs_factor_m1.png"
    plots.save_coverage_plot(
        coverage_plot_path,
        factors,
        np.array(coverage_full_series_m1),
        np.array(coverage_diag_series_m1),
        "Coverage vs reduced-count",
        "m1",
    )

    coverage_plot_y_path = figures_dir / "coverage_vs_factor_y.png"
    plots.save_coverage_plot(
        coverage_plot_y_path,
        factors,
        np.array(coverage_full_series_y),
        np.array(coverage_diag_series_y),
        "Coverage vs reduced-count",
        "Y",
    )

    group_runs = grouped[representative.group_id]
    cov_pred = [metrics.aggregate_covariances(run)[1] for run in group_runs]
    mu_emp = np.mean([metrics.run_means(run)["m"] for run in group_runs], axis=0)
    whitened = metrics.whitened_residuals(group_runs, cov_pred, mu_emp)
    qq_path = figures_dir / "whitened_residuals_qq.png"
    plots.save_qq_plot(qq_path, whitened, "Whitened residuals QQ")

    residual_std_plot_path = figures_dir / "residual_std_vs_factor.png"
    plots.save_residual_std_plot(
        residual_std_plot_path,
        factors,
        np.array(residual_std_full_series),
        np.array(residual_std_diag_series),
        "Residual std vs reduced-count",
    )

    latex_dir = paths["latex"]
    table_tex_path = latex_dir / "group_summary_table.tex"
    latex.write_group_summary_table(summary_join, table_tex_path)
    snippet_path = latex_dir / "phase5_uncertainty_validation.tex"
    latex.write_subsection_snippet(
        snippet_path,
        table_tex_path.relative_to(latex_dir),
        {
            "cov_ratio": cov_ratio_path.relative_to(outdir),
            "coverage": coverage_plot_y_path.relative_to(outdir),
            "qq": qq_path.relative_to(outdir),
            "residual_std": residual_std_plot_path.relative_to(outdir),
        },
    )

    warning_summary = {
        "symmetrized": int(sum(run.warnings.get("symmetrized", 0) for run in runs)),
        "nonfinite": int(sum(run.warnings.get("nonfinite", 0) for run in runs)),
        "regularized": int(sum(run.warnings.get("regularized", 0) for run in runs)),
        "regularization_shift_total": float(sum(run.warnings.get("regularization_shift_total", 0.0) for run in runs)),
    }
    logs = {
        "load_logs": load_logs,
        "warnings": warning_summary,
        "regularization": regularization_logs,
    }
    (paths["logs"] / "summary.json").write_text(json.dumps(logs, indent=2), encoding="utf-8")

    results_md = outdir / "README_results.md"
    results_md.write_text(
        "# Phase 5 uncertainty validation results\n\n"
        f"Group summary: {group_summary_path}\n"
        f"Coverage summary: {tables_dir / 'coverage_summary.csv'}\n"
        f"Residual summary: {tables_dir / 'residual_summary.csv'}\n"
        f"Breakdown summary: {tables_dir / 'breakdown_summary.csv'}\n"
        f"Covariance tables: {cov_table_path}\n"
        f"Figures: {figures_dir}\n",
        encoding="utf-8",
    )


def main() -> None:
    args = parse_args()
    if args.command == "validate-inputs":
        validate_inputs(args.manifest, args.data_root)
    elif args.command == "run":
        run_analysis(args)
    else:
        raise ValueError(f"Unknown command {args.command}")


if __name__ == "__main__":
    main()
