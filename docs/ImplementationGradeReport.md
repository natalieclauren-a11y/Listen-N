# LISTEN-N Implementation-Grade Archaeology Report

## 1) Executive summary
LISTEN-N is primarily a .NET 8 Windows Forms control application that manages neutron detector hardware over TCP/serial, streams list-mode binary events into `.lmx` files, and runs a real-time adaptive neutron noise analysis pipeline (factorial moments, Feynman-Y, covariance propagation, and FSM-driven operating modes). It has grown into a mixed runtime with three operational surfaces: GUI operations, deterministic replay CLI, and localization replay CLI. The repository also includes a distinct ML stack (`Localization.ML`, `Localization.Train`, `Localization.Eval`, `Localization.RuntimeCheck`) for feature extraction, classification/regression inference, OOD rejection, and model artifact validation.

The core online path is: detector bytes -> `Detector.ProcessAsBinary` decode -> `AdaptiveWindowEngine.OnDetection` -> per-step analysis (`RunAnalysis`) -> per-window summary (`RtWindowSummary`) -> localization policy/worker gating -> result/refusal records and NDJSON outputs. Offline paths include deterministic replay from list-mode text and localization replay from `rt_windows.ndjson`.

A practical rewrite split is feasible: move deterministic math + streaming state machine into C++ core; keep UI, orchestration, and deployment in C#. The largest coupling hotspot is `Detector.cs`, which currently combines transport, protocol dictionaries, parsing, file IO, state, and UI-facing concerns.

Evidence: `Program.cs`, `ListenN.cs`, `Detector.cs`, `AdaptiveWindowEngine.cs`, `RtReplayRunner.cs`, `LocalizationReplayRunner.cs`, `Localization.ML/*`, `Integrated/*`, `Localization.Train/Program.cs`.

---

## 2) Repo map + entry points

### Deliverable A — Repo Map

#### Top-level tree (concise)
- `Listen-N.csproj` root sources: WinForms app plus detector protocol/runtime analysis classes (not isolated into subfolders yet).
- `Integrated/`: localization runtime contracts/policy/worker/orchestration glue between adaptive windows and ML inference.
- `Localization.ML/`: reusable ML library (feature builder, trainer support, pipeline load/predict/save, schema/hash identity, Mahalanobis OOD).
- `Localization.Train/`: console training and artifact generation pipeline with plotting/report outputs.
- `Localization.Eval/`: console evaluation app for artifact-level model assessment/plots.
- `Localization.RuntimeCheck/`: runtime artifact/schema compatibility checker.
- `Localization.Metrics/` + `Localization.Metrics.Cli/`: phase-4 metrics model + CLI for scoring predictions against truth and producing CSV/JSON reports.
- `Listen-N.Tests/`, `Localization.ML.Tests/`, `Localization.Metrics.Tests/`: xUnit suites validating adaptive FSM behavior, ML feature/training utilities, and metrics calculations.
- `configs/`: replay runtime config JSON (`rt_replay_v1.json`).
- `docs/`: replay operational documentation.
- `phase5_uncertainty_validation/`: Python package/scripts for uncertainty validation workflows.

Evidence: `Localization.sln`, `Listen-N.csproj`, `README.md`, directory layout.

#### Primary executables/libraries and invocation
- WinForms app: `Listen-N` (from `Listen-N.csproj`), entry in `Program.Main`.
  - GUI mode: `dotnet run --project Listen-N.csproj`
  - Replay mode: `dotnet run --project Listen-N.csproj -- replay --input ...`
  - Localization replay mode: `dotnet run --project Listen-N.csproj -- localize-replay --input ... --output ... --models ...`
- ML training CLI: `Localization.Train` (`Localization.Train/Program.cs`).
- ML eval CLI: `Localization.Eval` (`Localization.Eval/Program.cs`).
- Runtime schema check CLI: `Localization.RuntimeCheck` (`Localization.RuntimeCheck/Program.cs`).
- Metrics CLI: `Localization.Metrics.Cli` (`Localization.Metrics.Cli/Program.cs`).
- Reusable libraries: `Localization.ML`, `Localization.Metrics`.

Evidence: `Program.cs`, `RtReplayRunner.cs`, `LocalizationReplayRunner.cs`, `Localization.Train/Program.cs`, `Localization.RuntimeCheck/Program.cs`, `Localization.Metrics.Cli/Program.cs`, project files.

---

## 3) Runtime pipelines (ingestion, analysis, localization, training/metrics)

## Deliverable B — System Architecture

### High-level text diagram
`Detector transport/protocol (TCP/Serial + command/reply)`
-> `Binary list-mode decode + LMX persistence`
-> `AdaptiveWindowEngine` (binning, moments, Y/sigmaY, gate selection, tau fit, FSM)
-> `RtWindowSummary / ReplayStep / Estimate events`
-> `LocalizationEpisodePolicy + LocalizationMlRequestLimiter`
-> `LocalizationWorker (bounded queue + cooldown)`
-> `LocalizationPipeline (features -> classifier/regressors -> OOD + centroid override)`
-> `Decision/event outputs (NDJSON/JSON summaries, GUI status)`

Supporting offline paths:
- `RtReplayRunner`: list-mode text -> same adaptive engine -> `rt_windows.ndjson` + `replay_summary.json`
- `LocalizationReplayRunner`: `rt_windows.ndjson` -> policy + worker + pipeline -> `localization_events.ndjson` + `localization_summary.json`
- `Localization.Train`: CSV datasets -> featurization -> ML.NET training -> artifact bundle + metrics/figures

Evidence: `Detector.cs`, `AdaptiveWindowEngine.cs`, `Integrated/LocalizationEpisodePolicy.cs`, `Integrated/LocalizationMlRequestLimiter.cs`, `Integrated/LocalizationWorker.cs`, `Localization.ML/LocalizationPipeline.cs`, `RtReplayRunner.cs`, `LocalizationReplayRunner.cs`, `Localization.Train/Program.cs`.

### Box-to-implementation mapping
- Detector I/O and protocol ownership:
  - Class: `Vf61Gui.Detector`.
  - Files: `Detector.cs`, plus data helper types (`ChannelCountsTime.cs`, `ThreatIdValues.cs`, `FileListItem.cs`, etc.).
- GUI orchestration ownership:
  - Class: `Listen_N.ListenN`.
  - File: `ListenN.cs`.
- Adaptive analysis ownership:
  - Class: `Listen_N.AdaptiveWindowEngine`.
  - File: `AdaptiveWindowEngine.cs`.
- Replay orchestration ownership:
  - Classes: `RtReplayRunner`, `LocalizationReplayRunner`.
  - Files: `RtReplayRunner.cs`, `LocalizationReplayRunner.cs`.
- Localization runtime policy/worker ownership:
  - Classes: `LocalizationEpisodePolicy`, `LocalizationMlRequestLimiter`, `LocalizationWorker`, `LocalizationHealthTracker`.
  - Files: `Integrated/*.cs`.
- ML inference ownership:
  - Class: `Localization.ML.LocalizationPipeline` and helpers (`FeatureBuilder`, `MahalanobisScorer`, `RegressionModelGroup`).
  - Files: `Localization.ML/*.cs`.

### Cross-cutting concerns
- Config loading:
  - Replay: `RtReplayConfig.Load` with strict validation.
  - Localization runtime: `LocalizationRuntimeConfig.Load/Validate`.
- Serialization/artifacts:
  - NDJSON/JSON writing in replay/localization runners.
  - Model artifacts under `classifier.zip`, `single_regressor/`, `dual_regressor/`, `pipeline_config.json`, `mahalanobis.json`, `manifest.json`.
- Concurrency:
  - `AdaptiveWindowEngine` uses `Channel<Detection>` and background worker.
  - `LocalizationWorker` uses bounded channel and queue saturation policy.
- Logging/diagnostics:
  - Detector ring buffers for sent/received/unknown/exception messages.
  - Localization decision records and event logs.

Evidence: `RtReplayConfig.cs`, `Integrated/LocalizationRuntimeConfig.cs`, `RtReplayRunner.cs`, `LocalizationReplayRunner.cs`, `AdaptiveWindowEngine.cs`, `Integrated/LocalizationWorker.cs`, `Detector.cs`.

---

## 4) Data contracts + algorithms

## Deliverable C — Data Contracts + Algorithms

### Artifact/DataType table
| Artifact / DataType | Defined in | Fields (key) | Produced by | Consumed by | Notes / assumptions |
|---|---|---|---|---|---|
| `Detection` | `AdaptiveWindowEngine.cs` | `TicksUs`, `DetectorId` | `Detector.ProcessAsBinary`, replay loaders | `AdaptiveWindowEngine.OnDetection` | timestamps assumed monotonic in replay path |
| `Estimate` | `AdaptiveWindowEngine.cs` | moments/covariances, `Y`, `SigmaY`, FSM flags | `AdaptiveWindowEngine.RunAnalysis` | GUI + replay subscribers | includes quality/diagnostic flags |
| `ReplayStep` | `AdaptiveWindowEngine.cs` | gate arrays, residuals, FSM/gate confirmation internals | adaptive replay callbacks | replay output tooling | detailed deterministic diagnostics |
| `RtWindowSummary` | `Integrated/LocalizationContracts.cs` | window times, `Counts15`, `RtState`, quality scalar | adaptive runtime | localization policy/replay | expects 15 channels |
| `AnalysisSnapshot` | `Integrated/LocalizationContracts.cs` | tube counts, totals, `Y`, `SigmaY`, FSM/flags, schema | integration layer | policy + trigger interfaces | schema hash/version carried as guard |
| `LocalizationRequest` / runtime request | `Integrated/LocalizationContracts.cs` + `Integrated/LocalizationIntegration.cs` | request id, trigger source/reason, metadata | operator/manual or auto policy | orchestrator/worker | manual bypass flag supported |
| `LocalizationResult` / prediction contracts | `Integrated/LocalizationContracts.cs`, `Integrated/LocalizationIntegration.cs` | label/coords/confidence/OOD/refusal | worker + pipeline | logs/UI/publishers | refusal reasons explicit |
| ML input row `LocalizationRow` | `Localization.ML/DataModels.cs` | 15 channels, duration, optional labels/coords metadata | train/eval/runtime wrappers | `FeatureBuilder` + `LocalizationPipeline` | channel count invariant = 15 |
| Feature vector | `Localization.ML/FeatureBuilder.cs` | 36 features (raw + total + normalized + entropy/gini/anisotropy/dipole + duration) | `FeatureBuilder` | trainers + inference | strict parity check |
| `rt_windows.ndjson` | runtime/replay writer path | per-window adaptive stats | `RtReplayRunner` | `LocalizationReplayRunner`, metrics CLI | canonical offline input for localization replay |
| `replay_summary.json` | replay path | run metadata + counts + hash | `RtReplayRunner` | audit scripts | config hash pinned |
| `localization_events.ndjson` | localization replay path | request/result/refusal events | `LocalizationReplayRunner` | downstream analysis | schema versioned (`v1.1`) |
| `localization_summary.json` | localization replay path | counts/hash | `LocalizationReplayRunner` | CI/audit | includes event sha256 |
| training artifact bundle | `LocalizationPipeline.Save` + train program | model zips + config + mahalanobis + summary + trigger policy | `Localization.Train` | runtime load and checks | schema hash, trainer IDs embedded |

Evidence: `AdaptiveWindowEngine.cs`, `Integrated/LocalizationContracts.cs`, `Integrated/LocalizationIntegration.cs`, `Localization.ML/DataModels.cs`, `Localization.ML/FeatureBuilder.cs`, `Localization.ML/LocalizationPipeline.cs`, `RtReplayRunner.cs`, `LocalizationReplayRunner.cs`.

### File formats and decoding rules
- LMX/list-mode network stream:
  - 8-byte event word; low 5 bits = channel ID, upper 59 bits = 10ns ticks.
  - Conversion used in code: `timestampUs = ticks10ns / 100`.
  - Header captured before `BinaryDataFollows`, with stream termination marker `CopyDone\n`.
- Replay input list mode (text):
  - One timestamp token per line (or first token in CSV/space-separated line), optional detector id token.
  - timestamp allowed as integer microseconds or floating seconds.
- Runtime/replay outputs:
  - NDJSON (`rt_windows.ndjson`, `localization_events.ndjson`) and JSON summaries.

Evidence: `Detector.cs` (`ProcessAsBinary`, `OpenNetLmxFile`), `RtReplayRunner.cs` (`LoadDetections`, `TryParseTimestamp`), `docs/replay.md`, `LocalizationReplayRunner.cs`.

### Invariants / thresholds observed
- Replay config validation enforces positive/sorted gates, valid window ranges, significance thresholds, FSM confirmation counts.
- Trigger defaults block states `{Hold, LowRate, Poisson, Degraded}` and require minimum counts/Zy.
- Runtime config validation enforces artifact existence, allowed schema hashes, supported durations, queue limits.
- Feature invariants enforce exactly 15 channels and 36 computed features, no NaN/Inf.

Evidence: `RtReplayConfig.cs`, `Integrated/LocalizationContracts.cs` (`DefaultLocalizationTriggerPolicy`), `Integrated/LocalizationRuntimeConfig.cs`, `Localization.ML/FeatureBuilder.cs`.

### Math / physics map (implementation anchors)
- Feynman Y:
  - `Y = (m2 - m1^2) / m1`, implemented in `MomentsMath.Y`.
- Uncertainty propagation:
  - linearized gradient method from `GradY` and `VarY` with covariance terms.
- Covariance regularization:
  - diagonal inflation by `lambda = epsilon * max(1,maxDiag)` in `RegularizeCov`.
- Correlation-time estimation:
  - gate-ladder fit pipeline in `FitCorrelationTime` with MAD outlier rejection.
- Adaptive window control:
  - relative uncertainty targets (`epsY`, `epsM1`) and shrink/expand multipliers in `AdaptWindow`.
- FSM mode transitions:
  - transitions among `Warmup/Track/Hold/Expand/Contract/LowRate/Degraded/Poisson` in `AdaptState` + `RequestFsmState` + `EnterState`.
- Localization feature math:
  - entropy, gini, anisotropy, dipole in `FeatureBuilder`.
- OOD scoring:
  - Mahalanobis distance over 4 derived features in `MahalanobisScorer` and `LocalizationPipeline`.

Evidence: `AdaptiveWindowEngine.cs`, `Localization.ML/FeatureBuilder.cs`, `Localization.ML/MahalanobisScorer.cs`, `Localization.ML/LocalizationPipeline.cs`.

---

## 5) Detector.cs refactor plan

## Deliverable D — Detector.cs Refactor Plan

### Observation: current dependency situation (what exists)
`Detector.cs` currently owns too many responsibilities:
- transport lifecycle (TCP/serial init/read/write/close)
- protocol command/reply dictionaries and dispatch tables
- binary/text parsing
- list-mode file persistence (`.lmx` headers/data)
- adaptive forwarding (`Adaptive?.OnDetection(...)`)
- state machine bits + timers + config file parsing
- UI concern leakage (`MessageBox.Show`, public mutable fields, event handlers)

It has broad type coupling to UI and utility classes (`System.Windows.Forms`, bitmap plot classes, status structs, ring buffers, config dictionaries), and many external consumers (`ListenN`, `FormFileTransfer`, `TabPageMessages`, `MomentsClient`).

Evidence: `Detector.cs`, `ListenN.cs`, `FormFileTransfer.cs`, `TabPageMessages.cs`, `MomentsClient.cs`.

### Public API surface snapshot (selected high-value members)
- Construction/lifecycle: `Detector()`, `Dispose()`, `Close()`.
- Connectivity: `IpInitializeTcp(...)`, `InitializeSerialPort(...)`.
- Command path: `Write`, `WriteAndWait`, `Send`.
- Binary processing path: `ProcessAsBinary`, `OpenNetLmxFile`.
- Config path: `ReadConfigFiles`, `SaveSetupFile`, many `ProcessSw*` handlers.
- State helpers: `SetState`, `ClearState`, `CheckState`, `ToggleState`.

Evidence: `Detector.cs` method declarations.

### Dependency table (targeted inversion)
| Dependency | Why it exists now | Keep/Remove | Replacement strategy |
|---|---|---|---|
| `System.IO.Ports`, `TcpClient/NetworkStream` | physical detector communication | Keep (adapter) | move to `IDetectorTransport` + TCP/Serial adapters |
| `MessageBox.Show` | immediate error surfacing | Remove from core | inject `IDetectorLogger`/`INotifier`; UI decides popup policy |
| file IO (`BinaryWriter`, path logic) | `.lmx` storage | Keep (adapter) | move to `ILmxSink` service |
| command/reply dictionaries in same class | protocol parsing | Keep logic, relocate | isolate into `DetectorProtocol` parser/dispatcher |
| `AdaptiveWindowEngine` direct reference | event forwarding | Keep boundary but invert | emit decoded events via interface/event (`IDetectionSink`) |
| WinForms-centric event handlers/bitmap references | GUI rendering state | Remove from core | push to GUI presenter/viewmodel layer |
| config file parsing for sw/hw defaults | startup configuration | Keep but separate | `IDetectorConfigRepository` |

### Proposed new file layout / namespaces
- `ListenN.DetectorCore/`
  - `DetectorCore.cs` (pure state machine + command protocol state)
  - `DetectorProtocolParser.cs` (text/binary decode)
  - `DetectorState.cs` (immutable/mutable domain snapshot)
  - `Contracts/DetectorCommand.cs`, `DetectorReply.cs`, `DetectionEvent.cs`
- `ListenN.DetectorAdapters/`
  - `TcpDetectorTransport.cs`, `SerialDetectorTransport.cs`
  - `LmxFileSink.cs`
  - `DetectorConfigFileRepository.cs`
  - `WinFormsNotifier.cs` (optional UI adapter only)
- `ListenN.App/` (existing WinForms host)
  - DI wiring in `ListenN.cs` or startup composition root.

### Minimal new API for DetectorCore
- `DetectorCore(DetectorCoreOptions options)`
- `void OnBytes(ReadOnlySpan<byte> chunk)`
- `void OnTextLine(string line)`
- `IReadOnlyList<DetectorDomainEvent> DrainEvents()`
- `DetectorSnapshot GetSnapshot()`
- `CommandEnvelope BuildCommand(DetectorCommand cmd)`
- `void ApplyCommandResult(CommandResult result)`
- `void Reset()`

No file IO, no UI, no transport objects inside `DetectorCore`.

### Step-by-step refactor plan (build stays green)
1. Introduce interfaces only (`IDetectorTransport`, `ILmxSink`, `IDetectorNotifier`, `IDetectionSink`) and adapt existing `Detector` to consume them with defaults.
2. Extract binary decode (`8-byte word -> Detection`) into pure static helper + tests.
3. Extract LMX header/write behavior into `LmxFileSink`; keep old calls delegated.
4. Extract command/reply dictionary setup into `DetectorProtocolRegistry` class.
5. Move `ProcessAsBinary` + text parse routing into `DetectorProtocolParser` with no UI calls.
6. Replace direct `MessageBox`/UI access with notifier callback; UI adapter shows dialogs.
7. Introduce `DetectorCore` and route `Detector` as façade/adapter.
8. Migrate `ListenN` and other callers to `DetectorCore + adapters`; deprecate monolithic `Detector`.
9. Remove dead fields and public mutable state, replace with snapshot + explicit methods.
10. Add contract tests (golden byte stream -> decoded events, command/reply expectations, copy completion semantics).

Evidence: `Detector.cs`, call sites in `ListenN.cs`/`FormFileTransfer.cs`/`TabPageMessages.cs`, tests under `Listen-N.Tests`.

---

## 6) C++/C# rewrite blueprint

## Deliverable E — Rewrite Blueprint

### What belongs in C++ core
- Streaming bin accumulator and window mechanics.
- Factorial moments + covariance + Y/sigmaY propagation.
- Gate selection, correlation time fit, Page-Hinkley detection.
- FSM transitions and adaptive window logic.
- Feature extraction (15-channel -> 36 features) and policy gating.
- Optional inference execution abstraction (or feature-only if model inference remains in C# temporarily).

Evidence: performance/algorithm concentration in `AdaptiveWindowEngine.cs` and `Localization.ML/FeatureBuilder.cs`.

### What remains in C#
- WinForms UI, operators, plotting, detector management UX.
- Runtime orchestration, config loading, artifacts and file browsing.
- Training/eval CLIs (can remain .NET initially due ML.NET dependence).
- Packaging/install tooling.

Evidence: `ListenN.cs`, `Form*`, `Localization.Train/*`, `Localization.Eval/*`.

### Interop boundary choice
**Recommended:** C ABI + P/Invoke.
- Why: no C++/CLI dependency on specific .NET runtime + easier cross-platform packaging + straightforward streaming/batch APIs.
- Throughput: event batching via pinned arrays amortizes interop overhead.
- Latency: deterministic native step execution per batch/step call.

### Stable C API contract sketch
- `ln_core_handle ln_core_create(const ln_config* cfg, ln_error* err);`
- `void ln_core_destroy(ln_core_handle h);`
- `int ln_core_reset(ln_core_handle h);`
- `int ln_core_push_events(ln_core_handle h, const ln_event* events, size_t count);`
- `int ln_core_force_step(ln_core_handle h, int64_t now_us);`
- `int ln_core_drain_windows(ln_core_handle h, ln_window_summary* out_buf, size_t* inout_count);`
- `int ln_core_get_state(ln_core_handle h, ln_state_snapshot* out_state);`
- `int ln_core_last_error(ln_core_handle h, char* utf8_buf, size_t* inout_len);`

Structs:
- `ln_event { int64_t ts_us; uint8_t detector_id; }`
- `ln_window_summary { ... y, sigma_y, selected_gate_us, state, counts15[15], duration_s ... }`
- `ln_localization_features { double features[36]; ... }`

### Memory + threading model
- Ownership: caller owns input arrays; core copies minimal metadata and processes synchronously unless configured worker thread enabled.
- Outputs: caller supplies buffers; API returns required count for two-pass allocation.
- Zero-copy: use pinned arrays in C# (`fixed`/`GCHandle`) for batch event push.
- Determinism: single-thread deterministic mode for replay parity; optional threaded mode for live GUI.

### C# wrapper design
- `SafeHandle` around native handle.
- `DllImport` signatures for create/push/step/drain/get_state.
- Managed DTOs mirroring existing `RtWindowSummary` and adaptive estimate fields.
- Adapter implementing current event callbacks in `ListenN`.

### Validation strategy
- Golden parity corpus:
  - feed same list-mode input + replay config into C# and C++ cores.
  - compare `rt_windows.ndjson` fields with tolerance bands.
- Numeric checks:
  - exact match for integer fields/state transitions/gate index when deterministic.
  - floating tolerances for `Y`, `sigmaY`, tau, residuals.
- Contract tests:
  - malformed input handling, monotonic timestamp enforcement, queue saturation behavior.

Evidence: `RtReplayRunner.cs`, `AdaptiveWindowEngine.cs`, tests in `Listen-N.Tests`, localization replay outputs in `LocalizationReplayRunner.cs`.

---

## 7) Risks and unknowns (specific next inspections)

### Known unknowns / missing evidence
- Exact detector protocol grammar is implicit across many `Process*` handlers; not centralized in a spec file.
- Some legacy code paths in `Detector.Write` appear duplicated/bug-prone (`networkStream` branch repeated, stray `serialPort.WriteLine` in TCP branch), requiring behavioral confirmation against hardware.
- `Localization.Train/Program.cs` is very large and multiplexes many figure/report subcommands; a command-by-command extraction matrix should be produced before rewrite.
- GUI-to-core coupling still includes direct field access patterns that are hard to infer statically without running forms workflow.

Evidence: `Detector.cs`, `Localization.Train/Program.cs`, `ListenN.cs`.

### What to inspect next
- Build a full protocol command/reply inventory from `Detector` dictionary initializers into a machine-readable table.
- Instrument one live/replay run and capture event sequence to validate ownership boundaries.
- Enumerate all mutable public fields in `Detector` and classify by read/write callers.
- Generate static dependency graph (`msbuild /t:GenerateRestoreGraphFile` + Roslyn call graph script) for objective migration planning.

