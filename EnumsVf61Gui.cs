using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;

namespace Vf61Gui
{

    public enum InstrumentState : uint
    {
        OFFLINE = 0x0000,   // Unit is offline
        ONLINE = 0x0001,    // Unit is connected
        ACTIVE = 0x0002,    // Collecting an acquisition - This is turned on when "Go" is sent. It is turned off when "Cancel" is Sent.
        PAUSED = 0x0004,
        HV_CALIB = 0x0008,  // Performing a High Voltage Calibration
        LFSACTIVE = 0x0010, // Local File System is Active
        COPYING_NET = 0x0020,   // Copying files from the MC-15 to the local computer - Set when a file is copied using NetFileSave 
        COPYING_USB = 0x0040,   // Copying files from the MC-15 to the MC-15s USB drive.
        STREAMING = 0X0080,     // Streaming data to the computer while the MC-15 is collecting data.
    }

    public enum ErrorCode : uint
    {
        NONE = 0x0000,
        ERRORGO = 0x8001,
        ERRORCANCEL = 0x8002,       
        ERRORPAUSE = 0x8004,      
        ERRORRESUME = 0x8008       
    }

    public enum InstrumentStorageLocation : int
    {
        UNDEFINED = -1,
        NONE,
        NET,
        USB,
        INSTRUMENT
    }

    public enum WhichMessage : uint
    {
        Nothing = 0x00,
        Messages = 0x01,
        MessagesSent = 0x02,
        MessagesReceived = 0x04,
        DataReceived = 0x08,
        Debugging = 0x10,
        Exception = 0x20,
        Unknown = 0x40          // This command is not in any dictionaries
    }

    public enum WriteError : int
    {
        ExceptionThrown = -4,
        NoCommandsGiven = -3,
        NoPortsAreOpen = -2,
        WriteIsBlocked = -1,
        None = 0,
        WroteSerialPort = 1,
        WroteTcpIp = 2
    }
    public enum InstrumentPrintData : int
    {
        None = 0,
        Event,
        BytesReceived
    };

    public enum TimedLockStatus : int
    {
        Undefined = 0x00,
        Ready = 0x01,
        Locked = 0x02,
        TimedOut = 0x04
    }

    public enum DetectorFormat : int
    {
        Undefined = -1,
        Symmetric = 0,
        MC15,
        MCSmalls,
        NPod,
        FissionMeter
    }

    public enum PlotType : int
    {
        Undefined = -1,
        BarPlot,
        CirclePlot
    }

    public enum ScalingType : int
    {
        Undefined = -1,
        None,
        MinMax,
        Average,
        Median
    }

    public enum UnitsDistance : int
    {
        Undefined = -1,
        Centimeters,
        Inches
    }

    public enum PodConfig : int
    {
        Undefined = -1,
        Single,
        Primary,
        Secondary
    }
    
    public enum VetoTrueState : int
    {
        Undefined = -1,
        Low,
        High
    }

    public enum AssayErrorCode : int
    {
        UnrecoverableError = -3,
        MemoryFull = -2,
        ToManyFiles = -1,
        None = 0
    }

    /// <summary>
    /// Use this to keep track of 
    /// </summary>
    public enum UsbFileState : int
    {
        // Results from USBsaveFile
        //  USBsaveFile filename =  1  : In Progress
        //  USBsaveFile filename = -1  : not enough space
        //  USBsaveFile filename = -2  : no usb drive found
        //  USBsaveFile filename = -3  : filename not found in LFS  - (MAN) 2024/01/06 -> Ignoring this for now
        //  USBsaveFile filename = -4  : no proper filename sent    - (MAN) 2024/01/06 -> Ignoring this for now

        // Results from USBsaveFileProgress
        //  if USBsaveFileProgress is positive then that is the percent done in the saving.
        //  -1 => instr.file.state == FILESTATE_NOSPACE
        //  -2 => instr.file.state == FILESTATE_NOMOUNT
        //  -3 => for FILESTATE_IDLE

        Idle         = -3,   // When USBsaveFileProgress = -3
        NoMount      = -2,   // When USBsaveFileProgress = -2  or USBsaveFile = -2
        NoSpace      = -1,   // When USBsaveFileProgress = -1  or USBsaveFile = -1
        Undefined    =  0,   // Only during Initialization
        InProgress   =  1,   // When USBsaveFileProgress >= 0  or USBsaveFile = 1
        SaveComplete =  2    // This is set when USBsaveFileProgress = 2, <filename>
    }

    public enum UserMode : int
    {
        Undefined = -1,
        RunToCountsOrDuration = 0,      //USER_MODE.FIELD
        RunToDuration = 1               //USER_MODE.LAB
    }

    public enum MeasurementType : uint
    {
        Undefined = 0,
        QuickDraw,
        Background,
        Ipc,
        Assay
    }

    public enum AssayStatus : uint
    {
        // How to read the binary
        // No bits -> Undefined
        // One bit set indicates type of measurement
 
        Undefined = 0x0000,
        Acquiring = 0x0001,
        Done = 0x0002,
        Paused = 0x0004,
        QuickDraw = 0x0010,
        QDraw_Acquiring = 0x0011,
        QDraw_Done = 0x0012,
        QDraw_Paused = 0x0014,
        Background = 0x0020,
        Bkg_Acquiring = 0x0021,
        Bkg_Done = 0x0022,
        Bkg_Paused = 0x0024,
        Ipc = 0x0040,
        Ipc_Acquiring = 0x0041,
        Ipc_Done = 0x0042,
        Ipc_Paused = 0x0044,
        Assay = 0x0080,
        Assay_Acquiring = 0x0081,
        Assay_Done = 0x0082,
        Assay_Paused = 0x0084
     }

    public enum AudioState : int
    {
        // Look in mc15_control/audioThread.c for more information
        Undefined   = -1,
        Off         =  0,
        On          =  1,
        Beat        =  2,
        Sweep       =  3,
        Rate        =  4
    }

    public enum UpdateSystemStatus : int
    {
        Undefined      = -1,
        NoError        =  0, 
        BootBinMissing =  1,
        ImageUbMissing =  2,
        NoUpdateFiles  =  3,
        NoUsb          =  4,
        Unknown        =  5
    }

    public enum NetworkState : int
    {
        Undefined = -1,
        No_Net_Control,
        Yes_Net_Control
    }
    public enum FrontPanelStatus : int
    {
        // Evaluates the status of the connected cables.
        // Cables does not and cannot configure anything.
        // Cables = <Cable State>, <Front Panel State>
        // Cables = 0, 2  => Single Unit, Front Panel Attached
        // Cables = 1, 2  => Primary Unit, Front Panel Attached
        // Cables = 2, 2  => Secondary Unit, Front Panel Attached
        // Cables = 0, 0  => Single Unit, Front Panel Not Attached
        // Cables = 0, 1  => Single Unit, Cable Attached To Main Body
        // Cables = 0, 3  => Single Unit, Cable Attached to Main Body and to Front Panel
        Undefined = -1,
        NothingAttached,
        CableAttached,
        FrontPanelAttached,
        CableAttachedToFrontPanel
    }

    /// <summary>
    /// An enumerated type corresponding to the isotope.<para/>
    /// The first Int16 is the isotope in hex. e.g. U = 92 (0x005C)<para/>
    /// The second Int16 is the mass number in hex. e.g. U238 = 0x005C00EB<para/>
    /// </summary>
    public enum Isotope : uint
    {
        None = 0x00000000,
        AlphaN = 0x00000001,
        U233 = 0x005C00E9,
        U235 = 0x005C00EB,
        U238 = 0x005C00EE,
        Pu238 = 0x005E00EE,
        Pu239 = 0x005E00EF,
        Pu240 = 0x005E00F0,
        Pu242 = 0x005E00F2,
        Cf252 = 0x006200FC
    }

    public enum FittingMethod : uint
    {
        Unknown,
        Normal,     // Gaussian Distribution = amp * Exp(-Pow((mu - x)/sigma, 2))
        Cauchy,     // Cauchy Distribution   = amp / (1 + Pow((mu - x)/sigma,2))
        CauchySetAtOne, // Cauchy Distribution where it's guaranteed to be 1 at x = 0.
        CauchyModified01,
        CauchyModifiedSetAtOne,
        CauchyModified02,
        CauchyModified03,
        GaussianModified,
        LogNormal,
        LogNormalWithBOffset,
        ExponentialOneDecay,
        ExponentialTwoDecay,
        W2WithBOffset,
        W3WithBOffset,
        W4WithBOffset,
        ErfWithBOffset,
        ArcTanWithBOffset
    }

    public enum RowRatio : uint
    {
        None  = 0x00,
        Row12 = 0x03,   // 0b0011
        Row13 = 0x05,   // 0b0101
        Row23 = 0x06    // 0b110
    }

}
