using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using Listen_N;

namespace Vf61Gui
{
    /// <summary>
    /// Holds the values from a ThreatId Calculation.
    /// </summary>
    public class ThreatIdValues
    {
        public Snm spont1;
        public Snm induced;
        public ValueLowHigh beta = new ValueLowHigh();
        public ValueLowHigh MTotal = new ValueLowHigh(1.0, 1.0, 1.0, 1.0, 100.0);
        public ValueLowHigh MLeakage = new ValueLowHigh(1.0, 1.0, 1.0, 1.0, 100.0);
        public ValueLowHigh K = new ValueLowHigh(1.0, 1.0, 1.0, 1.0, 100.0);

        /// <summary>
        /// <para> A value between 0.0 and 1.0 representing the relative level of threat.</para>
        /// <para> A value of 0.0 means no threat.</para>
        /// <para> A value of 0.5 means moderate threat.</para>
        /// <para> A value of 1.0 means high threat.</para>
        /// </summary>
        public ValueLowHigh threatLevel = new ValueLowHigh();


        /// <summary>
        /// <para> A value between 0.0 and 1.0 representing the relative level of criticality hazard.</para>
        /// <para> A value of 0.0 means no threat.</para>
        /// <para> A value of 0.5 means moderate threat.</para>
        /// <para> A value of 1.0 means high threat.</para>
        /// </summary>
        public ValueLowHigh critHazard = new ValueLowHigh();




        /// <summary>
        /// <para> Any value less than this value is considered background.</para>
        /// <para> This also assumes that the starter neutrons are from (a,n) reactions are driving HEU.</para>
        /// </summary>
        /// <remarks>
        /// <para> Beta = 0.196753914 ML = 1.10 MT = 1.14</para>
        /// <para> Beta = 0.246609489 ML = 1.15 MT = 1.24</para>
        /// <para> Beta = 0.282386521 ML = 1.20 MT = 1.33</para>MTO
        /// <para> Beta = 0.309310582 ML = 1.25 MT = 1.41</para>
        /// </remarks>
        public double betaBackgroundThreshold = 0.246609489;
        public double alpha = 0.0;  // The alpha ratio

        public ThreatIdValues(ThreatId threatId)
        {
            this.spont1 = new Snm();
            this.spont1.SetVs1(threatId.spont1.GetVs1().Name);
            this.spont1.SetVs2(threatId.spont1.GetVs2().Name);
            this.spont1.SetVi(threatId.spont1.GetVi().Name);

            this.induced = new Snm();
            this.induced.SetVs1(threatId.induced.GetVs1().Name);
            this.induced.SetVs2(threatId.induced.GetVs2().Name);
            this.induced.SetVi(threatId.induced.GetVi().Name);

            this.beta = new ValueLowHigh(threatId.beta);
            this.MTotal = new ValueLowHigh(threatId.MTotal);
            this.MLeakage = new ValueLowHigh(threatId.MLeakage);
            this.K = new ValueLowHigh(threatId.K);
            this.alpha = threatId.alpha;
            this.threatLevel = new ValueLowHigh(threatId.threatLevel);
            this.critHazard = new ValueLowHigh(threatId.critHazard);
            this.betaBackgroundThreshold = threatId.betaBackgroundThreshold;
        }
    }
}
