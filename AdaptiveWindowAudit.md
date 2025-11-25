# Adaptive Sliding Window Implementation Audit

This audit compares the current `AdaptiveWindowEngine` implementation to the Adaptive Sliding Window requirements spec. Each discrepancy lists the spec clause and the affected code locations, followed by suggested corrections.

## Findings

1. **Missing correlation-time fitting and tau usage (Correlation Time Fitting & FSM rules)**
   - The implementation never fits the required one-parameter correlation-time model or uses weighted least squares over the gate ladder. There is no `tau_hat`, and window/step-size decisions ignore `c_tau` and plateauing behavior tied to tau estimates. 【F:AdaptiveWindowEngine.cs†L190-L313】
   - **Correction:** Add a `FitCorrelationTime` routine that evaluates `Y_hat(Tg)` with weights `1/σ_Y^2` across significant gates, estimates `tau_hat`, and caches residuals. Use `tau_hat` to bound window contraction (`W >= c_tau * tau_hat`), initialize Hold horizons, and drive gate hysteresis.

2. **Gate selection lacks full hysteresis and plateau rule (Gate Width Selection)**
   - The plateau check uses `eta = _etaFrac * |Y|` but does not evaluate `|ΔY/Δ log Tg| ≤ eta` only for Tg ≥ first significant gate; it also does not require two consecutive confirmations before switching or enforce hysteresis across significance/plateau transitions. 【F:AdaptiveWindowEngine.cs†L205-L274】
   - **Correction:** Track previous significant/plateau decisions, require two consecutive updates confirming the same `desiredIdx`, and only allow plateau evaluation for gates at or above the first significant Tg as stated. Maintain Tg ladder as log-spaced and reset to smallest Tg in degraded/low-rate states.

3. **Low-rate mode behavior incomplete (Low-Rate Mode & Outputs)**
   - When no gate meets significance, the code sets `_fsm = LowRate` and forces the smallest gate, but does not increase `W` toward `W_max`, lacks an "insufficient statistics" flag, and does not track singles-only reporting. Exit criteria rely on the current gate only rather than re-evaluating the ladder. 【F:AdaptiveWindowEngine.cs†L262-L439】
   - **Correction:** In LowRate, always set `Tg` to the smallest ladder value, ramp `W` toward `W_max` each step, and emit flags/estimates that indicate insufficient statistics while continuing singles tracking. Exit LowRate when any ladder element achieves significance.

4. **Finite-state machine deviates from required transitions (FSM section)**
   - FSM lacks minimum dwell/confirmation for transitions, omits `Hold` horizon logic (`H = max(5S, 4τ_hat)`), and does not set `W ← max(W_min, c_tau * tau_hat)` or `S ← 0.1 W` on entering Hold. Expand/Contract lack bounds `r_down`, `r_up` and do not raise `Stats-Bound` when `W` hits `W_max` while targets remain unmet. Degraded state triggers only on `NaN/Inf` σY instead of covariance singularity, negative Y across all Tg, or saturation. 【F:AdaptiveWindowEngine.cs†L369-L439】
   - **Correction:** Implement explicit state entry/exit guards with dwell counters; on Hold entry, set `W` and `S` per spec and remain until both change-point monitors are quiet for `H`. Expand/Contract should scale `W` using `r_down/r_up` caps and raise Stats-Bound when maxed out. Degraded should trigger on ill-conditioned covariance, persistent negative Y across the ladder, or deadtime cues, and reset Tg to the smallest value.

5. **Window-length adaptation formula diverges from spec (Window Length Adaptation)**
   - Current scaling uses fixed `rDown=0.5` and `rUp=2.0` irrespective of targets and lacks `clip` on the max of the squared relative errors. `W_req` is not clamped against `[W_min, W_max]` via the specified multiplicative bounds per update, and beta selection is a heuristic instead of the state-driven `beta ∈ [0.1, 0.5]` with smaller values during transients. 【F:AdaptiveWindowEngine.cs†L346-L439】
   - **Correction:** Compute `W_req = clip(W * max((σY/Y)/εY)^2, (σm1/m1)/εm1)^2], r_down, r_up)`, then clamp to `[W_min, W_max]`. Drive `beta` by FSM state (`0.1` in transients/Hold, `0.5` when stable) rather than relative-error thresholds alone.

6. **Step size lacks FSM coupling (Step Size section)**
   - `StepSizeSec` always returns `max(delta, beta*W)` without the spec’s `S = beta * W` guidance tied to state stability; Hold state requires `S ← 0.1 W`. 【F:AdaptiveWindowEngine.cs†L183-L188】【F:AdaptiveWindowEngine.cs†L417-L439】
   - **Correction:** Compute `S` directly as `beta * W` with `beta` set by state, clamped to `[0.1W, 0.5W]`, and override to `0.1W` during Hold or other transients.

7. **Change-point monitoring incomplete (Change-Point Monitoring)**
   - Only a single Page–Hinkley on m1 is implemented with fixed `λ` and `δ`, omitting the forgetting factor range, ARL tuning, and the separate correlation monitor on `Z_Y`. Hold exit does not wait for both monitors to be quiet for horizon `H`. 【F:AdaptiveWindowEngine.cs†L112-L115】【F:AdaptiveWindowEngine.cs†L449-L457】
   - **Correction:** Add dual monitors: a tunable Page–Hinkley/CUSUM for singles with `λ ∈ [0.97, 0.995]` and a correlation jump monitor on `Z_Y`. Trigger Hold when either fires, enforce quiet horizon `H`, and reset accumulators when re-entering Track.

8. **Moment estimation outputs incomplete (Moment Estimation & Outputs)**
   - While m1–m3 and their covariances are computed, the engine does not expose `m2_hat`, `m3_hat`, or their uncertainties in `Estimate`, nor does it emit doubles or quality flags (Low-Rate, Degraded, Stats-Bound). 【F:AdaptiveWindowEngine.cs†L39-L313】
   - **Correction:** Extend `Estimate` to include `m2_hat`, `m3_hat`, uncertainties for singles/doubles, and quality/status flags per spec, including Low-Rate, Degraded, Stats-Bound, and insufficient statistics.

9. **Guardrails on covariance conditioning absent (Guardrails section)**
   - No ridge regularization or conditioning checks are applied before using the covariance matrix. Degraded entry does not consider ill-conditioned covariance or residual misfit from the correlation-time model. 【F:AdaptiveWindowEngine.cs†L218-L239】【F:AdaptiveWindowEngine.cs†L428-L431】
   - **Correction:** Before propagating variance, regularize covariance (`Cov + λI`), detect singular/negative variances, and enter Degraded with flags if stabilization fails. Monitor correlation-fit residuals and raise model-mismatch flags when tolerance is exceeded.

10. **Feynman-Y validity checks missing (Feynman-Y definition & guardrails)**
    - The code accepts negative or zero Y without flagging or entering Degraded when Y is negative across all gate widths, contrary to the spec. 【F:AdaptiveWindowEngine.cs†L218-L270】
    - **Correction:** Track Y across the ladder; if all Y_hat ≤ 0 with valid uncertainties, enter Degraded and report singles-only until recovery. Also guard against division by near-zero m1 and emit a clear invalid-Y flag.

11. **Change logging/testing hooks not in spec (Validation Criteria)**
    - Logging to `adaptive.log` occurs every step without alignment to validation datasets or modes; there are no hooks for the required validation scenarios (BeRP, dynamic moderation, MUSIC, Godiva) or convergence checks to fixed-window baselines. 【F:AdaptiveWindowEngine.cs†L289-L313】
    - **Correction:** Add validation hooks or modes that compare adaptive outputs to fixed-window references on the specified datasets, capturing convergence and tracking behavior for automated tests.

## Covariance propagation assessment

The gradient-based propagation for Feynman-Y uses the correct partial derivatives and includes covariance `Cov(m1, m2)`; however, it omits the full empirical covariance matrix `Cov(m)` in outputs and does not regularize ill-conditioned matrices before propagation. No uncertainty is reported for `m1_hat`, `m2_hat`, or `m3_hat`, and the calculation is skipped when `N ≤ 1`, which can mask low-statistics issues instead of flagging them. 【F:AdaptiveWindowEngine.cs†L218-L239】【F:AdaptiveWindowEngine.cs†L582-L605】

## Recommended implementation steps

- Implement correlation-time fitting with weighted least squares across the gate ladder, storing `tau_hat`, residuals, and using them to steer `W`, `S`, and FSM transitions.
- Rework FSM logic to honor dwell/confirmation, Hold horizons, Expand/Contract bounds, LowRate/Degraded entry criteria, and quality flags.
- Refine window-length and step-size adaptation to follow the specified `clip` formula with `r_down/r_up`, `beta` tied to state, and clamp against `[W_min, W_max]`.
- Add dual change-point monitors (singles and correlation), and enforce Hold-state behavior per spec.
- Expose full moment estimates, uncertainties, and quality flags in `Estimate`, and add guardrails for invalid/ill-conditioned covariance with ridge regularization.
- Enhance gate selection to incorporate plateau detection after significance, with two-step hysteresis and recovery rules for LowRate/Degraded.
- Add validation/test scaffolding to exercise the required datasets and verify convergence to fixed-window results.
