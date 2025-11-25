using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using Listen_N;

namespace Vf61Gui
{
    public class HageSolutionAlpha
    {
        public ValueStdev ML = new();
        public ValueStdev eff = new();
        public ValueStdev Fs = new();
        public ValueStdev alpha = new();
        public ValueStdev R1 = new();
        public ValueStdev R2 = new();
        public ValueStdev R3 = new();
        public ValueStdev R4 = new();
        //public ValueStdev thickness;    // moderator thickness in cm.
        //public ValueStdev row12;
        //public ValueStdev row13;
        //public ValueStdev row23;[
        public ValueStdev[][]? thickness;
        public ValueStdev[]? rowRatios;
        public Snm vi = new();
        public Snm vs1 = new();
        public Snm vs2 = new();
        public DateTime dateTime = DateTime.Now;
        public int mask;

        /// <summary>
        /// The total neutron multiplication factor.
        /// </summary>
        public ValueStdev MT => ML.MTotalFromMLeakage(vi.GetVi().Moment(1));


        /// <summary>
        /// In neutrons source strength. (netrons per second).
        /// </summary>
        public ValueStdev NSS => new ValueStdev(vs1.GetVs1().Moment(1) * this.Fs.value);

        /// <summary>
        /// Deep Copy Initializer.
        /// </summary>
        /// <param name="_ML">Leakage Multiplication</param>
        /// <param name="_eff">Total Efficiency</param>
        /// <param name="_Fs_1">Primary Spontaneous Fissioner Rate (fissions / sec)</param>
        /// <param name="_Fs_2">Secondary Spontaneous Fissioner Rate (fissions / sec)</param>
        public HageSolutionAlpha(ValueStdev ML, 
            ValueStdev eff, 
            ValueStdev Fs, 
            ValueStdev alpha) 
        {
            this.ML = new ValueStdev(ML);
            this.eff = new ValueStdev(eff);
            this.Fs = new ValueStdev(Fs);
            this.alpha = new ValueStdev(alpha);

            // I only want 1 sec precision.
            this.dateTime.AddMilliseconds(this.dateTime.Millisecond - 1000);
        }

        /// <summary>
        /// Deep Copy Initializer.
        /// </summary>
        /// <param name="ML"></param>
        /// <param name="eff"></param>
        /// <param name="Fs"></param>
        /// <param name="alpha"></param>
        /// <param name="R1"></param>
        /// <param name="R2"></param>
        /// <param name="R3"></param>
        /// <param name="R4"></param>
        public HageSolutionAlpha(ValueStdev ML, 
            ValueStdev eff, 
            ValueStdev Fs, 
            ValueStdev alpha, 
            ValueStdev R1, 
            ValueStdev R2, 
            ValueStdev R3, 
            ValueStdev R4)
        {
            this.ML = new ValueStdev(ML);
            this.eff = new ValueStdev(eff);
            this.Fs = new ValueStdev(Fs);
            this.alpha = new ValueStdev(alpha);
            this.R1 = new ValueStdev(R1);
            this.R2 = new ValueStdev(R2);
            this.R3 = new ValueStdev(R3);
            this.R4 = new ValueStdev(R4);
 
            // I only want 1 sec precision.
            this.dateTime.AddMilliseconds(this.dateTime.Millisecond - 1000);
        }

 
        /// <summary>
        /// Empty Initializer.
        /// </summary>
        public HageSolutionAlpha() 
        {
            // I only want 1 sec precision.
            this.dateTime.AddMilliseconds(this.dateTime.Millisecond - 1000);
        }

        /// <summary>
        /// Deep copy initializer.
        /// </summary>
        /// <param name="sol"></param>
        public HageSolutionAlpha(HageSolutionAlpha sol) : this(sol.ML, sol.eff, sol.Fs, sol.alpha) 
        {
            // I only want 1 sec precision.
            this.dateTime.AddMilliseconds(this.dateTime.Millisecond - 1000);
        }

        /// <summary>
        /// Checks to see if at least _Fs_1 or _Fs_2 is greater than zero.
        /// </summary>
        /// <returns></returns>
        public bool IsFsValid()
        {
            return (Fs.value > 0.0) || (alpha.value > 0.0);
        }

        /// <summary>
        /// Checks to see if the eff is between 0.0 and 1.0.
        /// </summary>
        /// <returns></returns>
        public bool IsEffValid()
        {
            return (eff.value > 0.0) && (eff.value <= 1.0);
        }

        /// <summary>
        /// Checks to see if the values are physicaly possible.
        /// </summary>
        /// <returns></returns>
        public bool IsValid()
        {
            return (ML.value >= 1.0)
                    && IsEffValid()
                    && IsFsValid();
        }

        public string Print()
        {
            string text = string.Format("R1    = {0:E03} ± {1:E03}\n", R1.value, R1.stdev);
            text += string.Format("R2    = {0:E03} ± {1:E03}\n", R2.value, R2.stdev);
            text += string.Format("R3    = {0:E03} ± {1:E03}\n", R3.value, R3.stdev);
            text += string.Format("R4    = {0:E03} ± {1:E03}\n", R4.value, R4.stdev);
            text += string.Format("ML    = {0:E03} ± {1:E03}\n", ML.value, ML.stdev);
            text += string.Format("eff   = {0:E03} ± {1:E03}\n", eff.value, eff.stdev);
            text += string.Format("Fs    = {0:E03} ± {1:E03}\n", Fs.value, Fs.stdev);
            text += string.Format("alpha = {0:E03} ± {1:E03}\n", alpha.value, alpha.stdev);
            return text;
        }

    }
}
