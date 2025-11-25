using System;
using System.Collections.Generic;
using System.Text;
using Listen_N;


namespace Vf61Gui
{
    public class Dictionaries
    {
        /// <summary>
        /// Holds the various configurations.
        /// </summary>
        /// <remarks>
        /// Keep this extended version for now just for future use.
        /// </remarks>
        //public static Dictionary<DetectorFormat, string> dictionaryDetectorFormat = new Dictionary<DetectorFormat, string>() {
        //    { DetectorFormat.Symmetric, "Uniform" },
        //    { DetectorFormat.MC15, "MC-15" },
        //    { DetectorFormat.MCSmalls, "MC-Smalls" },
        //    { DetectorFormat.NPod, "nPod" }
        //};

        /// <summary>
        /// Holds the various configurations.
        /// </summary>
        /// <remarks>
        /// Use this scaled down version for production.
        /// </remarks>
        public static Dictionary<DetectorFormat, string> dictionaryDetectorFormat = new Dictionary<DetectorFormat, string>() {
            { DetectorFormat.MC15, "MC-15" },
            { DetectorFormat.MCSmalls, "MC-Smalls" }
        };

        public static Dictionary<ScalingType, string> dictionaryScalingType = new Dictionary<ScalingType, string>() {
            { ScalingType.None, "None" },
            { ScalingType.MinMax, "MinMax" },
            { ScalingType.Average, "Average" },
            { ScalingType.Median, "Median" },
        };

        public static Dictionary<PodConfig, string> dictionaryConnectionConfig = new Dictionary<PodConfig, string>() {
            {PodConfig.Single, "Single Detector"},
            {PodConfig.Primary, "2 Detector Primary"},
            {PodConfig.Secondary, "2 Detector Secondary"}
        };

        public static Dictionary<string, string> dictionarySide = new Dictionary<string, string>()
        {
            {"SideA", "Side A"},
            {"SideB", "Side B"},
            {"SideC", "Side C"},
            {"SideD", "Side D"},
            {"SideE", "Side E"},
            {"SideF", "Side F"}
        };

        public static Dictionary<string, string> dictionaryTubeCounts = new Dictionary<string, string>()
        {
            {"Cum",        "Cumulative"},
            {"OneSec",     "One Second"},
            {"TimeSeries", "Count Rate"}
        };

        public static SortedList<UnitsDistance, string> sortedListUnitsDistance = new SortedList<UnitsDistance, string>() 
        {
            {UnitsDistance.Undefined, "Undefined"},
            {UnitsDistance.Centimeters, "cm"},
            {UnitsDistance.Inches, "inch"}
        };

        /// <summary>
        /// Used for comboBoxActiveDetectorSelect.
        /// </summary>
        /// <remarks>
        /// comboBoxActiveDetectorSelect =&gt; Dictionary&gt;ConnectionSetup, string&lt;>
        /// </remarks>
        public static Dictionary<PodConfig, string> dictionaryDetectorSelection = new Dictionary<PodConfig, string>() {
            {PodConfig.Primary, "Primary"},
            {PodConfig.Secondary, "Secondary"}
        };

        public static Dictionary<VetoTrueState, string> dictionaryVetoTrueState = new Dictionary<VetoTrueState, string>() {
            {VetoTrueState.Low, "Low"},
            {VetoTrueState.High, "High"}
        };

        public static Dictionary<string, string[]> dictionarySnm = new Dictionary<string, string[]>()
        {
                // Name, induced, spontaneous1, spontaneous2
                { "Pu", new[] { "Pu-239", "Pu-240", "alpha-n" } },
                { "U",  new[] { "U-235", "U-238", "alpha-n" } },
                { "Cf", new[] { "Cf-252", "U-235", "alpha-n" } }
        };


        public static Dictionary<string, string[]> dictionaryDefaultSwConfig = new Dictionary<string, string[]>() {
            { "version", new string[] {"1.00.01"}},
            { "#Remember to change DiagsForm.buttonSaveDefaults if this file changes", new string[] {" "}},
            { "#For gui version 1.3.2.190 min", new string[] {""}},
            { "linuxDebug", new string[] {"False"}},
            { "device", new string[] {"0"}},
            { "storage", new string[] {"3"}},
            { "minMemThreshold", new string[] {"5"}},
            { "maxFileThreshold", new string[] {"200"}},
            { "powerDownAlertThreshold", new string[] {"10"}},
            { "powerDownThreshold", new string[] {"5"}},
            { "noUserPowerDownTime", new string[] {"900"}},
            { "hvWrConv", new string[] {"1.80"}},   // REMOVED AT PICOZED TRANSITION
            { "hvRdConv", new string[] {"1.60"}},   // REMOVED AT PICOZED TRANSITION
            { "hvSet", new string[] {"1680"}},
            { "duration", new string[] {"600"}},
            { "autoRunDuration", new string[] {"1200"}},
            { "autoRunCounts", new string[] {"1000000"}},
            { "qdrDuration", new string[] {"300"}},
            { "qdrCounts", new string[] {"1000000"}},
            { "bkgDuration", new string[] {"1200"}},
            { "bkgCounts", new string[] {"1000000"}},
            { "ipcDuration", new string[] {"1200"}},
            { "ipcCounts", new string[] {"1000000"}},
            { "repetitions", new string[] {"1"}},
            { "description", new string[] {"No Description Provided"}},
            { "deadtime", new string[] {"1000"}},
            { "distance2object", new string[] {"0.0"}},
            { "distance2floor", new string[] {"21.6"}},
            { "useVeto", new string[] {"0"}},
            { "vetoTrueState", new string[] {"1"}},
            { "vetoDuration", new string[] {"20000"}},  //
            { "audible", new string[] {"True"}},
            { "debugEnable", new string[] {"True"}},
            { "threatEnable", new string[] {"False"}},
            { "backgroundEnable", new string[] {"False"}},
            { "collectionTime", new string[] {"30"}},
            { "bgRateDefault", new string[] {"2.00"}},
            { "bgYmDefault", new string[] {"0.01"}},
            { "bgRate", new string[] {"1.23"}},
            { "bgYm", new string[] {"0.01"}},
            { "units", new string[] {"cm"}},
            { "exit", new string[] {"True"}}
        };

        public static Dictionary<string, string[]> dictionaryDefaultHwConfig = new Dictionary<string, string[]>() {
            { "version", new string[] {"1.00.01"}},
            { "serialNumber",new string[] {"SN-Unknown"}},
            { "hardwareVersion",new string[] {"Unknown"}}
        };


     }
}
