# Listen-N C++ core

This directory is the first runnable, cross-platform implementation of the
LISTEN-N runtime domain core. It is intentionally **not** a line-for-line port of
the WinForms application. Transport, presentation, persistence, and analysis are
separate boundaries so that detector logic can be tested without hardware or a UI.

Implemented now:

- incremental decoding of the detector's little-endian 64-bit event words;
- validated, timestamp-ordered event ingestion and 15-channel window summaries;
- gate multiplicities and Feynman-Y calculation;
- a stable exception-safe C ABI suitable for P/Invoke;
- a dependency-free replay CLI and native tests.

Build and run:

```sh
cmake -S cpp -B build/cpp -DCMAKE_BUILD_TYPE=Release
cmake --build build/cpp
ctest --test-dir build/cpp --output-on-failure
build/cpp/listen-n-cli replay events.csv --window-us 1000000 --gate-us 1000
```

The legacy C# UI and ML.NET training programs remain available while migration is
incremental. See [`../docs/CppReimplementation.md`](../docs/CppReimplementation.md)
for the architecture decision, known flaws, and deletion/migration sequence.
