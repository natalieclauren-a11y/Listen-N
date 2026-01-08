/*
 * Abandon all hope, ye who enter here.
 * Im so sorry 
 */

// ListenN.cs
// Main application form for the LISTEN-N system.
// Orchestrates detector I/O, adaptive window analysis, logging, and real-time plotting.
// Handles asynchronous socket input, manages detector state, and updates the GUI. 


using OxyPlot;
using OxyPlot.Series;
using OxyPlot.WindowsForms;
using OxyPlot.Axes;
using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Data;
using System.Drawing;
using System.Windows.Forms;
using System.Xml;
using Listen_N;
using Vf61Gui;
using static System.ComponentModel.Design.ObjectSelectorEditor;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using static System.ComponentModel.Design.ObjectSelectorEditor;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.TaskbarClock;
using System.Net;
using WinFormsListViewItem = System.Windows.Forms.ListViewItem;
using Vf61Gui.Moments;
using System.Threading.Channels;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Integrated.Contracts;
using Integrated.Runtime;

namespace Listen_N
{
    // Main WinForms application form.
    // Coordinates detector configuration, socket I/O, adaptive window engine, logging, and plotting.
    // Heavy use of async sockets and background tasks; UI updates must be marshaled to the main thread.
    public partial class ListenN : Form
    {
        // --- Detector and networking state ---
        // ---- Detector & connection state ----

        // All known detectors (whether connected or not). Order mirrors listViewIpAddresses.
        private List<Detector> detectors = new List<Detector>();

        // Periodic attempt to reconnect offline detectors (non-blocking)
        private System.Windows.Forms.Timer reconnectTimer;

        // ---- Command input history (arrow up/down in the commands box) ----
        private List<string> commandsSent = new List<string>();
        private int commandsSentIndex = 0;

        // ---- File save / rollover control ----
        private long maxFileSizeBytes = 500 * 1024 * 1024; // Default 500 MB file size target for rollovers
        private int fileIndex = 1;                         // Incremented at each rollover
        private string baseFileName = "NetSaveFile";       // Must match instrument expectations
        private string currentFileName = "";               // Updated at start/rollover
        private System.Windows.Forms.Timer fileSizeTimer;  // 5-min guard timer to re-check size
        private bool runInProgress = false;                // UI toggle and accumulation guard

        // ---- Live rate refresh ----
        private System.Windows.Forms.Timer rateUpdateTimer; // Ticks every 1s to request "Rates" from online detectors

        // Optional nicknames displayed alongside IPs
        private Dictionary<string, string> detectorNicknames = new Dictionary<string, string>();

        // Cancellation source for the *scheduled* rollover (based on estimated size from live rate)
        private CancellationTokenSource? rolloverCts = null;

        // ---- Acquisition mode: timed vs continuous ----
        private int durationInSeconds = 300; // Timed run default (300 s)

        // Local folder for COMPUTER storage (NET)
        private string localSaveDirectory = Path.Combine(Application.StartupPath, "SavedFiles");

        // SNM selection (vs1/vs2/vi) shown on Configure tab
        private Snm snm = new Snm();

        // ---- Plotting (Tube Distribution) ----
        private CategoryAxis categoryAxis;
        private LinearAxis valueAxis;
        private BarSeries barSeriesCounts;
        private int tubeDisplayOffset = 0; // 0 => channels 1–16, 16 => 17–32, etc.

        // Palette consistent with original BitmapBarPlot
        private readonly OxyColor[] rowColors = new[]
        {
            OxyColors.DarkBlue, OxyColors.DodgerBlue, OxyColors.Cyan, OxyColors.Blue
        };

        // Plot mode: true=rolling (instantaneous CPS), false=cumulative (sum over run)
        private bool showRolling = true;

        // Backing stores for cumulative view and UI-running totals (the latter drives total counts textbox)
        private double[] cumulativeCounts = new double[32];
        private long[] uiRunningTotals = new long[32];

        // Tracks whether Tube Distribution tab is active; if false, skip heavy UI updates
        private bool tubeDistributionActive = false;

        private double[] fetchedGateUs;

        private Vf61Gui.Moments.MomentsClient momentsClient;

        private AdaptiveWindowEngine? _adaptive;

        private SyntheticDetector? _synthetic;

        private LocalizationEpisodePolicy? _localizationPolicy;
        private LocalizationWorker? _localizationWorker;
        private CancellationTokenSource? _localizationWorkerCts;
        private System.Windows.Forms.Timer? _localizationStatusTimer;
        private RtWindowSummary? _lastWindowSummary;
        private LocalizationEvaluation? _lastLocalizationEvaluation;
        private string? _lastLocalizationStatus;
        private bool _localizationAutoEnabled = true;



        public ListenN()
        {
            // Standard WinForms designer initialization
            InitializeComponent();
            this.Load += new EventHandler(Form1_Load); // Make sure combo boxes get populated on load
            _localizationAutoEnabled = checkBoxLocalizationAuto.Checked;

            // ---- Wire up UI interactions ----
            textBoxIpAddress.KeyDown += TextBoxIpAddress_KeyDown;
            textBoxCommands.KeyDown += TextBoxCommands_KeyDown;

            buttonAdd.Click += ButtonAdd_Click;
            buttonPing.Click += buttonPing_Click;
            buttonHVRead.Click += buttonHVRead_Click;
            buttonConnect.Click += buttonConnect_Click;
            buttonSynchTime.Click += buttonSynchTime_Click;
            buttonTotals.Click += buttonTotals_Click;
            buttonSendCommand.Click += ButtonSendCommand_Click;
            buttonDuration.Click += ButtonDuration_Click;
            buttonDescription.Click += ButtonDescription_Click;

            copyAddressToolStripMenuItem.Click += copyAddressToolStripMenuItem_Click;
            deleteAddressToolStripMenuItem.Click += deleteAddressToolStripMenuItem_Click;
            listViewIpAddresses.DoubleClick += listViewIpAddresses_DoubleClick;

            // Live rate poller: gentle (1 Hz), non-blocking, fan-out to online detectors
            rateUpdateTimer = new System.Windows.Forms.Timer
            {
                Interval = 1000
            };
            rateUpdateTimer.Tick += RateUpdateTimer_Tick;
            rateUpdateTimer.Start();

            radioButtonDuration.Checked = true;
            radioButtonContinuous.Checked = false;

            // Context menus for duration group and messages listview
            groupBoxDuration.ContextMenuStrip = contextMenuDuration;
            setMaxFileSizeToolStripMenuItem.Click += SetMaxFileSizeToolStripMenuItem_Click;
            numericUpDownDuration.ValueChanged += NumericUpDownDuration_ValueChanged;
            setSaveFolderToolStripMenuItem.Click += SetSaveFolderToolStripMenuItem_Click;

            // Initialize the plot once on startup
            ShowGenericBarGraph();

            // Disable most controls until at least one detector is connected
            EnableControls(false);

            // Auto-reconnect for resilience in the field (every 10 s)
            reconnectTimer = new System.Windows.Forms.Timer
            {
                Interval = 10000
            };
            reconnectTimer.Tick += reconnectTimer_Tick;
            reconnectTimer.Start();

            // Messages context menu (quality-of-life for copying logs)
            contextMenuMessages = new ContextMenuStrip();
            contextMenuMessages.Items.Add("Copy Selected", null, CopySelectedMessages_Click);
            contextMenuMessages.Items.Add("Copy All", null, CopyAllMessages_Click);
            listViewMessages.ContextMenuStrip = contextMenuMessages;

            // IP list context menu (nicknames)
            contextMenuIpList = new ContextMenuStrip();
            contextMenuIpList.Items.Add("Add Nickname", null, AddNickname_Click);
            listViewIpAddresses.ContextMenuStrip = contextMenuIpList;

            buttonGetRowRatios.Click += buttonGetRowRatios_Click;

            checkBoxLocalizationAuto.CheckedChanged += (s, e) =>
            {
                _localizationAutoEnabled = checkBoxLocalizationAuto.Checked;
                if (_localizationPolicy != null)
                {
                    _localizationPolicy.AutoModeEnabled = _localizationAutoEnabled;
                }
                AppendMessage(null,
                    new DebuggingMessage($"Localization auto mode {(_localizationAutoEnabled ? "enabled" : "disabled")}."),
                    "localization > ");
                EmitLocalizationStatus(force: true);
            };

            buttonLocalizeNow.Click += (s, e) => TriggerLocalizationNow();

            // Right-click actions on the plot (copy, save, clear)
            contextMenuTubeDistribution.Items.Clear();
            contextMenuTubeDistribution.Items.Add("Copy image to clipboard", null, (s, e) => CopyPlotImageToClipboard());
            contextMenuTubeDistribution.Items.Add("Save image as PNG...", null, (s, e) => SavePlotImage());
            contextMenuTubeDistribution.Items.Add("Clear plot", null, (s, e) => ClearTubeDistribution());
            plotViewTubeDistribution.ContextMenuStrip = contextMenuTubeDistribution;

            // Top-level plot buttons
            buttonPlotClearTube.Click += (s, e) => ClearTubeDistribution();
            buttonPlotsSaveTube.Click += (s, e) => SavePlotImage();

            DeterministicRng.SetSeed(42); // pick any number you like


            numericUpDownCorrelationMetric.ValueChanged += (s, e) =>
            {
                if (_adaptive != null)
                {
                    _adaptive.EpsY = (double)numericUpDownCorrelationMetric.Value;
                    AppendMessage(null,
                        new DebuggingMessage($"Adaptive εY set to {_adaptive.EpsY:F3}"),
                        "adaptive > ");
                }
            };
            numericUpDownCorrelationMetric.Enabled = checkBoxEnableAdaptiveWindowing.Checked;

            numericUpDownSinglesRate.ValueChanged += (s, e) =>
            {
                if (_adaptive != null)
                {
                    _adaptive.EpsM1 = (double)numericUpDownSinglesRate.Value;
                    AppendMessage(null,
                        new DebuggingMessage($"Adaptive εm1 set to {_adaptive.EpsM1:F3}"),
                        "adaptive > ");
                }
            };
            numericUpDownSinglesRate.Enabled = checkBoxEnableAdaptiveWindowing.Checked;

            numericUpDownStepFraction.ValueChanged += (s, e) =>
            {
                if (_adaptive != null)
                {
                    _adaptive.Beta = (double)numericUpDownStepFraction.Value;
                    AppendMessage(null,
                        new DebuggingMessage($"Adaptive β set to {_adaptive.Beta:F2}"),
                        "adaptive > ");
                }
            };

            numericUpDownPlateauSlope.ValueChanged += (s, e) =>
            {
                if (_adaptive != null)
                {
                    _adaptive.EtaFrac = (double)numericUpDownPlateauSlope.Value;
                    AppendMessage(null,
                        new DebuggingMessage($"Adaptive η set to {_adaptive.EtaFrac:F3}"),
                        "adaptive > ");
                }
            };
            numericUpDownZmin.ValueChanged += (s, e) =>
            {
                if (_adaptive != null)
                {
                    _adaptive.Zmin = (int)numericUpDownZmin.Value;
                    AppendMessage(null,
                        new DebuggingMessage($"Adaptive Zmin set to {_adaptive.Zmin}"),
                        "adaptive > ");
                }
            };


            // Track whether the Tube Distribution tab is visible
            tabControlPlots.SelectedIndexChanged += tabControlPlots_SelectedIndexChanged;

            checkBoxEnableAdaptiveWindowing.CheckedChanged += (s, e) =>
            {
                bool adaptiveEnabled = checkBoxEnableAdaptiveWindowing.Checked;

                if (adaptiveEnabled)
                {
                    // Spin up the engine if not already running
                    if (_adaptive == null)
                    {
                        _adaptive = new AdaptiveWindowEngine(baseDeltaUs: 500, windowStartSec: 2.0);

                        InitializeLocalizationRuntime();
                        _adaptive.OnWindowSummary += HandleWindowSummary;

                        // Start synthetic source
                        _synthetic = new SyntheticDetector(_adaptive);
                        AppendMessage(null, new DebuggingMessage("Synthetic detector started"), "synthetic > ");

                        _adaptive.OnEstimate += est =>
                        {
                            // Log adaptive outputs
                            AppendMessage(null,
                                new DebuggingMessage(
                                    $"Adaptive: Tg={est.GateUs}us, W={est.WindowSec:F2}s, " +
                                    $"m1={est.M1:F2}, Y={est.Y:F4} ±{est.SigmaY:F4}, " +
                                    $"ZY={est.ZY:F2}, State={est.State}"),
                                "adaptive > ");

                            // Structured JSON log
                            var log = PreflightOracle.Run(
                                DateTime.UtcNow,
                                est.State,
                                est.GateUs,
                                est.WindowSec,
                                0,        // N not in Estimate yet
                                est.M1,
                                0,        // M2 not in Estimate yet
                                0,        // M3 not in Estimate yet
                                est.Y,
                                est.SigmaY
                            );

                            var options = new JsonSerializerOptions
                            {
                                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
                                WriteIndented = false
                            };

                            string json = JsonSerializer.Serialize(log, options);
                            System.IO.File.AppendAllText("adaptive.log", json + Environment.NewLine);

                            // --- your existing textbox updates follow ---
                            if (textBoxFSMState.InvokeRequired)
                                textBoxFSMState.BeginInvoke(new Action(() => textBoxFSMState.Text = est.State));
                            else
                                textBoxFSMState.Text = est.State;

                            numericUpDownStepFraction.Enabled = adaptiveEnabled;
                            numericUpDownZmin.Enabled = adaptiveEnabled;
                            numericUpDownPlateauSlope.Enabled = adaptiveEnabled;
                            textBoxCurrentW.Enabled = adaptiveEnabled;
                            textBoxCurrentTg.Enabled = adaptiveEnabled;
                            if (!adaptiveEnabled) textBoxCurrentTg.Clear();
                            if (!adaptiveEnabled) textBoxCurrentW.Clear();

                            textBoxFSMState.Enabled = adaptiveEnabled;
                            if (!adaptiveEnabled) textBoxFSMState.Clear();

                            if (textBoxCurrentW.InvokeRequired)
                                textBoxCurrentW.BeginInvoke(new Action(() => textBoxCurrentW.Text = est.WindowSec.ToString("F2")));
                            else
                                textBoxCurrentW.Text = est.WindowSec.ToString("F2");

                            if (textBoxCurrentTg.InvokeRequired)
                                textBoxCurrentTg.BeginInvoke(new Action(() => textBoxCurrentTg.Text = (est.GateUs / 1000.0).ToString("F2")));
                            else
                                textBoxCurrentTg.Text = (est.GateUs / 1000.0).ToString("F2");

                            if (textBoxSigMetric.InvokeRequired)
                                textBoxSigMetric.BeginInvoke(new Action(() => textBoxSigMetric.Text = est.ZY.ToString("F2")));
                            else
                                textBoxSigMetric.Text = est.ZY.ToString("F2");

                            textBoxSigMetric.Enabled = adaptiveEnabled;
                            if (!adaptiveEnabled) textBoxSigMetric.Clear();

                            if (textBoxPlotsFeynmanm1.InvokeRequired)
                                textBoxPlotsFeynmanm1.BeginInvoke(new Action(() => textBoxPlotsFeynmanm1.Text = est.M1.ToString("F4")));
                            else
                                textBoxPlotsFeynmanm1.Text = est.M1.ToString("F4");

                            if (textBoxAdaptiveM1.InvokeRequired)
                                textBoxAdaptiveM1.BeginInvoke(new Action(() => textBoxAdaptiveM1.Text = est.M1.ToString("F4")));
                            else
                                textBoxAdaptiveM1.Text = est.M1.ToString("F4");

                            textBoxAdaptiveM1.Enabled = adaptiveEnabled;
                            if (!adaptiveEnabled) textBoxAdaptiveM1.Clear();
                        };

                        // Assign to detectors if any are already connected
                        foreach (var d in detectors)
                            d.Adaptive = _adaptive;
                    }
                }
                else
                {
                    // Shut it down cleanly
                    _adaptive?.Dispose();
                    _adaptive = null;

                    _synthetic?.Dispose();
                    _synthetic = null;
                    AppendMessage(null, new DebuggingMessage("Synthetic detector stopped"), "synthetic > ");
                }
            };




            comboBoxStorage.Items.AddRange(new[]
            {
                "Instrument",
                "Computer",
                "None"
            });
            comboBoxStorage.SelectedIndex = 0; // "Instrument" default

            // NOTE: accidental local re-declaration—keep only the field-level list!
            // List<Detector> detectors = new List<Detector>();

            buttonPriToCenter.Click += (s, e) =>
            {
                foreach (var d in detectors.Where(d => d.CheckState(InstrumentState.ONLINE)))
                    d.WriteAndCheck("DistPriToSrc", numericUpDownDistPriToCenter.Value.ToString());
            };

            buttonSecToCenter.Click += (s, e) =>
            {
                foreach (var d in detectors.Where(d => d.CheckState(InstrumentState.ONLINE)))
                    d.WriteAndCheck("DistSecToSrc", numericUpDownDistSecToCenter.Value.ToString());
            };

            buttonPriToFloor.Click += (s, e) =>
            {
                foreach (var d in detectors.Where(d => d.CheckState(InstrumentState.ONLINE)))
                    d.WriteAndCheck("DistPriToFlr", numericUpDownDistPriToFloor.Value.ToString());
            };

            buttonSecToFloor.Click += (s, e) =>
            {
                foreach (var d in detectors.Where(d => d.CheckState(InstrumentState.ONLINE)))
                    d.WriteAndCheck("DistSecToFlr", numericUpDownDistSecToFloor.Value.ToString());
            };

        }

        private void Form1_Load(object sender, EventArgs e)
        {
            // Populate SNM combo boxes and keep snm object in sync with user selections
            comboBoxVs1.Items.AddRange(snm.GetVs1List().ToArray());
            comboBoxVs2.Items.AddRange(snm.GetVs2List().ToArray());
            comboBoxVi.Items.AddRange(snm.GetViList().ToArray());

            comboBoxVs1.SelectedIndexChanged += (s, ev) => snm.SetVs1(comboBoxVs1.SelectedItem?.ToString());
            comboBoxVs2.SelectedIndexChanged += (s, ev) => snm.SetVs2(comboBoxVs2.SelectedItem?.ToString());
            comboBoxVi.SelectedIndexChanged += (s, ev) => snm.SetVi(comboBoxVi.SelectedItem?.ToString());

            // Initial selections (safe defaults)
            comboBoxVs1.SelectedIndex = 0;
            comboBoxVs2.SelectedIndex = 0;
            comboBoxVi.SelectedIndex = 0;

            comboBoxFeynmanGatewidth.SelectedIndexChanged += ComboBoxFeynmanGatewidth_SelectedIndexChanged;

        }

        /// <summary>
        /// Validate and add a detector by IPv4 string. Also creates a row in the ListView.
        /// </summary>
        /// 
        private void AddIpAddress()
        {
            string ip = textBoxIpAddress.Text.Trim();

            // Validate IP address format
            if (!Detector.IsValidIpAddress(ip))
            {
                MessageBox.Show("Invalid IP address.");
                return;
            }

            // Check for duplicate entries
            if (detectors.Any(d => d.IpAddress == ip))
            {
                MessageBox.Show("This IP address is already in the list.");
                return;
            }

            // Create new Detector instance and initialize with IP
            Detector detector = new Detector();
            detector.ipv4Address = ip.Split('.');

            detector.Adaptive = _adaptive;

            detectors.Add(detector); // Add to internal detector list


            // Create a ListView row for the detector
            string ipAddr = detector.IpAddress;
            string displayName = detectorNicknames.ContainsKey(ipAddr)
                ? $"{detectorNicknames[ip]} ({ipAddr})"
                : ip;

            WinFormsListViewItem item = new WinFormsListViewItem(displayName);
            item.SubItems.Add("Offline");
            item.SubItems.Add("Disconnected");
            item.SubItems.Add("");



            // Display current instrument state
            string stateDisplay = detector.CheckState(InstrumentState.ONLINE)
                    ? "Connected"
             : detector.pingReply?.Status == IPStatus.Success
                    ? "Ping OK"
                    : "Offline";

            item.SubItems.Add(stateDisplay);

            // If online, show total count rate
            string rate = detector.CheckState(InstrumentState.ONLINE) ? $"{detector.totalCountsOneSecond} cps" : "";
            item.SubItems.Add(rate);

            // Add the ListView item to the control
            listViewIpAddresses.Items.Add(item);
            textBoxIpAddress.Clear(); // Reset input field



        }
        private void AddNickname_Click(object sender, EventArgs e)
        {
            if (listViewIpAddresses.SelectedItems.Count != 1) return;

            var item = listViewIpAddresses.SelectedItems[0];
            string ip = GetIpFromListViewItem(item.Text); // parse clean IP

            string currentNickname = detectorNicknames.ContainsKey(ip) ? detectorNicknames[ip] : "";

            string nickname = Microsoft.VisualBasic.Interaction.InputBox(
                $"Enter nickname for {ip}:", "Add Nickname", currentNickname);

            if (!string.IsNullOrWhiteSpace(nickname))
            {
                detectorNicknames[ip] = nickname;
                item.Text = $"{nickname} ({ip})";
            }
        }
        /// <summary>
        /// Extract a raw IP from a label like "Nick (1.2.3.4)". Falls back to raw text.
        /// </summary>
        private string GetIpFromListViewItem(string text)
        {
            if (text.Contains("(") && text.Contains(")"))
            {
                int start = text.IndexOf('(') + 1;
                int end = text.IndexOf(')');
                return text.Substring(start, end - start);
            }
            return text; // fallback — assume it’s just an IP

        }
        /// <summary>
        /// Poll "Rates" from all ONLINE detectors once per second. UI-safe.
        /// </summary>
        private async void RateUpdateTimer_Tick(object sender, EventArgs e)
        {
            var originalCursor = Cursor.Current;
            Cursor.Current = Cursors.Default;

            try
            {
                var tasks = detectors
                    .Where(d => d.CheckState(InstrumentState.ONLINE))
                    .Select(d => Task.Run(() => d.WriteAndWait("Rates")));

                await Task.WhenAll(tasks);
                await Task.Delay(100); // allow responses to populate

                Invoke(() => UpdateRatesInListView());
            }
            finally
            {
                Cursor.Current = originalCursor;
            }
            if (!detectors.Any(d => d.CheckState(InstrumentState.ONLINE)))
                return;

        }
        /// <summary>
        /// Reflect per-detector one-second total counts (cps) into the IP ListView.
        /// </summary>
        /// 

        private void UpdateRatesInListView()
        {
            foreach (WinFormsListViewItem item in listViewIpAddresses.Items)
            {
                string ip = item.SubItems[0].Text;
                Detector? d = detectors.FirstOrDefault(det => det.IpAddress == ip);

                if (d == null || !d.CheckState(InstrumentState.ONLINE)) continue;

                // Ensure column exists
                while (item.SubItems.Count < 4)
                    item.SubItems.Add("");

                // Total count per second
                item.SubItems[3].Text = $"{d.totalCountsOneSecond} cps";
            }

        }

        // Event handler for Add button click - triggers IP add logic
        private void ButtonAdd_Click(object sender, EventArgs e)
        {
            AddIpAddress();
        }

        // Event handler to re-ping all detectors and refresh display
        private void buttonPing_Click(object sender, EventArgs e)
        {
            UpdateListViewDetectors(sender, e);
        }

        // Send high voltage read command to all online detectors
        private async void buttonHVRead_Click(object sender, EventArgs e)
        {
            buttonHVRead.Enabled = false;
            Cursor.Current = Cursors.WaitCursor;

            try
            {
                var tasks = detectors
                    .Where(d => d.CheckState(InstrumentState.ONLINE))
                    .Select(d => Task.Run(() => d.WriteAndWait("HVread")));

                await Task.WhenAll(tasks);
            }
            finally
            {
                buttonHVRead.Enabled = true;
                Cursor.Current = Cursors.Default;
            }
        }

        /// <summary>
        /// HVread callback—arrives on a detector thread; always marshal to UI.
        /// </summary>

        private void EventHVread(object sender, EventArgs e)
        {
            if (InvokeRequired)
            {
                Invoke(() => EventHVread(sender, e)); // Ensure we're on the UI thread
                return;
            }

            Detector? detector = sender as Detector;
            if (detector == null) return;

            // Find the ListView item corresponding to this detector's IP
            foreach (WinFormsListViewItem item in listViewIpAddresses.Items)
            {
                if (item.SubItems[0].Text == detector.IpAddress)
                {
                    // Ensure 5 columns are available
                    while (item.SubItems.Count < 5)
                    {
                        item.SubItems.Add("");
                    }

                    // Update HV readout column
                    item.SubItems[4].Text = $"{detector.hvRead} V";
                    return;
                }
            }
        }

        /// <summary>
        /// Connect/Disconnect toggle for all detectors in the list.
        /// </summary>
        private async void buttonConnect_Click(object sender, EventArgs e)
        {
            buttonConnect.Enabled = false;

            try
            {
                if (buttonConnect.Text.Equals("Connect"))
                    await ConnectToDetectors(); // 
                else
                    DisconnectFromDetectors();  // still OK because it’s fast
            }
            finally
            {
                buttonConnect.Enabled = true;
            }
        }

        private void EventUSBsaveFileProgress(object sender, EventArgs e)
        {
            Detector d = sender as Detector;
            if (d == null) return;

            string msg = $"Saving {d.fileCurrent.name}: {d.percentComplete}%";
            AppendMessage(d, new DebuggingMessage(msg), "progress > ");
        }
        private void EventRates(object sender, EventArgs e)
        {
            Invoke(() => UpdateRatesInListView());
        }
        private void CopySelectedMessages_Click(object sender, EventArgs e)
        {
            if (listViewMessages.SelectedItems.Count > 0)
            {
                var lines = listViewMessages.SelectedItems
                    .Cast<WinFormsListViewItem>()
                    .Select(item => string.Join("\t", item.SubItems.Cast<WinFormsListViewItem.ListViewSubItem>().Select(s => s.Text)));

                Clipboard.SetText(string.Join(Environment.NewLine, lines));
            }
        }

        private void CopyAllMessages_Click(object sender, EventArgs e)
        {
            var lines = listViewMessages.Items
                .Cast<WinFormsListViewItem>()
                .Select(item => string.Join("\t", item.SubItems.Cast<WinFormsListViewItem.ListViewSubItem>().Select(s => s.Text)));

            Clipboard.SetText(string.Join(Environment.NewLine, lines));
        }
        /// <summary>
        /// Attempt to connect each detector: ping, open TCP, start receive thread, attach events,
        /// initialize time/HV/format. UI updates after all attempts complete.
        /// </summary>
        private async Task ConnectToDetectors()
        {
            Cursor.Current = Cursors.WaitCursor;
            buttonConnect.Enabled = false;
            buttonConnect.Text = "Disconnect";

            List<string> failedDetectors = new List<string>();
            int connectedCount = 0;

            var connectTasks = detectors.Select(async detector =>
            {
                try
                {
                    detector.Ping();
                    if (detector.pingReply?.Status != IPStatus.Success)
                    {
                        failedDetectors.Add($"{detector.IpAddress} - Ping failed");
                        return false;
                    }

                    detector.IpInitializeTcp();
                    if (!detector.CheckState(InstrumentState.ONLINE))
                    {
                        failedDetectors.Add($"{detector.IpAddress} - TCP connection failed");
                        return false;
                    }

                    detector.threadTcpIpDataReceived = new Thread(() => detector.TcpIp_DataReceived())
                    {

                        IsBackground = true,
                        Name = $"TCP_Receive_{detector.IpAddress}"

                    };
                    detector.threadTcpIpDataReceived.Start();

                    // Hook up all detector events
                    detector.EventMessagesHaveChanged += (s, ev) => AppendMessage(detector, detector.messages.Front(), ">");
                    detector.EventMessagesSentHasChanged += (s, ev) => AppendMessage(detector, detector.messagesSent.Front(), "send > ");
                    detector.EventMessagesReceivedHasChanged += (s, ev) => AppendMessage(detector, detector.messagesReceived.Front(), "recv > ");
                    detector.EventHVread += EventHVread;
                    detector.EventAssayDone += EventAssayDone;
                    detector.EventUSBsaveFileProgress += EventUSBsaveFileProgress;
                    detector.EventRates += (s, ev) => UpdateTubeDistribution(detector);

                    // Initialization commands
                    detector.WriteAndWait(detector.mother);
                    detector.WriteAndWait("Time = " + DateTime.Now.ToString("yyyy MM dd HH mm ss"));
                    detector.WriteAndWait("Time");
                    detector.WriteAndWait("HVread");
                    detector.detectorFormat = DetectorFormat.MC15;
                    detector.detectorFormat_Changed(this, EventArgs.Empty);

                    Interlocked.Increment(ref connectedCount);
                    return true;
                }
                catch (Exception ex)
                {
                    failedDetectors.Add($"{detector.IpAddress} - {ex.Message}");
                    return false;
                }
            });

            await Task.WhenAll(connectTasks);

            // Once connected, try to load gate widths from the first online detector
            // Once connected, try to load gate widths from the first online detector
            var firstOnline = detectors.FirstOrDefault(d => d.CheckState(InstrumentState.ONLINE));
            if (firstOnline != null)
            {
                try
                {
                    var momentsClient = new Vf61Gui.Moments.MomentsClient(firstOnline);
                    double[] gateUs = await momentsClient.LoadGateWidthsAsync();

                    // Store for later use if needed
                    this.fetchedGateUs = gateUs; // requires: private double[] fetchedGateUs;

                    // Log them
                    AppendMessage(null,
                        new DebuggingMessage("Gate widths (µs): " + string.Join(", ", gateUs)),
                        "info > ");

                    // Populate comboBoxFeynmanGatewidth with µs values
                    comboBoxFeynmanGatewidth.Items.Clear();
                    foreach (var gw in gateUs)
                    {
                        comboBoxFeynmanGatewidth.Items.Add($"{gw} µs");
                    }

                    // Optionally select the first one by default
                    if (comboBoxFeynmanGatewidth.Items.Count > 0)
                        comboBoxFeynmanGatewidth.SelectedIndex = 0;
                }
                catch (Exception ex)
                {
                    AppendMessage(null,
                        new DebuggingMessage($"Failed to fetch gate widths: {ex.Message}"),
                        "warn > ");
                }
            }


            // Refresh UI
            Invoke(() =>
            {
                UpdateListViewDetectors(this, EventArgs.Empty);
                UpdateConnectionStatus();
                EnableControls(connectedCount > 0);
            });

            // Show results
            if (failedDetectors.Count > 0)
            {
                string message = $"Connected to {connectedCount} detector(s).\n\nFailed connections:\n" +
                                 string.Join("\n", failedDetectors);
                MessageBox.Show(message, "Connection Results", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            Cursor.Current = Cursors.Default;
            buttonConnect.Enabled = true;
        }


        private void EventTotals(object sender, EventArgs e)
        {
            Detector d = sender as Detector;
            if (d == null) return;

            string msg = $"Totals: {d.totalCountsTotal} counts in {d.elapsedTime} sec";
            AppendMessage(d, new DebuggingMessage(msg), "data > ");


        }

        private void EventAssayDone(object sender, EventArgs e)
        {
            Detector detector = sender as Detector;
            if (detector == null) return;

            string filename = detector.fileLastSaved.name;
            if (string.IsNullOrWhiteSpace(filename))
            {
                AppendMessage(detector, new DebuggingMessage("AssayDone received, but no filename found."), "error > ");
                return;
            }

            AppendMessage(detector, new DebuggingMessage($"AssayDone: {filename}"), "event > ");
            AppendMessage(detector, new DebuggingMessage($"Requesting NetSaveFile = {filename}"), "cmd > ");
            detector.WriteAndWait($"NetSaveFile = {filename}");
            AppendMessage(detector, new DebuggingMessage($"Total counts: {detector.totalCountsTotal}"), "debug > ");

            // Async wait for file transfer to complete (non-blocking UI)
            Task.Run(() =>
            {
                bool success = detector.resetEventNetSaveFile.WaitOne(60000);  // 60s
                Invoke(() =>
                {
                    if (success)
                    {
                        AppendMessage(detector, new DebuggingMessage("File transfer complete."), "file > ");
                    }
                    else
                    {
                        AppendMessage(detector, new DebuggingMessage("File transfer timed out."), "warn > ");
                    }
                });
            });
        }


        private void DisconnectFromDetectors()
        {
            Cursor.Current = Cursors.WaitCursor;
            buttonConnect.Enabled = false;
            buttonConnect.Text = "Connect";

            foreach (Detector detector in detectors)
            {
                try
                {
                    // Stop TCP thread if needed
                    if (detector.threadTcpIpDataReceived?.IsAlive == true)
                    {
                        detector.threadTcpIpDataReceived.Join(1000); // wait up to 1 second
                    }

                    // Unsubscribe from all events
                    detector.EventMessagesHaveChanged = null;
                    detector.EventMessagesSentHasChanged = null;
                    detector.EventMessagesReceivedHasChanged = null;
                    detector.EventHVread = null;
                    detector.EventAssayDone = null;
                    detector.EventUSBsaveFileProgress = null;

                    // Close connection
                    detector.Close();
                }
                catch (Exception ex)
                {
                    AppendMessage(detector, new DebuggingMessage($"Disconnect error: {ex.Message}"), "error > ");
                }
            }

            Invoke(() =>
            {
                UpdateListViewDetectors(this, EventArgs.Empty);
                UpdateConnectionStatus();
                EnableControls(false);
                buttonConnect.Enabled = true;
                Cursor.Current = Cursors.Default;
            });
        }

        private void reconnectTimer_Tick(object sender, EventArgs e)
        {
            foreach (var detector in detectors)
            {
                if (!detector.CheckState(InstrumentState.ONLINE))
                {
                    detector.Ping();
                    detector.IpInitializeTcp();

                    if (detector.CheckState(InstrumentState.ONLINE))
                    {
                        // reattach event handlers
                        detector.EventMessagesHaveChanged += (s, ev) => AppendMessage(detector, detector.messages.Front(), ">");
                        detector.EventMessagesSentHasChanged += (s, ev) => AppendMessage(detector, detector.messagesSent.Front(), "send > ");
                        detector.EventMessagesReceivedHasChanged += (s, ev) => AppendMessage(detector, detector.messagesReceived.Front(), "recv > ");

                        // resend initial messages
                        detector.WriteAndWait(detector.mother);
                        detector.WriteAndWait("Time = " + DateTime.Now.ToString("yyyy MM dd HH mm ss"));
                        detector.WriteAndWait("Time");
                        detector.WriteAndWait("HVread");
                    }
                }
            }

            UpdateListViewDetectors(this, EventArgs.Empty);
            UpdateConnectionStatus();
        }
        private void buttonSynchTime_Click(object sender, EventArgs e)
        {
            WriteAndWaitToAll_Time();
            WriteAndWaitToAll("Time");
        }
        private async void buttonTotals_Click(object sender, EventArgs e)
        {
            buttonTotals.Enabled = false;
            Cursor.Current = Cursors.WaitCursor;

            try
            {
                var tasks = detectors
                    .Where(d => d.CheckState(InstrumentState.ONLINE) &&
                                d.CheckState(InstrumentState.ACTIVE)) // Only during active runs
                    .Select(d => Task.Run(() => d.WriteAndWait("Totals")));

                await Task.WhenAll(tasks);
                await Task.Delay(100); // optional delay for replies to arrive

                Invoke(() => UpdateRatesInListView()); // reuse same visual update
            }
            finally
            {
                buttonTotals.Enabled = true;
                Cursor.Current = Cursors.Default;
            }
        }


        private void WriteAndWaitToAll_Time()
        {
            string timestamp = DateTime.Now.ToString("yyyy MM dd HH mm ss");
            WriteAndWaitToAll("Time = " + timestamp);
        }

        private void WriteAndWaitToAll(string command)
        {
            foreach (var detector in detectors)
            {
                if (detector.CheckState(InstrumentState.ONLINE))
                {
                    detector.WriteAndWait(command);
                }
            }
        }

        private void AppendMessage(Detector detector, DebuggingMessage message, string prefix)
        {
            if (InvokeRequired)
            {
                Invoke(() => AppendMessage(detector, message, prefix));
                return;
            }

            if (message == null) return;

            WinFormsListViewItem item = new WinFormsListViewItem(message.dateTime.ToString("HH:mm:ss.fff") + " " + prefix);

            // Handle null detector case
            if (detector != null)
            {
                item.SubItems.Add($"{detector.IpAddress}: {message.message}");
            }
            else
            {
                item.SubItems.Add($"System: {message.message}");  // Use "System" for non-detector messages
            }

            if (listViewMessages.Items.Count > 100)
                listViewMessages.Items.RemoveAt(0);

            listViewMessages.Items.Add(item);
            listViewMessages.EnsureVisible(listViewMessages.Items.Count - 1);
        }

        private void InitializeLocalizationRuntime()
        {
            if (_localizationPolicy != null)
            {
                return;
            }

            string artifactsDirectory = Path.Combine(Application.StartupPath, "artifacts");
            string? policyArtifacts = Directory.Exists(artifactsDirectory) ? artifactsDirectory : null;
            _localizationPolicy = new LocalizationEpisodePolicy(artifactsDirectory: policyArtifacts)
            {
                AutoModeEnabled = _localizationAutoEnabled
            };

            _localizationPolicy.OnRequestMl += request =>
            {
                if (_localizationWorker == null)
                {
                    AppendMessage(null, new DebuggingMessage("Localization worker unavailable; request dropped."), "localization > ");
                    return;
                }

                _localizationWorker.Enqueue(request);
            };

            _localizationPolicy.OnStatus += status =>
            {
                if (!string.IsNullOrWhiteSpace(status.Code))
                {
                    AppendMessage(null,
                        new DebuggingMessage($"Policy={status.Code} {status.Message}".Trim()),
                        "localization > ");
                }

                EmitLocalizationStatus(force: true);
            };

            _localizationPolicy.OnPublish += result =>
            {
                LogLocalizationPublish(result);
                EmitLocalizationStatus(force: true);
            };

            if (Directory.Exists(artifactsDirectory))
            {
                _localizationWorker = new LocalizationWorker(artifactsDirectory, capacity: 4);
                _localizationWorker.OnResult += HandleLocalizationResult;
                _localizationWorkerCts = new CancellationTokenSource();
                _localizationWorker.Start(_localizationWorkerCts.Token);
                AppendMessage(null, new DebuggingMessage($"Localization worker started (artifacts: {artifactsDirectory})."), "localization > ");
            }
            else
            {
                AppendMessage(null, new DebuggingMessage($"Localization artifacts not found at {artifactsDirectory}."), "localization > ");
            }

            _localizationStatusTimer = new System.Windows.Forms.Timer
            {
                Interval = 5000
            };
            _localizationStatusTimer.Tick += (s, e) => EmitLocalizationStatus(force: true);
            _localizationStatusTimer.Start();

            FormClosed += (_, _) =>
            {
                _localizationStatusTimer?.Stop();
                _localizationWorkerCts?.Cancel();
            };
        }

        private void HandleWindowSummary(RtWindowSummary summary)
        {
            _lastWindowSummary = summary;
            _localizationPolicy?.AddWindow(summary);
            EmitLocalizationStatus(force: false);
        }

        private void HandleLocalizationResult(LocalizationRequest request, LocalizationPrediction prediction)
        {
            _localizationPolicy?.OnMlResult(request, prediction);
            _lastLocalizationEvaluation = _localizationPolicy?.LastEvaluation;

            if (_lastLocalizationEvaluation is not null && request.IsProbe)
            {
                LogLocalizationProbe(_lastLocalizationEvaluation);
            }

            EmitLocalizationStatus(force: true);
        }

        private void TriggerLocalizationNow()
        {
            if (_localizationPolicy == null)
            {
                AppendMessage(null, new DebuggingMessage("Localization policy not initialized."), "localization > ");
                return;
            }

            if (_localizationWorker == null)
            {
                AppendMessage(null, new DebuggingMessage("Localization worker unavailable; cannot run manual probe."), "localization > ");
                return;
            }

            var request = _localizationPolicy.BuildManualProbeRequest();
            _localizationWorker.Enqueue(request);
            AppendMessage(null, new DebuggingMessage($"Manual probe enqueued for episode {request.EpisodeId}."), "localization > ");
            EmitLocalizationStatus(force: true);
        }

        private void LogLocalizationProbe(LocalizationEvaluation evaluation)
        {
            string delta = double.IsNaN(evaluation.Delta) ? "n/a" : evaluation.Delta.ToString("F2");
            AppendMessage(null,
                new DebuggingMessage(
                    $"Probe episode={evaluation.EpisodeId} counts={evaluation.TotalCounts:0} duration={evaluation.DurationSeconds:F1}s " +
                    $"label={evaluation.Label} p={evaluation.Probability:F3} ood={evaluation.IsOod} " +
                    $"mahal={evaluation.MahalanobisDistance:F2} delta={delta} stable={evaluation.StableCount}"),
                "localization > ");
        }

        private void LogLocalizationPublish(LocalizationPublishResult result)
        {
            string delta = _localizationPolicy != null && !double.IsNaN(_localizationPolicy.LastStabilityDelta)
                ? _localizationPolicy.LastStabilityDelta.ToString("F2")
                : "n/a";
            int stableCount = _localizationPolicy?.StableCount ?? 0;
            string reason = string.IsNullOrWhiteSpace(result.Reason) ? "none" : result.Reason;
            AppendMessage(null,
                new DebuggingMessage(
                    $"Publish episode={result.EpisodeId} counts={result.TotalCounts:0} duration={result.DurationSeconds:F1}s " +
                    $"label={result.Label} p={result.Probability:F3} ood={result.IsOod} " +
                    $"mahal={result.MahalanobisDistance:F2} delta={delta} stable={stableCount} " +
                    $"lowStatistics={result.LowStatistics} reason={reason}"),
                "localization > ");
        }

        private void EmitLocalizationStatus(bool force)
        {
            if (_localizationPolicy == null && _lastWindowSummary == null)
            {
                return;
            }

            string rtState = _lastWindowSummary?.RtState ?? "n/a";
            double rate = _lastWindowSummary?.RateTotalCps ?? double.NaN;
            string rateText = double.IsNaN(rate) ? "n/a" : rate.ToString("F1");
            bool episodeActive = _localizationPolicy?.IsEpisodeActive ?? false;
            double counts = _localizationPolicy?.AccumulatedCountsTotal ?? 0;
            double duration = _localizationPolicy?.AccumulatedDurationSeconds ?? 0;
            bool paused = _localizationAutoEnabled
                && _localizationPolicy != null
                && !_localizationPolicy.IsEpisodeActive
                && !_localizationPolicy.LastIsConfused;

            string mlSummary = "none";
            if (_lastLocalizationEvaluation is not null)
            {
                mlSummary = $"{_lastLocalizationEvaluation.Label} p={_lastLocalizationEvaluation.Probability:F2} " +
                            $"ood={_lastLocalizationEvaluation.IsOod} mahal={_lastLocalizationEvaluation.MahalanobisDistance:F2}";
            }

            string status = $"RT={rtState} rate={rateText}cps | EpisodeActive={episodeActive} " +
                            $"counts={counts:0} duration={duration:F1}s | LastML={mlSummary} | PausedNotConfused={paused}";

            if (!force && string.Equals(status, _lastLocalizationStatus, StringComparison.Ordinal))
            {
                return;
            }

            _lastLocalizationStatus = status;
            AppendMessage(null, new DebuggingMessage(status), "localization > ");
        }

        private void UpdateListViewDetectors(object sender, EventArgs e)
        {
            Cursor.Current = Cursors.WaitCursor;

            listViewIpAddresses.Items.Clear();
            foreach (Detector detector in detectors)
            {
                detector.Ping();
                listViewIpAddresses.Items.Add(ToListViewItem(detector));
            }

            Cursor.Current = Cursors.Default;
        }
        private WinFormsListViewItem ToListViewItem(Detector detector)
        {
            string ip = detector.IpAddress;
            string name = detectorNicknames.ContainsKey(ip)
                ? $"{detectorNicknames[ip]} ({ip})"
                : ip;

            WinFormsListViewItem item = new WinFormsListViewItem(name);

            if (detector.pingReply?.Status == IPStatus.Success)
            {
                string result = detector.pingReply.RoundtripTime < 1 ? "<1 ms" : $"{detector.pingReply.RoundtripTime} ms";
                item.SubItems.Add(result);
            }
            else
            {
                item.SubItems.Add("Offline");
            }

            string stateDisplay = detector.CheckState(InstrumentState.ONLINE)
                ? "Connected"
            : detector.pingReply?.Status == IPStatus.Success
                ? "Ping OK"
                : "Offline";

            item.SubItems.Add(stateDisplay);


            string rate = detector.CheckState(InstrumentState.ONLINE)
                ? $"{detector.totalCountsOneSecond} cps"
                : "";
            item.SubItems.Add(rate);

            return item;
        }


        private void TextBoxIpAddress_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                AddIpAddress();
            }
        }
        private void copyAddressToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (listViewIpAddresses.SelectedItems.Count > 0)
            {
                var addresses = listViewIpAddresses.SelectedItems
                    .Cast<WinFormsListViewItem>()
                    .Select(item => item.SubItems[0].Text);
                Clipboard.SetText(string.Join(Environment.NewLine, addresses));
            }
        }
        private void ButtonSendCommand_Click(object sender, EventArgs e)
        {
            SendCommandFromTextBox();
        }


        private void TextBoxCommands_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                SendCommandFromTextBox();
            }
            else if (e.KeyCode == Keys.Up)
            {
                if (commandsSent.Count > 0)
                {
                    commandsSentIndex--;
                    ClampCommandsIndex();
                    textBoxCommands.Text = commandsSent[commandsSentIndex];
                    textBoxCommands.SelectionStart = textBoxCommands.Text.Length;
                }
            }
            else if (e.KeyCode == Keys.Down)
            {
                if (commandsSent.Count > 0)
                {
                    commandsSentIndex++;
                    ClampCommandsIndex();
                    textBoxCommands.Text = commandsSent[commandsSentIndex];
                    textBoxCommands.SelectionStart = textBoxCommands.Text.Length;
                }
            }
        }
        private async void FlashCommandBoxRed()
        {
            var originalColor = textBoxCommands.BackColor;
            textBoxCommands.BackColor = Color.LightCoral;
            await Task.Delay(300);
            textBoxCommands.BackColor = originalColor;
        }

        private void SendCommandFromTextBox()
        {
            string command = textBoxCommands.Text.Trim();
            if (string.IsNullOrEmpty(command))
            {
                FlashCommandBoxRed(); // ✅ flash if empty
                return;
            }

            commandsSent.Add(command);
            commandsSentIndex = commandsSent.Count;

            // ✅ NEW: send to selected detector if only one is selected
            if (listViewIpAddresses.SelectedItems.Count == 1)
            {
                string ip = listViewIpAddresses.SelectedItems[0].SubItems[0].Text;
                Detector? d = detectors.FirstOrDefault(det => det.IpAddress == ip);

                if (d != null && d.CheckState(InstrumentState.ONLINE))
                {
                    d.WriteAndWait(command);
                    AppendMessage(d, new DebuggingMessage($"[Targeted] {command}"), "cmd > ");
                }
                else
                {
                    AppendMessage(null, new DebuggingMessage($"No online detector found at {ip}"), "warn > ");
                }
            }
            else
            {
                // ✅ Fall back to broadcasting
                WriteAndWaitToAll(command);
                AppendMessage(null, new DebuggingMessage($"[Broadcast] {command}"), "cmd > ");
            }

            textBoxCommands.Clear();
        }

        private void ClampCommandsIndex()
        {
            if (commandsSentIndex < 0)
                commandsSentIndex = 0;
            else if (commandsSentIndex >= commandsSent.Count)
                commandsSentIndex = commandsSent.Count - 1;
        }
        private void deleteAddressToolStripMenuItem_Click(object sender, EventArgs e)
        {
            int count = listViewIpAddresses.SelectedItems.Count;
            if (count == 0)
                return;

            DialogResult result = MessageBox.Show(
                $"Are you sure you want to delete {count} selected IP address{(count > 1 ? "es" : "")}?",
                "Confirm Delete",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result != DialogResult.Yes)
                return;

            for (int i = count - 1; i >= 0; i--)
            {
                int index = listViewIpAddresses.SelectedItems[i].Index;
                listViewIpAddresses.Items.RemoveAt(index);
                detectors.RemoveAt(index);
            }


        }

        private void listViewIpAddresses_DoubleClick(object sender, EventArgs e)
        {
            if (listViewIpAddresses.SelectedItems.Count != 1)
                return;

            WinFormsListViewItem item = listViewIpAddresses.SelectedItems[0];
            string currentIp = item.SubItems[0].Text;

            string? newIp = Microsoft.VisualBasic.Interaction.InputBox(
                $"Edit IP Address:", "Edit IP", currentIp);

            if (!string.IsNullOrWhiteSpace(newIp) &&
                newIp != currentIp &&
                Detector.IsValidIpAddress(newIp))
            {
                // Update the IP string in both ListView and Detector list
                int index = item.Index;
                detectors[index].ipv4Address = newIp.Split('.');
                detectors[index].Ping(); // Refresh the new IP

                UpdateListViewDetectors(sender, e);
            }
            else if (!string.IsNullOrWhiteSpace(newIp) && !Detector.IsValidIpAddress(newIp))
            {
                MessageBox.Show("That is not a valid IP address.", "Invalid IP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        private void UpdateConnectionStatus()
        {
            int connected = detectors.Count(d => d.CheckState(InstrumentState.ONLINE));
            int active = detectors.Count(d => d.CheckState(InstrumentState.ACTIVE));

            string status = $"Status: {connected} detector(s) connected";
            if (active > 0)
            {
                status += $", {active} acquiring data";
            }
            else
            {
                buttonTotals.Enabled = false;
            }
            labelStatus.Text = status;
        }
        private void EnableControls(bool enabled)
        {
            listViewIpAddresses.Enabled = enabled;
            buttonSynchTime.Enabled = enabled;
            buttonDuration.Enabled = enabled;
            buttonStartStop.Enabled = enabled;
            buttonHVRead.Enabled = enabled;

        }
        private void SetMaxFileSizeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            string input = Microsoft.VisualBasic.Interaction.InputBox(
                "Set max file size (in MB):",
                "File Size Limit",
                (maxFileSizeBytes / 1024 / 1024).ToString());

            if (int.TryParse(input, out int mb) && mb >= 100 && mb <= 10000)
            {
                maxFileSizeBytes = mb * 1024 * 1024;
                MessageBox.Show($"Max file size set to {mb} MB.");
            }
            else
            {
                MessageBox.Show("Invalid input. Enter a number between 100 and 10000.");
            }
        }
        private void SetSaveFolderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select folder to save files when using COMPUTER storage";
                dialog.SelectedPath = localSaveDirectory;

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    localSaveDirectory = dialog.SelectedPath;
                }
            }
        }


        private void PopulateComboStarterNeutrons()
        {

        }

        private void buttonShowTransmission_Click(object sender, EventArgs e)
        {
            FormTransmissionViewer viewer = new FormTransmissionViewer();
            viewer.Show();
        }

        private void textBoxIpAddress_TextChanged(object sender, EventArgs e)
        {

        }

        private void buttonStartStop_Click(object sender, EventArgs e)
        {
            if (buttonStartStop.Text == "Start")
            {
                StartRun();
            }
            else
            {
                StopRun();
            }
        }

        private void SendNetSaveFileCommand()
        {
            string timestamp = DateTime.Now.ToString("yyyy_MM_dd_HHmmss");
            currentFileName = $"{baseFileName}_{timestamp}_{fileIndex:D3}";  // Remove .bin extension

            foreach (var detector in detectors)
            {
                if (detector.CheckState(InstrumentState.ONLINE))  // Only send to online detectors
                {
                    detector.WriteAndWait($"NetSaveFile = {currentFileName}");
                }
            }

            AppendMessage(null, new DebuggingMessage($"Started new file: {currentFileName}"), "auto > ");
        }

        private async void StartRun()
        {
            buttonStartStop.Text = "Stop";
            radioButtonContinuous.Enabled = false;
            radioButtonDuration.Enabled = false;
            numericUpDownDuration.Enabled = false;
            buttonDuration.Enabled = false;
            buttonDescription.Enabled = false;
            buttonStorage.Enabled = false;
            runInProgress = true;
            Array.Clear(uiRunningTotals, 0, uiRunningTotals.Length);

            string storageMode = comboBoxStorage.SelectedItem?.ToString() ?? "INSTRUMENT";
            string description = textBoxDescription.Text.Trim();
            string timestamp = DateTime.Now.ToString("yyyy_MM_dd_HHmmss");

            try
            {
                foreach (var detector in detectors.Where(d => d.CheckState(InstrumentState.ONLINE)))
                {
                    // Set duration or counts if needed (example: optional)
                    // detector.WriteAndWait("autoRunCounts = 10000");

                    if (storageMode == "COMPUTER")
                    {
                        detector.netSaveFilepath = localSaveDirectory;
                        detector.WriteAndWait("Storage = 1");
                    }
                    else
                    {
                        detector.WriteAndWait($"Storage = {GetStorageValue(storageMode)}");
                    }

                    if (!string.IsNullOrEmpty(description))
                    {
                        detector.WriteAndWait($"runDescription = {description}");
                        AppendMessage(detector, new DebuggingMessage($"runDescription = {description}"), "cmd > ");
                    }

                    detector.SetState(InstrumentState.ACTIVE);
                }

                // When saving to the computer we need to request a new streamed file name
                // before sending Go so the instrument knows to push list-mode data over TCP.
                if (storageMode == "COMPUTER")
                {
                    SendNetSaveFileCommand();
                }

                // Small delay to give time for commands to settle
                await Task.Delay(100);

                await SendCommandToAllAsync("Go");
                AppendMessage(null, new DebuggingMessage("Batch Go sent"), "cmd > ");

                if (radioButtonContinuous.Checked && storageMode == "COMPUTER")
                {
                    StartFileSizeMonitor();
                    AppendMessage(null, new DebuggingMessage("Started continuous acquisition"), "info > ");
                }
                else if (radioButtonDuration.Checked)
                {
                    Task.Delay(durationInSeconds * 1000).ContinueWith(_ =>
                    {
                        Invoke(() => StopRun());
                    });
                    AppendMessage(null, new DebuggingMessage($"Started timed acquisition ({durationInSeconds} seconds)"), "info > ");
                }

                UpdateConnectionStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error starting run: {ex.Message}", "Start Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                StopRun(); // fallback if error during setup
            }


        }




        private void StartFileSizeMonitor()
        {
            if (fileSizeTimer != null)
            {
                fileSizeTimer.Stop();
                fileSizeTimer.Dispose();
            }

            lastFileStartTime = DateTime.Now;

            // Start fallback timer to recheck every 5 minutes
            fileSizeTimer = new System.Windows.Forms.Timer();
            fileSizeTimer.Interval = 300_000; // 5 minutes
            fileSizeTimer.Tick += FileSizeTimer_Tick;
            fileSizeTimer.Start();

            // Start scheduled rollover
            ScheduleEstimatedRollover();
        }
        private async void ScheduleEstimatedRollover()
        {
            // Cancel any previously scheduled rollover
            rolloverCts?.Cancel();
            rolloverCts = new CancellationTokenSource();

            double totalRate = detectors
                .Where(d => d.CheckState(InstrumentState.ONLINE))
                .Sum(d => d.totalCountsOneSecond);

            if (totalRate <= 0)
            {
                AppendMessage(null, new DebuggingMessage("Rollover skipped: no rate data available."), "warn > ");
                return;
            }

            double eventsPerMB = 128000;
            double maxFileSizeMB = maxFileSizeBytes / 1024.0 / 1024.0;
            double maxEvents = eventsPerMB * maxFileSizeMB;
            double secondsToRollover = maxEvents / totalRate;

            // Add margin
            int delayMs = (int)(secondsToRollover * 0.95 * 1000); // 95% of estimate
            delayMs = Math.Max(delayMs, 60_000); // Never schedule less than 60s ahead

            LogFileStart(currentFileName, totalRate, secondsToRollover);

            AppendMessage(null, new DebuggingMessage($"Scheduling rollover in ~{delayMs / 1000} seconds"), "info > ");

            try
            {
                await Task.Delay(delayMs, rolloverCts.Token);
                RolloverToNewFile();
                ScheduleEstimatedRollover(); // Schedule next one
            }
            catch (TaskCanceledException)
            {

            }
        }

        private int GetStorageValue(string storageMode)
        {
            return storageMode switch
            {
                "NONE" => 0,
                "COMPUTER" => 1,       // NET storage
                "USB" => 2,
                "INSTRUMENT" => 3,     // LFS (Local File System)
                _ => 3
            };
        }
        private async void StopRun()
        {
            fileSizeTimer?.Stop();
            fileSizeTimer?.Dispose();
            fileSizeTimer = null;
            runInProgress = false;

            try
            {
                await SendCommandToAllAsync("Cancel");
                AppendMessage(null, new DebuggingMessage("Batch Cancel sent"), "cmd > ");
            }
            catch (Exception ex)
            {
                AppendMessage(null, new DebuggingMessage($"Error during Cancel: {ex.Message}"), "error > ");
            }

            // UI re-enabling
            buttonStartStop.Text = "Start";
            radioButtonContinuous.Enabled = true;
            radioButtonDuration.Enabled = true;
            numericUpDownDuration.Enabled = true;
            buttonDuration.Enabled = true;
            buttonDescription.Enabled = true;
            buttonStorage.Enabled = true;

            AppendMessage(null, new DebuggingMessage("Run stopped"), "info > ");
            UpdateConnectionStatus();
        }



        private void NumericUpDownDuration_ValueChanged(object sender, EventArgs e)
        {
            durationInSeconds = (int)numericUpDownDuration.Value;
        }
        private void ButtonDuration_Click(object sender, EventArgs e)
        {
            durationInSeconds = (int)numericUpDownDuration.Value;
            // no message box, just update silently
        }
        private void UpdateStorageButtonState()
        {
            buttonStorage.Enabled = detectors.Any(d => d.CheckState(InstrumentState.ONLINE));
        }
        private void ButtonStorage_Click(object sender, EventArgs e)
        {
            if (comboBoxStorage.SelectedItem == null) return;

            string storageTarget = comboBoxStorage.SelectedItem.ToString();

            // If COMPUTER is selected, make sure save directory exists
            if (storageTarget == "COMPUTER")
            {
                try
                {
                    Directory.CreateDirectory(localSaveDirectory);

                    // Update detectors with the save path
                    foreach (var detector in detectors.Where(d => d.CheckState(InstrumentState.ONLINE)))
                    {
                        detector.netSaveFilepath = localSaveDirectory;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Cannot create save directory: {ex.Message}", "Directory Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            foreach (var detector in detectors.Where(d => d.CheckState(InstrumentState.ONLINE)))
            {
                detector.WriteAndWait($"Storage = {GetStorageValue(storageTarget)}");
            }

            AppendMessage(null, new DebuggingMessage($"Storage set to {storageTarget}"), "cmd > ");
        }
        private void ButtonDescription_Click(object sender, EventArgs e)
        {
            string descriptionText = textBoxDescription.Text.Trim();
            if (string.IsNullOrEmpty(descriptionText))
                return;

            foreach (var detector in detectors)
            {
                if (detector.CheckState(InstrumentState.ONLINE))
                {
                    detector.WriteAndWait($"runDescription = {descriptionText}");
                }
            }

            AppendMessage(null, new DebuggingMessage($"runDescription = {descriptionText}"), "cmd > ");
        }

        private void TransferSavedFiles_Click(object? sender, EventArgs e)
        {
            var transferForm = new FormFileTransfer(detectors);
            transferForm.Show();
        }

        //Plots

        private void ShowGenericBarGraph()
        {
            var model = new PlotModel
            {
                Title = "Tube Distribution",
                Background = OxyColors.White,
                PlotAreaBackground = OxyColors.White,
                // Border like your DrawBarPlotBorder()
                PlotAreaBorderColor = OxyColors.Black,
                PlotAreaBorderThickness = new OxyThickness(1)
            };

            categoryAxis = new CategoryAxis
            {
                Position = AxisPosition.Bottom,
                Key = "cat",
                IsPanEnabled = false,
                IsZoomEnabled = false,
                GapWidth = 0.1
            };
            // initial labels 1–16
            for (int i = 1; i <= 16; i++)
                categoryAxis.Labels.Add((i + tubeDisplayOffset).ToString());

            valueAxis = new LinearAxis
            {
                Position = AxisPosition.Left,
                Key = "val",
                Minimum = 0,                 // force start at zero (like you did)
                Maximum = double.NaN,        // let us set dynamically from data
                IsPanEnabled = false,
                IsZoomEnabled = false,
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColor.FromRgb(169, 169, 169), // DarkGray-ish
                MinorGridlineStyle = LineStyle.None,
                MajorTickSize = 10,          // tick “length”
                AxislineStyle = LineStyle.Solid,
                AxislineColor = OxyColors.Black,
                Title = "Counts"
            };

            model.Axes.Add(categoryAxis);
            model.Axes.Add(valueAxis);

            barSeriesCounts = new BarSeries
            {
                XAxisKey = "val",
                YAxisKey = "cat",
                StrokeThickness = 1,
                StrokeColor = OxyColor.FromRgb(169, 169, 169), // colorBorderChannel
                BarWidth = 0.8
            };

            // 16 bars for the page
            for (int i = 0; i < 16; i++)
                barSeriesCounts.Items.Add(new BarItem { Value = 0, CategoryIndex = i });

            // color by “row” of 4, like your colorRow[]
            ApplyRowColors();

            model.Series.Add(barSeriesCounts);
            plotViewTubeDistribution.Model = model;

        }

        private void UpdateTubeDistribution(Detector detector)
        {
            if (InvokeRequired)
            {
                BeginInvoke((Action)(() => UpdateTubeDistribution(detector)));
                return;
            }

            if (barSeriesCounts == null) return;

            // Example: aggregate across all connected detectors
            var summedCounts = new double[32];
            foreach (var d in detectors.Where(d => d.CheckState(InstrumentState.ONLINE)))
            {
                if (d.channelCountsOneSecond == null) continue;
                for (int i = 0; i < Math.Min(32, d.channelCountsOneSecond.Length); i++)
                    summedCounts[i] += d.channelCountsOneSecond[i];
            }

            double maxSeen = 1;
            for (int i = 0; i < 16; i++)
            {
                int channelIndex = i + tubeDisplayOffset;
                double value;

                if (showRolling)
                {
                    value = (channelIndex < summedCounts.Length) ? summedCounts[channelIndex] : 0;
                }
                else
                {
                    if (channelIndex < summedCounts.Length)
                        cumulativeCounts[channelIndex] += summedCounts[channelIndex];
                    value = cumulativeCounts[channelIndex];
                }

                barSeriesCounts.Items[i].Value = value;
                if (value > maxSeen) maxSeen = value;
            }

            if (showRolling)
                UpdateChannelCountsList(summedCounts);
            else
                UpdateChannelCountsList(cumulativeCounts);

            valueAxis.Minimum = 0;
            valueAxis.Maximum = Math.Ceiling(maxSeen * 1.05);

            plotViewTubeDistribution.Model.InvalidatePlot(true);

            double cpsTotal = detectors
                .Where(d => d.CheckState(InstrumentState.ONLINE))
                .Sum(d => (double)d.totalCountsOneSecond);

            textBoxMonitorCPS.Text = cpsTotal.ToString("N0");

            // --- Total Counts: track locally during active runs ---
            if (detectors.Any(d => d.CheckState(InstrumentState.ACTIVE)))
            {
                foreach (var d in detectors.Where(d => d.CheckState(InstrumentState.ONLINE)))
                {
                    for (int i = 0; i < Math.Min(32, d.channelCountsOneSecond.Length); i++)
                    {
                        uiRunningTotals[i] += d.channelCountsOneSecond[i];
                    }
                }

                long totalCounts = uiRunningTotals.Sum();
                textBoxTotalCounts.Text = totalCounts.ToString("N0");

                // --- Update Feynman m1..m4 while active ---
                if (momentsClient != null)
                {
                    int selectedGateIndex = comboBoxFeynmanGatewidth.SelectedIndex;
                    if (selectedGateIndex >= 0)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                if (momentsClient.TryGetMoments(selectedGateIndex, out var sample))
                                {
                                    textBoxPlotsFeynmanm1.Text = sample.M[0].ToString("F4");
                                    textBoxPlotsFeynmanm2.Text = sample.M[1].ToString("F4");
                                    textBoxPlotsFeynmanm3.Text = sample.M[2].ToString("F4");
                                    textBoxPlotsFeynmanm4.Text = sample.M[3].ToString("F4");
                                }

                            }
                            catch (Exception ex)
                            {
                                AppendMessage(detector,
                                    new DebuggingMessage($"Error updating moments: {ex.Message}"),
                                    "warn > ");
                            }
                        });
                    }
                }
            }
            else
            {
                // Not running → clear display and reset totals
                Array.Clear(uiRunningTotals, 0, uiRunningTotals.Length);
                textBoxTotalCounts.Clear();

                // Clear moment text boxes when run stops
                textBoxPlotsFeynmanm1.Clear();
                textBoxPlotsFeynmanm2.Clear();
                textBoxPlotsFeynmanm3.Clear();
                textBoxPlotsFeynmanm4.Clear();
            }
        }


        private void CopyPlotImageToClipboard()
        {
            using (var bmp = new Bitmap(plotViewTubeDistribution.Width, plotViewTubeDistribution.Height))
            {
                plotViewTubeDistribution.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size));
                using (System.Drawing.Image img = (System.Drawing.Image)bmp.Clone())
                {
                    System.Windows.Forms.Clipboard.SetImage(img);
                }
            }
        }

        private void SavePlotImage()
        {
            using (var sfd = new SaveFileDialog
            {
                Filter = "PNG Image (*.png)|*.png",
                FileName = $"TubeDistribution_{DateTime.Now:yyyyMMdd_HHmmss}.png"
            })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;

                using (var bmp = new Bitmap(plotViewTubeDistribution.Width, plotViewTubeDistribution.Height))
                {
                    plotViewTubeDistribution.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size));
                    bmp.Save(sfd.FileName, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
        }

        private void ClearTubeDistribution()
        {
            // If we're in cumulative mode, zero ONLY the visible 16 channels in the running totals
            if (!showRolling && cumulativeCounts != null)
            {
                for (int i = 0; i < 16; i++)
                {
                    int ch = i + tubeDisplayOffset;      // current page index
                    if (ch < cumulativeCounts.Length)
                        cumulativeCounts[ch] = 0;
                }
            }

            // Zero the visible bars (page) either way so the chart looks cleared immediately
            for (int i = 0; i < 16 && i < barSeriesCounts.Items.Count; i++)
                barSeriesCounts.Items[i].Value = 0;

            plotViewTubeDistribution.Model.InvalidatePlot(true);

            // Keep the ListView in sync
            if (showRolling)
                UpdateChannelCountsList(new double[32]); // rolling will refill next update
            else
                UpdateChannelCountsList(cumulativeCounts);
        }

        private void ApplyRowColors()
        {
            for (int i = 0; i < barSeriesCounts.Items.Count; i++)
            {
                OxyColor color;
                if (i >= 0 && i <= 6) // channels 1-7
                    color = OxyColors.DarkBlue;
                else if (i >= 7 && i <= 12) // channels 8-13
                    color = OxyColors.DodgerBlue;
                else if (i >= 13 && i <= 14) // channels 14-15
                    color = OxyColors.Cyan;
                else // channel 16
                    color = OxyColors.Blue;

                barSeriesCounts.Items[i].Color = color;
            }
        }


        private void numericUpDownTubeDistributionDisplay_ValueChanged(object sender, EventArgs e)
        {
            tubeDisplayOffset = ((int)numericUpDownTubeDistributionDisplay.Value - 1) * 16;

            categoryAxis.Labels.Clear();
            for (int i = 1; i <= 16; i++)
                categoryAxis.Labels.Add((i + tubeDisplayOffset).ToString());

            ApplyRowColors();

            // refresh both the bars and the ListView for the new page
            RefreshTubeDistributionWithLastData();
        }


        private void RadioButtonTubeMode_CheckedChanged(object sender, EventArgs e)
        {
            if (radioButtonTubeRolling.Checked)
            {
                showRolling = true;
                Array.Clear(cumulativeCounts, 0, cumulativeCounts.Length); // reset totals
            }
            else if (radioButtonTubeCumulative.Checked)
            {
                showRolling = false;
                Array.Clear(cumulativeCounts, 0, cumulativeCounts.Length); // start fresh
            }
        }
        private void UpdateChannelCountsList(double[] counts)
        {
            listViewPlotsChannelCounts.BeginUpdate();
            listViewPlotsChannelCounts.Items.Clear();

            for (int i = 0; i < 16; i++)
            {
                int channelIndex = i + tubeDisplayOffset;
                string channelName = (channelIndex + 1).ToString(); // 1-based display
                string countValue = (channelIndex < counts.Length) ? counts[channelIndex].ToString("N0") : "0";

                var item = new System.Windows.Forms.ListViewItem(channelName);
                item.SubItems.Add(countValue);
                listViewPlotsChannelCounts.Items.Add(item);
            }

            listViewPlotsChannelCounts.EndUpdate();
        }
        private void tabControlPlots_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (tabControlPlots.SelectedTab == tabPageTubeDistribution)
            {
                tubeDistributionActive = true;
                RefreshTubeDistributionWithLastData(); // catch up immediately
            }
            else
            {
                tubeDistributionActive = false;
            }
        }

        private double[] GetCurrentRollingSnapshot()
        {
            var summed = new double[32];
            foreach (var d in detectors.Where(d => d.CheckState(InstrumentState.ONLINE)))
            {
                if (d.channelCountsOneSecond == null) continue;
                int len = Math.Min(32, d.channelCountsOneSecond.Length);
                for (int i = 0; i < len; i++)
                    summed[i] += d.channelCountsOneSecond[i];
            }
            return summed;
        }

        private void RefreshTubeDistributionWithLastData()
        {
            double[] src = showRolling
                ? GetCurrentRollingSnapshot()
                : uiRunningTotals.Select(x => (double)x).ToArray();

            double maxSeen = 1;
            for (int i = 0; i < 16; i++)
            {
                int ch = i + tubeDisplayOffset;
                double v = (ch < src.Length) ? src[ch] : 0;
                barSeriesCounts.Items[i].Value = v;
                if (v > maxSeen) maxSeen = v;
            }

            valueAxis.Minimum = 0;
            valueAxis.Maximum = Math.Ceiling(maxSeen * 1.05);

            UpdateChannelCountsList(src);
            plotViewTubeDistribution.Model.InvalidatePlot(true);
        }



        private double[] RandomWalk(int count)
        {
            var rand = DeterministicRng.Instance;
            double[] data = new double[count];
            double val = 0;
            for (int i = 0; i < count; i++)
            {
                val += rand.NextDouble() - 0.5;
                data[i] = Math.Round(val, 2);
            }
            return data;
        }
        private void FileSizeTimer_Tick(object sender, EventArgs e)
        {
            // Only re-evaluate if we're using INSTRUMENT or COMPUTER
            string mode = comboBoxStorage.SelectedItem?.ToString();
            if (mode != "COMPUTER" && mode != "INSTRUMENT")
                return;

            double elapsedSeconds = (DateTime.Now - lastFileStartTime).TotalSeconds;
            long estimatedBytes = GetEstimatedFileSize(elapsedSeconds);

            if (estimatedBytes >= maxFileSizeBytes)
            {
                RolloverToNewFile();
                ScheduleEstimatedRollover(); // Reset the schedule based on new file
            }
            else
            {
                // Re-schedule using current rate
                ScheduleEstimatedRollover();
            }
        }


        private DateTime lastFileStartTime = DateTime.Now;
        private int maxFileTimeSeconds = 3600; // 1 hour default

        private void RolloverToNewFile()
        {
            fileIndex++;
            lastFileStartTime = DateTime.Now;

            // Stop current file and start new one
            // The detector should close the current file when receiving a new NetSaveFile command
            SendNetSaveFileCommand();

            AppendMessage(null, new DebuggingMessage($"File rollover: Starting file #{fileIndex}"), "auto > ");
        }
        private void LogFileStart(string filename, double rate, double estDurationSec)
        {
            string logLine = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{filename}\t{rate:F1} cps\t~{estDurationSec:F1} sec";
            File.AppendAllText("FileRolloverLog.txt", logLine + Environment.NewLine);
        }

        private long GetEstimatedFileSize(double elapsedSeconds)
        {
            // Estimate based on total count rate
            // Each event is 8 bytes, plus some overhead for headers

            long totalCountRate = 0;
            foreach (var detector in detectors.Where(d => d.CheckState(InstrumentState.ONLINE)))
            {
                totalCountRate += detector.totalCountsOneSecond;
            }

            // 8 bytes per event + ~10% overhead for headers and metadata
            long bytesPerSecond = totalCountRate * 8;
            long estimatedBytes = (long)(bytesPerSecond * elapsedSeconds * 1.1);

            return estimatedBytes;
        }
        private async Task SendCommandToAllAsync(string command, Func<Detector, bool>? filter = null)
        {
            var targets = detectors
                .Where(d => d.CheckState(InstrumentState.ONLINE))
                .Where(d => filter == null || filter(d));

            var tasks = targets
                .Select(d => Task.Run(() => d.WriteAndWait(command)));

            await Task.WhenAll(tasks);
        }



        //
        //
        // Configure Tab
        //
        //
        private void PopulateSnmCombos()
        {
            comboBoxVs1.Items.Clear();
            comboBoxVs2.Items.Clear();
            comboBoxVi.Items.Clear();

            comboBoxVs1.Items.AddRange(snm.GetVs1List().ToArray());
            comboBoxVs2.Items.AddRange(snm.GetVs2List().ToArray());
            comboBoxVi.Items.AddRange(snm.GetViList().ToArray());

            comboBoxVs1.SelectedIndex = 0;
            comboBoxVs2.SelectedIndex = 0;
            comboBoxVi.SelectedIndex = 0;
        }

        private void buttonGetRowRatios_Click(object sender, EventArgs e)
        {
            listViewRowRatio.Items.Clear();

            foreach (Detector d in detectors.Where(d => d.CheckState(InstrumentState.ONLINE)))
            {
                d.WriteAndWait("Rates"); // update cumulative row ratios

                var ratios = d.rowRatioCumulative;
                if (ratios.Count == 0)
                    continue;

                double[,] matrix = ratios[0]; // primary unit
                int numRows = matrix.GetLength(0);
                int numCols = matrix.GetLength(1);

                for (int i = 0; i < numRows; i++)
                {
                    for (int j = i + 1; j < numCols; j++)
                    {
                        string label = $"Row {i + 1}/{j + 1}";
                        string value = matrix[i, j].ToString("0.000");

                        var item = new WinFormsListViewItem(label);
                        item.SubItems.Add(value);
                        listViewRowRatio.Items.Add(item);
                    }
                }
            }


        }


        private async void ComboBoxFeynmanGatewidth_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (momentsClient == null)
                return;

            int index = comboBoxFeynmanGatewidth.SelectedIndex;
            if (index < 0)
                return;

            try
            {
                // Query the detector for the selected gate's moments
                var sample = await momentsClient.QueryGateAsync(index);

                // Update the text boxes with m1..m4
                textBoxPlotsFeynmanm1.Text = sample.M[0].ToString("F4");
                textBoxPlotsFeynmanm2.Text = sample.M[1].ToString("F4");
                textBoxPlotsFeynmanm3.Text = sample.M[2].ToString("F4");
                textBoxPlotsFeynmanm4.Text = sample.M[3].ToString("F4");
            }
            catch (Exception ex)
            {
                AppendMessage(null, new DebuggingMessage($"Error querying moments: {ex.Message}"), "warn > ");
            }
        }

        private void label53_Click(object sender, EventArgs e)
        {

        }

        private void checkBoxEnableAdaptiveWindowing_CheckedChanged(object sender, EventArgs e)
        {

        }
    }
}
