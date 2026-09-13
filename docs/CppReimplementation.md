# C++ reimplementation: architecture and migration plan

## Decision

LISTEN-N should be migrated as a **strangler rewrite**, not replaced in one large
change. The native core in `cpp/` is synchronous and deterministic. A host owns
threads, sockets, serial ports, files, logs, and UI. The C ABI is the compatibility
boundary for the current C# host and for future Qt, service, or Python hosts.

```text
TCP / serial adapters -> protocol decoder -> analyzer -> window DTOs
                                               |
                         replay CLI / C ABI / future UI
```

This structure makes replay the executable specification: identical event batches
must produce equivalent window records in C# and C++ before a component migrates.

## Flaws found and corrective design

| Current flaw | Consequence | C++ design / recommended action |
| --- | --- | --- |
| `Detector.cs` combines transport, parsing, persistence, mutable device state, analysis forwarding, and UI notifications. | Hardware-free tests are difficult and faults cross subsystem boundaries. | Keep the decoder pure; implement TCP, serial, and LMX as replaceable host adapters. Remove WinForms calls from the domain layer. |
| `ListenN.cs` is both form and application coordinator. | Run lifecycle cannot be reused by replay/service hosts. | Extract an application service and expose immutable snapshots to the view. Eventually replace WinForms only if cross-platform UI is a requirement. |
| `AdaptiveWindowEngine.cs` combines queueing, clocks, numerical methods, policy, and diagnostics. | Timing and numeric behavior are hard to test separately. | Native `Analyzer` is synchronous. Add covariance, fitting, and FSM as independent stateful components; schedule them in the host. |
| Public mutable detector fields and sentinel-heavy state are widely shared. | Invalid states and races are representable. | Use validated config values, value DTOs, explicit state transitions, and read-only snapshots. |
| Several executables and tests are included/excluded through broad glob rules. | Project ownership is unclear and accidental compilation is likely. | Give each deployable one CMake target; split managed projects by UI, adapters, and compatibility wrapper. |
| The repository contains generated images, duplicate harness CSVs, and `LISTEN.zip`. | Large, stale artifacts obscure source and inflate clones. | Verify provenance, move reproducible outputs to CI artifacts, and delete duplicates/binary archives from source control. |
| Protocol behavior is encoded in handlers rather than a versioned specification. | A rewrite can silently change hardware behavior. | Capture golden byte streams and command/reply transcripts; version the grammar and test malformed/truncated input. |
| ML.NET artifact loading is coupled to the managed runtime. | A total C++ port would require a model conversion with parity risk. | Keep training in C#. Define an inference port; adopt ONNX only after prediction and calibration parity tests pass. |

## Migration sequence and acceptance gates

1. **Protocol (implemented):** compare golden 64-bit words and fragmented reads.
2. **Window primitives (implemented baseline):** compare counts, multiplicities, and
   Feynman-Y. Add C#-generated golden replay fixtures.
3. **Numerics:** port covariance regularization and correlation-time fitting as pure
   functions with randomized parity tests and explicit tolerances.
4. **Adaptive controller:** port Page-Hinkley, gate selection, and FSM. Require exact
   state-transition parity and bounded floating-point drift.
5. **Interop:** add a managed `SafeHandle` wrapper and batch events over the C ABI.
   Shadow-run both engines in production before selecting native output.
6. **Hardware adapters:** extract TCP/serial/LMX from `Detector.cs`; retain simulated
   detector contract tests and fault-injection tests.
7. **Presentation:** leave WinForms as a thin host or build a Qt/QML host. UI replacement
   is independent of physics-core correctness.
8. **Removal:** only delete a C# component after replay parity, soak testing, and a
   documented rollback release have passed.

## Explicit non-goals for this increment

The baseline does not claim parity for uncertainty propagation, adaptive gate
selection, correlation-time fitting, FSM behavior, localization policy, ML
inference, hardware control, or the operator GUI. Labeling those omissions is safer
than presenting an incomplete port as suitable for neutron-assay decisions.
