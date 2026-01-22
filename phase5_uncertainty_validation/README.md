# Phase 5 uncertainty validation

## Commands

Validate inputs:

```bash
python -m phase5_uncertainty_validation validate-inputs \
  --manifest path/to/manifest.csv \
  --data-root path/to/runs_root
```

Run the analysis:

```bash
python -m phase5_uncertainty_validation run \
  --manifest path/to/manifest.csv \
  --data-root path/to/runs_root \
  --outdir path/to/out_phase5 \
  --min-windows-per-run 30 \
  --min-runs-per-group 3 \
  --warmup-drop 0 \
  --reduced-count-factors 1.0,0.75,0.5,0.25,0.1 \
  --ci-level 0.95 \
  --seed 123
```

## Outputs

The command writes:

- `outdir/figures/`: PDF or PNG figures for covariance ratios, coverage, QQ plots, and residual calibration.
- `outdir/tables/`: CSV tables for group summaries, coverage, covariance comparisons, and breakdown analysis.
- `outdir/latex/`: LaTeX snippets for a dissertation subsection.
- `outdir/logs/summary.json`: counts of exclusions and any covariance regularization shifts.
- `outdir/README_results.md`: summary with pointers to figures and tables.

## Interpretation notes

- Grouping uses `covariance_group_id` and only rows flagged `usable_for_covariance`.
- The empirical covariance is computed across run means within each group.
- System covariance uses two aggregation modes: mean window covariance and mean-of-mean covariance.
- Reduced-count analysis uses deterministic window subsampling when raw counts are unavailable.
