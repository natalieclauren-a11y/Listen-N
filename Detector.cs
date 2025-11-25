using System;
using System.Collections.Generic;
using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO.Ports;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

using RingBuffer;
using System.Net.NetworkInformation;
using Microsoft.VisualBasic.Devices;
using System.Security.Policy;
using System.Windows.Forms.Design;
using System.Diagnostics.CodeAnalysis;
using MultiPass;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Listen_N;

//
// How to mount the usb card on the MC-15
// telnet to the MC-15 with cmd or powershell
// MC-15 login: root
// Password: root
// mkdir usb1
// mount /dev/mmcblk0p1 usb1 # internal sd card
// mount /dev/mmcblk0p1 usb1 # internal sd card
// mkdir usb2
// mount /dev/sda1 usb2 # externally attached usb drive

// Additional Commands Not Documented in Process<Command>
// Pause -> No Return -> Pause Acquisition
// Resume -> No Return -> Resume Acquisition
// Exit
// ShutDown -> No Return -> Power Off system
// reboot   -> Reboot Linux
// updateSystem -> Update the Linux firmware

// A list of most of the MS Visual Studio versions and their defines
// https://stackoverflow.com/questions/70013/how-to-detect-if-im-compiling-code-with-a-particular-visual-studio-version
// to be used as
// #if defined _MSC_VER #  if _MSC_VER >= 1800 
// #else
// #endif

// MSVC++ 14.21 _MSC_VER == 1921(Visual Studio 2019 version 16.1)
// MSVC++ 14.2  _MSC_VER == 1920(Visual Studio 2019 version 16.0)
// MSVC++ 14.16 _MSC_VER == 1916(Visual Studio 2017 version 15.9)
// MSVC++ 14.15 _MSC_VER == 1915(Visual Studio 2017 version 15.8)
// MSVC++ 14.14 _MSC_VER == 1914(Visual Studio 2017 version 15.7)
// MSVC++ 14.13 _MSC_VER == 1913(Visual Studio 2017 version 15.6)
// MSVC++ 14.12 _MSC_VER == 1912(Visual Studio 2017 version 15.5)
// MSVC++ 14.11 _MSC_VER == 1911(Visual Studio 2017 version 15.3)
// MSVC++ 14.1  _MSC_VER == 1910(Visual Studio 2017 version 15.0)
// MSVC++ 14.0  _MSC_VER == 1900(Visual Studio 2015 version 14.0)
// MSVC++ 12.0  _MSC_VER == 1800(Visual Studio 2013 version 12.0)
// MSVC++ 11.0  _MSC_VER == 1700(Visual Studio 2012 version 11.0)
// MSVC++ 10.0  _MSC_VER == 1600(Visual Studio 2010 version 10.0)
// MSVC++ 9.0   _MSC_FULL_VER == 150030729(Visual Studio 2008, SP1)
// MSVC++ 9.0   _MSC_VER == 1500(Visual Studio 2008 version 9.0)
// MSVC++ 8.0   _MSC_VER == 1400(Visual Studio 2005 version 8.0)
// MSVC++ 7.1   _MSC_VER == 1310(Visual Studio.NET 2003 version 7.1)
// MSVC++ 7.0   _MSC_VER == 1300(Visual Studio.NET 2002 version 7.0)
// MSVC++ 6.0   _MSC_VER == 1200(Visual Studio 6.0 version 6.0)
// MSVC++ 5.0   _MSC_VER == 1100(Visual Studio 97 version 5.0)
namespace Vf61Gui
{

    public delegate void MethodDelegate(string[] replies);

    public class Detector : IDisposable
    {

        // For Windows Mobile, replace user32.dll with coredll.dll
        [DllImport("coredll.dll", SetLastError = true)]
        public extern static IntPtr FindWindow(string lpClassName, string lpWindowName);

        // Find window by Caption only. Note you must pass IntPtr.Zero as the first parameter.
        [DllImport("coredll.dll", EntryPoint = "FindWindow", SetLastError = true)]
        public extern static IntPtr FindWindowByCaption(IntPtr ZeroOnly, string lpWindowName);

        public int maxArrayListSize = 256;
        public int timeBetweenWrites = 50;   // How long to pause after sending the write command.
        public int hvSet = -1;
        public int hvRead = -1;
        public int hvMax = -1;
        public int lcdDieTemp = 0;
        public int currentFeynmanIndex = 4;
        public int[] fpgaDieTemp = new int[2];          //c RO -  FPGA temp -> Returned with DieTemp = <instr.TempDie>, <instr.TempDie>
        public int[] repetitions = new int[2];                 // Stores the current repetition int[0] and the max repetition int[1]
        public int percentComplete = 0;   // The percent complete of saving files. This is updated in USBsaveFileProgress = <percent Complete>, <current filename>
        public int writeAndWaitTimeout = 1000;   // How long should mutexWriteAndWait wait in ms.
        //public int lastFileSaved_Size = 0;    // The size of the last file saved. This is retrieved with the AssayDone reply.
        public int vetoGateWidth = 20000;   // Stored as nano seconds.
        public int feynmanIndex256 = 4; // The index of feynmans associated with the 256 us gate.

        public int numberOfUnrecognizedReplies = 0;
        public int LFScount = 0;      // This is updated with LFSlist or LFSstatus
        public int percentFileMemoryFilled = 0;    //An integer between 0 and 100 indicating percent of the file memory filled.
        public int drawNum = 0;
        // public int totalCounts = 0; replaced by totalCountsTotal so that it's not confused with totalCountsCumulative
        // public int repetitions = 1;      made into int[]       // How many times do you want to repeat the meausurement. This is sent to lmc_control with "Rep = <current index>, <repetitions>". There is not return from lmc_control.
        public int minMemThreshold = 5;         // This is only used by Detector. It is not stored in lmc_control.
        public int maxFileThreshold = 201;     // One more than the maximum number of files. This is only used in Detector and is not stored anywhere on lmc_control
        public int powerDownAlertThreshold = 10; // This isn't used in the original code. I think it's a leftover and is replaced by powerDownThreshold
        public int powerDownThreshold = 5;      // What is the battery level threshold to start shutting down.
        public int noUserPowerDownTime = 300000;    // Units are in milliseconds
        public int audioRate = 0;
        public int saveFileProgress = 0;       // This is checked with USBsaveFileProgress
        public int betaVsTimePlotLength = 10;
        public int betaVsSm2PlotLength = 10;
        public int audioVolume;
        public int audioFrequency;
        //public int longestReplyLength = 0;  // Stores the longest reply expected. Use this to limit how much is examined in replies.

        public int timeToWaitForBoot = 30000;   // How long do you wait for lmc_control to boot in milliseconds.
        private int hashCode = 0;

        public uint backgroundCollectionTime = 1200;    // I'm not sure what this is really for. It's a holdover from Eric Sorensen
        public uint deadtime = 2500;
        public uint defaultLabDuration = 1000;
        public uint elapsedTime = 0;
        public uint duration = 1000;
        public uint dmaTimeInterval = 50;
#if DEBUG
        public uint trgDuration = 10;
        public uint bkgDuration = 10;
        public uint ipcDuration = 10;
        public uint qdrDuration = 10;
        public uint autoRunDuration = 10;  // How long do you want to count for?
#else
        public uint trgDuration = 600;
        public uint bkgDuration = 1200;
        public uint ipcDuration = 1200;
        public uint qdrDuration = 600;     // Five minutes
        public uint autoRunDuration = 1200;  // How long do you want to count for?
#endif
        public uint trgCounts = 1000000;
        public uint bkgCounts = 1000000;
        public uint ipcCounts = 1000000;
        public uint qdrCounts = 1000000;
        public uint autoRunCounts = 1000000; // Determines the maximum number of events to collect.
        public uint totalCountsTotalCollectionTime = 0;    // The time associated for when totalCountsTotal was updated.
        public uint maxSamplesInFile = 0;       // Set and Get to LMC with maxSamplesInFile
        public uint bytesTransferred = 0; // how many bytes have been transferred using NetSaveFile
        public uint bytesFileSize = 0;    // size in bytes

        /// <summary>
        /// The total number of counts in the current acquisition. The sum of the counts in channelCountsTotals.
        /// </summary>
        public uint totalCountsTotal = 0;      // The sum of the counts in channelCountsTotals

        /// <summary>
        /// The number of counts that happened in the last second.
        /// </summary>
        public uint totalCountsOneSecond = 0;  // The sum of the counts in channelCountsOneSecond

        /// <summary>
        /// The total number of counts from the Rates command.
        /// </summary>
        public uint totalCountsCumulative = 0; // The sum of the counts in channelCountsCumulative

        // Totals is only available when an assay is in progress.
        public uint[] channelCountsTotals = new uint[32];       // The results from the command Totals.
        // Rates are available anytime. There is no indication at what time the rates are correlated to.
        public uint[] channelCountsOneSecond = new uint[32];    // The one second counts for each channel. This is changed when the commabnd Rates is received.
        public uint[] channelCountsCumulative = new uint[32];   // The cumulative counts for each channel. This is changed when the command Rates is received.

        public uint[] totalCountsTotalByUnit = new uint[2];     // The total counts in the primary (totalCountsTotalByUnit[0]) and the secondary (totalCountsTotalByUnit[1])
        public uint[] totalCountsOneSecondByUnit = new uint[2]; // The total counts in the primary (totalCountsOneSecondByUnit[0]) and the seondary (totalCountsOneSecondByUnit[1])
        public uint[] totalCountsCumulativeByUnit = new uint[2];// The total cumulative counts in the primary (totalCountsCumulativeByUnit[0]) and the secondary (totalCountsCumulativeByUnit[0])

        // copyDonePattern is used to find the end of the header at the end of data streaming.
        public byte[] copyDonePattern = new byte[] { (byte)'C', (byte)'o', (byte)'p', (byte)'y', (byte)'D', (byte)'o', (byte)'n', (byte)'e', (byte)'\n' };

        public int[][] rowRatioIndices = { [] }; // The indices used to determine the row ratios. These can be initialized / changed with detectorFormat_Changed(object, EventArgs)
        public List<double[,]> rowRatioTotals = new List<double[,]>();    // The row ratios from channelCountsTotals
        public List<double[,]> rowRatioCumulative = new List<double[,]>();    // The row ratios from channelCountsCumulative
        public List<ErrorCode> errorCodes = new List<ErrorCode>();

        //public uint activeChannels = 0x7FFF7FFF;            // All of the active channels (Primary and Secondary)
        //public uint activeChannelsPrimary = 0x00007FFF;     // The active channels for the Primary
        //public uint activeChannelsSecondary = 0x7FFF0000;   // The active channels for the Secondary
        //public uint analysisChannels = 0x7FFF7FFF;            // All of the analysis channels (Primary and Secondary)
        //public uint analysisChannelsPrimary = 0x00007FFF;     // The analysis channels for the Primary
        //public uint analysisChannelsSecondary = 0x7FFF0000;   // The analysis channels for the Secondary
        public uint analysisMask = 0x7FFF7FFF;  // This is read-out from the MC-15 / ALMM / MC-Smalls
        public uint detectorChannelMask = 0xFFFFFFFF;  // This is the desired mask of the detector. It is intended to "remember" what is desired.
        public uint detectorAnalysisMask = 0x7FFF7FFF;  // This is the desired mask of the detector. It is intended to "remember" what is desired.
        public uint channelMask = 0xFFFFFFFF;   // This is read-out from the MC-15 / ALMM / MC-Smalls

        public BatteryStatus[] batteryStatus;               // Initialize this in InitializeComponent - The size of this is 2. One for each battery
        public BitmapBarPlot[] bitmapBarPlotCumulative;     // Initialize this in InitializeComponent - The size of this is 2. One for primary and one for secondary
        public BitmapBarPlot[] bitmapBarPlotOneSecond;      // Initialize this in InitializeComponent - The size of this is 2. One for primary and one for secondary

        public BitmapCountHistory bitmapCountHistory;       // Initialize this in InitializeComponent
        public BitmapSm2Ib bitmapSm2Ib;
        public BitmapHageAlphaCalcs? bitmapHageAlphaCalcs;


        public double bgRateDefault = 2.0;
        public double bgYmDefault = 0.1;
        public double bgRate = 0.0;
        public double bgYm = 0.0;
        public double distPriToSrc = 0.0;
        public double distSecToSrc = 0.0;
        public double distPriToFlr = 27.5;
        public double distSecToFlr = 27.5;
        public double hvWrConv = 1.8;           // I don't think this is used in the original code anymore
        public double hvRdConv = 1.6;           // I don't think this is used in the original code anymore

        public string serialNumber = "Unknown";
        public string userGuiVersion = "Unknown";
        public string hardwareVersion = "Unknown";
        public string linuxCCodeVersion = "Unknown";
        public string firmwareVersion = "Unknown";
        //public string lcdVersion = "Unknown";
        public string description = "None";
        //public string currentFilename = ""; // The current (or last) file that has been saved. This is updated in USBsaveFileProgress = <percent Complete>, <current filename>
        public string detLoca = "";        // DETector LOCAtion (Side_A) when in field user mode. e.g. DetLoca = "SideNN"
        public string mother = @"Mother";   // The first command the MC-15 expects is "Mother". If "Mother" is not the first command then the MC-15 will not connect.
        public string netSaveFilepath = @"C:\Data\";
        public string netSaveFileName = ""; // When NetSaveFile is sent, save the requested filename here.
        public string currentNetLmxPath = string.Empty;          // Full path to the active streamed LMX file
        public bool lmxHeaderWritten = false;                    // Tracks whether the LMX header has been persisted
        public string lastCommand = "";     // The last command sent to the MC-15.
        public string lastReply = "";       // The last reply sent from the MC-15.

        private bool disposed = false;
        public bool verbose = false;
        public bool verboseIn = true;
        public bool verboseOut = true;
        public bool audibleFeedback = true;   // In the original code this is audibleFeedback
        public bool enableStartupBackground = false;
        public bool enableDebug = false;    // This is to enable debugging in the UI. I'm not sure it is used in the original code
        public bool linuxDebug = false;     // This is to enable the debugging of the linux system. i.e. it turns on/off Verbose mode, which pipes information out the serial port.
        public bool linuxBooted = false;
        public bool startUpComplete = false;
        public bool systemConfigured = false;
        public bool acPresent = false;
        public bool exit = false;           // I don't think is used anymore in the original code.
        public bool enableThreatGraphic = false;
        public bool vetoActive = false;
        public bool wasAssayCancelled = false;

        /// <summary>
        /// Holds the ipv4Address. This is length 4. [169][254][30][30]
        /// </summary>
        public string[] ipv4Address = new string[] { "169", "254", "30", "30" };
        public string[] macAddress = new string[] { "00", "0A", "35", "00", "25", "01" };     // Holds the MAC address
        public string ipv4Address_Port = "5011";
        public string lmxFileheader = "";

        public DateTime dateTime = DateTime.Now;
        public DateTime dateTimeWhenRebootStarted = DateTime.Now;
        public DateTime dateTimeTimerBootCreated;   // This keeps track of when timerBoot was created. This is because timerBoot.Change(time, time) is from when timerBoot was created, not current time.
        public DateTime dateTimeBuildDate;
        public TimeSpan timeSpanRebooting = new TimeSpan();  // How long has has it been while the system is rebooting.

        public ThreatId threatId = new ThreatId(1.2);

        /// <summary>
        /// The current (or last) file that has been saved. This is updated in USBsaveFileProgress = &lt;percent Complete&gt;, &lt;current filename&gt;
        /// </summary>
        public FileListItem fileCurrent = new FileListItem();    // The current (or last) file that has been saved. This is updated in USBsaveFileProgress = <percent Complete>, <current filename>

        /// <summary>
        /// The last file saved. This is retrieved with the AssayDone reply.
        /// </summary>
        public FileListItem fileLastSaved = new FileListItem();  // The last file saved. This is retrieved with the AssayDone reply.

        // Declare enumerated types here
        public InstrumentState instrumentState = InstrumentState.OFFLINE;
        public PodConfig podConfig = PodConfig.Single;
        public InstrumentStorageLocation storageLocation = InstrumentStorageLocation.INSTRUMENT; // Maybe also be storeState in original code
        public DetectorFormat detectorFormat = DetectorFormat.MCSmalls;
        public AssayErrorCode assayErrorCode = AssayErrorCode.None;
        public UnitsDistance unitsDistance = UnitsDistance.Centimeters;
        public UsbFileState usbFileState = UsbFileState.Undefined;
        public UserMode userMode;
        public VetoTrueState vetoTrueState = VetoTrueState.Undefined;
        public FrontPanelStatus frontPanelStatus = FrontPanelStatus.Undefined;
        public MeasurementType measurementType = MeasurementType.Undefined;
        public AudioState audioState = AudioState.Undefined;
        public UpdateSystemStatus updateSystemStatus = UpdateSystemStatus.NoError;
        public NetworkState networkState = NetworkState.No_Net_Control;
        public WriteError writeError = WriteError.None;

        // Declare RingBuffer Here
        public RingBuffer<ChannelCountsTime> channelCountsHistory = new RingBuffer<ChannelCountsTime>(600, true);
        public RingBuffer<Sm2Ib> sm2IbOneSecondTime = new RingBuffer<Sm2Ib>(600, true);
        public RingBuffer<ThreatIdValues> threatIdOneSecondTime = new RingBuffer<ThreatIdValues>(600, true);
        public RingBuffer<HageSolutionAlpha> hageSolutionsAlphaOneSecondTime = new RingBuffer<HageSolutionAlpha>(600, true);

        public RingBuffer<DebuggingMessage> messages = new RingBuffer<DebuggingMessage>(20, true);
        public RingBuffer<DebuggingMessage> messagesSent = new RingBuffer<DebuggingMessage>(20, true);
        public RingBuffer<DebuggingMessage> messagesReceived = new RingBuffer<DebuggingMessage>(20, true);
        public RingBuffer<DebuggingMessage> messagesExceptions = new RingBuffer<DebuggingMessage>(20, true);
        public RingBuffer<DebuggingMessage> messagesUnknown = new RingBuffer<DebuggingMessage>(20, true);

        public Thickness[] thicknessArray = new Thickness[0];
        public Thickness thickness = new Thickness();

        public Efficiency efficiency = new Efficiency();

        /// <summary>
        /// The status of the last ping. If pingReply == null no ping has been sent.
        /// </summary>
        public PingReply pingReply;

        public Thread threadTcpIpDataReceived;
        public AdaptiveWindowEngine? Adaptive { get; set; }

        public sealed class Detection
        {
            public long TimestampUs { get; }
            public byte ChannelId { get; }

            public Detection(long timestampUs, byte channelId)
            {
                TimestampUs = timestampUs;
                ChannelId = channelId;
            }
        }

        //public Snm[] snm = new Snm[] { new Snm("Pu239"), 
        //    new Snm("Pu240"), 
        //    new Snm("U235"), 
        //    new Snm("U238"), 
        //    new Snm("Cf252"), 
        //    new Snm("AlphaN"),
        //    new Snm("Induced"),
        //    new Snm("Spontaneous")
        //};

        /// <summary>
        /// Instantiates a HageAlpha class for future calculations.<para />
        /// </summary>
        /// <remarks>
        /// We don't want to create then dispose of this everytime we do a calculation.<para />
        /// This should be done when Sm2Ib is called.<para />
        /// </remarks>
        public HageAlpha hageAlpha = new();

        public string[] swConfigPaths = new string[] {
            @"\FlashDisk\mc15\swConfig.txt",
            @"\SD Card\AutoRun\swConfig.txt"
        };
        public string[] hwConfigPaths = new string[] {
            @"\FlashDisk\mc15\hwConfig.txt",
            @"\SD Card\AutoRun\hwConfig.txt"
        };

        // ***** Communication Portals *****
        public TcpClient tcpClient;
        public SerialPort serialPort;
        private NetworkStream networkStream;
        public BinaryWriter binaryWriter = null;                   // The filestream to save data either by local storage or by file transfer.
        public ExpectedReply expectedReply = null;   // Stores the expected reply after sending a SendAndWait command.

        public FileListItem[] fileList = new FileListItem[0];
        public List<Feynman> feynmans = new List<Feynman>();

        //private static readonly object lockingObjectWrite = new Object();
        //private static readonly object lockingObjectWriteAndWait = new Object();

        //public static AutoResetEvent autoResetEventWriteAndWait = new AutoResetEvent(false);
        //public static AutoResetEvent autoResetEventBoot = new AutoResetEvent(false);
        //public static System.Threading.Timer timerBoot;

        public AutoResetEvent autoResetEventBoot = new AutoResetEvent(false);
        public ManualResetEvent manualResetEventWriteAndWait = new ManualResetEvent(false);
        public ManualResetEvent resetEventNetSaveFile = new ManualResetEvent(false);
        public object writeLock = new object(); // This is used to lock any writing to the TcpClient or SerialPort.
        public object processReplyLock = new object();  // This is used to lock processing of replies.
        public System.Threading.Timer timerBoot;

        /// <summary>
        /// This is used to delay SendAsync when we are expecting a reply.
        /// </summary>
        // public SortedList<string, Thread> sortedList_DelegateSendAndWait;

        /// <summary>
        /// Keeps track of how many times a specific reply is received. e.g. how many times did we receive the reply "rates = ..."
        /// </summary>
        public Dictionary<string, uint> dictionary_RepliesCounts = new Dictionary<string, uint>();

        /// <summary>
        /// Keeps track of how many times a specific command was sent. e.g. how many times did we send the command "rates"
        /// </summary>
        public Dictionary<string, uint> dictionary_CommandsCounts = new Dictionary<string, uint>();

        /// <summary>
        /// <para>This correlates the sent message with the expected response.</para>
        /// <para>e.g. Send&gt; "Moments"; Receive "FeynmanMoments = values".</para>
        /// <para>This is only for commands that do not have any sent parameters.</para>
        /// </summary>
        public Dictionary<string, ExpectedReply> dictionary_CommandReply = new Dictionary<string, ExpectedReply>();

        ///// <summary>
        ///// <para>This correlates the sent message with the expected response.</para>
        ///// <para>e.g. Send&gt; "Moments = value"; Receive "FeynmanMoments = values".</para>
        ///// <para>This is for commands with parameters.</para>
        ///// </summary>
        //public Dictionary<string, string[]> dictionary_CommandReply_WithParameters = new Dictionary<string, string[]>();

        /// <summary>
        /// This correlates what member function should be called when an async message was received.
        /// </summary>
        public Dictionary<string, MethodDelegate> dictionary_DelegateReceive = new Dictionary<string, MethodDelegate>();
        /// <summary>
        /// This correlates what member function should be called when a message is sent.
        /// </summary>
        public Dictionary<string, MethodDelegate> dictionary_DelegateSend = new Dictionary<string, MethodDelegate>();

        /// <summary>
        /// The swConfig file. This should be read in initially and then rewritten when a modifiable element is changed.
        /// </summary>
        //public Dictionary<string, string[]> swConfigDictionary;

        /// <summary>
        /// Holds a list of the delegates that should be performed for each entry in swConfig.txt
        /// </summary>
        public Dictionary<string, MethodDelegate> dictionary_SwConfig = new Dictionary<string, MethodDelegate>();

        /// <summary>
        /// The hwconfigDictinary only needs to be read in once.
        /// </summary>
        //public Dictionary<string, string[]> hwConfigDictionary;

        /// <summary>
        /// Holds a list of the delegates that should be performed for each entry in hsConfig.txt
        /// </summary>
        public Dictionary<string, MethodDelegate> dictionary_HwConfig = new Dictionary<string, MethodDelegate>();

        /// <summary>
        /// Keeps track of the last command sent for each command.
        /// e.g. if Moments = 1 is sent, the KeyValuePair is &lt;Moments&gt; &lt;Moments, 1&gt;
        /// if Moments = 2 is then sent, the KeyValuePair is replaced with &lt;Moments&gt; &lt;Moments, 2&gt;
        /// </summary>
        public Dictionary<string, string[]> lastCommandSent = new Dictionary<string, string[]>();

        /// <summary>
        /// If there is any bookkeeping that needs to be done when a command is sent, store it here. 
        /// These commands are sent before Write
        /// Don't make this to big.
        /// </summary>
        public Dictionary<string, MethodDelegate> dictionary_ProcessLastCommandSent_PreSend = new Dictionary<string, MethodDelegate>();


        /// <summary>
        /// If there is any bookkeeping that needs to be done when a command is sent, store it here. 
        /// These commands are sent after Write
        /// Don't make this to big.
        /// </summary>
        public Dictionary<string, MethodDelegate> dictionary_ProcessLastCommandSent_PostSend = new Dictionary<string, MethodDelegate>();

        #region Properties
        /// <summary>
        /// Returns the Ipv4Address as a string. e.g. 169.254.30.30.
        /// </summary>
        public string IpAddress
        {
            get
            {
                string result = ipv4Address[0];
                for (int i = 1; i < 4; ++i)
                {
                    result += "." + ipv4Address[i];
                }
                return result;
            }

            set
            {
                string[] str = value.Split((char)'.');
                if (str.Length != 4)
                {
                    throw new Exception("IpAddress must have 4 items.");
                }
                for (int i = 0; i < 3; ++i)
                {
                    ipv4Address[i] = str[i];
                }
                string[] strPort = str[3].Split((char)':');
                ipv4Address[3] = strPort[0];
                if (strPort.Length > 1)
                {
                    ipv4Address_Port = strPort[1];
                }
            }
        }

        public string IpAddressAndPort
        {
            get
            {
                return IpAddress + ":" + ipv4Address[4];
            }
            set
            {
                IpAddress = value;
            }
        }

        public int HashCode
        {
            get { return hashCode; }
        }
        #endregion Properties

        #region Events
        /// 
        /// Events associated with error and debugging messages
        /// 
        public EventHandler<EventArgs> EventMessagesHaveChanged;
        public EventHandler<EventArgs> EventMessagesSentHasChanged;
        public EventHandler<EventArgs> EventMessagesReceivedHasChanged;
        public EventHandler<EventArgs> EventMessagesExceptionsHaveChanged;
        public EventHandler<EventArgs> EventMessagesUnrecognizedHaveChanged;
        public EventHandler<EventArgs> EventApplicationExit;

        /// 
        /// Events associated with general changes
        /// 
        public EventHandler<EventArgs> EventInstrumentStateHasChanged;
        public EventHandler<EventArgs> EventSentGo_Presend;
        /// 
        /// Events associated with replies received from lmcControl
        /// 
        public EventHandler<EventArgs> EventActive;
        public EventHandler<EventArgs> EventAssayCancelled;
        public EventHandler<EventArgs> EventAssayDone;
        public EventHandler<EventArgs> EventAssayError;
        public EventHandler<EventArgs> EventAssayPaused;
        public EventHandler<EventArgs> EventAssayPausedError;
        public EventHandler<EventArgs> EventAssayResumed;
        public EventHandler<EventArgs> EventAssayResumedError;
        public EventHandler<EventArgs> EventBinaryDataFollows;
        public EventHandler<EventArgs> EventCancel;
        public EventHandler<EventArgs> EventCancelError;
        public EventHandler<EventArgs> EventCables;
        public EventHandler<EventArgs> EventDetLoca;
        public EventHandler<EventArgs> EventDieTemp;
        public EventHandler<EventArgs> EventDistPriToFlr;
        public EventHandler<EventArgs> EventDistPriToSrc;
        public EventHandler<EventArgs> EventDistSecToFlr;
        public EventHandler<EventArgs> EventDistSecToSrc;
        public EventHandler<EventArgs> EventDone;
        public EventHandler<EventArgs> EventDrawNum;
        public EventHandler<EventArgs> EventDuration;
        public EventHandler<EventArgs> EventFeynman;
        public EventHandler<EventArgs> EventFeynmanMoments;
        public EventHandler<EventArgs> EventFeynmanParams;
        public EventHandler<EventArgs> EventFeynmanResults;
        public EventHandler<EventArgs> EventHVcalib;
        public EventHandler<EventArgs> EventHVread;
        public EventHandler<EventArgs> EventHVset;
        public EventHandler<EventArgs> EventLFSlist;    // Call this when the file list has changed.
        public EventHandler<EventArgs> EventLFSstatus;  // LFSstatus = <percent used>, <file count>
        public EventHandler<EventArgs> EventListModeDataFileVersion;
        public EventHandler<EventArgs> EventMeasType;
        public EventHandler<EventArgs> EventNPOD2;
        public EventHandler<EventArgs> EventPower;
        public EventHandler<EventArgs> EventRates;
        public EventHandler<EventArgs> EventSm2Ib;
        public EventHandler<EventArgs> EventStorage;
        public EventHandler<EventArgs> EventTime;
        public EventHandler<EventArgs> EventTotals;
        public EventHandler<EventArgs> EventUSBsaveFile;
        public EventHandler<EventArgs> EventUSBsaveFileCancel;  // Invoked when USBsaveFileCancel is received
        public EventHandler<EventArgs> EventUSBsaveFileProgress;    // USBsaveFileProgress = <percent Complete>, <current filename>
        public EventHandler<EventArgs> EventUserMode;
        public EventHandler<EventArgs> EventVersion;
        public EventHandler<EventArgs> EventAnalysisMask;
        public EventHandler<EventArgs> EventAudioBeat;
        public EventHandler<EventArgs> EventAudioEnable;
        public EventHandler<EventArgs> EventAudioRate;
        public EventHandler<EventArgs> EventAudioState;
        public EventHandler<EventArgs> EventAudioTone;
        public EventHandler<EventArgs> EventChannelMask;
        public EventHandler<EventArgs> EventCStatus;
        public EventHandler<EventArgs> EventDeadTime;
        public EventHandler<EventArgs> EventDmaTimeInterval;
        public EventHandler<EventArgs> EventIpAddr;
        public EventHandler<EventArgs> EventLcdTemp;
        public EventHandler<EventArgs> EventMacAddr;
        public EventHandler<EventArgs> EventMaxSamplesInFile;
        public EventHandler<EventArgs> EventNetworkState;
        public EventHandler<EventArgs> EventReps;
        public EventHandler<EventArgs> EventRunDescription;
        public EventHandler<EventArgs> EventSampleLossCount;
        public EventHandler<EventArgs> EventStartUpComplete;    // startUpComplete
        public EventHandler<EventArgs> EventUnrecognizedCommand;
        public EventHandler<EventArgs> EventUpdateSystem;
        public EventHandler<EventArgs> EventVeto;
        public EventHandler<EventArgs> EventVetoGateWidth;
        public EventHandler<EventArgs> EventVerbose;
        public EventHandler<EventArgs> EventVetoTrue;

        ///
        /// Events associated with reading in swConfig.txt
        /// 
        public EventHandler<EventArgs> EventSwAudible;
        public EventHandler<EventArgs> EventSwAutoRunCounts;
        public EventHandler<EventArgs> EventSwAutoRunDuration;
        public EventHandler<EventArgs> EventSwBackgroundEnable;
        public EventHandler<EventArgs> EventSwBgRate;
        public EventHandler<EventArgs> EventSwBgRateDefault;
        public EventHandler<EventArgs> EventSwBgYm;
        public EventHandler<EventArgs> EventSwBgYmDefault;
        public EventHandler<EventArgs> EventSwBkgCounts;
        public EventHandler<EventArgs> EventSwBkgDuration;
        public EventHandler<EventArgs> EventSwCollectionTime;
        public EventHandler<EventArgs> EventSwDeadtime;
        public EventHandler<EventArgs> EventSwDebugEnable;
        public EventHandler<EventArgs> EventSwDescription;
        public EventHandler<EventArgs> EventSwDevice;
        public EventHandler<EventArgs> EventSwDistance2floor;
        public EventHandler<EventArgs> EventSwDistance2object;
        public EventHandler<EventArgs> EventSwDuration;
        public EventHandler<EventArgs> EventSwExit;
        public EventHandler<EventArgs> EventSwHvRdConv;
        public EventHandler<EventArgs> EventSwHvSet;
        public EventHandler<EventArgs> EventSwHvWrConv;
        public EventHandler<EventArgs> EventSwIpcCounts;
        public EventHandler<EventArgs> EventSwIpcDuration;
        public EventHandler<EventArgs> EventSwLinuxDebug;
        public EventHandler<EventArgs> EventSwMaxFileThreshold;
        public EventHandler<EventArgs> EventSwMinMemThreshold;
        public EventHandler<EventArgs> EventSwNoUserPowerDownTime;
        public EventHandler<EventArgs> EventSwPowerDownAlertThreshold;
        public EventHandler<EventArgs> EventSwPowerDownThreshold;
        public EventHandler<EventArgs> EventSwQdrCounts;
        public EventHandler<EventArgs> EventSwQdrDuration;
        public EventHandler<EventArgs> EventSwRepetitions;
        public EventHandler<EventArgs> EventSwStorage;
        public EventHandler<EventArgs> EventSwThreatEnable;
        public EventHandler<EventArgs> EventSwUnits;
        public EventHandler<EventArgs> EventSwUseVeto;
        public EventHandler<EventArgs> EventSwVetoDuration;
        public EventHandler<EventArgs> EventSwVetoTrueState;

        /// 
        /// Events associated with reading in hwConfig.txt
        /// 
        public EventHandler<EventArgs> EventHwHardwareVersion;
        public EventHandler<EventArgs> EventHwSerialNumber;

        ///
        /// Events associated with rebooting
        /// 
        public EventHandler<EventArgs> EventTimeRebootingUpdated;

        /// 
        /// Events not associated with direct commands sent to the Avnet card.
        /// 
        public EventHandler<EventArgs> EventPing;

        #endregion

#pragma warning disable CS8618
        public Detector()
        {
            InitializeComponent();

            hashCode = GetHashCode();

            Version? version = typeof(Detector).Assembly.GetName().Version;
            if (version == null)
            {
                userGuiVersion = "<Unknown>";
                dateTimeBuildDate = new DateTime();
            }
            else
            {
                //lcdVersion = version.ToString();
                userGuiVersion = version.ToString();
                dateTimeBuildDate = new DateTime(2000, 1, 1).AddDays(version.Build).AddSeconds(version.Revision * 2);
            }

            // ReadConfigFiles(); Do this in Vf61Gui
        }
#pragma warning restore CS8618

        public void Dispose()
        {
            /// <summary>
            Dispose(true);
            // This object will be cleaned up by the Dispose method.
            // Therefore, you should call GC.SuppressFinalize to
            // take this object off the finalization queue
            // and prevent finalization code for this object
            // from executing a second time.
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            /// <summary>
            /// 
            /// </summary>
            /// <remarks>
            /// Dispose(bool disposing) executes in two distinct scenarios.
            /// If disposing equals true, the method has been called directly
            /// or indirectly by a user's code. Managed and unmanaged resources
            /// can be disposed.
            /// If disposing equals false, the method has been called by the
            /// runtime from inside the finalizer and you should not reference
            /// other objects. Only unmanaged resources can be disposed.
            /// </remarks>
            /// <param name="disposing"></param>
            /// 

#pragma warning disable CS8625
            if (manualResetEventWriteAndWait != null)
            {
                manualResetEventWriteAndWait.Dispose();
                manualResetEventWriteAndWait = null;
            }
            if (autoResetEventBoot != null)
            {
                autoResetEventBoot.Dispose();
                autoResetEventBoot = null;
            }
            if (timerBoot != null)
            {
                timerBoot.Dispose();
                timerBoot = null;
            }
#pragma warning restore CS8625

            Close();

            // Check to see if Dispose has already been called.
            if (!this.disposed)
            {
                // If disposing equals true, dispose all managed
                // and unmanaged resources.
                if (disposing)
                {
                    // Dispose managed resources.
                    // components.Dispose();
                }

                // Call the appropriate methods to clean up
                // unmanaged resources here.
                // If disposing is false,
                // only the following code is executed.
                //CloseHandle(handle);
                //handle = IntPtr.Zero;

                // Note disposing has been done.
                disposed = true;
            }
        }


        /// <summary>
        /// Closes the current connections to the Avnet card.
        /// Call this if you want to reset the connection but not get rid of the current detector.
        /// </summary>
        public void Close()
        {
            // Disable the timer to prevent null exceptions from being thrown

#pragma warning disable CS8625
            if (networkStream != null)
            {
                byte[] buffer = Encoding.ASCII.GetBytes("Cancel" + "\r\n");
                networkStream.Write(buffer, 0, buffer.Length);
                networkStream.Close();
                networkStream.Dispose();
                networkStream = null;
            }

            if (tcpClient != null)
            {
                tcpClient.Close();
                tcpClient.Close();
                tcpClient = null;
            }

            if (serialPort != null)
            {
                byte[] buffer = Encoding.ASCII.GetBytes("Cancel");
                serialPort.Write(buffer, 0, buffer.Length);
                serialPort.Close();
                serialPort.Dispose();
                serialPort = null;
            }
#pragma warning restore CS8625

            instrumentState = InstrumentState.OFFLINE;
        }

        public void IpInitializeTcp()
        {
            // default MC-15 ipaddress is = 169.254.30.30
            // port is hardcoded for all MC-15s to 5011
            try
            {
                // Close previous connections if they exist.
                Close();
                Ping();
                if (pingReply?.Status != IPStatus.Success)
                {
                    instrumentState = InstrumentState.OFFLINE;
                    return;
                }
                string ip = IpAddress;
                int port = 5011;
                if (!int.TryParse(ipv4Address_Port, out port))
                {
                    port = 5011;
                }
                tcpClient = new TcpClient()
                {
                    NoDelay = true
                };
                tcpClient.Connect(ip, port);
                //tcpClient.ConnectAsync(ip, port);
                networkStream = tcpClient.GetStream();
                instrumentState = InstrumentState.ONLINE;
            }
            catch (Exception ex)
            {
                instrumentState = InstrumentState.OFFLINE;
                string errorMessage = "TcpIpInitialize -> " + ex.Message;
                MessageBox.Show(errorMessage);
                EnqueueMessage(errorMessage, WhichMessage.Exception);
            }
            finally
            {
            }
            if (tcpClient == null)
            {
                MessageBox.Show("tcpClient == null");
            }
            else
            {
                if (!tcpClient.Client.Connected)
                {
                    MessageBox.Show("tcpClient is not connected");
                }
            }
        }

        public void IpInitializeTcp(string ip)
        {
            // default MC-15 ipaddress is = 169.254.30.30
            // port is hardcoded for all MC-15s to 5011
            IpAddress = ip;
            IpInitializeTcp();
        }

        public void IpInitializeTcp(string ip, int port)
        {
            IpAddress = ip;
            if (port.InRangeII(0, 65535))
            {
                ipv4Address[4] = port.ToString();
            }
            else
            {
                // Invalid port
                throw new Exception("Invalid port");
            }
            IpInitializeTcp();
        }

        public void InitializeSerialPort()
        {
            InitializeSerialPort("Com2");
        }

        public void InitializeSerialPort(string comPort)
        {
            InitializeSerialPort(comPort, 115200, Parity.None, 8, StopBits.One);
        }

        public void InitializeSerialPort(string comPort, int baudRate, Parity parity, int dataBits, StopBits stopBits)
        {
            try
            {
                // When a serial input occurs serialPortDataReceived is invoked.
                serialPort = new SerialPort(comPort, 115200, Parity.None, 8, StopBits.One);

                serialPort.DataReceived += new SerialDataReceivedEventHandler(SerialPort_DataReceived);
                serialPort.ErrorReceived += new SerialErrorReceivedEventHandler(SerialPort_ErrorReceived);
                serialPort.PinChanged += new SerialPinChangedEventHandler(SerialPort_PinChanged);

                serialPort.NewLine = "\n";
                serialPort.Open();
                instrumentState = InstrumentState.ONLINE;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Detector.InitializeSerialPort -> " + ex.Message);
                if (serialPort != null)
                {
                    // WinCE does not have the null coalescing operator ?.
                    // Do not replace this with the null coalescing operator
                    // because I want to be able to move this back to Vf61Gui_2023 with minimal changes.
                    serialPort.Dispose();
                    serialPort = null;
                }
                System.Windows.Forms.Application.Exit();

                if (EventApplicationExit != null)
                {
                    // WinCE does not have the null coalescing operator ?.
                    // Do not replace this with the null coalescing operator
                    // because I want to be able to move this back to Vf61Gui_2023 with minimal changes.
                    EventApplicationExit.Invoke(this, new EventArgs());
                    EventApplicationExit = null;
                }
                instrumentState = InstrumentState.OFFLINE;
            }
        }

        //called when an event happens on the serial port
        private void SerialPort_DataReceived(object sender, EventArgs e)
        {
            byte[] bufferRead = new byte[serialPort.ReadBufferSize];
            int bytesRead = 0;
            try
            {
                if (serialPort == null)
                {
                    return;
                };
                while (serialPort.BytesToRead > 0)
                {
                    bytesRead = serialPort.Read(bufferRead, 0, serialPort.ReadBufferSize);
                    ProcessReply(bufferRead, bytesRead);
                    // https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.control.invoke?view=netframework-3.5#system-windows-forms-control-invoke(system-delegate)
                }
            }
            catch (Exception ex)
            {
                string errorMessage = "Detector.SerialPortDataReceived(object, EventArgs) --> " + ex.Message;
                errorMessage += " Command Received => ";
                string str = Encoding.ASCII.GetString(bufferRead);
                if (str == null)
                {
                    errorMessage += "<null>";
                }
                else
                {
                    errorMessage += str;
                }
#if DEBUG
                MessageBox.Show(errorMessage);
#endif
                EnqueueMessage(errorMessage, WhichMessage.Exception);
            }
        }

        private void SerialPort_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            MessageBox.Show("SerialPort.ErrorReceived => " + e.EventType.ToString());
        }

        private void SerialPort_PinChanged(object sender, SerialPinChangedEventArgs e)
        {
            MessageBox.Show("SerialPort.PinChanged => " + e.EventType.ToString());
        }

        /// <summary>
        /// When data is received over the Tcp/IP socket.
        /// Put this into a background thread.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public void TcpIp_DataReceived()
        {

            try
            {
                int bytesRead;
                byte[] bufferRead = new byte[tcpClient.ReceiveBufferSize];
                while ((tcpClient != null) && (networkStream != null) && (tcpClient.Connected))
                {
                    //bytesRead = await networkStream.ReadAsync(bufferRead, 0, bufferRead.Length);
                    bytesRead = networkStream.Read(bufferRead, 0, bufferRead.Length);
                    //bytesRead = (int)networkStream.ReadAsync(bufferRead, 0, bufferRead.Length).Result;
                    ProcessReply(bufferRead, bytesRead);
                    
                }
            }
            catch (Exception)
            {
                // When Detector closes networkStream is set to null. This causes an exception to be thrown.
            }
        }

        public void ProcessReply(byte[] bufferRead, int bytesRead)
        {
            lock (processReplyLock)
            {
                // MANelson 1/16/2025
                // I'm going to assume for now that reading from the SerialPort will 
                // always flush the buffer.
                // If the buffer is always flushed then the following should work.
                // Note: The LCD communicates via SerialPort.
                //
#if DEBUG
                string debug_string = Encoding.ASCII.GetString(bufferRead, 0, bytesRead);
#endif
                if (bytesRead > 0)
                {
                    if (networkStream == null)
                    {
                        // ***** SerialPort Communication *****
                        // Asynchronous callback when the socket receives a transmission.
                        // Don't spend a lot of time in here, as you will lose data at high rates.
                        // Information from the instrument is transmitted over the net in 2 ways.
                        // Even number of bytes, or odd.
                        // All you have to do is check using modulo, then act on it one way or another.
                        if (bytesRead % 8 == 0)
                        {
                            // EVEN NUMBER OF BYTES
                            //   This is binary data.
                            //
                            // Comment by MANelson 1/10/25
                            // It is not always true that an even number of bytes will be binary data.
                            // TcpClient may send responses back to back
                            // e.g. a response may be
                            // NetSaveFile = 2024_11_21_183227,3632,Time = Fri Jan 10 15:42:55 2025
                            // and the length is 70 (even).
                            // This is because TcpClient doesn't necessarily flush the buffer after each read.
                            // Changed this to modulo 8 because each event is 8 bytes long.

                            // There needs to be one more check to see if there is an equals in the early parts of the 
                            ProcessAsBinary(bufferRead, bytesRead);
                        }
                        else
                        {
                            // ODD NUMBER OF BYTES
                            //    This is a message/status/header/NOT DATA.
                            ProcessAsMessage(bufferRead, bytesRead);
                        }
                    }
                    else
                    {
                        // ***** TCP/IP Communication *****
                        // Asynchronous callback when the socket receives a transmission.
                        // Don't spend a lot of time in here, as you will lose data at high rates.
                        // Information from the instrument is transmitted over the net in 2 ways.
                        // Even number of bytes, or odd.
                        // All you have to do is check using modulo, then act on it one way or another.
                        if (bytesRead % 8 == 0)
                        {
                            // EVEN NUMBER OF BYTES
                            //   This is binary data.
                            //
                            // Comment by MANelson 1/10/25
                            // It is not always true that an even number of bytes will be binary data.
                            // TcpClient may send responses back to back
                            // e.g. a response may be
                            // NetSaveFile = 2024_11_21_183227,3632,Time = Fri Jan 10 15:42:55 2025
                            // and the length is 70 (even).
                            // This is because TcpClient doesn't necessarily flush the buffer after each read.
                            // Changed this to modulo 8 because each event is 8 bytes long.

                            // There needs to be one more check to see if there is an equals in the early parts of the 
                            ProcessAsBinary(bufferRead, bytesRead);


                            //byte[][] messages = bufferRead.GetCommands(bytesRead);
                            //if (messages.Length < 1)
                            //{
                            //    ProcessAsBinary(bufferRead, bytesRead);
                            //}
                            //else
                            //{
                            //    foreach (byte[] message in messages)
                            //    {
                            //        ProcessAsMessage(message, bytesRead);
                            //    }
                            //}
                        }
                        else
                        {
                            // ODD NUMBER OF BYTES
                            //    This is a message/status/header/NOT DATA.
                            ProcessAsMessage(bufferRead, bytesRead);

                            //byte[][] messages = bufferRead.GetCommands(bytesRead);
                            //foreach (byte[] message in messages)
                            //{
                            //    ProcessAsMessage(message, bytesRead);
                            //}
                        }
                    }
                }
            }
        }

        private void ProcessAsMessages(byte[][] messages)
        {
            for (int i = 0; i < messages.Length; ++i)
            {

            }
        }

        private void SetLmxHeader(string headerText)
        {
            if (string.IsNullOrEmpty(headerText))
            {
                return;
            }

            lmxFileheader = headerText;
            lmxHeaderWritten = false;
            TryWriteLmxHeaderToFile();
        }

        private void TryWriteLmxHeaderToFile()
        {
            if (binaryWriter == null || string.IsNullOrEmpty(lmxFileheader) || lmxHeaderWritten)
            {
                return;
            }

            byte[] headerBytes = Encoding.ASCII.GetBytes(lmxFileheader);
            FileStream fileStream = binaryWriter.BaseStream as FileStream;
            if (fileStream == null)
            {
                return;
            }

            if (fileStream.Length == 0)
            {
                binaryWriter.Write(headerBytes, 0, headerBytes.Length);
            }
            else
            {
                string tempPath = Path.GetTempFileName();

                // Flush existing data so we can rebuild the file with the header prefix
                binaryWriter.Flush();
                fileStream.Position = 0;

                using (FileStream tempStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    tempStream.Write(headerBytes, 0, headerBytes.Length);
                    fileStream.CopyTo(tempStream);
                }

                string destinationPath = string.IsNullOrEmpty(currentNetLmxPath) && fileStream.Name != null
                    ? fileStream.Name
                    : currentNetLmxPath;

                binaryWriter.Close();
                binaryWriter.Dispose();

                File.Copy(tempPath, destinationPath, true);
                File.Delete(tempPath);

                binaryWriter = new BinaryWriter(File.Open(destinationPath, FileMode.Open, FileAccess.Write, FileShare.Read));
                binaryWriter.BaseStream.Position = binaryWriter.BaseStream.Length;
            }

            lmxHeaderWritten = true;
        }
        /// <summary>
        /// Process the incoming byte[] as a text response from the TCP/IP socket or the serial port.
        /// </summary>
        /// <param name="bufferRead"></param>
        /// <param name="bytesRead"></param>
        private void ProcessAsMessage(byte[] bufferRead, int bytesRead)
        {
            int startOfCopyDone = bufferRead.IndexOfPattern(copyDonePattern, 0, bytesRead);
            if (CheckState(InstrumentState.COPYING_NET))
            {
                // Currently we are copying data from the MC-15 to the local computer using NetSaveFile
                // The end of the stream will end with "CopyDone\n"
                // This makes this bufferRead an odd number.
                // Remove the CopyDone\n from the buffer
                if (binaryWriter != null)
                {
                    if (startOfCopyDone > -1)
                    {
                        // We found the CopyDone\n string.
                        int size = Math.Max(0, startOfCopyDone);  // Remove "CopyDone\n"
                        binaryWriter.Write(bufferRead, 0, size);
                        binaryWriter.Close();
                        binaryWriter.Dispose();
                        binaryWriter = null;
                    }
                }
                // Regardless of the state of binaryWriter, I want to reset everything.
                ClearState(InstrumentState.COPYING_NET);
                resetEventNetSaveFile.Set();   // Signal that the file transfer is complete

                int endOfBinaryData = bytesRead - startOfCopyDone - copyDonePattern.Length;
                if (endOfBinaryData < 1)
                {
                    // There's no additional data tacked on the end.
                    // If there are additional commands then we may have a problem.
                    return;
                }

                // Move the data up in bufferRead
                bytesRead = bytesRead - endOfBinaryData;
                if (bytesRead < 1)
                {
                    // A double check
                    // No additional data is present.
                    return;
                }
                Buffer.BlockCopy(bufferRead, endOfBinaryData, bufferRead, 0, bytesRead);
                string test = Encoding.ASCII.GetString(bufferRead);
                return;
            }

            string str = Encoding.ASCII.GetString(bufferRead, 0, bytesRead);
            //https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.control.invoke?view=netframework-3.5#system-windows-forms-control-invoke(system-delegate)

            //string[] replies = str.SplitToValues();
            string[] replies = str.Split(new char[] { (char)'=', (char)',' }).Select(a => a.Trim(new char[] { '\n', '\0', ' ', '\t' })).Where(a => !string.IsNullOrEmpty(a)).ToArray();

            if (replies.Length == 0)
            {
                // Not necessarily true.
                return;
            }

            lastReply = replies[0];
            int index = 0;
            // Check to see if there is anything that needs to be done with the data.
            // e.g. was the reply Rates = 0,0,.....?
            //      if so, we need to do something with the data.
            if (dictionary_DelegateReceive.ContainsKey(lastReply))
            {
                // There is a delegate available to process the data.
                // Find out which delegate to use and process the data
                MethodDelegate methodDelegate = dictionary_DelegateReceive[lastReply];
                if (methodDelegate != null)
                {
                    // Update the tallies on what replies are seen.
                    if (dictionary_RepliesCounts.ContainsKey(lastReply))
                    {
                        dictionary_RepliesCounts[lastReply] = dictionary_RepliesCounts[lastReply] + 1;
                    }
                    else
                    {
                        // Need to create the entry into the dictionary.
                        dictionary_RepliesCounts[lastReply] = 1;
                    }
                    // Process the reply
                    dictionary_DelegateReceive[lastReply](replies);
                }
                if (expectedReply == null)
                {
                    // If there is no expected reply, move on in WriteAndWait if need be.
                    manualResetEventWriteAndWait.Set();
                    return;
                }
                else
                {
                    if (expectedReply.replies[0].Equals(lastReply))
                    {
                        // Cool, we got the reply we've been waiting on so move on in WriteAndWait.
                        manualResetEventWriteAndWait.Set();
                    }
                }
                if (!str.StartsWith("Rates"))
                    EnqueueMessage(str, WhichMessage.MessagesReceived);

            }
            else if ((index = str.IndexOf("ListModeDataFileVersion")) > -1)
            {
                // Check to see if this is a header.
                //int index = str.IndexOf("ListModeDataFileVersion");
                if (index == 0)
                {
                    EnqueueMessage("<Start of NetFileSave>", WhichMessage.MessagesReceived);
                    // ****** if index == 0 and the file starts with ListModeDataFileVersion there could be two options. ******
                    // 1) The file is being sent via NetSaveFile and there's not a lot of data
                    //      In this case the binaryWriter == null
                    //      Check if the array ends in "CopyDone\n" to see if it's a legit file.
                    // 2) The file is being streamed from the unit and it just so happens that the header starts here.
                    //      In this case binaryWriter != null

                    // Case 1) NetSaveFile
                    if (binaryWriter == null)
                    {
                        int i = Math.Max(0, bytesRead - 9);
                        string ending = Encoding.ASCII.GetString(bufferRead, i, 9);
                        if (ending.Contains("CopyDone"))
                        {
                            OpenNetLmxFile(bufferRead, bytesRead);
                            binaryWriter.Write(bufferRead, 0, bytesRead - i);
                            binaryWriter.Close();
                            binaryWriter.Dispose();
                            binaryWriter = null;
                            resetEventNetSaveFile.Set();   // Signal that the file transfer is complete
                            ClearState(InstrumentState.COPYING_NET);
                            return;
                        }

                        SetLmxHeader(Encoding.ASCII.GetString(bufferRead, 0, bytesRead));
                    }
                    else // Case 2) Local File Storage
                    {
                        // binaryWriter != null
                        // The Fileheader comes after the data is streamed.
                        SetLmxHeader(Encoding.ASCII.GetString(bufferRead, index, bytesRead));
                    }
                }
                else if (index > 0)
                {
                    EnqueueMessage("<index > 0>", WhichMessage.MessagesReceived);
                    //

                    // This is the header from the lmxFile
                    SetLmxHeader(Encoding.ASCII.GetString(bufferRead, index, bytesRead));
                    // write the last message to the file.
                    // This might be a partial data + last message
                    // Before we started writing data to the file we set aside an area at the TOP
                    // of the file to right the header.  Back up to the beginning of the file to write header.

                }
                //else if ((lastCommand == @"Mother") && (dictionary_CommandsCounts["Mother"]))
                //{
                //    // Mother is a unique situation.
                //    // The MC-15 is looking for the first response to be "Mother"
                //    // The return string contains information on the type of Avnet card, serial number, etc.
                //    // Do this last because it only happens once.
                //    EnqueueMessage(str, WhichMessage.MessagesReceived);
                //    manualResetEventWriteAndWait.Set();
                //    return;
                //}
                else
                {
                    // You could maybe do something here with the result from Mother.?


                    // Uknown reply.
                    EnqueueMessage(str, WhichMessage.Unknown);
                }
            }
            // Release the manualResetEvent if there's no expected replies.
            if (expectedReply == null)
            {
                manualResetEventWriteAndWait.Set();
            }

        }

        /// <summary>
        /// Process the incoming byte[] as a text response from the TCP/IP socket or the serial port.
        /// </summary>
        /// <param name="bufferRead"></param>
        /// <param name="bytesRead"></param>
        private void ProcessAsBinary(byte[] bufferRead, int bytesRead)
        {
#if DEBUG
            string str = Encoding.ASCII.GetString(bufferRead, 0, bytesRead);
#endif
            if (binaryWriter == null)
            {
                OpenNetLmxFile(bufferRead, bytesRead);
            }
            TryWriteLmxHeaderToFile();
            binaryWriter.Write(bufferRead, 0, bytesRead);
            bytesTransferred += (uint)bytesRead;

            // --- NEW: decode each 8-byte record and forward to adaptive engine ---
            for (int offset = 0; offset < bytesRead; offset += 8)
            {
                ulong word = BitConverter.ToUInt64(bufferRead, offset);

                // Decode channel ID (lowest 5 bits)
                byte channelId = (byte)(word & 0x1F);

                // Decode timestamp (upper 59 bits, 10 ns ticks)
                long ticks10ns = (long)(word >> 5);

                // Convert to microseconds
                long timestampUs = ticks10ns / 100;  // 100 × 10 ns = 1 µs

                // Debug: print first few events
                if (offset < 80) // first 10 events
                {
                    System.Diagnostics.Debug.WriteLine($"DEBUG hit: ch={channelId}, tsUs={timestampUs}");

                }

                Adaptive?.OnDetection(new Listen_N.Detection(timestampUs, channelId));
            }
        }


        /// <summary>
        /// Opens an lmx file to get ready to write an lmx file from the MC-15.
        /// </summary>
        /// <param name="bufferRead"></param>
        /// <param name="bytesRead"></param>
        public void OpenNetLmxFile(byte[] bufferRead, int bytesRead)
        {
            // There is no binaryWriter open.
            // This means that NetSaveFile was sent and the MC-15 is returning a file 
            //  to be saved.
            // If you are streaming data from the MC-15 then binaryWriter will be opened when
            //  the command Go is sent and storageLocation == InstrumentStorageLocation.NET
            SetState(InstrumentState.COPYING_NET);
            bytesTransferred = 0;
            string path = Path.Combine(new string[] { netSaveFilepath, IpAddress.Replace('.', '_'), netSaveFileName + ".lmx" });
            currentNetLmxPath = path;
            if (string.IsNullOrEmpty(lmxFileheader))
            {
                lmxHeaderWritten = false;
                lmxFileheader = Encoding.ASCII.GetString(bufferRead, 0, bytesRead);
                int length = lmxFileheader.IndexOf("BinaryDataFollows");
                if (length > 0)
                {
                    lmxFileheader = lmxFileheader.Substring(0, length);
                }
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                binaryWriter = new BinaryWriter(File.Open(path, FileMode.Create));
                TryWriteLmxHeaderToFile();
                //resetEventFileTransfer.Reset();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "ProcesAsBinary");
                binaryWriter = null;
                resetEventNetSaveFile.Set();   // Signal that the file transfer is complete
            }
        }

        /// <summary>
        /// Sends the command via TCP/IP to the unit.
        /// </summary>
        /// <param name="command"></param>
        /// <returns></returns>
        public WriteError Write(string command)
        {
            try
            {
                //string[] commands = command.SplitToValues();
                string[] commands = command.Split((char)'=').Select(a => a.Trim(new char[] { '\n', '\0', ' ', '\t' })).Where(a => !string.IsNullOrEmpty(a)).ToArray();

                if (commands.Length == 0)
                {
                    this.writeError = WriteError.NoCommandsGiven;
                    return this.writeError;
                }
                lastCommand = commands[0];
                lastCommandSent[lastCommand] = commands;

                // Check to see if there is anything that needs to be done before a command is sent.
                if (dictionary_ProcessLastCommandSent_PreSend.ContainsKey(lastCommand))
                {
                    dictionary_ProcessLastCommandSent_PreSend[lastCommand](commands);
                }

                // Update how many commands of each type were sent.
                if (dictionary_CommandsCounts.ContainsKey(lastCommand))
                {
                    dictionary_CommandsCounts[lastCommand] = dictionary_CommandsCounts[lastCommand] + 1;
                }
                else
                {
                    dictionary_CommandsCounts[lastCommand] = 1;
                }

                // Send the command
                if (serialPort != null)
                {
                    // Writing via SerialPort
                    serialPort.WriteLine(command);
                    this.writeError = WriteError.WroteSerialPort;
                }
                else if (networkStream != null)
                {
                    // Writing via TCP/IP
                    byte[] buffer = Encoding.ASCII.GetBytes(command + "\r\n");
                    networkStream.Write(buffer, 0, buffer.Length);
                    this.writeError = WriteError.WroteTcpIp;
                }
                else
                {
                    string errorMessage = "Detector.Write(string) --> No ports are open.";
                    EnqueueMessage(errorMessage, WhichMessage.Exception);
                    MessageBox.Show(errorMessage);
                    this.writeError = WriteError.NoPortsAreOpen;
                }

                // Check to see if there is anything that needs to occur after you send a command.
                if (dictionary_ProcessLastCommandSent_PostSend.ContainsKey(lastCommand))
                {
                    dictionary_ProcessLastCommandSent_PostSend[lastCommand](commands);
                }
                if (!command.StartsWith("Rates"))
                    EnqueueMessage(command, WhichMessage.MessagesSent);

                return this.writeError;
            }
            catch (Exception ex)
            {
                string errorMessage = "Detector.Write(string) --> " + ex.Message;
                errorMessage += "Command Sent = ";
                if (command == null)
                {
                    errorMessage += "<null>";
                }
                else
                {
                    errorMessage += command;
                }
                EnqueueMessage(errorMessage, WhichMessage.Exception);
                MessageBox.Show(errorMessage);
                return this.writeError = WriteError.ExceptionThrown;
            }
        }

        /// <summary>
        /// Sends the command straight to the detector.
        /// </summary>
        /// <param name="command"></param>
        /// <returns></returns>
        public WriteError Send(string command)
        {
            serialPort?.WriteLine(command);
            return WriteError.None;
        }

        /// <summary>
        /// Writes a command to the lmc and waits for a response to come back.
        /// </summary>
        /// <param name="command"></param>
        /// <returns></returns>
        public WriteError WriteAndWait(string command)
        {
            lock (writeLock)
            {
                expectedReply = null;

                //string[] commands = command.SplitToValues();
                string[] commands = command.Split((char)'=').Select(a => a.Trim(new char[] { '\n', '\0', ' ', '\t' })).Where(a => !string.IsNullOrEmpty(a)).ToArray();
                if (commands.Length == 0)
                {
                    // There's no commands to send
                    return this.writeError = WriteError.NoCommandsGiven;
                }
                // Setup the waiting parameters
                if (!dictionary_CommandReply.ContainsKey(commands[0]))
                {
                    // There is no reply on record, just write the command and leave
                    return Write(command);
                }
                expectedReply = dictionary_CommandReply[commands[0]];
                if (expectedReply.replyAlwaysExpected)
                {
                    // No matter what, a reply is always expected from the command.
                    manualResetEventWriteAndWait.Reset();
                }
                else if (commands.Length == 1)
                {
                    // A reply is not always expected
                    // A reply is only expected when there are no arguments
                    // i.e. commands.Length == 1
                    manualResetEventWriteAndWait.Reset();
                }
                else
                {
                    // This should only happen when you're sending a set command and there is no expected reply
                    manualResetEventWriteAndWait.Set();
                    expectedReply = null;
                }
                Write(command);
#if DEBUG
                int waitTime = commands[0].StartsWith("Mother") ? 1000 : 1000;
#else
                int waitTime = commands[0].StartsWith("Mother") ? 1000 : 100;
#endif
                manualResetEventWriteAndWait.WaitOne(waitTime, false);
            }
            return 0;
        }

        /// <summary>
        /// Writes a command with a value to the lmc and then writes only the command to check the value sent.
        /// </summary>
        /// <remarks>
        /// Not all command and value pairs return a value. Use this when you send a command and want to check if the command went through.</remarks>
        /// <param name="command"></param>
        /// <param name="value"></param>
        public void WriteAndCheck(string command, string value)
        {
            string send = command + " = " + value;
            this.Write(send);
            Thread.Sleep(10);
            this.WriteAndWait(command);
        }

        //public void TcpIpWrite(string command)
        //{
        //    byte[] buffer = Encoding.ASCII.GetBytes(command);
        //    string messageString = "Send> " + Encoding.ASCII.GetString(buffer, 0, buffer.Length).TrimAtNull();

        //    networkStream.Write(buffer, 0, buffer.Length);
        //}

        public void EnqueueMessage(string message, WhichMessage whichMessage)
        {
            DebuggingMessage classMessage = new DebuggingMessage(message);
            switch (whichMessage)
            {
                default:
                case WhichMessage.Messages:
                    messages.Add(classMessage);
                    EventMessagesHaveChanged.Solicit(this);
                    break;
                case WhichMessage.MessagesSent:
                    messagesSent.Add(classMessage);
                    EventMessagesSentHasChanged.Solicit(this);
                    break;
                case WhichMessage.MessagesReceived:
                    messagesReceived.Add(classMessage);
                    EventMessagesReceivedHasChanged.Solicit(this);
                    break;
                case WhichMessage.Exception:
                    messagesExceptions.Add(classMessage);
                    EventMessagesExceptionsHaveChanged.Solicit(this);
                    break;
                case WhichMessage.Unknown:
                    messagesUnknown.Add(classMessage);
                    EventMessagesUnrecognizedHaveChanged.Solicit(this);
                    break;
            }
        }

        public void Start()
        {
            // Write("Verbose = 1"); // No such command. This is a reply command.
            GetGatewidths();
            Write("Cables");
            Write("ipAddr");
            Write("macAddr");
            Write("HVread");
            Write("DieTemp");
            Write("Power");
            Write("Version");
            Write("channelMask = 0xffffffff");
            Write("LFSstatus");
            Write("Duration");
            Write("HVset");

            // System.Threading.Timer timer = ();


            // Start any necessary threads
            //timerOneSecond.Enabled = true;

            Write("audioBeat = 250, 50, 1");
        }

        //public void WriteAnalysisMask()
        //{
        //    uint mask = detectorAnalysisMask;
        //    if (connectionSetup != ConnectionSetup.Primary)
        //    {
        //        // If the unit is not primary then don't utilize the upper channels.
        //        mask &= 0xFFFF;
        //    }
        //    Write("analysisMask = " + mask.ToString("x"));
        //}

        //public void WriteChannelMask()
        //{
        //    uint mask = detectorChannelMask;
        //    if (connectionSetup != ConnectionSetup.Primary)
        //    {
        //        // If the unit is not primary then don't utilize the upper channels.
        //        mask &= 0xFFFF;
        //    }
        //    Write("channelMask = " + mask.ToString("x"));
        //}
        /// <summary>
        /// Returns the sum of all the counts in the array channelCountsOneSecond
        /// </summary>
        /// <returns></returns>
        public long TotalCountsOneSecond()
        {
            return totalCountsOneSecond = channelCountsOneSecond.Sum();
        }

        /// <summary>
        /// Returns the sum of all the counts in the array channelCountsCumulative
        /// </summary>
        /// <returns></returns>
        public long TotalCountsCumulative()
        {
            return totalCountsCumulative = channelCountsCumulative.Sum();
        }

        public void SetIpAddress()
        {
            string message = "ipAddr = " + ipv4Address[0] + "."
                + ipv4Address[1] + "."
                + ipv4Address[2] + "."
                + ipv4Address[3];
            Write(message);
        }

        public void SetIpAddressAndCheck()
        {
            SetIpAddress();
            Thread.Sleep(10);
            Write("ipAddr");
        }

        public void SetMacAddress()
        {
            string message = "macAddr = "
                + macAddress[0].AddPreValue(2, "0").ToUpper() + ":"
                + macAddress[1].AddPreValue(2, "0").ToUpper() + ":"
                + macAddress[2].AddPreValue(2, "0").ToUpper() + ":"
                + macAddress[3].AddPreValue(2, "0").ToUpper() + ":"
                + macAddress[4].AddPreValue(2, "0").ToUpper() + ":"
                + macAddress[5].AddPreValue(2, "0").ToUpper();
            Write(message);
        }

        public void SetMacAddressAndCheck()
        {
            SetMacAddress();
            Thread.Sleep(10);
            Write("macAddr");
        }

        /// <summary>
        /// Write the time to the Linux OS.
        /// The LCD RTC holds the clock battery and real time clock (RTC).
        /// </summary>
        public void SetTime()
        {
            Write("Time = " + DateTime.Now.ToString("yyyy MM dd HH mm ss"));
            Write("Time");
        }

        //public void SetSystemTime(DateTime dateTime)
        //{
        //    SystemTime systemTime = new SystemTime();
        //    systemTime.wYear = (ushort)dateTime.Year;
        //    systemTime.wMonth = (ushort)dateTime.Month;
        //    systemTime.wDay = (ushort)dateTime.Day;
        //    systemTime.wHour = (ushort)dateTime.Hour;
        //    systemTime.wMinute = (ushort)dateTime.Minute;
        //    systemTime.wSecond = (ushort)dateTime.Second;
        //    systemTime.wMilliseconds = (ushort)dateTime.Millisecond;
        //    AccessSystemTime.SetSystemTime(ref systemTime);
        //    SetTime();
        //}

        //public DateTime GetSystemTime()
        //{
        //    SystemTime systemTime = new SystemTime();
        //    AccessSystemTime.GetSystemTime(ref systemTime);
        //    DateTime dateTime = new DateTime((int)systemTime.wYear,
        //        (int)systemTime.wMonth,
        //        (int)systemTime.wDay,
        //        (int)systemTime.wHour,
        //        (int)systemTime.wMinute,
        //        (int)systemTime.wSecond,
        //        (int)systemTime.wMilliseconds);
        //    return dateTime;
        //}

        //public void SetLocalTime(DateTime dateTime)
        //{
        //    SystemTime systemTime = new SystemTime();
        //    systemTime.wYear = (ushort)dateTime.Year;
        //    systemTime.wMonth = (ushort)dateTime.Month;
        //    systemTime.wDay = (ushort)dateTime.Day;
        //    systemTime.wHour = (ushort)dateTime.Hour;
        //    systemTime.wMinute = (ushort)dateTime.Minute;
        //    systemTime.wSecond = (ushort)dateTime.Second;
        //    systemTime.wMilliseconds = (ushort)dateTime.Millisecond;

        //    AccessSystemTime.SetLocalTime(ref systemTime);
        //    SetTime();
        //}

        //public DateTime GetLocalTime()
        //{
        //    SystemTime systemTime = new SystemTime();
        //    AccessSystemTime.GetLocalTime(ref systemTime);
        //    DateTime dateTime = new DateTime((int)systemTime.wYear,
        //        (int)systemTime.wMonth,
        //        (int)systemTime.wDay,
        //        (int)systemTime.wHour,
        //        (int)systemTime.wMinute,
        //        (int)systemTime.wSecond,
        //        (int)systemTime.wMilliseconds);
        //    return dateTime;
        //}

        private void InitializeComponent()
        {
            ///
            /// batteryStatus
            /// 
            batteryStatus = new BatteryStatus[] {
                new BatteryStatus("Top Battery"),
                new BatteryStatus("Bottom Battery")
            };

            ///
            /// bitmapBarPlotCumulative
            /// 
            bitmapBarPlotCumulative = new BitmapBarPlot[] {
                new BitmapBarPlot(300, 150, DetectorFormat.Symmetric),  // Primary
                new BitmapBarPlot(300, 150, DetectorFormat.Symmetric)   // Secondary
            };

            ///
            /// bitmapBarPlotOneSecond
            ///
            bitmapBarPlotOneSecond = new BitmapBarPlot[] {
                new BitmapBarPlot(300, 150, DetectorFormat.Symmetric),  // Primary
                new BitmapBarPlot(300, 150, DetectorFormat.Symmetric)   // Secondary
            };

            bitmapCountHistory = new BitmapCountHistory(300, 150);

            ///
            /// timerBoot
            /// 
            timerBoot = new System.Threading.Timer(RebootingStatusCheck, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            dateTimeTimerBootCreated = DateTime.Now; // Keep track of when timerBoot is created so that when timerBoot.Change is called it'll be from 

            // ****************************************************************************
            // Look in https://gitlab.lanl.gov/mc-15/mc15_control/-/blob/master/main_control.c
            //  for all of the responses and updates.
            // ****************************************************************************


            #region sortedList_CommandReply
            ///
            /// sortedList_SendReceive
            /// These values should be added already sorted for maximum efficiency
            /// The key here is what command is sent to the lmcControl
            /// The value is what the lmcControl will respond with
            /// Sometimes theres more than one reply, like with Cancel.
            dictionary_CommandReply = new Dictionary<string, ExpectedReply>(){
                {"analysisMask", new ExpectedReply(new string[] {"analysisMask"}, false, false)},
                {"audioBeat", new ExpectedReply(new string[] {"audioBeat"}, false, false)},
                {"audioEnable", new ExpectedReply(new string[] {}, false, false)}, // No reply ever.
                {"audioRate", new ExpectedReply(new string[] {"audioRate"}, false, false)},
                {"audioState", new ExpectedReply(new string[] {"audioState"}, false, false)},
                {"audioTone", new ExpectedReply(new string[] {"audioTone"}, false, false)},
                {"Cancel", new ExpectedReply(new string[] {"AssayCancelled", "Cancel error: not in assay active mode"}, true, false)},    //Call with Cancel
                {"channelMask", new ExpectedReply(new string[] {"channelMask"}, false, false)},
                {"cStatus", new ExpectedReply(new string[] {"cStatus"}, true, true)},
                {"Cables", new ExpectedReply(new string[] {"Cables"}, true, true)},
                {"deadTime", new ExpectedReply(new string[] {"deadTime"}, false, false)},
                // {"Debug", new ExpectedReply(new string[] {"verbose"}},   // **** Do not delete this line. This is documentation that the command "Debug" exists. ****
                {"DetLoca", new ExpectedReply(new string[] {"DetLoca"}, false, false)},
                {"DieTemp", new ExpectedReply(new string[] {"DieTemp"}, true, true)},
                {"DistPriToSrc", new ExpectedReply(new string[] {"DistPriToSrc"}, false, false)},
                {"DistSecToFlr", new ExpectedReply(new string[] {"DistSecToFlr"}, false, false)},
                {"DistSecToSrc", new ExpectedReply(new string[] {"DistSecToSrc"}, false, false)},
                {"dmaTimeInterval", new ExpectedReply(new string[] {"dmaTimeInterval"}, false, false)},
                {"DrawNum", new ExpectedReply(new string[] {"DrawNum"}, false, false)},
                {"Duration", new ExpectedReply(new string[] {"Duration"}, false, false)},
                {"Feynman", new ExpectedReply(new string[] {"Feynman"}, true, false)},
                {"FeynmanParams", new ExpectedReply(new string[] {"FeynmanParams"}, true, true)}, // I think this only returns the gatewidth of  the FeynmanHistogram of primary interest (e.g. 256 us)
                {"FeynmanResults", new ExpectedReply(new string[] {"FeynmanResults"}, true, true)},
                {"HVcalib", new ExpectedReply(new string[] {"HVcalib"}, false, false)},    // *** Not sure what the commands are: Needs to be tracked down.
                {"HVread", new ExpectedReply(new string[] {"HVread"}, false, false)},
                {"HVset", new ExpectedReply(new string[] {"HVset"}, false, true)},
                {"ipAddr", new ExpectedReply(new string[] {"ipAddr"}, false, false)},
                {"lcdTemp", new ExpectedReply(new string[] {"lcdTemp"}, false, true)}, // This doesn't seem to do anything.
                {"LFSformat", new ExpectedReply(new string[] {"LFSlist"}, true, false)},   // Call with both LFSlist and LFSformat - LFSformat removes the data files from the unit.
                {"LFSlist", new ExpectedReply(new string[] {"LFSlist"}, true, true)},   // Called with both LFSlist and LFSformat
                {"LFSstatus", new ExpectedReply(new string[] {"LFSstatus"}, true, true)},
                {"macAddr", new ExpectedReply(new string[] {"macAddr"}, false, false)},
                {"maxSamplesInFile", new ExpectedReply(new string[] {"maxSamplesInFile"}, false, false)},
                {"MeasType", new ExpectedReply(new string[] {"MeasType"}, false, false)},
                {"Moments", new ExpectedReply(new string[] {"FeynmanMoments"}, true, false)},
                {"Mother", new ExpectedReply(new string[] {""}, true, true)},  // The first command sent to the MC-15 is "Mother". The return is four strings.
                //{"NetSaveFile", new ExpectedReply(new string[] {"ListModeDataFileVersion"}},  // Use this to save from the MC-15 to the local computer.
                {"NPOD2", new ExpectedReply(new string[] {"NPOD2"}, false, false)},
                {"Pause", new ExpectedReply(new string[] {"AssayPaused"}, true, false)},
                {"Power", new ExpectedReply(new string[] {"Power"}, true, true)},
                {"Rates", new ExpectedReply(new string[] {"Rates"}, true, true)},
                {"reps", new ExpectedReply(new string[] {"reps"}, false, false)}, // Note: Reps does not send anything back through Tcp/Ip
                {"Resume", new ExpectedReply(new string[] {"AssayResumed", "Resume error: not in assay paused mode"}, true, false)},
                {"runDescription", new ExpectedReply(new string[] {"runDescription"}, false, false)},
                {"Sm2Ib", new ExpectedReply(new string[] {"Sm2Ib"}, true, true)},
                {"Storage", new ExpectedReply(new string[] {"Storage"}, false, false)},
                {"Time", new ExpectedReply(new string[] {"Time"}, false, false) },  // Sets the system time.
                {"Totals", new ExpectedReply(new string[] {"Totals"}, true, true)},
                {"updateSystem", new ExpectedReply(new string[] {"updateSystem"}, true, false)}, // ***** Check on this!!!!! *****
                {"USBsaveFile", new ExpectedReply(new string[] {"USBsaveFile"}, true, false)},      // Use this to save from the MC-15 to the the MC-15s usb drive.
                {"USBsaveFileCancel", new ExpectedReply(new string[] {"USBsaveFileCancel"}, true, false)}, // ******* This functionality needs to be investigated more !!!!!
                {"USBsaveFileProgress", new ExpectedReply(new string[] {"USBsaveFileProgress"}, true, true)},
                {"UserMode", new ExpectedReply(new string[] {"UserMode"}, false, false)},
                {"Version", new ExpectedReply(new string[] {"Version"}, true, true)},
                {"veto", new ExpectedReply(new string[] {"veto"}, false, false)},
                {"vetoGateWidth", new ExpectedReply(new string[] {"vetoGateWidth" }, false, false) },
                {"vetoTrue", new ExpectedReply(new string[] {"vetoTrue"}, false, false)}
            };
            #endregion sortedList_CommandReply

            #region sortedList_DelegateReceive
            ///
            /// sortedList_DelegateReceive
            /// The key is the command received from lmcControl
            /// The value is the delegate that will be called when the key is called.
            /// See https://stackoverflow.com/questions/3813261/how-to-store-delegates-in-a-list
            /// for how to use delegates.
            dictionary_DelegateReceive = new Dictionary<string, MethodDelegate>() {
                {"Active", ProcessActive},
                {"AssayCancelled", ProcessAssayCancelled},
                {"AssayDone", ProcessAssayDone},
                {"AssayError", ProcessAssayError},
                {"AssayPaused", ProcessAssayPaused},
                {"Pause error: not in assay active mode", ProcessAssayPausedError},
                {"AssayResumed", ProcessAssayResumed},
                {"Resume error: not in assay paused mode", ProcessAssayResumedError},
                {"BinaryDataFollows", ProcessBinaryDataFollows},
                {"Cancel", ProcessCancel},
                {"Cancel error: not in assay active mode", ProcessCancelError},
                {"Cables", ProcessCables},
                {"DetLoca", ProcessDetLoca},
                {"DieTemp", ProcessDieTemp},
                {"DistPriToSrc", ProcessDistPriToSrc},
                {"DistPriToFlr", ProcessDistPriToFlr},
                {"DistSecToFlr", ProcessDistSecToFlr},
                {"DistSecToSrc", ProcessDistSecToSrc},
                {"DrawNum", ProcessDrawNum},
                {"Done", ProcessDone},
                {"done", ProcessDone},
                {"Duration", ProcessDuration},
                //{"Done", ProcessDone},    // Don't process Done. Done is sent back for a variety commands.
                {"Feynman", ProcessFeynman},
                {"FeynmanMoments", ProcessFeynmanMoments},
                {"FeynmanParams", ProcessFeynmanParams},
                {"FeynmanResults", ProcessFeynmanResults},
                {"go:error: assay state not idle.", ProcessGoError},
                {"HVcalib", ProcessHVcalib},
                {"HVread", ProcessHVread},
                {"HVset", ProcessHVset},  // There is no reply back when this is sent. You need to send "HVset" to get the value.
                //{"LFSformat", ProcessLFSlist},  // Deletes the files on the remote machine. Sends back LFSlist in return. - There is no reply for LFSformat.
                {"LFSlist", ProcessLFSlist},
                {"LFSstatus", ProcessLFSstatus},
                {"ListModeDataFileVersion", ProcessListModeDataFileVersion},
                {"MeasType", ProcessMeasType},
                {"NPOD2", ProcessNPOD2},
                {"Power", ProcessPower},
                {"Rates", ProcessRates},
                {"SampleLossCount", ProcessSampleLossCount},
                {"Sm2Ib", ProcessSm2Ib},
                {"Storage", ProcessStorage},
                {"start", ProcessStart},    // Linux will return start when it's booted
                {"startUpComplete", ProcessStartUpComplete},
                {"Time", ProcessTime},
                {"Totals", ProcessTotals},
                {"USBsaveFile", ProcessUSBsaveFile},
                {"USBsaveFileCancel", ProcessUSBsaveFileCancel},
                {"USBsaveFileProgress", ProcessUSBsaveFileProgress},
                {"UserMode", ProcessUserMode},
                {"Version", ProcessVersion},
                {"analysisMask", ProcessAnalysisMask},
                {"audioBeat", ProcessAudioBeat},
                {"audioEnable", ProcessAudioEnable},
                {"audioRate", ProcessAudioRate},
                {"audioState", ProcessAudioState},
                {"audioTone", ProcessAudioTone},
                {"channelMask", ProcessChannelMask},
                {"cStatus", ProcessCStatus},
                {"deadTime", ProcessDeadTime},
                {"dmaTimeInterval", ProcessDmaTimeInterval},
                {"ipAddr", ProcessIpAddr},
                {"lcdTemp", ProcessLcdTemp},
                {"macAddr", ProcessMacAddr},
                {"maxSamplesInFile", ProcessMaxSamplesInFile},
                {"network_state", ProcessNetWorkState},
                {"reps", ProcessReps},
                {"runDescription", ProcessRunDescription},
                {"updateSystem", ProcessUpdateSystem},
                {"unrecognized", ProcessUnrecognized},
                {"verbose", ProcessVerbose},
                {"veto", ProcessVeto},
                {"vetoGateWidth", ProcessVetoGateWidth},
                {"vetoTrue", ProcessVetoTrue}
            };

            // Get the longest reply expected. This is to limit how many bytes are searched at 
            //  the beginning of a reply.
            // The +3 is to accomodate searching for " = " at the end.
            //longestReplyLength = dictionary_DelegateReceive.GetLongestKeyLength() + 3;

            #endregion sortedList_DelegateReceive

            ///
            /// dictionary_DelegateSend
            ///
            /// See https://stackoverflow.com/questions/3813261/how-to-store-delegates-in-a-list
            /// for how to use delegates.
            dictionary_DelegateSend = new Dictionary<string, MethodDelegate>();

            #region dictionary_SwConfig
            ///
            /// dictionary_SwConfig
            /// 
            dictionary_SwConfig = new Dictionary<string, MethodDelegate>()
            {
                {"audible", ProcessSwAudible},
                {"autoRunCounts", ProcessSwAutoRunCounts},
                {"autoRunDuration", ProcessSwAutoRunDuration},
                {"backgroundEnable", ProcessSwBackgroundEnable},
                {"bgRate", ProcessSwBgRate},
                {"bgRateDefault", ProcessSwBgRateDefault},
                {"bgYm", ProcessSwBgYm},
                {"bgYmDefault", ProcessSwBgYmDefault},
                {"bkgCounts", ProcessSwBkgCounts},
                {"bkgDuration", ProcessSwBkgDuration},
                {"collectionTime", ProcessSwCollectionTime},
                {"deadtime", ProcessSwDeadtime},
                {"debugEnable", ProcessSwDebugEnable},
                {"description", ProcessSwDescription},
                {"device", ProcessSwDevice},
                {"distance2floor", ProcessSwDistance2floor},
                {"distance2object", ProcessSwDistance2object},
                {"duration", ProcessSwDuration},
                {"exit", ProcessSwExit},
                {"hvRdConv", ProcessSwHvRdConv},
                {"hvSet", ProcessSwHvSet},
                {"hvWrConv", ProcessSwHvWrConv},
                {"ipcCounts", ProcessSwIpcCounts},
                {"ipcDuration", ProcessSwIpcDuration},
                {"linuxDebug", ProcessSwLinuxDebug},
                {"maxFileThreshold", ProcessSwMaxFileThreshold},
                {"minMemThreshold", ProcessSwMinMemThreshold},
                {"noUserPowerDownTime", ProcessSwNoUserPowerDownTime},
                {"powerDownAlertThreshold", ProcessSwPowerDownAlertThreshold},
                {"powerDownThreshold", ProcessSwPowerDownThreshold},
                {"qdrCounts", ProcessSwQdrCounts},
                {"qdrDuration", ProcessSwQdrDuration},
                {"repetitions", ProcessSwRepetitions},
                {"storage", ProcessSwStorage},
                {"threatEnable", ProcessSwThreatEnable},
                {"units", ProcessSwUnits},
                {"useVeto", ProcessSwUseVeto},
                {"vetoDuration", ProcessSwVetoDuration},
                {"vetoTrueState", ProcessSwVetoTrueState},
                {"ipAddress", ProcessSwIpAddress}
            };
            #endregion dictionary_SwConfig

            #region dictionary_HwConfig
            ///
            /// dictionary_HwConfig
            /// 
            dictionary_HwConfig = new Dictionary<string, MethodDelegate>()
            {
                {"hardwareVersion", ProcessHwHardwareVersion},
                {"serialNumber", ProcessHwSerialNumber}
            };
            #endregion dictionary_HwConfig

            dictionary_ProcessLastCommandSent_PreSend = new Dictionary<string, MethodDelegate>()
            {
                {"Moments", ProcessCommandSentLastFeynman_PreSend},
                {"Mother",  ProcessCommandSentMother_PreSend},
                {"NetSaveFile", ProcessNetSaveFile_PreSend },
                {"Feynmans", ProcessCommandSentLastFeynman_PreSend},
                {"FeynmanResults", ProcessCommandSentLastFeynman_PreSend},
                {"Go", ProcessCommandSentGo_PreSend},
                {"Cancel", ProcessCommandSentCancel_PreSend},
                {"audioBeat", ProcessCommandSentAudioBeat_Presend},
                {"Pause", ProcessCommandSentPause_PreSend},
                {"Resume", ProcessCommandSentResume_PreSend}
            };
            dictionary_ProcessLastCommandSent_PostSend = new Dictionary<string, MethodDelegate>()
            {
                {"reboot", ProcessCommandSentReboot_PostSend}
            };

            thicknessArray = new Thickness[] {
               new Thickness()
                {
                    // All the measured data for no Cd was used
                    // This data includes different reflector to detector distances
                    name = "MC-15 With Cd",
                    detectorFormat = DetectorFormat.MC15,
                    cdPresent = true,
                    simulated = false,
                    amp = new double[]
                    {
                        4.1444E-1,  // row 1/2
                        1.1224E0,   // row 1/3
                        4.3582E-1   // row 2/3
                    },
                    offset = new double[]
                    {
                        7.0714E-1,  // row 1/2
                        1.1407E0,   // row 1/3
                        1.5836E0    // row 2/3
                    },
                    mu = new double[] {
                        2.9745,       // cm // row 1/2
                        2.9164,             // row 1/3
                        2.8013              // row 2/3
                    },
                    sigma = new double[] {
                        1.5007,     // cm
                        1.4145,
                        1.5521
                    },
                    thicknessMin = new double[]
                    {
                        0.0,
                        0.0,
                        0.0
                    },
                    thicknessMax = new double[]
                    {
                        25.0,             // cm
                        25.0,
                        25.0
                    },
                    channels = new int[][]
                    {
                        new int[] {1, 2, 4, 5, 17, 18, 20, 21},     // row 1
                        new int[] {8, 9, 10, 11, 24, 25, 26, 27},   // row 2
                        new int[] {13, 13, 14, 14, 29, 29, 30, 30 } // row 3
                    }
                },
                new Thickness()
                {
                    // All the measured data for no Cd was used
                    // This data includes different reflector to detector distances
                    name = "MC-15 No Cd",
                    detectorFormat = DetectorFormat.MC15,
                    cdPresent = false,
                    simulated = false,
                    amp = new double[] {
                        8.64445E-1,
                        2.0739,
                        4.7295E-1
                    },
                    offset = new double[] {
                        7.4321E-1,
                        1.2301,
                        1.6017
                    },
                    mu = new double [] {
                        3.27337,        // cm
                        3.1316,
                        2.8660
                    },
                    sigma =  new double[] {
                        1.4314,    // cm
                        1.3314,
                        1.4849
                    },
                    thicknessMin = new double[]
                    {
                        0.0,
                        0.0,
                        0.0
                    },
                    thicknessMax = new double[]
                    {
                        25.0,             // cm
                        25.0,
                        25.0
                    },
                    channels = new int[][]
                    {
                        new int[] {1, 2, 4, 5, 17, 18, 20, 21},
                        new int[] {8, 9, 10, 11, 24, 25, 26, 27},
                        new int[] {13, 13, 14, 14, 29, 29, 30, 30 }
                    }
                },
                new Thickness() {
                    // All the measured data for no Cd was used
                    // This data includes different reflector to detector distances
                    name = "MC-Smalls No Cd",
                    detectorFormat = DetectorFormat.MCSmalls,
                    cdPresent = false,
                    simulated = false,
                    amp = new double[] {
                        8.64445E-1,
                        2.0739
                    },
                    offset = new double[] {
                        7.4321E-1,
                        1.2301,
                        1.6017
                    },
                    mu = new double [] {
                        3.27337,        // cm
                        3.1316
                    },
                    sigma =  new double[] {
                        1.4314,    // cm
                        1.3314
                    },
                    thicknessMin = new double[]
                    {
                        0.0,
                        0.0
                    },
                    thicknessMax = new double[]
                    {
                        25.0,             // cm
                        25.0
                    },
                    channels = new int[][]
                    {
                        new int[] {1, 2, 3, 4, 17, 18, 19, 20},
                        new int[] {7, 8 ,9, 23, 24 ,25}
                    }
                }

            };
            thickness = thicknessArray[0];

            detectorFormat_Changed(this, new EventArgs());
        }



        #region Processes


        /// <summary>
        /// Processes the reply of "Active"<para />
        /// Suggested:<para />
        /// &lt;None&gt;
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        ///             MainForm.cs
        /// Set:        &lt;None&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        &lt;None&gt;
        /// Returns:    &lt;None&gt;
        ///             
        /// Suggested:  &lt;None&gt;
        /// 
        /// Note: This is in the original GUI code but I don't think it's used anymore. I'm keeping it here just in case.
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessActive(string[] replies)
        {
            /// Comment from original GUI code
            // tbd:: we requested something c-code can't supply because of active assay
            try
            {
                EventActive.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessActive(string[]) --> ", "ProcessActive");
            }
        }

        /// <summary>
        /// Processes the replies of "Cancel" and "Cancel error: not in assay active mode"<para />
        /// Suggested:<para />
        /// Write("Cancel")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        &lt;None&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        Cancel
        /// Returns:    AssayCancelled=1
        ///             Cancel error: not in assay active mode
        ///             
        /// Suggested:  Write("Cancel");
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessAssayCancelled(string[] replies)
        {
            try
            {
                // The only reply for this is
                // AssayCancelled=1
                // or
                // Cancel error: not in assay active mode
                repetitions[0] = repetitions[1];
                wasAssayCancelled = true;
                if (CheckState(InstrumentState.ACTIVE))
                {
                    ClearState(InstrumentState.ACTIVE);
                    EventInstrumentStateHasChanged.Solicit(this);
                }
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessAssayCancelled(string[]) --> ", "ProcessAssayCancelled");
            }
        }

        /// <summary>
        /// Process the reply AssayDone = &lt;Storage Location&gt;, &lt;filename&gt;, &lt;filesize or bytes sent&gt;<para />
        /// Suggested:<para />
        /// &lt;None&gt;
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/lmc_control.c
        /// Set:        &lt;None&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        &lt;None&gt; - Occurs at end of counting
        /// Returns:    AssayDone = STORE_LFS, instr.filename, writeThreadData->dmaSampleCount * 8 + headerSize
        ///             AssayDone = STORE_NET, instr.filename, data_packets_sent
        ///             AssayDone = STORE_NONE
        ///             
        /// Suggested:  &lt;None&gt;
        /// </remarks>
        /// <param name="replies"></param>
        /// 
        public void ProcessAssayDone(string[] replies)
        {
            // This information is stored in MC-15/mc15_control
            // Process the reply AssayDone = <Storage Location>, <filename>, <filesize or bytes sent>
            // AssayDone = <Storage Location == STORE_NONE (0)>
            // AssayDone = <Storage Location == STORE_NET (1)>, <filename>, <bytes sent>
            // AssayDone = <Storage Location == STORE_USB (2)>, <filename>, <filesize>
            // AssayDone = <Storage Location == STORE_LFS (3)>, <filename>, <filesize>
            try
            {
                wasAssayCancelled = false;
                ClearState(InstrumentState.ACTIVE);
                int numberOfBeats = (++repetitions[0] >= repetitions[1]) ? 7 : 2; // increments repetitions[0]
                string command = "audioBeat = 250, 50, " + numberOfBeats.ToString();
                Write(command);

                if (replies.Length > 1)
                {
                    // AssayDone tells us where the data was stored.
                    // Storage state
                    storageLocation = replies[1].ToInstrumentStorageLocation();
                }
                if (replies.Length > 2)
                {
                    // Filename
                    fileLastSaved.name = replies[2];
                }
                if (replies.Length > 3)
                {
                    // bytes sent or filesize
                    fileLastSaved.size = replies[3].ToInt64(0);
                }

                EventInstrumentStateHasChanged.Solicit(this);
                EventAssayDone.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessAssayDone(string[]) --> ", "ProcessAssayDone");
            }
        }

        /// <summary>
        /// Process the reply "SampleLossCount"<para />
        /// Suggested:<para />
        /// &lt;None&gt;
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/lmc_control.c
        /// Set:        &lt;None&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        &lt;None&gt; - Occurs at end of counting
        /// Get Return: SampleLossCount = &lt;int&gt;
        /// 
        /// Suggested:  &lt;None&gt;
        /// 
        /// Note:       The return integer is (listData.fifo_wr_count - writeThreadData->dmaSampleCount)
        ///             This will only occur if there are sample counts lost during the acquisition
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessSampleLossCount(string[] replies)
        {
            try
            {
                EventSampleLossCount.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessSampleLossCount(string[]) --> ", "ProcessSampleLossCount");
            }
        }

        /// <summary>
        /// When there is an error associated with storing a file on-board.<para />
        /// The string to be evaluated is 
        /// AssayError = &lt;Error Code&gt;.
        /// </summary>
        /// <remarks>
        /// Error Code = -1
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessAssayError(string[] replies)
        {
            try
            {
                if (replies.Length < 2)
                {
                    // No error codes present
                    return;
                }
                else
                {
                    assayErrorCode = (AssayErrorCode)Convert.ToInt32(replies[1]);
                    EventAssayError.Solicit(this);
                }
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessAssayError(string[]) --> ", "ProcessAssayError");
            }
        }


        /// <summary>
        /// Process the reply "AssayPaused"<para />
        /// Suggested:<para />
        /// Write("Pause")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        Pause
        /// Set Return: AssayPaused
        ///             Pause error: not in assay active mode
        /// Get:        &lt;None&gt;
        /// Get Return: &lt;None&gt;
        /// 
        /// Suggested:  Write("Pause")
        /// 
        /// Notes:      Sending "Pause" will put the detector into a Pause state and it will return either
        ///             "AssayPaused" or "Pause error: not in assay active mode"
        /// </remarks>
        public void ProcessAssayPaused(string[] replies)
        {
            try
            {
                if (CheckState(InstrumentState.PAUSED))
                {
                    // System is already paused
                }
                else
                {
                    // System is NOT paused
                    // Set the state to Paused
                    SetState(InstrumentState.PAUSED);
                    EventAssayPaused.Solicit(this);
                }
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessAssayPaused(string[]) --> ", "ProcessAssayPaused");
            }
        }

        /// <summary>
        /// Process the reply "Pause error: not in assay active mode"<para />
        /// Suggested:<para />
        /// Write("Pause")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        Pause
        /// Set Return: AssayPaused
        ///             Pause error: not in assay active mode
        /// Get:        &lt;None&gt;
        /// Get Return: &lt;None&gt;
        /// 
        /// Suggested:  Write("Pause");
        /// 
        /// Notes:      Sending "Pause" will put the detector into a Pause state and it will return either
        ///             "AssayPaused" or "Pause error: not in assay active mode"
        /// </remarks>
        public void ProcessAssayPausedError(string[] replies)
        {
            try
            {
                errorCodes.Add(ErrorCode.ERRORPAUSE);
                SetState(InstrumentState.PAUSED);
                EventAssayPausedError.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessPauseError(string[]) --> ", "ProcessPauseError");
            }
        }

        /// <summary>
        /// Process the reply "AssayResumed"<para />
        /// Suggested:<para />
        /// Write("Resume")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        Resume
        /// Set Return: AssayResumed
        ///             Resume error: not in assay paused mode
        /// Get:        &lt;None&gt;
        /// Get Return: &lt;None&gt;
        /// 
        /// Suggested:  Write("Resume");
        /// 
        /// Notes:      Sending "Resume" will resume collecting of data if the detector is in a Paused state and it will return either
        ///             "AssayResumed" or "Resume error: not in assay paused mode"
        /// </remarks>
        public void ProcessAssayResumed(string[] replies)
        {
            try
            {
                ClearState(InstrumentState.PAUSED);
                EventAssayResumed.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessAssayResumed(string[]) --> ", "ProcessAssayResumed");
            }
        }

        /// <summary>
        /// Process the reply "Resume error: not in assay paused mode"<para />
        /// Suggested:<para />
        /// Write("Resume")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        Resume
        /// Set Return: AssayResumed
        ///             Resume error: not in assay paused mode
        /// Get:        &lt;None&gt;
        /// Get Return: &lt;None&gt;
        /// 
        /// Suggested:  Write("Resume");
        /// 
        /// Notes:      Sending "Resume" will resume collecting of data if the detector is in a Paused state and it will return either
        ///             "AssayResumed" or "Resume error: not in assay paused mode"
        /// </remarks>
        public void ProcessAssayResumedError(string[] replies)
        {
            try
            {
                errorCodes.Add(ErrorCode.ERRORRESUME);
                EventAssayResumedError.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessAssayResumedError(string[]) --> ", "ProcessAssayResumedError");
            }
        }

        public void ProcessBinaryDataFollows(string[] replies) { }

        /// <summary>
        /// Process the reply "Cables"<para />
        /// Suggested:<para />
        /// Write("Cables")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        &lt;None&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        Cables
        /// Get Return: Cables = &lt;(int)Synch Cable State&gt;, &lt;(int)Front Panel State&gt;
        /// 
        /// Suggested:  Write("Cables");
        /// </remarks>
        public void ProcessCables(string[] replies)
        {
            // Evaluates the status of the connected cables.
            // Cables does not and cannot configure anything on the Instrument.
            // Cables = <Cable State>, <Front Panel State>
            // Cables = 0, 2  => Single Unit, Front Panel Attached
            // Cables = 1, 2  => Primary Unit, Front Panel Attached
            // Cables = 2, 2  => Secondary Unit, Front Panel Attached
            // Cables = 0, 0  => Single Unit, Front Panel Not Attached
            // Cables = 0, 1  => Single Unit, Cable Attached To Main Body
            // Cables = 0, 3  => Single Unit, Cable Attached to Main Body and to Front Panel
            try
            {
                if (replies.Length > 1)
                {
                    this.podConfig = replies[1].ToPodConfig();
                }
                if (replies.Length > 2)
                {
                    this.frontPanelStatus = replies[2].ToFrontPanelStatus();
                }
                EventCables.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessCables(string[]) --> ", "ProcessCables");
            }
        }

        /// <summary>
        /// Process the command "Cancel"<para />
        /// Suggested:<para />
        /// Write("Cancel")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        Cancel
        /// Set Return: AssayCancelled=1
        ///             Cancel error: not in assay active mode
        /// Get:        &lt;None&gt;
        /// Get Return: &lt;None&gt;
        /// 
        /// Suggested:  Write("Cancel");
        /// 
        /// Note:       Sending "Cancel" will cancel an assay if it's collecting data
        /// </remarks>
        public void ProcessCancel(string[] replies)
        {
            // The only reply for this is
            // AssayCancelled=1
            // or
            // Cancel error: not in assay active mode
            if (CheckState(InstrumentState.ACTIVE))
            {
                // clear the active state.
                ClearState(InstrumentState.ACTIVE);
                EventAssayCancelled.Solicit(this);
            }
            else
            {
                // The detector is already inactive.
                // Just a placeholder
            }
        }


        /// <summary>
        /// Process the reply "Cancel error: not in assay active mode"<para />
        /// Suggested:<para />
        /// Write("Cancel")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        Cancel
        /// Set Return: AssayCancelled=1
        ///             Cancel error: not in assay active mode
        /// Get:        &lt;None&gt;
        /// Get Return: &lt;None&gt;
        /// 
        /// Suggested:  Write("Cancel");
        /// 
        /// Note:       Sending "Cancel" will cancel an assay if it's collecting data
        ///             Even if this error occurs, you can resume an acquisition.
        ///             This is not a terminating error.
        /// </remarks>
        public void ProcessCancelError(string[] replies)
        {
            errorCodes.Add(ErrorCode.ERRORCANCEL);
            EventCancelError.Solicit(this);
        }

        /// <summary>
        /// Process the reply DetLoca = &lt;(string) detector location (Side_A)&gt;
        /// detector location (Side_A) when in field user mode<para />
        /// Suggested:<para />
        /// WriteAndCheck("DetLoca", "&lt;(string) detector location (Side_A)&gt;")<para />
        /// Write("DetLoca")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        DetLoca = &lt;(string)&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        DetLoca
        /// Get Return: DetLoca = &lt;(string) detector location (Side_A)&gt;
        /// 
        /// Suggested:  WriteAndCheck("DetLoca", "&lt;(string) detector location (Side_A)&gt;")
        ///             Write("DetLoca")
        /// 
        /// Note:       Sending "DetLoca = &lt;string&gt;" does not send a return.
        ///             You must send "DetLoca" to retrieve the value
        ///             The string must not include any spaces
        ///             Good: Side_A
        ///             Bad:  Side A
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessDetLoca(string[] replies)
        {
            // send DETector LOCAtion for file name
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                // detector location (Side_A) when in field user mode
                this.detLoca = replies[1];
                EventDetLoca.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessDetLoca(string[]) --> ", "ProcessDieTemp");
            }
        }

        /// <summary>
        /// Process the reply DieTemp = &lt;(int)instr.TempDie&gt;, &lt;(int)instr.TempDie&gt<para />
        /// Suggested:<para />
        /// Write("DieTemp")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        &lt;None&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        DieTemp
        /// Get Return: DieTemp = &lt;(int)instr.TempDie&gt;, &lt;(int)instr.TempDie&gt
        /// 
        /// Suggested:  Write("DieTemp")
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessDieTemp(string[] replies)
        {
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                if (replies.Length > 1)
                {
                    fpgaDieTemp[0] = replies[1].ToInt32();
                }
                if (replies.Length > 2)
                {
                    fpgaDieTemp[1] = replies[2].ToInt32();
                }
                EventDieTemp.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessDieTemp(string[]) --> ", "ProcessDieTemp");
            }
        }

        /// <summary>
        /// Process the reply DistPriToSrc = &lt;(double)distance in cm&gt;<para />
        /// The distance of the primary unit to the center of the source.<para />
        /// Suggested:<para />
        /// WriteAndCheck("DistPriToSrc", "&lt;(double)) distance in cm&gt;")<para />
        /// Write("DistPriToSrc")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        DistPriToSrc = &lt;(double) distance in cm&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        DistPriToSrc
        /// Get Return: DistPriToSrc = &lt;(double) distance in cm&gt;
        /// 
        /// Suggested:  WriteAndCheck("DistPriToSrc, "&lt;(double)) distance in cm&gt;")
        ///             Write("DistPriToSrc")
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessDistPriToSrc(string[] replies)
        {
            /// Process the replie DistPriToSrc = <distance in cm>
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                this.distPriToSrc = replies[1].ToDouble(0.0);
                EventDistPriToSrc.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessDistPriToSrc(string[]) --> ", "ProcessDieTemp");
            }
        }

        /// <summary>
        /// Process the reply DistPriToFlr = &lt;(double)distance in cm&gt;<para />
        /// The distance from the primary unit to the floor.<para />
        /// Suggested:<para />
        /// WriteAndCheck("DistPriToFlr", "&lt;(double) distance in cm&gt;")<para />
        /// Write("DistPriToFlr")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        DistPriToFlr = &lt;(double) distance in cm&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        DistPriToFlr
        /// Get Return: DistPriToFlr = &lt;(double) distance in cm&gt;
        /// 
        /// Suggested:  WriteAndCheck("DistPriToFlr", "&lt;(double) distance in cm&gt;")
        ///             Write("DistPriToFlr")
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessDistPriToFlr(string[] replies)
        {
            /// Process the replie DistPriToFlr = <distance in cm>
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                this.distPriToFlr = replies[1].ToDouble(0.0);
                EventDistPriToFlr.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessDistPriToFlr(string[]) --> ", "ProcessDieTemp");
            }
        }

        /// <summary>
        /// Process the reply DistSecToFlr = &lt;distance in cm&gt;<para />
        /// The distance of the secondary unit to the floor.<para />
        /// Suggested:<para />
        /// WriteAndCheck("DistSecToFlr",  "&lt;(double) distance in cm&gt;")
        /// <para />Write("DistSecToFlr")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        DistSecToFlr = &lt;(double) distance in cm&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        DistSecToFlr
        /// Get Return: DistSecToFlr = &lt;(double) distance in cm&gt;
        /// 
        /// Suggested:  WriteAndCheck("DistSecToFlr",  "&lt;(double) distance in cm&gt;")
        ///             Write("DistSecToFlr")
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessDistSecToFlr(string[] replies)
        {
            /// Process the replie DistSecToFlr = <distance in cm>
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                this.distSecToFlr = replies[1].ToDouble(0.0);
                EventDistSecToFlr.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessDistSecToFlr(string[]) --> ", "ProcessDieTemp");
            }
        }

        /// <summary>
        /// Process the reply DistSecToSrc = &lt;(double)distance in cm&gt;<para />
        /// The distance of the secondary unit to the center of the source.<para />
        /// Suggested:<para />
        /// WriteAndCheck("DistSecToSrc",  "&lt;(double) distance in cm&gt;")<para />
        /// Write("DistSecToSrc")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        DistSecToSrc = &lt;(double)) distance in cm&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        DistSecToSrc
        /// Get Return: DistSecToSrc = &lt;(double)) distance in cm&gt;
        /// 
        /// Suggested:  WriteAndCheck("DistSecToSrc",  "&lt;(double) distance in cm&gt;")
        ///             Write("DistSecToSrc")
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessDistSecToSrc(string[] replies)
        {
            /// Process the replie DistSecToSrc = <distance in cm>
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                this.distSecToSrc = replies[1].ToDouble(0.0);
                EventDistSecToSrc.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessDistSecToSrc(string[]) --> ", "ProcessDieTemp");
            }
        }


        /// <summary>
        /// Process the reply DrawNum = &lt;(int)draw number&gt;<para />
        /// The draw number specified by the user.<para />
        /// Suggested:<para />
        /// WriteAndCheck("DrawNum",  "&lt;(int)draw number&gt;")<para />
        /// Write("DrawNum")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        DrawNum = &lt;(int)draw number&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        DrawNum
        /// Get Return: DrawNum = &lt;(int)draw number&gt;
        /// 
        /// Suggested:  WriteAndCheck("DrawNum",  "&lt;(int)draw number&gt;")
        ///             Write("DrawNum")
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessDrawNum(string[] replies)
        {
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                this.drawNum = replies[1].ToInt32(0);
                EventDrawNum.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessDrawNum(string[]) --> ", "ProcessDieTemp");
            }
        }

        /// <summary>
        /// Process the reply Duration = &lt;(int)duration in sec&gt;<para />
        /// The duration of the acquisition.<para />
        /// Suggested:<para />
        /// WriteAndCheck("Duration", "&lt;duration in sec&gt;")<para />
        /// Write("Duration")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        Duration = &lt;(int)duration&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        Duration
        /// Get Return: Duration = &lt;(int)duration&gt;
        /// 
        /// Suggested:  WriteAndCheck("Duration", "&lt;duration in sec&gt;")
        ///             Write("Duration")
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessDuration(string[] replies)
        {
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                this.duration = replies[1].ToUInt32(0);
                EventDuration.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessDuration(string[]) --> ", "ProcessDieTemp");
            }
        }

        /// <summary>
        /// Handles the reply Done.<para />
        /// Done is sent back after a lot of different commands so I'm not sure this should be attached.
        /// </summary>
        /// <param name="replies"></param>
        public void ProcessDone(string[] replies)
        {
            try
            {
                EventDone.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessDone(string[]) --> ", "ProcessDone");
            }
        }

        /// <summary>
        /// Process the reply Feynman = &lt;bin[0]&gt;, &lt;bin[1]&gt;, ...<para />
        /// Returns the Feynman histogram.<para />
        /// Suggested:<para />
        /// Write("Feynman = &lt;index&gt;")<para />
        /// Write("Feynman")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        Feynman = &lt;(int)Feynman histogram index&gt;
        /// Set Return: Feynman = &lt;(int)bin[0]&gt;, &lt;(int)bin[1]&gt;, ...
        /// Get:        Feynman
        /// Get Return: Feynman = &lt;(int)duration&gt;
        /// 
        /// Suggested:  Write("Feynman = &lt;index&gt;")
        ///             Write("Feynman")
        /// 
        /// Note:       Sending "Feynman" will retrieve the current indexed Feynman histogram
        ///             The return is only the Feynman histogram. There is no information on the gatewidth
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessFeynman(string[] replies)
        {
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                int feynmans_Count = feynmans.Count();
                if (currentFeynmanIndex.InRangeIE(0, feynmans_Count))
                {
                    feynmans[currentFeynmanIndex].feynmanHistogram.bin = new ulong[replies.Length - 1];
                    for (int i = 1; i < replies.Length; ++i)
                    {
                        feynmans[currentFeynmanIndex].feynmanHistogram.bin[i - 1] = replies[i].ToUInt32(0);
                    }
                    feynmans[currentFeynmanIndex].feynmanHistogram.CalculateMoments();
                }
                EventFeynman.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessFeynman(string[]) --> ", "ProcessFeynman");
            }
        }

        /// <summary>
        /// Process the reply FeynmanMoments = &lt;(int)gate width&gt;, &lt;(double)m1&gt;, &lt;(double)m2&gt;, ...&lt;(double)m8&gt;<para />
        /// Returns the gatewidth and the first eight reduced factorial moments of the currently indexed Feynman histogram.<para />
        /// Suggested:<para />
        /// Write("Moments = &lt;index&gt;")<para />
        /// Write("Moments")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        Moments = &lt;(int)Feynman histogram index&gt;
        /// Set Return: FeynmanMoments = &lt;(int)bin[0]&gt;, &lt;(int)bin[1]&gt;, ...
        /// Get:        Moments
        /// Get Return: FeynmanMoments = &lt;(int)duration&gt;
        /// 
        /// Suggested:  Write("Moments = &lt;index&gt;")
        ///             Write("Moments")
        /// 
        /// Note:       Sending "Moments" will retrieve the information for the currently indexed Feynman histogram
        ///             
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessFeynmanMoments(string[] replies)
        {
            // FeynmanMoments = <gate width>, <m1>, <m2>, <m3>, <m4>, <m5>, <m6>, <m7>, <m8>
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                Feynman feynman = new Feynman(replies);
                FeynmanSearch search = new FeynmanSearch(feynman.gatewidth);

                int index = feynmans.FindIndex(0, feynmans.Count(), search.Gatewidth);
                if (index < 0)
                {
                    feynmans.Add(feynman);
                    feynmans.Sort();
                }
                else
                {
                    feynmans[index].feynmanMoments.m = feynman.feynmanMoments.m;
                }
                EventFeynmanMoments.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessFeynmanMoments(string[]) --> ", "ProcessFeynmanMoments");
            }
        }

        public void ProcessFeynmanParams(string[] replies) { }

        /// <param name="replies"></param>
        /// <summary>
        /// Process the reply FeynmanResults = &lt;(double)Ym&gt;, &lt;(double)cBar&gt;,  &lt;c2Bar&gt;<para />
        /// Returns information on the currently indexed Feynman histogram.<para />
        /// Suggested:<para />
        /// Write("FeynmanResults = &lt;index&gt;")<para />
        /// Write("FeynmanResults")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        FeynmanResults = &lt;(int)Feynman histogram index&gt;
        /// Set Return: &lt;(double)Ym&gt;, &lt;(double)cBar&gt;,  &lt;c2Bar&gt;
        /// Get:        FeynmanResults
        /// Get Return: FeynmanResults = &lt;(double)Ym&gt;, &lt;(double)cBar&gt;,  &lt;c2Bar&gt;
        /// 
        /// Suggested:  Write("FeynmanResults = &lt;index&gt;")
        ///             Write("FeynmanResults")
        /// 
        /// Note:       Sending "Moments" will retrieve the information for the currently indexed Feynman histogram
        ///             
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessFeynmanResults(string[] replies)
        {
            /// Command sent is: FeynmanResults = <Index of Histogram>
            /// Command received is : FeynmanResults = <Ym>, <cBar>, <c2Bar>
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                if (currentFeynmanIndex.InRangeIE(0, feynmans.Count()))
                {
                    feynmans[currentFeynmanIndex].feynmanResults.Assign(replies);
                }
                EventFeynmanResults.Solicit(this);

            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessFeynmanResults(string[]) --> ", "ProcessFeynmanResults");
            }
        }

        /// <summary>
        /// Process the reply HVcalib = &lt;(int)hvSet&gt;, &lt;(int)hvRead&gt;, &lt;chanTotals[0]&gt;, ... &lt;chanTotals[]&gt;<para />
        /// Suggested:<para />
        /// Write("HVcalib")<para />
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        &lt;None&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        HVcalib
        /// Get Return: HVcalib = &lt;(int)instr.hv_setpoint&gt;, &lt;(int)inst.hv_value&gt;, &lt;chanTotals[0]&gt;, ... &lt;chanTotals[]&gt;
        /// 
        /// Suggested:  Write("HVcalib")
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessHVcalib(string[] replies)
        {
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                if (replies.Length > 1)
                {
                    this.hvSet = replies[1].ToInt32(-1);
                }
                if (replies.Length > 2)
                {
                    this.hvSet = replies[2].ToInt32(-1);
                }
                Array.Clear(channelCountsTotals, 0, channelCountsTotals.Length);
                int I = Math.Min(replies.Length - 3, channelCountsTotals.Length);
                for (int i = 0; i < I; ++i)
                {
                    channelCountsTotals[i] = replies[i + 2].ToUInt32(0u);
                }
                EventHVcalib.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessHVcalib(string[]) --> ", "ProcessHVcalib");
            }
        }

        /// <summary>
        /// Process the reply HVread = &lt;(int) High voltage Value&gt;<para />
        /// Suggested:<para />
        /// Write("HVread")<para />
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        &lt;None&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        HVread
        /// Get Return: HVread = &lt;(int) High voltage Value&gt;
        /// 
        /// Suggested:  Write("HVread")
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessHVread(string[] replies)
        {
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                hvRead = replies[1].ToInt32(-1);
                EventHVread.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessHVread(string[]) --> ", "ProcessHVread");
            }
        }

        /// <summary>
        /// Process the reply HVset = &lt;(int) High voltage Value&gt;<para />
        /// Suggested:<para />
        /// WriteAndCheck("HVset", "&lt;(int)High Voltage Set&gt;")<para />
        /// Write("HVset")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        HVset = &lt;(int) High voltage Value&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        HVset
        /// Get Return: HVset = &lt;(int) High voltage Value&gt;
        /// 
        /// Suggested:  WriteAndCheck("HVset", "&lt;(int)High Voltage Set&gt;")
        ///             Write("HVset")
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessHVset(string[] replies)
        {
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                hvSet = replies[1].ToInt32(-1);
                EventHVset.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessHVread(string[]) --> ", "ProcessHVread");
            }
        }

        /// <summary>
        /// Gets a list of the files on the detector and puts them in a List&lt;ListViewItem&gt;.<para />
        /// Suggested:<para />
        /// Write("LFSlist")<para />
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        &lt;None&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        LFSlist
        /// Get Return: LFSlist = &lt;(string)filename[0]&gt;, ... &lt;(string)filename[last]&gt;
        /// 
        /// Suggested:  Write("LFSlist")
        /// 
        /// Note:       This is also called when LFSformat is called.
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessLFSlist(string[] replies)
        {
            try
            {
                List<FileListItem> tempFileList = new List<FileListItem>();
                if (replies.Length < 3)
                {
                    //No files are on the instrument.
                    fileList = new FileListItem[0];
                    percentFileMemoryFilled = 0;
                    LFScount = 0;
                    return;
                }
                for (int i = 1; i < replies.Length; i += 2)
                {
                    if (i < replies.Length)
                    {
                        FileListItem item = new FileListItem(replies[i]);
                        if (i + 1 < replies.Length)
                        {
                            item.size = Convert.ToUInt32(replies[i + 1]);
                            item.CreateFileSizeString();
                        }
                        tempFileList.Add(item);
                    }
                }
                LFScount = tempFileList.Count();
                // Put newest time first.
                tempFileList.Sort((left, right) => right.dateTime.CompareTo(left.dateTime));
                fileList = tempFileList.ToArray();
                EventLFSlist.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessLFSlist(string[]) --> ", "ProcessLFSlist");
            }
        }

        /// <summary>
        /// Gets the status of the files on the detector.<para />
        /// The return is percent filled and number of files on the unit.<para />
        /// Suggested:<para />
        /// Write("LFSlist")<para />
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        &lt;None&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        LFSstatus
        /// Get Return: LFSstatus = &lt;(int)Percent Memory Used&gt;, &lt;(int)Number of Files&gt;
        /// 
        /// Suggested:  Write("LFSlist")
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessLFSstatus(string[] replies)
        {
            // replies[1] = percent of memory filled
            // replies[2] = number of files on the detector (LFScount)
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                if (replies.Length > 1)
                {
                    percentFileMemoryFilled = replies[1].ToInt32(-1);
                }
                if (replies.Length > 2)
                {
                    LFScount = replies[2].ToInt32(-1);
                }
                EventLFSstatus.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessLFSstatus(string[]) --> ", "ProcessLFSstatus");
            }

        }

        /// <summary>
        /// Processes the ListModeDataFileVersion.
        /// </summary>
        /// <param name="replies"></param>
        public void ProcessListModeDataFileVersion(string[] replies)
        {
            // This is the header to a file
            // Just keep it for now.
            // file_header = reply;
            //EnqueueMessage(reply);


            // Write the last message to the file.
            // Before we started writing data to the file we set aside an area at the TOP
            // of the file to right the header.  Back up to the beginning of the file to write the header.

            //if (binary_writer != null) {
            //    // the header is at the top of the file.  It was prefilled with spaces and the /r/n
            //    binary_writer.BaseStream.Position = 0;


            //    byte[] bstr = Encoding.ASCII.GetBytes(reply); // this eliminates the length bytes a string type writes to files.
            //    binary_writer.Write(bstr);

            //    // close file 
            //    binary_writer.Close();
            //    binary_writer = null;
            //    string message = "Data saved in " + currentDataFilename;
            //    EnqueueMessage(message, messages, MessagesMaxSize, WhatChangedInMessage.Messages);
            //}
        }


        /// <summary>
        /// Gets the measurement type of the acquisition.<para />
        /// MeasType = &lt;(int)measurement type&gt;<para />
        /// i.e. QuickDraw (0), Background (1), IPC (2), Default (3),<para />
        /// Suggested:<para />
        /// WriteAndCheck("MeasType", "&lt;(int)measurement type&gt;")<para />
        /// Write("MeasType")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        MeasType = &lt;(int)measurement type&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        MeasType
        /// Get Return: MeasType = &lt;(int)measurement type&gt;
        /// 
        /// Suggested:  WriteAndCheck("MeasType", "&lt;(int)measurement type&gt;")
        ///             Write("MeasType")
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessMeasType(string[] replies)
        {
            // public enum MeasurementType : int
            // {
            //    Undefined  = -1,
            //    QuickDraw  =  0,
            //    Background =  1,
            //    Ipc        =  2,
            //    Default    =  3
            // }
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                this.measurementType = replies[1].ToMeasurementType();
                EventMeasType.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessMeasType(string[]) --> ", "ProcessMeasType");
            }
        }

        /// <summary>
        /// Shows the status of the configuration from NPOD2 = &lt;(int)pod configuration&gt;<para />
        /// Suggested:<para />
        /// WriteAndCheck("NPOD2", "&lt;(int)measurement type&gt;")<para />
        /// Write("NPOD2")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        NPOD2 = &lt;(int)pod configuration&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        NPOD2
        /// Get Return: NPOD2 = &lt;(int)pod configuration&gt;
        /// 
        /// Suggested:  WriteAndCheck("NPOD2", "&lt;(int)measurement type&gt;")
        ///             Write("NPOD2")
        /// 
        /// NPOD2 sets or gets the configuration of the detector.
        /// NPOD2 = 0 => Single Unit
        /// NPOD2 = 1 => Primary Unit
        /// NPOD2 = 2 => Secondry Unit
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessNPOD2(string[] replies)
        {
            // NPOD2 sets or gets the configuration of the detector.
            // NPOD2 = 0 => Single Unit
            // NPOD2 = 1 => Primary Unit
            // NPOD2 = 2 => Secondry Unit
            try
            {
                if (replies.Count() < 2)
                {
                    return;
                }
                this.podConfig = (PodConfig)Convert.ToInt32(replies[1]);
                EventNPOD2.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessNPOD2(string[]) --> ", "ProcessNPOD2");
            }
        }

        /// <summary>
        /// Processes the reply Power = &lt;instr.ACpower&gt;, &lt;instr.BATT1power&gt;, &lt;instr.BATT2power&gt;, &lt;instr.BATT1charge&gt;, &lt;instr.BATT2charge&gt;<para />
        /// Suggested:<para />
        /// Write("Power")<para />
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        &lt;None&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        Power
        /// Get Return: Power = &lt;instr.ACpower&gt;, &lt;instr.BATT1power&gt;, &lt;instr.BATT2power&gt;, &lt;instr.BATT1charge&gt;, &lt;instr.BATT2charge&gt;<para />
        /// instr.Bat&lt;Battery Number&gt; =&gt; -1 = not installed / [1..100] = Battery Charge State. <para />
        /// instr.Batt&lt;Battery Number&gt;charge =&gt; 0 = not charging / 1 = charging
        /// Suggested:  Write("Power")<para />
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessPower(string[] replies)
        {
            try
            {
                if (replies.Count() < 6)
                {
                    // There's not enough information.
                    // Draw empty batteries.
                    //acPresent = false;
                    //batteryStatus[0].Clear();
                    //batteryStatus[1].Clear();
                    //EventPower.Solicit(this);
                    return;
                }
                // Check if AC is present
                acPresent = replies[1].Equals("1");

                // Process Battery[0] first.
                int chargeLevel = Convert.ToInt32(replies[2]);
                int charging = Convert.ToInt32(replies[4]);
                if (chargeLevel > 0)
                {
                    // This minimizes the flickering
                    batteryStatus?[0].SetCharge(chargeLevel, charging);
                }

                chargeLevel = Convert.ToInt32(replies[3]);
                charging = Convert.ToInt32(replies[5]);
                if (chargeLevel > 0)
                {
                    // This minimizes the flickering
                    batteryStatus?[1].SetCharge(chargeLevel, charging);
                }

                EventPower.Solicit(this);
            }
            catch (Exception ex)
            {
                acPresent = false;
                batteryStatus?[0].Clear();
                batteryStatus?[1].Clear();

                HandleException(ex, "Detector.ProcessPower(string[]) --> ", "ProcessPower");
            }
        }

        /// <summary>
        /// Processes the reply Rates = &lt;(int)chan[0]&gt;, &lt;(int)chan[1]&gt;, &lt;(int)chan[2]&gt;, ... &lt;(int)chan[31]&gt;<para />
        /// Suggested:<para />
        /// Write("Rates")<para />
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        &lt;None&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        Rates
        /// Get Return: Rates = &lt;(int)chan[0]&gt;, &lt;(int)chan[1]&gt;, &lt;(int)chan[2]&gt;, ... &lt;(int)chan[31]&gt;
        /// 
        /// Suggested:  Write("Power")
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessRates(string[] replies)
        {
            try
            {
                // Clear the array. This is cheap insurance to ensure the data are correct.
                Array.Clear(channelCountsOneSecond, 0, channelCountsOneSecond.Count());
                if (replies.Length < 2)
                {
                    return;
                }

                int I = Math.Min(32, replies.Length - 1);
                for (int i = 0; i < I; ++i)
                {
                    uint counts = replies[i + 1].ToUInt32(0);
                    channelCountsOneSecond[i] = counts;
                    //if (instrumentState != InstrumentState.PAUSED)
                    if (CheckState(InstrumentState.PAUSED))
                    {
                        // Instrument is Paused, don't do anything
                    }
                    else
                    {
                        // Instrument should be collecting data
                        channelCountsCumulative[i] += counts;
                    }
                }
                totalCountsOneSecond = channelCountsOneSecond.Sum();
                totalCountsCumulative = channelCountsCumulative.Sum();
                totalCountsOneSecondByUnit[0] = channelCountsOneSecond.Sum(0, 16);
                totalCountsOneSecondByUnit[1] = channelCountsOneSecond.Sum(16, 32);
                totalCountsCumulativeByUnit[0] = channelCountsCumulative.Sum(0, 16);
                totalCountsCumulativeByUnit[1] = channelCountsCumulative.Sum(16, 32);

                thickness.CalculateThickness(channelCountsCumulative);

                // Add the rates to the channelCountsOneSecondTime Circular Buffer.
                if (CheckState(InstrumentState.PAUSED))
                {
                    // Instrument is paused, don't do anything
                }
                else
                {
                    ChannelCountsTime channelCountsTime = new ChannelCountsTime(channelCountsOneSecond);
                    if (channelCountsHistory.Size == 0)
                    {
                        channelCountsHistory.Add(channelCountsTime);
                    }
                    else
                    {
                        if (channelCountsTime.dateTime != channelCountsHistory.Back()?.dateTime)
                        {
                            channelCountsHistory.Add(channelCountsTime);
                        }
                    }
                }

                //Draw the BarPlots
                DrawBitmapBarPlots();

                GetRowRatios(0, rowRatioIndices, channelCountsCumulative, ref rowRatioCumulative);
                GetRowRatios(1, rowRatioIndices, channelCountsCumulative, ref rowRatioCumulative);

                EventRates.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessRates(string[]) --> ", "ProcessRates");
            }
        }

        /// <summary>
        /// Processes the reply Sm2Ib = &lt;(int)elapsedTime&gt;, &lt;(double)R1&gt;, &lt;(double)dR1&gt;, &lt;(double)R2&gt;, &lt;(double)dR2&gt;, <para />
        ///                             &lt;(double)R3&gt;, &lt;(double)dR3&gt;, &lt;(double)Sm2&gt;, &lt;(double)dSm2&gt;, <para />
        ///                             &lt;(double)InvBeta&gt;, &lt;(double)dInvBeta&gt;, <para />
        ///                             &lt;(double)lambda1&gt;, &lt;(double)lambda2&gt;, &lt;(double)fraction&gt;<para />
        /// Suggested:<para />
        /// Write("Sm2Ib")<para />
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        &lt;None&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        Sm2Ib
        /// Get Return: Sm2Ib = &lt;(int)elapsedTime&gt;, &lt;(double)R1&gt;, &lt;(double)dR1&gt;, &lt;(double)R2&gt;, &lt;(double)dR2&gt;, 
        ///                             &lt;(double)R3&gt;, &lt;(double)dR3&gt;, &lt;(double)Sm2&gt;, &lt;(double)dSm2&gt;, 
        ///                             &lt;(double)InvBeta&gt;, &lt;(double)dInvBeta&gt;, 
        ///                             &lt;(double)lambda1&gt;, &lt;(double)lambda2&gt;, &lt;(double)fraction&gt;
        /// 
        /// Suggested:  Write("Sm2Ib")
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessSm2Ib(string[] replies)
        {
            try
            {
                // From:      mc15_control / main_control.c
                if (replies.Length < 2)
                {
                    return;
                }
                //   sprintf(rtncmd, "Sm2Ib = %d, %e,%e,%e,%e,%e,%e,%e,%e,%e,%e,%e,%e,%e",
                //          elapsedTime, R1, dR1, R2, dR2, R3, dR3, Sm2, dSm2, InvBeta, dInvBeta, lambda1, lambda2, fraction);
                Sm2Ib sm2Ib = new Sm2Ib(replies);
                HageSolutionAlpha hageSolutionsAlpha = new HageSolutionAlpha()
                {
                    R1 = new ValueStdev(sm2Ib.R1),
                    R2 = new ValueStdev(sm2Ib.R2),
                    R3 = new ValueStdev(sm2Ib.R3),
                    ML = new ValueStdev(1.0, 0.0),
                    eff = new ValueStdev(hageAlpha.eff),
                    Fs = new ValueStdev(0.0, 0.0),
                    alpha = new ValueStdev(hageAlpha.alpha)
                };

                // Solve for ML and Fs
                // mask = 0x0A  => 0b1010
                hageAlpha.R1 = sm2Ib.R1;
                hageAlpha.R2 = sm2Ib.R2;
                hageAlpha.R3 = sm2Ib.R3;
                hageAlpha.R4 = new ValueStdev();

                // If the isotope is Cf-252, then solve for mass and efficiency (0x06).
                // otherwise solve for mass and multiplication (0x0A)
                string selectedIsotope = hageAlpha.vs1.GetVs1().Name;

                int mask = selectedIsotope == "Cf-252" ? 0x06 : 0x0A;

                hageAlpha.Solve(ref hageSolutionsAlpha, mask);


                hageSolutionsAlpha.thickness = thickness.thickness.DeepCopy();
                hageSolutionsAlpha.rowRatios = thickness.rowRatios.DeepCopy();

                if (CheckState(InstrumentState.PAUSED))
                {
                    // Instrument is Paused.
                    // Don't do anything
                    // This is a place holder
                }
                else
                {
                    // Instrument is not Paused
                    if (sm2IbOneSecondTime.Size.Equals(0))
                    {
                        sm2IbOneSecondTime.Add(sm2Ib);
                    }
                    else
                    {
                        if (sm2IbOneSecondTime.Back()?.dateTime != sm2Ib.dateTime)
                        {
                            sm2IbOneSecondTime.Add(sm2Ib);
                        }
                    }
                    if (hageSolutionsAlphaOneSecondTime.Size.Equals(0))
                    {
                        hageSolutionsAlphaOneSecondTime.Add(hageSolutionsAlpha);
                    }
                    else
                    {
                        // if (!hageSolutionsAlphaOneSecondTime.Back().dateTime.Equals(hageSolutionsAlpha.dateTime))
                        if (hageSolutionsAlphaOneSecondTime.Back()?.dateTime != hageSolutionsAlpha.dateTime)
                        {
                            hageSolutionsAlphaOneSecondTime.Add(hageSolutionsAlpha);
                        }
                    }
                }

                threatId.CalculateThreat(sm2Ib.Beta);

                threatIdOneSecondTime.Add(new ThreatIdValues(threatId));
                EventSm2Ib.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessSm2Ib(string[]) --> ", "ProcessSm2Ib");
            }
        }

        /// <summary>
        /// Processes the reply Storage = &lt;(int)storage location&gt;,<para />
        /// Suggested:<para />
        /// WriteAndCheck("Storage", "&lt;(int)storage location&gt;")<para />
        /// Write("Storage")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        Storage = &lt;(int)storage location&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        Storage
        /// Get Return: Storage = &lt;(int)storage location&gt;
        /// 
        /// Suggested:  WriteAndCheck("Storage", "&lt;(int)storage location&gt;")
        ///             Write("Storage")
        /// 
        ///    UNDEFINED  = -1,
        ///    NONE       =  0,
        ///    NET        =  1,
        ///    USB        =  2,
        ///    INSTRUMENT =  3
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessStorage(string[] replies)
        {
            //public enum InstrumentStorageLocation : int
            //{
            //    UNDEFINED  = -1,
            //    NONE       =  0,
            //    NET        =  1,
            //    USB        =  2,
            //    INSTRUMENT =  3
            //};
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                this.storageLocation = replies[1].ToInstrumentStorageLocation();
                EventStorage.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessStorage(string[]) --> ", "ProcessStorage");
            }
        }

        /// <summary>
        /// Processes the reply Time = &lt;(string)day name&gt;, &lt;month name&gt;, &lt;(int)day num&gt;, &lt;(int)hour num&gt;<para />
        ///                            :&lt;(int)min num&gt;:&lt;(int)sec num&gt; &lt;(int)year num&gt;<para />
        /// e.g. Time = Sat Jun  8 07:52:49 2024<para />
        /// Suggested:<para />
        /// SetTime()
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        Write("Time = " + DateTime.Now.ToString("yyyy MM dd HH mm ss"))
        /// Set Return: &lt;None&gt;
        /// Get:        Time
        /// Get Return: Time = &lt;(string)day name&gt;, &lt;month name&gt;, &lt;(int)day num&gt;, 
        ///                    &lt;(int)hour num&gt;:&lt;(int)min num&gt;:&lt;(int)sec num&gt; &lt;(int)year num&gt;
        /// 
        /// Suggested:  SetTime()
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessTime(string[] replies)
        {
            //https://stackoverflow.com/questions/4189520/using-invokerequired-when-not-a-form
            try
            {
                if (replies.Count() < 2)
                {
                    return;
                }
                // Right now I'm going to ignore processing the time stamp.
                //string format = "ddd MMM d HH:mm:ss yyyy";
                //this.dateTime = DateTime.ParseExact(replies[1],
                //    format,
                //    CultureInfo.InvariantCulture,
                //    DateTimeStyles.AllowWhiteSpaces);
                //Match match = Regex.Match(replies[1], @"\d{2} \d{2} \d{2}:\d{2}")
                //EventTime.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessTime(string[]) --> ", "ProcessTime");
            }
        }


        /// <summary>
        /// Processes the reply Totals = &lt;(int)collection time&gt;, &lt;(int)channel counts[0]&gt;, ... &lt;(int)channel counts[31]&gt;, &lt;(int)total counts&gt;<para />
        /// Suggested:<para />
        /// Write("Totals")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        &lt;None&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        Totals
        /// Get Return: Totals = &lt;(int)collection time&gt;, &lt;(int)channel counts[0]&gt;, ... &lt;(int)channel counts[31]&gt;, &lt;(int)total counts&gt;
        /// 
        /// Suggested:  Write("Totals")
        /// 
        /// Note:       Totals is only available when an acquisition is ongoing. If Totals is sent and the acquisition is not ongoing then nothing is returned.
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessTotals(string[] replies)
        {
            /// The return for Totals is
            /// Totals = Collection Time, counts in channel[0], ... counts in channel[31], total number of counts.
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                // Clear the array. This is cheap insurance to ensure the data are correct.
                elapsedTime = 0;
                Array.Clear(channelCountsTotals, 0, channelCountsTotals.Count());
                Array.Clear(totalCountsTotalByUnit, 0, totalCountsTotalByUnit.Count());
                if (replies.Length < 3)
                {
                    return;
                }
                elapsedTime = replies[1].ToUInt32(0);

                int I = replies.Length - 3; // The last value in the string will be the sum of all of the counts.
                for (int i = 0; i < I; ++i)
                {
                    uint counts = replies[i + 2].ToUInt32(0);
                    channelCountsTotals[i] = counts;
                }
                totalCountsTotal = Convert.ToUInt32(replies.Last());
                totalCountsTotalByUnit[0] = channelCountsTotals.Sum(0, 16);
                totalCountsTotalByUnit[1] = channelCountsTotals.Sum(16, 32);
                GetRowRatios(0, rowRatioIndices, channelCountsTotals, ref rowRatioTotals);
                GetRowRatios(1, rowRatioIndices, channelCountsTotals, ref rowRatioTotals);
                EventTotals.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessTotals(string[]) --> ", "ProcessTotals");
            }
        }

        /// <summary>
        /// Processes the reply USBsaveFile = &lt;(int)result&gt;<para />
        /// Suggested:<para />
        /// Write("USBsaveFile = &lt;(string)filename&gt;")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        USBsaveFile = &lt;(string)filename&gt;
        /// Set Return: USBsaveFile = &lt;(int)file save status&gt;
        /// Get:        &lt;None&gt;
        /// Get Return: &lt;None&gt;
        /// 
        /// Suggested:  Write("USBsaveFile = &lt;(string)filename&gt;")
        /// 
        /// Note:       Use USBsaveFileProgress instead to determine status.
        /// </remarks>
        /// <example>
        /// Send>    USBsaveFile
        /// Receive> USBsaveFile = -4
        /// 
        /// Send>    USBsaveFile = file_name
        /// Receive> USBsaveFile = 1
        /// </example>
        /// <param name="replies"></param>
        public void ProcessUSBsaveFile(string[] replies)
        {
            // Source Code for this is found in MC-15 / mc15_control / USB.c
            // and in
            // MC-15 / mc15_control / main_control.c
            //
            /// (MAN) 2024/01/06 -> Ignoring this for now.
            /// 
            // returns are USBsaveFile filename = 1   : in progress
            //             USBsaveFile filename = 0   : unknown error. did not do it. send for no file.
            //             USBsaveFile filename = -1  : not enough space
            //             USBsaveFile filename = -2  : no usb drive found -> Will return -2 here.
            //             USBsaveFile filename = -3  : filename not found in LFS
            //             USBsaveFile filename = -4  : no proper filename sent

            // Correlation between return values of USBsaveFile & USBsaveFileProgresss
            // USBsaveFile	                    USBsaveFileProgress
            //   1	In Progress			            Positive ->	percent done	In Progress
            //  -1	No Space			            -1	No Space	
            //  -2	No Mount			            -2	No Mount	
            //  -3	File not Found			        -3	Idle	
            //  -4	No proper filename sent					

            try
            {
                int flag = 0;
                fileCurrent.name = "";
                percentComplete = 0;
                // The ordering of the Length is important.
                // Always place the longest return first.
                if (replies.Length < 2)
                {
                    return;
                }
                if (replies.Length > 2)
                {
                    // This will give both the current filename and state it's in progress.
                    // This is normally returned after the filename is sent
                    // flag = Convert.ToInt32(replies[1]);
                    flag = Convert.ToInt32(replies[1]);
                    fileCurrent.name = replies[2];
                }
                else if (replies.Length > 1)
                {
                    // USBsaveFile = 1  will occur right after USBsaveFile = <filename> is sent & it's successful
                    flag = Convert.ToInt32(replies[1]);
                    // No filename is sent so keep it empty.
                }
                else
                {
                    // No replies where sent
                    // Don't do anything
                    return;
                }
                usbFileState = flag.ToUsbFileState();

                EventUSBsaveFileProgress.Solicit(this);
                EventUSBsaveFile.Solicit(this);    // Most likely I don't want to attach anything to this.
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessUSBsaveFile(string[]) --> ", "ProcessUSBsaveFile");
            }
        }

        /// <summary>
        /// Processes the reply USBsaveFileCancel = &lt;(string)result&gt;<para />
        /// Suggested:<para />
        /// Write("USBsaveFileCancel")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        USBsaveFileCancel
        /// Set Return: USBsaveFileCancel = &lt;(string)result&gt;
        /// Get:        &lt;None&gt;
        /// Get Return: &lt;None&gt;
        /// 
        /// Suggested:  Write("USBsaveFileCancel")
        /// 
        /// Note:       The only reply for this command is
        ///             USBsaveFileCancel = Done
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessUSBsaveFileCancel(string[] replies)
        {
            // From here is "Done"
        }

        /// <summary>
        /// Processes the reply USBsaveFileProgress = &lt;(int)result&gt;<para />
        /// Suggested:<para />
        /// Write("USBsaveFileProgress")<para />
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        &lt;None&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        USBsaveFileProgress
        /// Get Return: ** Occurs when no file is being saved. **
        ///             USBsaveFileProgress = &lt;(int)result&gt; 
        ///             ** Occurs when a file is being saved or the first time USBsaveFileProgress is called after saving a file. **
        ///             USBsaveFileProgress = &lt;percentComplete&gt;, &lt;currentFilename&gt; 
        /// 
        /// Suggested:  Write("USBsaveFileProgress")
        /// 
        /// Note:       If replies.Length == 2 then no file is being saved
        ///             If replies.Length == 3 then a file is being saved or just finished saving
        /// </remarks>
        /// <param name="replies"></param>

        public void ProcessUSBsaveFileProgress(string[] replies)
        {
            // There was an error? Maybe?
            // -1 => instr.file.state == FILESTATE_NOSPACE
            // -2 => instr.file.state == FILESTATE_NOMOUNT
            // -3 => for FILESTATE_IDLE

            // USBsaveFileProgress = <percent Complete>, <current filename>
            //    or
            // USBsaveFileProgress = <status>

            // Correlation between return values of USBsaveFile & USBsaveFileProgresss
            // USBsaveFile	                    USBsaveFileProgress
            //   1	In Progress			            Positive ->	percent done	In Progress
            //  -1	No Space			            -1	No Space	
            //  -2	No Mount			            -2	No Mount	
            //  -3	File not Found			        -3	Idle	
            //  -4	No proper filename sent					

            try
            {
                percentComplete = 0;
                if (replies.Length < 2)
                {
                    // No return values
                    // Do nothing
                    return;
                }
                if (replies.Length > 2)
                {
                    // USBsaveFileProgress = <percent Complete>, <current filename>
                    int result = replies[1].ToInt32(0);
                    usbFileState = Convert.ToInt32(replies[1]).ToUsbFileState();
                    percentComplete = (usbFileState == UsbFileState.InProgress) ? result : 0;
                    fileCurrent.name = replies[2];
                }
                else if (replies.Length > 1)
                {
                    // USBsaveFileProgress = <status>
                    fileCurrent.name = "";
                    percentComplete = 0;
                    usbFileState = Convert.ToInt32(replies[1]).ToUsbFileState();
                }
                EventUSBsaveFileProgress.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessUSBsaveFileProgress(string[]) --> ", "ProcessUSBsaveFileProgress");
            }
        }

        /// <summary>
        /// Processes the reply UserMode = &lt;(string)result&gt;<para />
        /// UserMode corresponds to if you RunToDuration (USER_MODE.FIELD == 0) or RunToCounts (USER_MODE.LAB == 1).<para />
        /// Suggested:<para />
        /// WriteAndCheck("UserMode", "&lt;(int)user mode&gt;")<para />
        /// Write("UserMode")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        UserMode = &lt;(int)user mode&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        UserMode
        /// Get Return: UserMode = &lt;(int)user mode&gt;
        /// 
        /// Suggested:  WriteAndCheck("UserMode", "&lt;(int)user mode&gt;")
        ///             Write("UserMode")
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessUserMode(string[] replies)
        {
            //public enum UserMode : int
            //{
            //    Undefined = -1,
            //    RunToDuration = 0,      //USER_MODE.FIELD
            //    RunToCounts = 1         //USER_MODE.LAB
            //}
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                this.userMode = replies[1].ToUserMode();
                EventUserMode.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessUserMode(string[]) --> ", "ProcessUserMode");
            }
        }

        /// <summary>
        /// Processes the reply Version = &lt;(string)linuxCCodeVersion&gt;, &lt;(string)firmwareVersion&gt;<para />
        /// Suggested:<para />
        /// Write("Version")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        &lt;None&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        Version
        /// Get Return: Version = &lt;(string)linuxCCodeVersion&gt;, &lt;(string)firmwareVersion&gt;
        /// 
        /// Suggested:  Write("Version")
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessVersion(string[] replies)
        {
            try
            {
                // linuxCCodeVersion = VVVVMMYY
                // e.g.              = 14150122
                //                   = 1415 => Version
                //                   = 09   => January
                //                   = 22   => Year 2022
                // 
                // firmwareVersion   = VFVFMMYY
                // e.g.              = 3b5f0520
                //                   = 3b5f => Version 3.5
                //                          => b => MC-15
                //                          => f => Feynmans Loaded
                //                   = 05   => May
                //                   = 21   => Year 2021
                linuxCCodeVersion = replies[1];
                firmwareVersion = replies[2];
                EventVersion.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessVersion(string[]) --> ", "ProcessVersion");
            }
        }

        /// <summary>
        /// Processes the reply analysisMask = &lt;(hex)analysis mask&gt;<para />
        /// Suggested:<para />
        /// WriteAndCheck("analysisMask", "&lt;(hex)analysis mask&gt;")<para />
        /// Write("analysisMask")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        analysisMask = &lt;(hex)analysis mask&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        analysisMask
        /// Get Return: analysisMask = &lt;(hex)analysis mask&gt;
        /// 
        /// Suggested:  WriteAndCheck("analysisMask", "&lt;(hex)analysis mask&gt;")
        ///             Write("analysisMask")
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessAnalysisMask(string[] replies)
        {
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                analysisMask = Convert.ToUInt32(replies[1], 16);
                EventAnalysisMask.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessAnalysisMask(string[]) --> ", "ProcessAnalysisMask");
            }
        }


        /// <summary>
        /// Processes the reply audioBeat = &lt;(int)OnTime&gt;, &lt;(int)OffTime&gt;, &lt;(int)Reps&gt;<para />
        /// Suggested:<para />
        /// Write("audioBeat = &lt;(int)OnTime&gt;, &lt;(int)OffTime&gt;, &lt;(int)Reps&gt;")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        audioBeat = &lt;(int)OnTime&gt;, &lt;(int)OffTime&gt;, &lt;(int)Reps&gt;
        /// Set Return: Makes the unit beep.
        /// Get:        audioBeat
        /// Get Return: audioBeat = 1,1,1   => Does nothing
        /// 
        /// Suggested:  WriteAndCheck("analysisMask", "&lt;(hex)analysis mask&gt;")
        ///             Write("analysisMask")
        /// 
        /// Note:       This is here just for documentation. There really is no useable reply with audioBeat
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessAudioBeat(string[] replies) { }


        /// <summary>
        /// Processes the reply audioEnable = &lt;(bool)On/Off&gt;.<para />
        /// There is no reply. audioEnable sets if the detector should beep every so many counts, which is set by audioRate.<para />
        /// Suggested:<para />
        /// Write("audioEnable = &lt;(bool)On/Off&gt;")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        audioEnable = &lt;(bool)On/Off&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        &lt;None&gt;
        /// Get Return: &lt;None&gt;
        /// 
        /// Suggested:  Write("audioEnable = &lt;(bool)On/Off&gt;")
        /// 
        /// Note:       This is here just for documentation. There really is no useable reply with audioEnable
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessAudioEnable(string[] replies) { }

        /// <summary>
        /// Processes the reply audioRate = &lt;(int)beep every so many counts&gt;.<para />
        /// Suggested:<para />
        /// Write("audioRate = &lt;(int)beep every so many counts&gt;")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        audioRate = &lt;(int)beep every so many counts&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        &lt;None&gt;
        /// Get Return: &lt;None&gt;
        /// 
        /// Suggested:  Write("audioEnable = &lt;(bool)On/Off&gt;")
        /// 
        /// Note:       This is here just for documentation. There really is no useable reply with audioEnable
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessAudioRate(string[] replies) { }


        /// <summary>
        /// Processes the reply audioState = &lt;(int)audio state&gt;<para />
        /// 
        /// Suggested:<para />
        /// WriteAndCheck("audioState", "&lt;(int)audioState&gt;")<para />
        /// Write("audioState")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        audioState = &lt;(int)audio state&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        audioRate
        /// Get Return: audioState = &lt;(int)audio state&gt;
        /// 
        /// Suggested:  WriteAndCheck("audioState", "&lt;(int)audioState&gt;")
        ///             Write("audioState = &lt;(int)audio state&gt;")
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessAudioState(string[] replies)
        {
            // public enum AudioState : int
            // {
            //    // Look in mc15_control/audioThread.c for more information
            //    Undefined   = -1,
            //    Off         =  0,
            //    On          =  1,
            //    Beat        =  2,
            //    Sweep       =  3,
            //    Rate        =  4
            // }

            // Information on audioState can be found in
            // mc15_control/audioThread.c
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                this.audioState = replies[1].ToAudioState();
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessAudioState(string[]) --> ", "ProcessAudioState");
            }
        }

        /// <summary>
        /// Processes the reply audioTone = &lt;(int)volume&gt;, &lt;frequency&gt;<para />
        /// 
        /// Suggested:<para />
        /// WriteAndCheck("audioTone", "&lt;(int)volume&gt;, &lt;frequency&gt;")<para />
        /// Write("audioTone")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        audioTone = &lt;(int)volume&gt;, &lt;frequency&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        audioTone
        /// Get Return: audioTone = &lt;(int)volume&gt;, &lt;frequency&gt;
        /// 
        /// Suggested:  WriteAndCheck("audioTone", "&lt;(int)volume&gt;, &lt;frequency&gt;")
        ///             Write("audioTone")
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessAudioTone(string[] replies)
        {
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                if (replies.Length > 1)
                {
                    audioVolume = replies[1].ToInt32(-1);
                }
                if (replies.Length > 2)
                {
                    audioVolume = replies[2].ToInt32(-1);
                }
                EventAudioTone.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessAudioTone(string[]) --> ", "ProcessAudioTone");
            }

        }

        /// <summary>
        /// Processes the reply channelMask = &lt;(hex)channel mask&gt;<para />
        /// The channelMask determines which channels are recorded.<para />
        /// 
        /// Suggested:<para />
        /// WriteAndCheck("channelMask", "&lt;(hex)channel mask&gt;")<para />
        /// Write("channelMask")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        channelMask = &lt;(hex)channel mask&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        channelMask
        /// Get Return: channelMask = &lt;(hex)channel mask&gt;
        /// 
        /// Suggested:  WriteAndCheck("channelMask", "&lt;(hex)channel mask&gt;")
        ///             Write("channelMask")
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessChannelMask(string[] replies)
        {
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                channelMask = Convert.ToUInt32(replies[1], 16);
                EventChannelMask.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessChannelMask(string[]) --> ", "ProcessChannelMask");
            }
        }

        /// <summary>
        /// Processes the reply cStatus = &lt;(bool)verbose&gt;, &lt;(int)PSstate&gt;, &lt;(int)hv_setpoint&gt;,<para />
        ///                               &lt;(int)hv_value&gt;, &lt;(int)hv_max&gt;, &lt;(int)0&gt;, <para />
        ///                               &lt;(int)storage&gt;, &lt;(int)vetoActive&gt;, &lt;(int)vetoTrueState&gt;, <para />
        ///                               &lt;(double)distPriToSource&gt;, &lt;(double)distPriToFloor&gt;<para />
        /// 
        /// Suggested:<para />
        /// Write("cStatus")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        &lt;None&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        cStatus
        /// Get Return: cStatus = &lt;(bool)verbose&gt;, &lt;(int)PSstate&gt;, &lt;(int)hv_setpoint&gt;,
        ///                       &lt;(int)hv_value&gt;, &lt;(int)hv_max&gt;, &lt;(int)0&gt;, 
        ///                       &lt;(int)storage&gt;, &lt;(int)vetoActive&gt;, &lt;(int)vetoTrueState&gt;, 
        ///                       &lt;(double)distPriToSource&gt;, &lt;(double)distPriToFloor&gt;
        /// 
        /// Suggested:  Write("cStatus")
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessCStatus(string[] replies)
        {
            try
            {
                //sprintf(rtncmd, "cStatus = %d,%d,%d,%d,%d,%d,%d,%d,%d,%f,%f",
                //        instr.verbose, 
                //        instr.PSstate,
                //        instr.hv_setpoint, 
                //        instr.hv_value, 
                //        instr.hv_max,
                //        0, 
                //        instr.storage,
                //        instr.lmc.vetoActive, 
                //        instr.lmc.vetoTrueState,
                //        instr.distPriToSource, 
                //        instr.distPriToFloor);
                if (replies.Length < 2)
                {
                    return;
                }
                if (replies.Length > 1)
                {
                    // verbose
                    verbose = replies[1].ToBool();
                }
                if (replies.Length > 2)
                {
                    // PSState => PrimarySecondary State
                    podConfig = replies[2].ToPodConfig();
                }
                if (replies.Length > 3)
                {
                    //hv_setpoint
                    hvSet = replies[3].ToInt32(-1);
                }
                if (replies.Length > 4)
                {
                    //hv_value
                    hvRead = replies[4].ToInt32(-1);
                }
                if (replies.Length > 5)
                {
                    //hv_max
                    hvMax = replies[5].ToInt32(-1);
                }
                if (replies.Length > 6)
                {
                    // Always is 0. I don't know what it corresponds to

                }
                if (replies.Length > 7)
                {
                    //storage
                    storageLocation = replies[7].ToInstrumentStorageLocation();
                }
                if (replies.Length > 8)
                {
                    //vetoActive
                    vetoActive = replies[8].ToBool();
                }
                if (replies.Length > 9)
                {
                    //vetoTrueState
                    vetoTrueState = replies[9].ToVetoTrueState();
                }
                if (replies.Length > 10)
                {
                    //distPriToSource
                    distPriToSrc = replies[10].ToDouble(0.0);
                }
                if (replies.Length > 11)
                {
                    //distPriToFloor
                    distPriToFlr = replies[11].ToDouble(0.0);
                }
                EventCStatus.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessCStatus(string[]) --> ", "ProcessCStatus");
            }

        }

        /// <summary>
        /// Processes when the response is deadTime = &lt;(int)deadTime&gt; ns<para />
        /// 
        /// Suggested:<para />
        /// WriteAndCheck("deadTime", "&lt;(int)deadTime&gt;")<para />
        /// Write("deadTime")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        deadTime = &lt;(int)deadTime&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        deadTime
        /// Get Return: deadTime = &lt;(int)deadTime&gt;
        /// 
        /// Suggested:  WriteAndCheck("deadTime", "&lt;(int)deadTime&gt;"(
        ///             Write("deadTime")
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessDeadTime(string[] replies)
        {
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                // the reply is 
                // deadTime = <deadTime> ns
                // e.g. 
                // deadTime = 1000 ns
                // the reply string needs to be split again at the space.
                //string[] repliesSplit = replies[1].Split(new char[] { ' ' });
                string[] repliesSplit = replies[1].Split((char)' ');
                this.deadtime = repliesSplit[0].ToUInt32(1000);
                EventDeadTime.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessDeadTime(string[]) --> ", "ProcessDeadTime");
            }

        }

        /// <summary>
        /// Processes the command dmaTimeInterval = &lt;(int)time interval milliseconds&gt;<para />
        /// 
        /// Suggested:<para />
        /// WriteAndCheck("dmaTimeInterval", "&lt;(int)time interval milliseconds&gt;")<para />
        /// Write("dmaTimeInterval")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        dmaTimeInterval = &lt;(int)time interval milliseconds&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        dmaTimeInterval
        /// Get Return: dmaTimeInterval = &lt;(int)time interval milliseconds&gt;
        /// 
        /// Suggested:  WriteAndCheck("dmaTimeInterval", "&lt;(int)time interval milliseconds&gt;")
        ///             Write("dmaTimeInterval")
        /// 
        /// Note:       dma time slice in milliseconds
        ///             let 50 &lt;= ts &gt;= 500 mS
        ///             time_slice is # of 10nS cpu tics so multiply by 100000
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessDmaTimeInterval(string[] replies)
        {
            // dma time slice in milliseconds
            // let 50 <= ts <= 500 mS
            // time_slice is # of 10nS cpu tics so multiply by 100000
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                this.dmaTimeInterval = replies[1].ToUInt32();
                EventDmaTimeInterval.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessDmaTimeInterval(string[]) --> ", "ProcessDmaTimeInterval");
            }
        }

        /// <summary>
        /// Processes the command ipAddr = &lt;(string)IPv4Address&gt;<para />
        /// The IPv4Address is a string with the numbers separated by "."<para />
        /// IPv4Address = &lt;(int)IPv4[0]&gt;.&lt;(int)IPv4[1]&gt;.&lt;(int)IPv4[2]&gt;.&lt;(int)IPv4[3]&gt;<para />
        /// e.g. ipAddr = 169.254.30.30<para />
        /// 
        /// Suggested:<para />
        /// SetIpAddress()<para />
        /// SetIpAddressAndCheck()<para />
        /// WriteAndCheck("ipAddr", "&lt;(string)IPv4Address&gt;")<para />
        /// Write("ipAddr")<para />
        /// e.g. Ipv4Address = 169.254.30.30<para />
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        ipAddr = &lt;(string)IPv4Address&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        ipAddr
        /// Get Return: ipAddr = &lt;(string)IPv4Address&gt;
        /// 
        /// Suggested:  SetIpAddress()
        ///             SetIpAddressAndCheck()
        ///             WriteAndCheck("ipAddr", "&lt;(int)IPv4[0]&gt;.&lt;(int)IPv4[1]&gt;.&lt;(int)IPv4[2]&gt;.&lt;(int)IPv4[3]&gt;")
        ///             Write("dmaTimeInterval")
        /// 
        /// Note:       IPv4Address = &lt;(int)IPv4[0]&gt;.&lt;(int)IPv4[1]&gt;.&lt;(int)IPv4[2]&gt;.&lt;(int)IPv4[3]&gt;
        ///             e.g. ipAddr = 169.254.30.30    
        ///             replies.Length == 2 because SplitToValues only uses '=' and ','
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessIpAddr(string[] replies)
        {
            //ipv4Address
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                //string[] addr = replies[1].SplitToValues(new char[] { '.' });
                string[] addr = replies[1].Split((char)'.').Select(a => a.Trim(new char[] { '\n', '\0', ' ' })).Where(a => !string.IsNullOrEmpty(a)).ToArray();
                int I = Math.Min(addr.Length, 4);
                for (int i = 0; i < I; ++i)
                {
                    ipv4Address[i] = addr[i];
                }
                EventIpAddr.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessIpAddr(string[]) --> ", "ProcessIpAddr");
            }
        }

        /// <summary>
        /// Processes the command macAddr = &lt;(string)macAddress&gt;<para />
        /// The macAddress is a string with the numbers separated by :<para />
        /// macAddress = &lt;(hex)macAddr[0]&gt;:&lt;(hex)macAddr[1]&gt;:&lt;(hex)macAddr[2]&gt;:&lt;(hex)macAddr[3]&gt;:&lt;(hex)macAddr[4]&gt;:&lt;(hex)macAddr[5]&gt;<para />
        /// e.g. macAddr = "00:0a:35:00:22:01"<para />
        /// 
        /// Suggested:<para />
        /// SetMacAddress()<para />
        /// SetMacAddressAndCheck()<para />
        /// WriteAndCheck("macAddr", "&lt;(string)macAddress&gt;")<para />
        /// Write("macAddr")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        macAddr = &lt;(string)macAddress&gt"
        /// Set Return: &lt;None&gt;
        /// Get:        macAddr
        /// Get Return: macAddr = &lt;(string)macAddress&gt"
        /// 
        /// Suggested:  SetMacAddress()
        ///             SetMacAddressAndCheck()
        ///             WriteAndCheck("macAddr", "&lt;(string)macAddress&gt;")
        ///             Write("macAddr")
        /// 
        /// Note:       macAddress = &lt;(hex)macAddr[0]&gt;:&lt;(hex)macAddr[1]&gt;:&lt;(hex)macAddr[2]&gt;:&lt;(hex)macAddr[3]&gt;:&lt;(hex)macAddr[4]&gt;:&lt;(hex)macAddr[5]&gt;
        ///             e.g. macAddr = "00:0a:35:00:22:01"
        ///             replies.Length == 2 because SplitToValues only uses '=' and ','
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessMacAddr(string[] replies)
        {
            // replies[0] = "macAddr"
            // replies[1] = "00:0a:35:00:22:01"
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                //this.macAddress = replies[1].Split(new char[] { ':' }).Select(p => p.Trim().ToUpper().AddPreValue(2, "0")).ToArray();
                this.macAddress = replies[1].Split((char)':').Select(p => p.Trim().ToUpper().AddPreValue(2, "0")).ToArray();
                EventMacAddr.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessMacAddr(string[]) --> ", "ProcessMacAddr");
            }
        }

        /// <summary>
        /// Processes the command lcdTemp = &lt;(int)temp&gt;<para />
        /// 
        /// Suggested:<para />
        /// Write("lcdTemp")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        &lt;None&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        lcdTemp
        /// Get Return: lcdTemp = &lt;(int)temp&gt;
        /// 
        /// Suggested:  Write("lcdTemp")
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessLcdTemp(string[] replies)
        {
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                lcdDieTemp = replies[1].ToInt32(-1);
                EventLcdTemp.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessLcdTemp(string[]) --> ", "ProcessLcdTemp");
            }
        }

        /// <summary>
        /// Processes the command maxSamplesInFile = &lt;(int)max samples in file&gt;<para />
        /// This is used to limit the maximum number of samples in a file to prevent the file from getting to large. <para />
        /// We want to keep the max file size to &lt; 2GBytes<para />
        /// 
        /// Suggested:<para />
        /// WriteAndCheck("maxSamplesInFile", "&lt;(int)max samples&gt;")<para />
        /// Write("maxSamplesInFile")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        maxSamplesInFile = &lt;(int)max samples in file&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        maxSamplesInFile
        /// Get Return: maxSamplesInFile = &lt;(int)max samples in file&gt;
        /// 
        /// Suggested:  WriteAndCheck("maxSamplesInFile", "&lt;(int)max samples&gt;")
        ///             Write("maxSamplesInFile")
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessMaxSamplesInFile(string[] replies)
        {
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                maxSamplesInFile = replies[1].ToUInt32();
                EventMaxSamplesInFile.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessMaxSamplesInFile(string[]) --> ", "ProcessMaxSamplesInFile");
            }
        }

        /// <summary>
        /// Processes the command reps = &lt;(int)currentRep&gt;, &lt;(int)repTotal&gt;<para />
        /// 
        /// Suggested:<para />
        /// WriteAndCheck("Rep", "&lt;(int)currentRep&gt;, &lt;(int)repTotal&gt;")<para />
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        Rep = &lt;(int)currentRep&gt;, &lt;(int)repTotal&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        Rep
        /// Get Return: reps = &lt;(int)currentRep&gt;, &lt;(int)repTotal&gt;
        /// 
        /// Suggested:  WriteAndCheck("Rep", "&lt;(int)currentRep&gt;, &lt;(int)repTotal&gt;")
        ///             Write("Rep")
        /// 
        /// Note:       I don't think "reps = &lt;(int)currentRep&gt;, &lt;(int)repTotal&gt;" ever gets reported back through TCP/IP.
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessReps(string[] replies)
        {
            // I don't think there is any return on Reps from lmc_control.
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                if (replies.Length > 1)
                {
                    repetitions[0] = replies[1].ToInt32(-1);
                }
                if (replies.Length > 2)
                {
                    repetitions[1] = replies[2].ToInt32(-1);
                }
                EventReps.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessReps(string[]) --> ", "ProcessReps");
            }
        }

        /// <summary>
        /// Processes the command network_state = &lt;(int)network state&gt;<para />
        /// 
        /// Suggested:<para />
        /// &lt;None&gt;<para />
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        &lt;None&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        &lt;None&gt;
        /// Get Return: &lt;None&gt;
        /// 
        /// Suggested:  &lt;None&gt;
        /// 
        /// Note:       This was in the original GUI but I don't think it's used outside of the mc15_control. 
        /// I included it just in case.
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessNetWorkState(string[] replies)
        {
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                this.networkState = replies[1].ToNetworkState();
                EventNetworkState.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessNetWorkState(string[]) --> ", "ProcessNetWorkState");
            }
        }

        /// <summary>
        /// Processes the command runDescription = &lt;(string)description&gt;<para />
        /// The return string may contain spaces.<para />
        /// Suggested:<para />
        /// WriteAndCheck("runDescription", "&lt;(string)description&gt;")<para />
        /// Write("runDescription")
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        runDescription = &lt;(string)description&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        runDescription
        /// Get Return: runDescription = &lt;(string)description&gt;
        /// 
        /// Suggested:  WriteAndCheck("runDescription", "&lt;(string)description&gt;")
        ///             Write("runDescription")
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessRunDescription(string[] replies)
        {
            try
            {
                if (replies.Length < 2)
                {
                    this.description = "";
                }
                else
                {
                    this.description = replies[1];
                }
                EventRunDescription.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessRunDescription(string[]) --> ", "ProcessRunDescription");
            }
        }

        /// <summary>
        /// Processes the command uncrecognized. There are no set or gets for this command.<para />
        /// 
        /// Suggested:<para />
        /// &lt;None&gt;
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        &lt;None&gt;
        /// Set Return: &lt;None&gt;
        /// Get:        &lt;None&gt;
        /// Get Return: &lt;None&gt;
        /// 
        /// Suggested:  &lt;None&gt;
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessUnrecognized(string[] replies)
        {
            // While booting I'm sending a junk command to lmc_control.
            // If "unrecognized" comes back then I assume lmc_control is up and running and that I want to 
            //   stop timerBoot and reset autoResetEventBoot.
            // Do these things first to ensure they are stopped.
            timerBoot.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            autoResetEventBoot.Set();

            /// There's no set or get with this. This is to only record how many unrecognized commands there are.
            ++numberOfUnrecognizedReplies;

            EventUnrecognizedCommand.Solicit(this);
        }


        /// <summary>
        /// <para>Processes the command updateSystem = &lt;(int)status&gt;</para>
        /// <para>updateSystem will automatically reply with updateSystem = &lt;(int)status&gt;</para>
        /// <para>Suggested:</para>
        /// <para>Write("updateSystem")</para>
        /// </summary>
        /// <remarks>
        /// Location:   mc15_control/main_control.c
        /// Set:        updateSystem
        /// Set Return: updateSystem = &lt;(int)status&gt;
        /// Get:        &lt;None&gt;
        /// Get Return: &lt;None&gt;
        /// 
        /// Suggested:  Write("updateSystem")
        /// 
        /// Note:       
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessUpdateSystem(string[] replies)
        {

            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                updateSystemStatus = replies[1].ToUpdateSystemStatus();
                if (updateSystemStatus.Equals(UpdateSystemStatus.NoError))
                {
                    // These should be set in ProcessCommandSentReboot
                    //linuxDebug = false;     
                    //linuxBooted = false;
                    //startUpComplete = false;
                    Write("reboot");
                    // Probably want a ManualResetEvent here that is triggered by the reply "start"
                }

                EventUpdateSystem.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessUpdateSystem(string[]) --> ", "ProcessUpdateSystem");
            }
        }
        public void ProcessVerbose(string[] replies)
        {
            // This should be the reply when "Debug = <number>"
            // is sent, but I don't think it actually gets reported over the serial port.
            // Nonetheless, I'm putting it in just in case.
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                linuxDebug = Convert.ToBoolean(replies[1]);
                EventVerbose.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessVerbose(string[]) --> ", "ProcessVerbose");
            }
        }

        /// <summary>
        /// Processes the command vetoGateWidth = &lt;(int)gate width in ns&gt; ns<para />
        /// 
        /// Suggested:<para />
        /// WriteAndCheck("vetoGateWidth", "&lt;gate width in ns&gt")<para />
        /// Write("vetoGateWidth")
        /// </summary>
        /// <example>
        /// vetoGateWidth = 2000 nS
        /// </example>
        /// <param name="replies"></param>
        public void ProcessVetoGateWidth(string[] replies)
        {
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                this.vetoGateWidth = replies[1].ToInt32(1000);
                EventVetoGateWidth.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessVetoGateWidth(string[]) --> ", "ProcessVetoGateWidth");
            }
        }

        /// <summary>
        /// Processes the command veto = &lt;(int)veto&gt;<para />
        /// veto = [0, 1]<para />
        /// Suggested:<para />
        /// WriteAndCheck("veto", "&lt;(int)veto&gt;<para />
        /// Write("veto")
        /// </summary>
        /// <remarks>
        /// Sets or gets instr.lmc.vetoActive in lmc_control.c
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessVeto(string[] replies)
        {
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                this.vetoActive = replies[1].ToBool();
                EventVeto.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessVeto(string[]) --> ", "ProcessVeto");
            }
        }

        /// <summary>
        /// Processes the command vetoTrue = &lt;(int)VetoTrueState&gt;<para />
        /// 
        /// Suggested:<para />
        /// WriteAndCheck("vetoTrue", "&lt;(int)VetoTrueState*gt;)<para />
        /// Write("vetoTrue")
        /// </summary>
        /// <remarks>
        /// VetoTrueState is either high or low.
        /// Send: vetoTrue = 0
        ///       there is no reply
        /// Receive: vetoTrue = 0
        ///       you must actively ask for vetoTrue state.
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessVetoTrue(string[] replies)
        {
            //////////////////////////////////////////
            // From main_control.c
            // set veto true state 0 or 1 only.  When the external line equals this state, it is a true veto
            // used in conjunction with vetoActive. firmware is set to veto true = 1 so no need to set it if that is prefered.
            //////////////////////////////////////////
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                this.vetoTrueState = replies[1].ToVetoTrueState();

                EventVetoTrue.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessVeto(string[]) --> ", "ProcessVeto");
            }
        }

        public void ProcessStartUpComplete(string[] replies)
        {
            // startUpComplete
            try
            {
                EventStartUpComplete.Solicit(this);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessStartUpComplete(string[]) --> ", "ProcessStartUpComplete");
            }

        }

        /// <summary>
        /// Processes the reply "go:error: assay state not idle."
        /// </summary>
        /// <param name="replies"></param>
        public void ProcessGoError(string[] replies)
        {
            errorCodes.Add(ErrorCode.ERRORGO);
        }

        public void ProcessStart(string[] replies)
        {

        }
        #endregion Processes

        #region ProcessCommandSent

        /// <summary>
        /// Keeps track of the last index of a Feynman requested for Feynman = &lt;index&gt; &amp; Moments = &lt;index&gt;
        /// </summary>
        /// <param name="replies"></param>
        public void ProcessCommandSentLastFeynman_PreSend(string[] replies)
        {
            try
            {
                if (replies.Length < 2)
                {
                    return;
                }
                this.currentFeynmanIndex = replies[1].ToInt32(-1);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessCommandSentLastFeynman(string[]) --> ", "ProcessCommandSentLastFeynman");
            }
        }
        public void ProcessCommandSentMother_PreSend(string[] replies)
        {

        }

        public void ProcessNetSaveFile_PreSend(string[] replies)
        {
            try
            {
                if (replies.Length < 2)
                {
                    netSaveFileName = "";
                    return;
                }
                netSaveFileName = replies[1];
                currentNetLmxPath = string.Empty;
                lmxFileheader = "";
                lmxHeaderWritten = false;
            }
            catch (Exception ex)
            {
                HandleException(ex, "Detector.ProcessNetSaveFile_PreSend(string[]) --> ", "ProcessNetSaveFile_PreSend");
            }
        }
        /// <summary>
        /// Set the detector state to Active when "Go" is sent.
        /// </summary>
        /// <param name="replies"></param>
        public void ProcessCommandSentGo_PreSend(string[] replies)
        {
            sm2IbOneSecondTime.Clear();
            hageSolutionsAlphaOneSecondTime.Clear();

            if (CheckState(InstrumentState.ACTIVE))
            {
            }
            else
            {
                // There is no return from typing "Go" so we must set the acquisition state here.
                SetState(InstrumentState.ACTIVE);
                EventInstrumentStateHasChanged.Solicit(this);
                EventSentGo_Presend.Solicit(this);
            }
        }

        public void ProcessCommandSentCancel_PreSend(string[] replies)
        {
            // I don't think there are any presend items that need to be addressed.
            // Keeping this for now as a place holder.
        }

        /// <summary>
        /// <para>Sets the instrumentState bit as requested by state.</para>
        /// <para>e.g. SetState(ONLINE) will set the ONLINE bit to 1.</para>
        /// </summary>
        /// <param name="state"></param>
        /// <returns></returns>
        public InstrumentState SetState(InstrumentState state)
        {
            instrumentState |= state;
            return instrumentState;
        }

        /// <summary>
        /// <para>Clears the instrumentState bit as requested by state.</para>
        /// <para>e.g. ClearState(ONLINE) will set the ONLINE bit to 0.</para>
        /// </summary>
        /// <param name="state"></param>
        /// <returns></returns>
        public InstrumentState ClearState(InstrumentState state)
        {
            instrumentState &= ~state;
            return instrumentState;
        }

        /// <summary>
        /// <para>Checks to see if the instrument state is the same as state.</para>
        /// <para>Checks to see if the selected bit is set.</para>
        /// <para>Will return if OFFLINE and CheckState(OFFLINE) is tested.</para>
        /// </summary>
        /// <param name="state"></param>
        /// <returns></returns>
        public bool CheckState(InstrumentState state)
        {
            // if you are requesting to see if it's offline
            if (state == InstrumentState.OFFLINE)
            {
                return state == InstrumentState.OFFLINE;
            }
            return (instrumentState & state) != 0;
        }

        public InstrumentState ToggleState(InstrumentState state)
        {
            return (instrumentState ^= state);
        }

        public void ProcessCommandSentResume_PreSend(string[] replies)
        {
            if (CheckState(InstrumentState.PAUSED))
            {
            }
            else
            {
            }
        }

        public void ProcessCommandSentPause_PreSend(string[] replies)
        {
            if (CheckState(InstrumentState.PAUSED))
            {
            }
            else
            {
            }
        }

        public void ProcessCommandSentAudioBeat_Presend(string[] replies)
        {

        }
        public void ProcessCommandSentReboot_PostSend(string[] replies)
        {
            autoResetEventBoot.Reset();
            TimeSpan start = DateTime.Now - dateTimeTimerBootCreated + new TimeSpan(0, 0, 0, 0, 1000);
            TimeSpan period = new TimeSpan(0, 0, 0, 0, 250);
            timerBoot.Change(start, period);

            dateTimeWhenRebootStarted = DateTime.Now;
            //Don't close the SerialPort
            linuxBooted = false;
            startUpComplete = false;
            systemConfigured = false;
            autoResetEventBoot.WaitOne(timeToWaitForBoot, false);
            timerBoot.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            MessageBox.Show("Rebooted");
        }

        public void RebootingStatusCheck(object stateInfo)
        {
            timeSpanRebooting = DateTime.Now - dateTimeWhenRebootStarted;
            // I am sending a junk command and waiting for the system to reply with "unrecognized". 
            // This should indicate that lmc_control is receiving commands and sending replies.
            // In ProcessUnrecognized make sure you do autoResetEventBoot.Set() to unblock Vf61Gui.WaitForLmcControl
            this.Write("Boot");
            EventTimeRebootingUpdated.Solicit(this);
        }

        #endregion ProcessCommandSent

        #region ConfigurationFiles
        /// <summary>
        /// Call this to read in all of the configuration files.
        /// </summary>
        public void ReadConfigFiles()
        {
            ReadConfigFile(ref swConfigPaths,
                dictionary_SwConfig,
                Dictionaries.dictionaryDefaultSwConfig);
            ReadConfigFile(ref hwConfigPaths,
                dictionary_HwConfig,
                Dictionaries.dictionaryDefaultHwConfig);
            this.WriteAndWait("cStatus");
            this.Write("mc15version = " + serialNumber + "," +
                                          hardwareVersion + "," +
                                          "fwVer,ccVer," +
                                          userGuiVersion + "\n");
        }


        /// <summary>
        /// Finds the first existing file in paths. 
        /// If a file exists in paths then paths is rewritten to only contain the existing path.
        /// If no file exists in paths then no modifications to paths is performed.
        /// </summary>
        /// <param name="paths"></param>
        /// <returns></returns>
        private static bool FindFirstFile(ref string[] paths)
        {
            for (int i = 0; i < paths.Count(); ++i)
            {
                if (File.Exists(paths[i]))
                {
                    paths = new string[] { paths[i] };
                    return true;
                }
            }
            return false;
        }


        public void ResetDefaultValues(SortedList<string, MethodDelegate> delegates,
            Dictionary<string, string[]> defaultValues)
        {
            foreach (KeyValuePair<string, string[]> defaultValue in defaultValues)
            {
                try
                {
                    delegates[defaultValue.Key](defaultValue.Value);
                }
                catch (Exception ex)
                {
                    string errorMessage = "Detector.ResetDefaultValues(SortedList<string, MethodDelegate>, Dictionary<string, string[]>) --> ("
                                                                + defaultValue.Key + ") : "
                                                                + ex.Message;
                    EnqueueMessage(errorMessage, WhichMessage.Exception);
                }
            }
        }

        private void SaveDefaultFile(string path, Dictionary<string, string[]> defaults)
        {
            try
            {
                StreamWriter stream = new StreamWriter(path);
                if (stream == null)
                {
                    string message = "Detector.SaveDefaultFile(string, Dictionary<string, string[]>) --> "
                        + path
                        + " could not be created / opened";
                    EnqueueMessage(message, WhichMessage.Exception);
                    return;
                }
                foreach (KeyValuePair<string, string[]> kvp in defaults)
                {
                    // Write the key
                    stream.Write(kvp.Key);
                    if (kvp.Value != null)
                    {
                        // This is not a comment
                        stream.Write("=" + kvp.Key[0]);
                    }
                    stream.Write(Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                string message = "Detector.SaveDefaultFile(string, Dictionary<string, string[]>) --> " + ex.Message;
                EnqueueMessage(message, WhichMessage.Exception);
            }
        }

        /// <summary>
        /// Sets the values in the Feynmanhistograms to 0.0.
        /// </summary>
        public void Feynmans_SetToZero()
        {
            foreach (Feynman feynman in feynmans)
            {
                feynman.Clear();
            }
            UtilitiesVf61Gui.Clear(ref rowRatioTotals);
        }

        /// <summary>
        /// Reads in a single configuration file. 
        /// If the desired file does not exists then it returns false & configFile == null.
        /// </summary>
        /// <param name="path"></param>
        /// <param name="configFile"></param>
        /// <returns></returns>
        private bool ReadConfigFile(ref string[] paths,
            Dictionary<string, MethodDelegate> dictionaryListDelegates,
            Dictionary<string, string[]> dictionaryDefaultValues)
        {
            if (!FindFirstFile(ref paths))
            {
                // Can't find a configuration file
                foreach (string path in paths)
                {
                    SaveDefaultFile(path, dictionaryDefaultValues);
                }
                return false;
            }

            string errorMessageBase = "Detector.ReadConfigFile(string, SortedList<string, MethodDelegate>) --> ";
            StreamReader? stream;
            try
            {
                stream = File.OpenText(paths[0]);
            }
            catch (Exception)
            {
                stream = null;
            }
            if (stream == null)
            {
                return false;
            }

            char[] delims = new char[] { '=' };
            string? line;
            int lineNumber = 0;
            while ((line = stream?.ReadLine()) != null)
            {
                if (!line.StartsWith("#"))
                {
                    string[] valuePair = line.SplitAtFirstDelim(delims).ToArray();
                    if (valuePair.Length == 0)
                    {
                        string errorMessage = errorMessageBase + " (line " + lineNumber + " ) ";
                        errorMessage += "Input line has no length";
                        EnqueueMessage(errorMessage, WhichMessage.Exception);
                    }
                    else if (valuePair.Length == 1)
                    {
                        string errorMessage = errorMessageBase + " (line " + lineNumber + " ) ";
                        errorMessage += "Input line does not contain a value";
                        EnqueueMessage(errorMessage, WhichMessage.Exception);
                    }
                    else if (valuePair.Length > 2)
                    {
                        // There are to many deliminators
                        string errorMessage = errorMessageBase + " (line " + lineNumber + " ) ";
                        errorMessage += "Input line has to many values";
                        EnqueueMessage(errorMessage, WhichMessage.Exception);
                    }
                    else if (valuePair[0] != null)
                    {
                        try
                        {
                            if (dictionaryListDelegates.ContainsKey(valuePair[0]))
                            {
                                dictionaryListDelegates[valuePair[0]](valuePair);
                            }
                            else
                            {
                                string errorMessage = "Config Tag <" + valuePair[0] + "> not found.";
                            }
                        }
                        catch (Exception ex)
                        {
                            string errorMessage = errorMessageBase + " (line " + lineNumber + " ) " + ex.Message;
                            EnqueueMessage(errorMessage, WhichMessage.Exception);
                        }
                    }
                }
                ++lineNumber;
            }

            return true;
        }

        public void SaveSetupFile(string filename, Dictionary<string, string[]> values)
        {
        }

        /// <summary>
        /// Processes the command audible in the software configuration file.
        /// </summary>
        /// <param name="replies"></param>
        public void ProcessSwAudible(string[] replies)
        {
            // This is connected. Checked 2024 Feb 27
            ProcessSwLine(replies, ref this.audibleFeedback, "ProcessSwAudible");
            EventSwAudible.Solicit(this);
        }
        public void ProcessSwAutoRunCounts(string[] replies)
        {
            ProcessSwLine(replies, ref this.autoRunCounts, "ProcessSwAutoRunCounts");
            // EventSwAutoRunCounts.Solicit(this);  // No connection is required on read. 2024 Feb 27
        }

        public void ProcessSwAutoRunDuration(string[] replies)
        {
            ProcessSwLine(replies, ref this.autoRunDuration, "ProcessSwAutoRunDuration");
            //EventSwAutoRunDuration.Solicit(this);  // No connection is required on read. 2024 Feb 27
        }
        public void ProcessSwBackgroundEnable(string[] replies)
        {
            try
            {
                if (replies.Count() > 1)
                {
                    // hard-coded NO ENABLE BACKGROUND
                    // enableStartupBackground = Convert.ToBoolean(lineString[1]);
                    this.enableStartupBackground = false;
                    EventSwBackgroundEnable.Solicit(this);
                }
            }
            catch (Exception ex)
            {
                string errorMessage = "Detector.ProcessSwBackgroundEnable(string[])" + ex.Message;
                EnqueueMessage(errorMessage, WhichMessage.Exception);
            }

            // Uncomment if you don't want to hard code this value.
            // ProcessSwLine(replies, ref thisenableStartupBackground, "ProcessSwBackgroundEnable");
            // EventSwBackgroundEnable.Solicit(this);

        }
        public void ProcessSwBgRate(string[] replies)
        {
            ProcessSwLine(replies, ref this.bgRate, "ProcessSwBgRate");
            // EventSwBgRate.Solicit(this);  // No connection is required on read. 2024 Feb 27
        }
        public void ProcessSwBgRateDefault(string[] replies)
        {
            ProcessSwLine(replies, ref this.bgRateDefault, "ProcessSwBgRateDefault");
            // EventSwBgRateDefault.Solicit(this);  // No connection is required on read. 2024 Feb 27
        }
        public void ProcessSwBgYm(string[] replies)
        {
            ProcessSwLine(replies, ref this.bgYm, "ProcessSwBgYm");
            // EventSwBgYm.Solicit(this);  // No connection is required on read. 2024 Feb 27
        }
        public void ProcessSwBgYmDefault(string[] replies)
        {
            ProcessSwLine(replies, ref this.bgYmDefault, "ProcessSwBgYmDefault");
            // EventSwBgYmDefault.Solicit(this);  // No connection is required on read. 2024 Feb 27
        }
        public void ProcessSwBkgCounts(string[] replies)
        {
            ProcessSwLine(replies, ref this.bkgCounts, "ProcessSwBkgCounts");
            // EventSwBkgCounts.Solicit(this);  // No connection is required on read. 2024 Feb 27
        }
        public void ProcessSwBkgDuration(string[] replies)
        {
            ProcessSwLine(replies, ref this.bkgDuration, "ProcessSwBkgDuration");
            // EventSwBkgDuration.Solicit(this);  // No connection is required on read. 2024 Feb 27
        }
        public void ProcessSwCollectionTime(string[] replies)
        {
            ProcessSwLine(replies, ref this.backgroundCollectionTime, "ProcessSwCollectionTime");
            EventSwCollectionTime.Solicit(this);
        }
        public void ProcessSwDeadtime(string[] replies)
        {
            ProcessSwLine(replies, ref this.deadtime, "ProcessSwDeadtime");
            this.WriteAndCheck("deadTime", this.deadtime.ToString());
            // EventSwDeadtime.Solicit(this);   // deadTime should get updated with WriteAndCheck 2024 Feb 27
        }

        /// <summary>
        /// This is to enable debugging in the UI. I'm not sure it is used in the original code.
        /// </summary>
        /// <param name="replies"></param>
        public void ProcessSwDebugEnable(string[] replies)
        {
            ProcessSwLine(replies, ref this.enableDebug, "ProcessSwDebugEnable");
            EventSwDebugEnable.Solicit(this);
        }
        public void ProcessSwDescription(string[] replies)
        {
            string descriptionSend = "";
            ProcessSwLine(replies, ref descriptionSend, "ProcessSwDescription");
            WriteAndCheck("runDescription", descriptionSend);
            // If there is a WriteAndCheck the description should be updated on the read.
            // There is no reason to do a EventSwDescrition.Solicit(this);
            //EventSwDescription.Solicit(this);
        }

        /// <summary>
        /// Processes the command device from the software configuration file.
        /// device corresponds to the configuration of the detector. i.e. single, primary, or secondary.
        /// </summary>
        /// <param name="replies"></param>
        public void ProcessSwDevice(string[] replies)
        {
            try
            {
                if (replies.Length > 1)
                {
                    PodConfig podConfigSend = replies[1].ToPodConfig();
                    // When "NPOD2 = <value>" is sent, there is no return confirmation. 
                    // You need to ask for the status to confirm it took hold by sending "NPOD2"
                    // WriteNpod2 checks for you.
                    this.WriteNpod2(podConfigSend);
                }
            }
            catch (Exception ex)
            {
                string errorMessage = "Detector.ProcessSwDevice(string[]) -> " + ex.Message;
                EnqueueMessage(errorMessage, WhichMessage.Exception);
            }
            EventSwDevice.Solicit(this);
        }

        public void ProcessSwDistance2floor(string[] replies)
        {
            double distPriToFlrSend = 0.0;
            ProcessSwLine(replies, ref distPriToFlrSend, "ProcessSwDistance2floor");
            WriteAndCheck("DistPriToFlr", distPriToFlrSend.ToString());
            // If there is a WriteAndCheck() then there is no reason to do a EventSwDistance2floor.Solicit(this).
            // distPriToFlrSend should be updated on the Check part.
            EventSwDistance2floor.Solicit(this);
        }

        public void ProcessSwDistance2object(string[] replies)
        {
            double distPriToFlrSend = 0.0;
            ProcessSwLine(replies, ref distPriToFlrSend, "ProcessSwDistance2object");
            WriteAndCheck("DistPriToSrc", distPriToFlrSend.ToString());
            // If there is a WriteAndCheck() then there is no reason to do a EventSwDistance2object.Solicit(this).
            // distPriToFlrSend should be updated on the Check part.
            EventSwDistance2object.Solicit(this);
        }

        public void ProcessSwDuration(string[] replies)
        {
            uint durationSend = 0;
            ProcessSwLine(replies, ref durationSend, "ProcessSwDuration");
            WriteAndCheck("Duration", durationSend.ToString());
            // If there is a WriteAndCheck() then there is no reason to do a EventSwDuration.Solicit(this).
            // durationSend should be updated on the Check part.
            EventSwDuration.Solicit(this);
        }
        public void ProcessSwExit(string[] replies)
        {
            // I don't think exit is used in the original code anymore
            ProcessSwLine(replies, ref this.exit, "ProcessSwExit");
            // EventSwExit.Solicit(this); // Not hooking this up because I don't think it's used anymore 
        }
        public void ProcessSwHvRdConv(string[] replies)
        {
            // I don't think exit is used in the original code anymore
            ProcessSwLine(replies, ref this.hvRdConv, "ProcessSwHvRdConv");
            // EventSwHvRdConv.Solicit(this); // Not hooking this up because I don't think it's used anymore 
        }
        public void ProcessSwHvSet(string[] replies)
        {
            int hvSetSend = 0;
            ProcessSwLine(replies, ref hvSetSend, "ProcessSwHvSet");
            WriteAndCheck("HVset", hvSetSend.ToString());
            // Update HVread while we're here.
            Write("HVread");
            EventSwHvSet.Solicit(this);
        }
        public void ProcessSwHvWrConv(string[] replies)
        {
            // I don't think exit is used in the original code anymore
            ProcessSwLine(replies, ref this.hvWrConv, "ProcessSwHvWrConv");
            // EventSwHvWrConv.Solicit(this); // Not hooking this up because I don't think it's used anymore. Feb 27, 2024.
        }
        public void ProcessSwIpcCounts(string[] replies)
        {
            ProcessSwLine(replies, ref this.ipcCounts, "ProcessSwIpcCounts");
            EventSwIpcCounts.Solicit(this);
        }
        public void ProcessSwIpcDuration(string[] replies)
        {
            ProcessSwLine(replies, ref this.ipcDuration, "ProcessSwIpcDuration");
            EventSwIpcDuration.Solicit(this);
        }

        /// <summary>
        /// This is to enable the debugging of the linux system. i.e. it turns on/off Verbose mode, which pipes information out the serial port.
        /// </summary>
        /// <remarks>
        /// There are no return confirmation nor is there any way to check if it's turn on or off other than looking at the serial port communications.
        /// </remarks>
        /// <param name="replies"></param>
        public void ProcessSwLinuxDebug(string[] replies)
        {
            ProcessSwLine(replies, ref this.linuxDebug, "ProcessSwLinuxDebug");
#if DEBUG
            //Always set to true if in debug mode.
            this.linuxDebug = true;
#endif
            this.Write("Debug = " + Convert.ToInt32(this.linuxDebug).ToString());

            EventSwLinuxDebug.Solicit(this); // Debug does not return anything. You must call EventSwLinuxDebug.Solicit(this);
        }

        public void ProcessSwMaxFileThreshold(string[] replies)
        {
            ProcessSwLine(replies, ref this.maxFileThreshold, "ProcessSwMaxFileThreshold");
            EventSwMaxFileThreshold.Solicit(this);
        }
        public void ProcessSwMinMemThreshold(string[] replies)
        {
            ProcessSwLine(replies, ref this.minMemThreshold, "ProcessSwMinMemThreshold");
            EventSwMinMemThreshold.Solicit(this);
        }
        public void ProcessSwNoUserPowerDownTime(string[] replies)
        {
            ProcessSwLine(replies, ref this.noUserPowerDownTime, "ProcessSwNoUserPowerDownTime");
            this.noUserPowerDownTime *= 1000;   // Convert from seconds to milliseconds.
            EventSwNoUserPowerDownTime.Solicit(this);
        }
        public void ProcessSwPowerDownAlertThreshold(string[] replies)
        {
            ProcessSwLine(replies, ref this.powerDownAlertThreshold, "ProcessSwPowerDownAlertThreshold");
            EventSwPowerDownAlertThreshold.Solicit(this);
        }
        public void ProcessSwPowerDownThreshold(string[] replies)
        {
            ProcessSwLine(replies, ref this.powerDownThreshold, "ProcessSwPowerDownThreshold");
            EventSwPowerDownThreshold.Solicit(this);
        }
        public void ProcessSwQdrCounts(string[] replies)
        {
            ProcessSwLine(replies, ref this.qdrCounts, "ProcessSwQdrCounts");
            EventSwQdrCounts.Solicit(this);
        }
        public void ProcessSwQdrDuration(string[] replies)
        {
            ProcessSwLine(replies, ref this.qdrDuration, "ProcessSwQdrDuration");
            EventSwQdrDuration.Solicit(this);
        }
        public void ProcessSwRepetitions(string[] replies)
        {
            // Set the max number of repetitions.
            ProcessSwLine(replies, ref this.repetitions[1], "ProcessSwRepetitions");
            EventSwRepetitions.Solicit(this);
        }
        public void ProcessSwStorage(string[] replies)
        {
            //public enum InstrumentStorageLocation : int
            // {
            //     NONE = 0,
            //     NET,
            //     USB,
            //     INSTRUMENT
            // };
            ProcessSwLine(replies, ref this.storageLocation, "ProcessSwStorage");
            WriteAndCheck("Storage", ((int)(this.storageLocation)).ToString());
            EventSwStorage.Solicit(this);
        }
        public void ProcessSwThreatEnable(string[] replies)
        {
            ProcessSwLine(replies, ref this.enableThreatGraphic, "ProcessSwThreatEnable");
            EventSwThreatEnable.Solicit(this);
        }
        public void ProcessSwUnits(string[] replies)
        {
            ProcessSwLine(replies, ref this.unitsDistance, "ProcessSwUnits");
            EventSwUnits.Solicit(this);
        }
        public void ProcessSwUseVeto(string[] replies)
        {
            // corresponding command is 
            // vetoActive = <number>
            ProcessSwLine(replies, ref this.vetoActive, "ProcessSwUseVeto");
            WriteAndCheck("veto", this.vetoActive.ToInt32(0).ToString());
            EventSwUseVeto.Solicit(this);
        }
        public void ProcessSwVetoDuration(string[] replies)
        {
            ProcessSwLine(replies, ref this.vetoGateWidth, "ProcessSwVetoDuration");
            WriteAndCheck("vetoGateWidth", this.vetoGateWidth.ToString());
            EventSwVetoDuration.Solicit(this);
        }
        public void ProcessSwVetoTrueState(string[] replies)
        {
            try
            {
                if (replies.Length > 1)
                {
                    this.vetoTrueState = replies[1].ToVetoTrueState();
                    string value = (vetoTrueState == VetoTrueState.Low) ? "0" : "1";
                    WriteAndCheck("vetoTrue", value);
                }
            }
            catch (Exception ex)
            {
                string errorMessage = "Detector.ProcessSwVetoTrueState(string[]) -> " + ex.Message;
                EnqueueMessage(errorMessage, WhichMessage.Exception);
            }
        }

        public void ProcessSwIpAddress(string[] replies)
        {
            try
            {
                if (replies.Length > 1)
                {
                    IpInitializeTcp(replies[1]);
                }
            }
            catch (Exception ex)
            {
                string errorMessage = "Detector.ProcessSwIpAddress(string[]) -> " + ex.Message;
                EnqueueMessage(errorMessage, WhichMessage.Exception);
            }
        }

        public void ProcessSwLine(string[] replies, ref int value, string functionName)
        {
            try
            {
                if (replies.Length > 1)
                {
                    value = Convert.ToInt32(replies[1]);
                }
            }
            catch (Exception ex)
            {
                string errorMessage = "Detector." + functionName + "(string[]) -> " + ex.Message;
                EnqueueMessage(errorMessage, WhichMessage.Exception);
            }
        }

        public void ProcessSwLine(string[] replies, ref uint value, string functionName)
        {
            try
            {
                if (replies.Length > 1)
                {
                    value = Convert.ToUInt32(replies[1]);
                }
            }
            catch (Exception ex)
            {
                string errorMessage = "Detector." + functionName + "(string[]) -> " + ex.Message;
                EnqueueMessage(errorMessage, WhichMessage.Exception);
            }
        }
        public void ProcessSwLine(string[] replies, ref double value, string functionName)
        {
            try
            {
                if (replies.Length > 1)
                {
                    value = Convert.ToDouble(replies[1]);
                }
            }
            catch (Exception ex)
            {
                string errorMessage = "Detector." + functionName + "(string[]) -> " + ex.Message;
                EnqueueMessage(errorMessage, WhichMessage.Exception);
            }
        }

        public void ProcessSwLine(string[] replies, ref bool value, string functionName)
        {
            try
            {
                if (replies.Length > 1)
                {
                    string reply = replies[1].ToLower();
                    int replyInt = reply.ToInt32(-1);
                    if (replyInt.Equals(-1))
                    {
                        // It's not a number string, so use the string
                        value = Convert.ToBoolean(reply);
                    }
                    else
                    {
                        // It's a number, so convert using an integer
                        value = Convert.ToBoolean(replyInt);
                    }
                }
            }
            catch (Exception ex)
            {
                string errorMessage = "Detector." + functionName + "(string[]) -> " + ex.Message;
                EnqueueMessage(errorMessage, WhichMessage.Exception);
            }
        }
        public void ProcessSwLine(string[] replies, ref InstrumentStorageLocation value, string functionName)
        {
            try
            {
                if (replies.Length > 1)
                {
                    value = replies[1].ToInstrumentStorageLocation();
                }
            }
            catch (Exception ex)
            {
                string errorMessage = "Detector." + functionName + "(string[]) -> " + ex.Message;
                EnqueueMessage(errorMessage, WhichMessage.Exception);
            }
        }
        public void ProcessSwLine(string[] replies, ref UnitsDistance value, string functionName)
        {
            try
            {
                if (replies.Length > 1)
                {
                    value = replies[1].ToUnitsDistance();
                }
            }
            catch (Exception ex)
            {
                string errorMessage = "Detector." + functionName + "(string[]) -> " + ex.Message;
                EnqueueMessage(errorMessage, WhichMessage.Exception);
            }
        }


        public void ProcessSwLine(string[] replies, ref string value, string functionName)
        {
            try
            {
                if (replies.Length > 1)
                {
                    value = replies[1];
                }
            }
            catch (Exception ex)
            {
                string errorMessage = "Detector." + functionName + "(string[]) -> " + ex.Message;
                EnqueueMessage(errorMessage, WhichMessage.Exception);
            }
        }

        public void ProcessHwSerialNumber(string[] replies)
        {
            string newSerialNumber = (replies.Length < 2) ? "Unkown" : replies[1];
            if (newSerialNumber != serialNumber)
            {
                serialNumber = newSerialNumber;
                EventHwSerialNumber.Solicit(this);
            }
        }

        public void ProcessHwHardwareVersion(string[] replies)
        {
            string newHardwareVersion = (replies.Length < 2) ? "Unknown" : replies[1];
            if (newHardwareVersion != hardwareVersion)
            {
                hardwareVersion = newHardwareVersion;
                EventHwHardwareVersion.Solicit(this);
            }
        }
        #endregion ConfigurationFiles

        public string PrintSystemInformation()
        {
            double[] tabStops = new double[] { 150.0, 30.0 };

            string message = "";
            Font font = new Font(FontFamily.GenericMonospace, 12, FontStyle.Regular);
            DateTime buildDate = BuildDate();

            message += "Serial Number        = " + serialNumber + Environment.NewLine;
            message += "Hardware Version     = " + hardwareVersion + Environment.NewLine;
            message += "User GUI Version     = " + typeof(Detector).Assembly.GetName().Version + Environment.NewLine;
            message += "Build Date           = " + buildDate.ToShortDateString() + Environment.NewLine;
            message += "Build Time           = " + buildDate.ToShortTimeString() + Environment.NewLine;
            message += "Linux C code Version = " + linuxCCodeVersion + Environment.NewLine;
            message += "Firmware Version     = " + firmwareVersion + Environment.NewLine;
            message += "LCD Die Temp         = " + lcdDieTemp + " °C" + Environment.NewLine;
            message += "FPGA Die Temp        = " + fpgaDieTemp[0] + " °C" + Environment.NewLine;
            message += "HV Reading           = " + hvRead + " V" + Environment.NewLine;

            return message;
        }

        public static DateTime BuildDate()
        {
            Version? version = typeof(Detector).Assembly.GetName().Version;
            if (version == null)
            {
                return new DateTime();
            }
            return (new DateTime(2000, 1, 1)).AddDays(version.Build).AddSeconds(version.Revision * 2);
        }

        /// <summary>
        /// Draws both the Cumulative and One Second bar plots
        /// </summary>
        public void DrawBitmapBarPlots()
        {
            bitmapBarPlotCumulative?[0].Format(detectorFormat);
            bitmapBarPlotOneSecond?[0].Format(detectorFormat);
            bitmapBarPlotCumulative?[1].Format(detectorFormat);
            bitmapBarPlotOneSecond?[1].Format(detectorFormat);
            bitmapBarPlotCumulative?[0].DrawBarPlot(this.channelCountsCumulative);
            bitmapBarPlotOneSecond?[0].DrawBarPlot(this.channelCountsOneSecond);
            bitmapBarPlotCumulative?[1].DrawBarPlot(this.channelCountsCumulative.SubArray(16, 16));
            bitmapBarPlotOneSecond?[1].DrawBarPlot(this.channelCountsOneSecond.SubArray(16, 16));
        }

        /// <summary>
        /// Sums up file size in Detector.fileList
        /// </summary>
        /// <returns></returns>
        public ulong LFSbytesUsed()
        {
            ulong sum = 0ul;
            foreach (FileListItem item in fileList)
            {
                sum += (ulong)item.size;
            }
            return sum;
        }

        // Call this on startup
        public void GetGatewidths()
        {
            int numOfGatewidths = 16;
            feynmans = new List<Feynman>();

            for (int i = 0; i < numOfGatewidths; ++i)
            {
                //Write("Moments = " + i.ToString());
                WriteAndWait("Moments = " + i.ToString());
            }
        }

        public void HandleException(Exception ex, string functionNameLong, string functionNameShort)
        {
            string errorMessage = functionNameLong + ex.Message;
            EnqueueMessage(errorMessage, WhichMessage.Exception);
#if DEBUG
            MessageBox.Show(errorMessage, functionNameShort, MessageBoxButtons.OK, MessageBoxIcon.Exclamation, MessageBoxDefaultButton.Button1);
#endif

        }

        /// <summary>
        /// Sends the command NPOD2 = &lt;PodConfig&gt; to the detector. This checks for any mis identifications.
        /// When NPOD2 = &lt;PodConfig&gt; is sent, there is not return confirmation.
        /// This asks for the status to confirm it took hold by sending "NPOD2"
        /// </summary>
        /// <param name="podConfig"></param>
        public void WriteNpod2(PodConfig podConfig)
        {
            // When "NPOD2 = <value>" is sent, there is no return confirmation. 
            // You need to ask for the status to confirm it took hold by sending "NPOD2"
            string value = "0";
            switch (podConfig)
            {
                default:
                case PodConfig.Single:
                    value = "0";
                    break;
                case PodConfig.Primary:
                    value = "1";
                    break;
                case PodConfig.Secondary:
                    value = "2";
                    break;
            }

            this.WriteAndCheck("NPOD2", value); // Ask for the setup -> This should trigger EventNPOD2
        }

        /// <summary>
        /// Draw the β vs time plot. To get the bitmap to the gui type pictureBoxThreatId.SolicitDraw(detector.bitmapSm2Ib.bitmapBeta);
        /// </summary>
        public void DrawBetaVsTime()
        {
            Sm2Ib[] sm2IbArray = sm2IbOneSecondTime.ToArray();
            bitmapSm2Ib?.DrawBetaVsTime(sm2IbArray, betaVsTimePlotLength);
        }

        public void DrawBetaVsSm2()
        {
            Sm2Ib[] sm2IbArray = sm2IbOneSecondTime.ToArray();
            bitmapSm2Ib?.DrawBetaVsSm2(sm2IbArray, betaVsSm2PlotLength);
        }

        public void DrawMultiplicationCalcs()
        {

        }

        public void DrawMultiplicationTable()
        {
            HageSolutionAlpha[] hageSolutionAlphaArray = hageSolutionsAlphaOneSecondTime.ToArray();
            //bitmapHageAlphaCalcs.DrawTable(hageSolutionsAlphaOneSecondTime);
        }

        public void detectorFormat_Changed(object sender, EventArgs e)
        {
            switch (detectorFormat)
            {
                default:
                case DetectorFormat.MC15:
                    rowRatioIndices = new int[][] {
                        new int[] {1, 2, 4, 5},
                        new int[] {8, 9, 10, 11},
                        new int[] {13, 13, 14, 14}
                    };
                    rowRatioTotals = new List<double[,]>() {
                        new double[3, 3],
                        new double[3, 3]
                    };
                    rowRatioCumulative = new List<double[,]>() {
                        new double[3, 3],
                        new double[3, 3]
                    };
                    break;
                case DetectorFormat.MCSmalls:
                    rowRatioIndices = new int[][] {
                        new int[] {1, 2, 3, 4},
                        new int[] {7, 8, 9, 10}
                    };
                    rowRatioTotals = new List<double[,]>() {
                        new double[2, 2],
                        new double[2, 2]
                    };
                    rowRatioCumulative = new List<double[,]>() {
                        new double[2, 2],
                        new double[2, 2]
                    };
                    break;
            }
        }

        /// <summary>
        /// Returns a string that can be printed to a TextBox displaying the number of repetitions.
        /// </summary>
        /// <returns></returns>
        public string RepetitionsString()
        {
            int num = (repetitions[0] + 1).Clamp(1, repetitions[1]);
            return num.ToString() + " of " + repetitions[1];
        }

        /// <summary>
        /// Writes the repetition string to the detector. e.g. Write("Reps = 1,1")
        /// </summary>
        public void RepetitionsWrite(int index, int max)
        {
            if (max < 1)
            {
                max = 1;
            }
            repetitions[0] = index.Clamp(0, max - 1);
            repetitions[1] = max;
            RepetitionsWrite();
        }

        /// <summary>
        /// Writes the repetition string to the detector. e.g. Write("Reps = 1,1")
        /// </summary>
        public void RepetitionsWrite(int index)
        {
            repetitions[0] = index.Clamp(0, repetitions[1] - 1);
            RepetitionsWrite();
        }

        /// <summary>
        /// Writes the repetition string to the detector. e.g. Write("Reps = 1,1")
        /// </summary>
        public void RepetitionsWrite()
        {
            if (repetitions[0] > repetitions[1])
            {
                repetitions[0] = repetitions[1];
            }

            string text = String.Format("Reps = {0},{1}",
                (repetitions[0] + 1).Clamp(1, repetitions[1]),
                repetitions[1]);

            Write(text);
        }

        public IPStatus Ping()
        {
            return Ping(120);
        }
        /// <summary>
        /// Pings the Avnet board to check for connectivity.
        /// </summary>
        public IPStatus Ping(int timeout)
        {
            Ping pingSender = new Ping();

            byte[] buffer = Encoding.ASCII.GetBytes(new string('a', 32));

            // Set options for transmission:
            // The data can go through 64 gateways or routers
            // before it is destroyed, and the data packet
            // cannot be fragmented.
            PingOptions options = new PingOptions(64, true);

            pingReply = pingSender.Send(IpAddress, timeout, buffer, options);
            return pingReply.Status;
        }

        public string ElapsedTimeString()
        {
            uint num = elapsedTime.Clamp(0u, duration);
            return num.ToString() + " of " + duration.ToString() + " sec";
        }

        /// <summary>
        /// This prints the results to be displayed in tabPageAssay
        /// </summary>
        /// <returns></returns>
        public string PrintAssayResults()
        {
            string message = "";
            Feynman feynman = (feynmans.Count() > feynmanIndex256) ? feynmans[feynmanIndex256] : new Feynman();

            uint totalCounts = feynman.feynmanResults.cBar == 0.0 ? 0u : totalCountsTotal;
            message += string.Format("Total Counts = {0}{1}", totalCounts, Environment.NewLine);

            double averageRate = (totalCountsTotal == 0) ? 0.0 : feynman.countRate;
            message += string.Format("Average Rate = {0:0.00}{1}", averageRate, Environment.NewLine);

            message += string.Format("Ym ({0,3} µs)  = {1:0.000000}{2}",
                feynman.gatewidth / 1000UL,
                feynman.feynmanResults.Ym,
                Environment.NewLine);

            message += string.Format(" C-bar       = {0:0.000000}{1}", feynman.feynmanResults.cBar, Environment.NewLine);
            message += string.Format("C2-bar       = {0:0.000000}{1}", feynman.feynmanResults.c2Bar, Environment.NewLine);

            switch (detectorFormat)
            {
                default:
                    break;
                case DetectorFormat.MC15:
                    message += string.Format("Row 1/2      = {0,0:0.000}",
                        rowRatioTotals[0][0, 1]) + Environment.NewLine;
                    message += string.Format("Row 1/3      = {0,0:0.000}",
                        rowRatioTotals[0][0, 2]) + Environment.NewLine;
                    break;
                case DetectorFormat.MCSmalls:
                    message += string.Format("Row 1/2      = {0,0:0.000}",
                        rowRatioTotals[0][0, 1]) + Environment.NewLine;
                    break;
            }

            return message;
        }

        public static int[] GetMacAddrDefaultInts()
        {
            return new int[] { 0x00, 0x0A, 0x35, 0x00, 0x25, 0x01 };
        }

        public static string[] GetMacAddrDefault()
        {
            return GetMacAddrDefaultInts().Select(x => x.Clamp(0, 256).ToString("X2")).ToArray();
        }

        /// <summary>
        /// Estimates how long the acquisition will take given the current rate and total number of counts.
        /// </summary>
        /// <returns></returns>
        public uint EstimateTotalAcquisitionTime()
        {
            if ((elapsedTime <= 0.0)
                || (totalCountsTotal == 0))
            {
                return duration;
            }
            double averageRate = (double)totalCountsTotal / (double)elapsedTime;
            double estimatedTime = Math.Ceiling((double)maxSamplesInFile / averageRate);
            return ((uint)estimatedTime).Clamp(0u, duration);
        }

        public static void GetRowRatios(int indexDetector, int[][] rowRatioIndices, uint[] channelCounts, ref List<double[,]> rowRatios)
        {
            ulong[] rowSums = new ulong[rowRatioIndices.Length];
            int offset = (indexDetector == 0) ? 0 : 1;

            for (int i = 0; i < rowSums.Length; ++i)
            {
                foreach (int index in rowRatioIndices[i])
                {
                    rowSums[i] += (ulong)channelCounts[index + offset];
                }
            }
            for (int iNum = 0; iNum < rowRatios[indexDetector].GetLength(0); ++iNum)
            {
                ulong numerator = rowSums[iNum];
                for (int iDen = iNum; iDen < rowRatios[indexDetector].GetLength(1); ++iDen)
                {
                    ulong denominator = rowSums[iDen];
                    rowRatios[indexDetector][iNum, iDen] = (denominator == 0ul) ? 0.0 : (double)numerator / (double)denominator;
                }
            }
        }

        /// <summary>
        /// Returns the dose rate for double[all, master, secondary]. Units are rem / hr.
        /// </summary>
        /// <returns></returns>
        public double[] GetDose()
        {
            switch (detectorFormat)
            {
                default:
                    return new double[3];
                case DetectorFormat.MC15:
                    return GetDose_MC15();
            }
        }

        public double[] GetDose_MC15()
        {
            // MC-15 conversion factor is 243 +- 15 cps / (mrem/hr)
            // This uses the count rate of the sum of tubes 14 & 15 in each unit.
            double conversion = 243000.0;   // cps / (rem / hr)

            ulong[] counts = new ulong[] {
                0ul,    // Space holder for total
                channelCountsOneSecond[14] + channelCountsOneSecond[15],
                channelCountsOneSecond[30] + channelCountsOneSecond[31]
            };
            counts[0] = counts[1] + counts[2];

            return new double[3]
            {
                counts[0] / conversion,
                counts[1] / conversion,
                counts[2] / conversion
            };

        }

        /// <summary>
        /// Checks if the IpAddress is in a valid format. e.g. xxx.xxx.xxx.xxx
        /// </summary>
        /// <param name="address"></param>
        /// <returns></returns>
        public static bool IsValidIpAddress(string address)
        {
            string[] values = address.Split((char)'.');

            // Check if there is the correct number of values.
            if (values.Length != 4)
            {
                return false;
            }

            // Make sure each value only contains digits and is between [0, 255]
            foreach (string value in values)
            {
                if (!value.All(char.IsDigit))
                {
                    return false;
                }
                bool result = int.TryParse(value, out int add);
                if (!result)
                {
                    return true;
                }
                if ((add < 0) || (add > 255))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Formats the IpAddress to remove any leading 0's.
        /// </summary>
        /// <param name="address"></param>
        /// <returns></returns>
        public static bool FormatIpAddress(ref string address)
        {
            if (!IsValidIpAddress(address))
            {
                return false;
            }
            string[] values = address.Split((char)'.');
            string result = "";

            for (int i = 0; i < values.Length; ++i)
            {
                if (int.TryParse(values[i], out int add))
                {
                    result += add.ToString("0");
                }
                else
                {
                    result += values[i];
                }
                if (i < values.Length - 1)
                {
                    result += ".";
                };
            }
            address = new string(result);
            return IsValidIpAddress(address);
        }


        //public uint totalCountsTotalCollectionTime = 0;    // The time associated for when totalCountsTotal was updated.
        //public uint totalCountsTotal = 0;      // The sum of the counts in channelCountsTotals
        //public uint totalCountsOneSecond = 0;  // The sum of the counts in channelCountsOneSecond
        //public uint totalCountsCumulative = 0; // The sum of the counts in channelCountsCumulative

        //// Totals is only available when an assay is in progress.
        //public uint[] channelCountsTotals = new uint[32];       // The results from the command Totals.


    }
}
