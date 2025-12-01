# Adaptive Sliding-Window Multiplicity Engine

## Purpose and problem statement
The adaptive sliding window is designed for real-time neutron multiplicity monitoring where the source may be non-stationary (drifts, jumps, or correlated bursts). It ingests list-mode timestamps, maintains rolling factorial-moment estimates, and automatically adjusts gate width and observation window so Feynman-Y statistics stay both responsive and statistically stable for anomaly detection and safeguards use cases. 【F:AdaptiveWindowEngine.cs†L181-L207】【F:AdaptiveWindowEngineHarness.cs†L108-L124】

## Quantities produced
At each analysis step the engine emits estimates of the first three factorial moments (m1, m2, m3), the Feynman-Y statistic, its uncertainty (sigmaY), the corresponding Z-score (Y/sigmaY), the current gate width, and the finite-state machine (FSM) state flags (significance, low-rate, degraded, etc.). 【F:AdaptiveWindowEngine.cs†L413-L493】

## Sliding accumulator and window stepping
Timestamps are binned into a circular accumulator whose bin width is derived from the base gate ladder. The buffer slides forward as time advances, expiring old bins, while `Add` inserts new events and `SlideLeftTo` advances the head when timestamps exceed the buffer. Factorial moments across gates inside the current window are computed by sliding a gate-wide sum through the bins, yielding m1–m3 and their sample covariances. The analysis window length W is moved in steps of size S = max(beta·W, delta), clamped to reasonable bounds. 【F:AdaptiveWindowEngine.cs†L896-L1010】【F:AdaptiveWindowEngine.cs†L905-L918】【F:AdaptiveWindowEngine.cs†L920-L1010】【F:AdaptiveWindowEngine.cs†L253-L263】【F:AdaptiveWindowEngine.cs†L375-L379】

## Gate ladder selection and advancement
A ladder of candidate gate widths (tgUs) is evaluated each step. For each gate the engine computes Y and sigmaY, looking for the smallest gate with statistical significance (Z ≥ zMin) and a flat slope in log-space (plateau) to favor convergence. Gate transitions are double-confirmed before taking effect. If no gate reaches significance, the FSM is nudged into LowRate and the smallest gate is selected; otherwise, the desired gate is the first significant or plateau gate. 【F:AdaptiveWindowEngine.cs†L193-L204】【F:AdaptiveWindowEngine.cs†L400-L460】【F:AdaptiveWindowEngine.cs†L520-L544】【F:AdaptiveWindowEngine.cs†L440-L459】

## Significance testing and adaptive decisions
For each gate the engine forms Z = Y/sigmaY; significant gates require finite uncertainty, positive Y, and Z above zMin. The maximum |Z| across valid gates informs change detection thresholds (zTrack, zHold, zPoisson). Singles-rate and ZY Page–Hinkley detectors flag change-points that push the FSM into Hold or other adaptive states. Insufficient statistics and ill-conditioned covariance matrices mark degraded conditions that constrain decisions. 【F:AdaptiveWindowEngine.cs†L400-L470】【F:AdaptiveWindowEngine.cs†L176-L179】【F:AdaptiveWindowEngine.cs†L520-L698】

## Finite-state machine
The FSM progresses through Warmup → Poisson → Track with branches to Expand, Contract, Hold, LowRate, and Degraded. Warmup waits for a filled window and quiet Poisson behavior; Poisson validates stability before tracking; Track adapts gate/window unless a change or degradation is detected. Expand and Contract temporarily widen or shrink W when relative uncertainties exceed or undershoot targets. Hold freezes adaptation for a quiet horizon after change detection. LowRate and Degraded force small gates and cautious beta when significance is absent or the covariance is ill-conditioned or Y is non-positive. State requests require two confirmations to avoid chatter. 【F:AdaptiveWindowEngine.cs†L585-L699】【F:AdaptiveWindowEngine.cs†L701-L760】【F:AdaptiveWindowEngine.cs†L153-L179】

### Transition drivers
- **Warmup → Poisson/Track/Hold**: window filled with enough gates, low max |Z| for Poisson, or elevated |Z| triggers Track/Hold. 【F:AdaptiveWindowEngine.cs†L585-L617】
- **Poisson → Track/Hold/Degraded**: based on significance thresholds or degraded inputs. 【F:AdaptiveWindowEngine.cs†L619-L639】
- **Track → Expand/Contract/Hold/Degraded**: driven by relative uncertainty (relMax), change detectors, or degradation. 【F:AdaptiveWindowEngine.cs†L641-L666】
- **Expand/Contract → Track**: return when relative uncertainty returns toward targets or bounds reached. 【F:AdaptiveWindowEngine.cs†L667-L677】
- **LowRate/Degraded → Track**: regain significance and healthy covariances. 【F:AdaptiveWindowEngine.cs†L685-L698】

## Stabilizing estimates
AdaptWindow scales W using the larger of the relative uncertainties of Y and m1 against configurable targets (epsY, epsM1). W is clamped between a tau-informed lower bound (max(wMin, 3·tauHat)) and wMax, with scaling factors preventing abrupt jumps. The sliding step fraction beta depends on state (Track uses 0.5, others 0.1), yielding step S that bounds how far the window moves per estimate. Ill-conditioned fits or hitting wMax flag statsBound to signal uncertainty-limited operation. 【F:AdaptiveWindowEngine.cs†L155-L175】【F:AdaptiveWindowEngine.cs†L520-L544】【F:AdaptiveWindowEngine.cs†L667-L677】【F:AdaptiveWindowEngine.cs†L345-L352】【F:AdaptiveWindowEngine.cs†L375-L379】【F:AdaptiveWindowEngine.cs†L890-L918】

## Correlation-time estimation and model mismatch
Across the gate ladder, the engine fits a one-term exponential Y(t) model to derive tauHat. Weighted residuals indicate model mismatch; excessive RMS residuals mark ModelMismatch and influence adaptive behavior and Hold decisions. TauLowerBound uses tauHat to keep W above a multiple of the fitted correlation time. 【F:AdaptiveWindowEngine.cs†L445-L483】【F:AdaptiveWindowEngine.cs†L820-L884】【F:AdaptiveWindowEngine.cs†L340-L352】【F:AdaptiveWindowEngine.cs†L667-L676】

## Harness for validation
The harness builds deterministic timestamp scenarios: stable Poisson at multiple rates, slow drift, sudden rate jump, and correlated burst sources with configurable multiplicity and intra-burst jitter. Each scenario feeds the engine in single-threaded mode, forcing a step per timestamp to make the run reproducible. 【F:AdaptiveWindowEngineHarness.cs†L108-L124】【F:AdaptiveWindowEngineHarness.cs†L19-L90】【F:AdaptiveWindowEngineHarness.cs†L217-L223】

### Metrics captured
For every estimate, the harness writes a CSV row containing the scenario name, estimate index, time, FSM state, gate width, window size W, step S, tauHat, factorial moments, Y and sigmaY, significance flags, status flags (low-rate, degraded, stats-bound, insufficient statistics, model mismatch), max |Z| across gates, Poisson quiet streak, and debug counters for event/bin occupancy. These fields enable offline validation of adaptive behavior and reproducibility across scenarios. 【F:AdaptiveWindowEngineHarness.cs†L126-L215】

## Using the harness to verify adaptive behavior
Researchers can run the harness to produce a CSV trace for each synthetic source and then analyze whether FSM transitions, gate choices, and window adjustments align with expectations (e.g., quick expansion after a rate jump, contraction during low-rate periods, or Hold during correlated bursts). The recorded tauHat, step size, and significance flags make it straightforward to spot whether adaptation met uncertainty targets without instability, validating correctness before integrating with live detectors. 【F:AdaptiveWindowEngineHarness.cs†L108-L164】【F:AdaptiveWindowEngineHarness.cs†L185-L215】
