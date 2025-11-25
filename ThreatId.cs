using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Drawing;
using Listen_N;
namespace Vf61Gui
{
    /// <summary>
    /// <para> Calculates the relative threat and criticality hazard levels based off of beta.</para>
    /// <para> It is assumed that the secondary spontaneous fission is (a,n) if it is present.</para>
    /// <para> Currently the alpha ratio must be defined but it may change in the future.</para>
    /// </summary>
    public class ThreatId : Multiplicity
    {

        //public Snm snmPu240 = new Snm("Pu240");
        //public Snm snmPu239 = new Snm("Pu239");
        //public Snm snmAlphaN = new Snm("AlphaN");
        //public Snm snmU235 = new Snm("U235");
        //public Snm snmU238 = new Snm("U238");
        //public Snm snmCf252 = new Snm("Cf252");
        //public Snm snmInduced = new Snm("Spont");
        //public Snm snmSpontaneous = new Snm("Induced");

        public Snm spont1;
        public Snm induced;
        public ValueLowHigh beta = new ValueLowHigh();
        public ValueLowHigh MTotal = new ValueLowHigh(1.0, 1.0, 1.0, 1.0, 100.0);
        public ValueLowHigh MLeakage = new ValueLowHigh(1.0, 1.0, 1.0, 1.0, 100.0);
        public ValueLowHigh K = new ValueLowHigh(1.0, 1.0, 1.0, 1.0, 100.0);
        public double alpha = 0.0;  // The alpha ratio

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
        /// <para> Beta = 0.282386521 ML = 1.20 MT = 1.33</para>
        /// <para> Beta = 0.309310582 ML = 1.25 MT = 1.41</para>
        /// </remarks>
        public double betaBackgroundThreshold = 0.246609489;
        
       
        /// <summary>
        /// <para> Default constructor for ThreatId where the MT_threashold for (a,n) on HEU is 1.2.</para>
        /// </summary>
        public ThreatId() : this(1.2){}

        /// <summary>
        /// <para> Constructor for ThreatId where the threshold between (a,n) threat / no threat is set by MT_Threshold.</para>
        /// </summary>
        /// <param name="MT_Threshold">The minimum value to estimate a threat based for (a,n) starters on HEU.</param>
        public ThreatId(double MT_Threshold)
        {
            spont1 = new Snm();
            induced = new Snm();

            spont1.SetVs1("Pu-240");   
            induced.SetVi("Pu-239");

            SetBackgroundThreshold(MT_Threshold);
        }


        public void CalculateThreat(double beta, double stdev)
        {
            CalculateThreat(new ValueStdev(beta, stdev));
        }
        public void CalculateThreat(ValueStdev beta)
        {
            MTotalCalculation(beta);
        }

        /// <summary>
        /// <para> Calculates the total neutron multiplication factor based off of value beta.</para>
        /// <para> If beta &lt; 0.5 it is assumed that the system is (a,n) driving HEU.</para>
        /// </summary>
        /// <param name="beta"></param>
        public void MTotalCalculation(ValueStdev beta)
        {
            if (beta.value < 0.5)
            {
                // (a,n) starters
                this.beta = new ValueLowHigh(beta, 0.0, 0.5);
            }
            else
            {
                // Spontaneous fissioners
                this.beta = new ValueLowHigh(beta, 0.5, 2.0);
            }

            // Calculate K
            // K = (ML - 1) / (vi1 - 1)
            K_Calculation();

            double vi1 = induced.GetVi().Moment(1);  // ⬅️ use this instead of induced.nu_1

            MLeakage = new ValueLowHigh(
                ML(K.value, vi1),
                ML(K.low, vi1),
                ML(K.high, vi1),
                1.0,
                100.0);

            MTotal = new ValueLowHigh(
                MT(MLeakage.value, vi1),
                MT(MLeakage.low, vi1),
                MT(MLeakage.high, vi1),
                1.0,
                100.0);
        }


        /// <summary>
        /// <para> Sets the lower background threshold for Beta based on the desired Total Multiplication.</para>
        /// <para> This assumes (a,n) starters and HEU for the induced fissioner.</para>
        /// </summary>
        /// <param name="MTotal"></param>
        /// <returns>Returns betaBackgroundThreshold </returns>
        public double SetBackgroundThreshold(double MTotal)
        {
            Snm snmU235 = new Snm();
            snmU235.SetVs1("U-235");

            double u235_nu1 = snmU235.GetVs1().Moment(1);

            double ML = (MTotal * (u235_nu1 - 1.0) + 1.0) / u235_nu1;
            double K = (ML - 1.0) / (u235_nu1 - 1.0);

            double induced_nu2 = induced.GetVi().Moment(2);
            double induced_nu3 = induced.GetVi().Moment(3);

            double numer = induced_nu2 * K;
            numer *= numer; // square numer

            double denom = 2.0 * numer + induced_nu3 * K;

            if (denom.Equals(0.0))
            {
                return 0.1;
            }

            return betaBackgroundThreshold = (numer / denom).Clamp(0.1, 0.3);
        }


        public string[][] GetStringArray()
        {
            string[][] message = new string[][]
            {
                //new string[] {"Beta", "=", this.beta.PrintEng()}
            };
            return message;
        }

        public Bitmap DrawTable(Size size, Color colorText, Font font)
        {
            string[][] itemsArray = GetStringArray();
            StringFormat[] formats = new StringFormat[] {
                new StringFormat() {Alignment = StringAlignment.Near},      // Name
                new StringFormat() {Alignment = StringAlignment.Center},    // =
                new StringFormat() {Alignment = StringAlignment.Far},       // Value
                new StringFormat() {Alignment = StringAlignment.Center},    // ±
                new StringFormat() {Alignment = StringAlignment.Far}        // Stdev
            };
            Color[] colorTextArray = new Color[5].Fill(colorText);
            Color[] colorBackArray = new Color[5].Fill(Color.White);
            Font[] fonts = new Font[5].Fill(font);
            return itemsArray.DrawTable(colorTextArray, colorBackArray, formats, fonts, size);
        }

        /// <summary>
        /// <para>Generates the a coefficient in the quadratic used solve for K for a given beta.</para>
        /// <para>The equation solved is a * K * K + b * K + c = 0</para>
        /// </summary>
        /// <returns></returns>
        private double a(double _beta)
        {
            double spont1_nu1 = spont1.GetVs1().Moment(1);
            double induced_nu2 = induced.GetVi().Moment(2);

            double x = spont1_nu1 * induced_nu2;
            return x * x * (2.0 * _beta - 1.0);
        }


        /// <summary>
        /// <para>Generates the b coefficient in the quadratic used solve for K for a given beta.</para>
        /// <para>The equation solved is a * K * K + b * K + c = 0</para>
        /// </summary>
        /// <returns></returns>
        private double b(double _beta)
        {
            double spont1_nu1 = spont1.GetVs1().Moment(1);
            double spont1_nu2 = spont1.GetVs1().Moment(2);
            double induced_nu2 = induced.GetVi().Moment(2);
            double induced_nu3 = induced.GetVi().Moment(3);

            return (
                2.0 * spont1_nu2 * (_beta - 1.0) * induced_nu2 +
                _beta * spont1_nu1 * induced_nu3
            ) * spont1_nu1;
        }


        /// <summary>
        /// <para>Generates the c coefficient in the quadratic used solve for K for a given beta.</para>
        /// <para>The equation solved is a * K * K + b * K + c = 0</para>
        /// </summary>
        /// <returns></returns>
        private double c(double _beta)
        {
            double spont1_nu1 = spont1.GetVs1().Moment(1);
            double spont1_nu2 = spont1.GetVs1().Moment(2);
            double spont1_nu3 = spont1.GetVs1().Moment(3);

            return _beta * spont1_nu1 * spont1_nu3 - spont1_nu2 * spont1_nu2;
        }

        /// <summary>
        /// <para>Solves for K in the equation a * K * K + b * K + c = 0.</para>
        /// </summary>
        /// <returns></returns>
        private void K_Calculation()
        {
            //if (beta.value < betaBackgroundThreshold)
            //{
            //    // Assume it's background
            //    KAll[0] = new ValueLowHigh(0.0, 0.0, 0.0, 0.0, 100.0);
            //    KAll[1] = new ValueLowHigh(0.0, 0.0, 0.0, 0.0, 100.0);
            //    return;
            //}

            if (beta.value < 0.5)
            {
                // Assume it's an alpha-n starter
                // There is only one solution
                double value = K_WhenAlphaN(beta.value, induced);
                double low = K_WhenAlphaN(beta.low, induced);
                double high = K_WhenAlphaN(beta.high.Clamp(value, 0.5), induced);
                K = new ValueLowHigh(value, low, high, 0.0, 100.0);
                return;
            }

            if (beta.Equals(0.5))
            {
                // Multiplication is going to be high.
                K = new ValueLowHigh(100.0, 100.0, 100.0, 0.0, 100.0);
                return;
            }

            // Assume the starters are defined by (SNM)spont1 & (SNM)induced
            double _aValue = a(beta.value);
            double _bValue = b(beta.value);
            double _cValue = c(beta.value);
            double _aLow = a(beta.low);
            double _bLow = b(beta.low);
            double _cLow = c(beta.low);
            double _aHigh = a(beta.high);
            double _bHigh = b(beta.high);
            double _cHigh = c(beta.high);

            double solValue = 0.0;
            double solLow = 0.0;
            double solHigh = 0.0;
            Quadratic.Solve(_aValue, _bValue, _cValue, out solValue, true);
            Quadratic.Solve(_aLow, _bLow, _cLow, out solLow, true);
            Quadratic.Solve(_aHigh, _bHigh, _cHigh, out solHigh, true);

            K = new ValueLowHigh(solValue, solLow, solHigh, 0.0, 100.0);

        }

        /// <summary>
        /// <para> Calculates MLeakage for a given K and vi1</para>
        /// </summary>
        /// <param name="MT"></param>
        /// <param name="vi_nu_1"></param>
        /// <returns></returns>
        public static double ML(double K, double vi_nu_1)
        {
            return K * (vi_nu_1 - 1.0) + 1;
        }

        /// <summary>
        /// <para> Calculates MTotal for a given MLeakage and vi1</para>
        /// </summary>
        /// <param name="MT"></param>
        /// <param name="vi_nu_1"></param>
        /// <returns></returns>
        public static double MT(double ML, double vi_nu_1)
        {
            return (ML * vi_nu_1 - 1.0) / (vi_nu_1 - 1.0);
        }

        /// <summary>
        /// <para> Calculuates the value Beta for a given total neutron multiplication factor </para>
        /// <para> using the currently selected nuclear data constants.</para>
        /// </summary>
        /// <param name="MT"></param>
        /// <returns></returns>
        public double Beta(double MT)
        {
            double b22 = b2(MT);
            return b22 * b22 / b1() / b3(MT);
        }

        /// <summary>
        /// <para> Calculates the b1 term as defined in Cifarelli-Hage (1986).</para>
        /// <para> We are using the standard alpha ratio definition.</para>
        /// </summary>
        /// <returns></returns>
        public double b1()
        {
            double spont1_nu1 = spont1.GetVs1().Moment(1);
            return spont1_nu1 * (1.0 + alpha);
        }


        public double b2(double MT)
        {
            double spont1_nu1 = spont1.GetVs1().Moment(1);
            double spont1_nu2 = spont1.GetVs1().Moment(2);
            double induced_nu2 = induced.GetVi().Moment(2);

            double k = K_From_MT(MT, spont1_nu1);

            return spont1_nu2 + spont1_nu1 * induced_nu2 * (1.0 + alpha) * k;
        }


        public double b3(double MT)
        {
            double spont1_nu1 = spont1.GetVs1().Moment(1);
            double spont1_nu2 = spont1.GetVs1().Moment(2);
            double spont1_nu3 = spont1.GetVs1().Moment(3);

            double induced_nu2 = induced.GetVi().Moment(2);
            double induced_nu3 = induced.GetVi().Moment(3);

            double k = K_From_MT(MT, spont1_nu1);

            double x = induced_nu2 * k;
            double a1 = 2.0 * spont1_nu1 * (1.0 + alpha) * x * x;
            double a2 = (spont1_nu1 * induced_nu3 * (1.0 + alpha) + 2.0 * spont1_nu2 * induced_nu2) * k;

            return a1 + a2 + spont1_nu3;
        }


        /// <summary>
        /// <para> Calculates K for a given MLeakage and ni1.</para>
        /// <para> K = (ML - 1) / (vi1 - 1).</para>
        /// </summary>
        /// <param name="ML"></param>
        /// <param name="vi_nu_1"></param>
        /// <returns></returns>
        public static double K_From_ML(double ML, double vi_nu_1)
        {
            return (ML - 1.0)/(vi_nu_1 - 1.0);
        }

        /// <summary>
        /// <para> Calculates MLeakage for a given MTotal and vi1</para>
        /// </summary>
        /// <param name="MT"></param>
        /// <param name="vi_nu_1"></param>
        /// <returns></returns>
        public static double ML_From_MT(double MT, double vi_nu_1)
        {
            return (MT * (vi_nu_1 - 1.0) + 1.0) / vi_nu_1;
        }


        /// <summary>
        /// <para> Calculates K for a given MTotal and vi1</para>
        /// </summary>
        /// <para> K = (ML - 1) / (vi1 - 1).</para>
        /// <param name="MT"></param>
        /// <param name="vi_nu_1"></param>
        /// <returns></returns>
        public static double K_From_MT(double MT, double vi_nu_1)
        {
            double MLeakage = ML_From_MT(MT, vi_nu_1);
            return K_From_ML(MLeakage, vi_nu_1);
        }

        /// <summary>
        /// <para> Calculates the solution for K when it's a (a,n) starter.</para>
        /// </summary>
        /// <param name="beta"></param>
        /// <returns></returns>
        public static double K_WhenAlphaN(double beta, Snm induced)
        {
            double nu2 = induced.GetVi().Moment(2);
            double nu3 = induced.GetVi().Moment(3);

            double numer = beta * nu3;
            double denom = (1.0 - 2.0 * beta) * nu2 * nu2;

            if (denom == 0.0)
            {
                return 100.0;
            }
            else
            {
                return numer / denom;
            }
        }



    }
}
