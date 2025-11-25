# Listen-N

Listen-N is a Windows Forms application for coordinating LISTEN-N neutron detectors, orchestrating data collection, adaptive analysis, and real-time visualization. The app targets .NET 8.0 (Windows) and uses OxyPlot/ScottPlot for plotting and System.IO.Ports for serial communication.

## Project structure
- **Entry point:** `Program.Main` boots WinForms and launches the `ListenN` form. 【F:Program.cs†L5-L16】
- **Main form (`ListenN`)** handles detector discovery/connection, run control (timed vs. continuous), file rollovers, logging, and real-time tube distribution plots. 【F:ListenN.cs†L44-L199】
- **Adaptive analysis:** `AdaptiveWindowEngine` processes detection events on a background thread, maintaining sliding windows and emitting Feynman-Y estimates with significance tracking. 【F:AdaptiveWindowEngine.cs†L1-L120】
- **Numerical helpers:** Supporting files such as `HageCalculation.cs`, `Multiplicity.cs`, `RootFinding.cs`, and `Histogram.cs` provide domain-specific math routines used by the UI and adaptive pipeline.
- **Resources & designers:** `.resx` files, `.Designer.cs` files, and the `Resources/` folder hold WinForms layout and embedded assets.

## Key features
- **Detector management:** Maintains a list of detectors, periodically pings them, auto-reconnects, and lets operators assign nicknames and send commands. 【F:ListenN.cs†L52-L199】
- **Run control:** Supports timed runs or continuous acquisition with configurable save folders, file size rollovers, and start/stop state guarding. 【F:ListenN.cs†L62-L183】
- **Visualization:** Provides live tube distribution plots with cumulative/rolling modes and context menu actions to copy or save images. 【F:ListenN.cs†L88-L199】
- **Adaptive statistics:** Background engine adapts gate/window sizes to maintain uncertainty targets and surfaces significance metrics for Feynman-Y. 【F:AdaptiveWindowEngine.cs†L1-L120】

## Getting started
1. **Prerequisites:** Windows with .NET 8.0 SDK installed. The project is WinForms-only (`<UseWindowsForms>true</UseWindowsForms>`) and references OxyPlot/ScottPlot plus serial I/O packages. 【F:Listen-N.csproj†L4-L18】
2. **Restore & build:**
   ```bash
   dotnet restore
   dotnet build
   ```
3. **Run:**
   ```bash
   dotnet run
   ```
   The app launches the `ListenN` form, enabling detector connection, run configuration, and live plotting.

## Tips for exploration
- Start in `ListenN.cs` to follow UI wiring, timers, and run-flow logic.
- Review `AdaptiveWindowEngine.cs` to understand how detection events are consumed and how estimates are produced.
- Check `HageCalculation.cs`, `Feynman*.cs`, `Multiplicity.cs`, and `Histogram.cs` for the statistical routines behind the UI visualizations.

## License
Currently no license file is present. Add one before distributing binaries.
