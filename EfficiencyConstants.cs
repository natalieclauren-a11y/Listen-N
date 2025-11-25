using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;

namespace Vf61Gui
{
    public class EfficiencyConstants
    {
        public string name = "";
        public DetectorFormat detectorFormat;
        public double sd;
        public double rd;
        public double eff;  // The detector efficiency corrected for the number of tubes.
        private double eff_notcorrected;    // The detector efficiency NOT corrected for the number of tubes.
        public double[]? dose;   // The dose conversion factor for each tube.
        public double A;        // parameters associated with determining the detector efficiency
        public double B;        // parameters associated with determining the detector efficiency
        public double lambda1;  // parameters associated with determining the detector efficiency
        public double lambda2;  // parameters associated with determining the detector efficiency
        public double lambda3;  // parameters associated with determining the detector efficiency
        public PointF[]? position; // the tube positions. x is lateral and is from the center line of the detector. y is depth and from the front face
        public int numTubes;
        public int totalNumTubes = 15;
        public bool cd_present;

        public EfficiencyConstants()
        {
        }

        public double efficiency(double sd, double rd)
        {
            this.sd = sd;
            this.rd = rd;
            return eff = totalNumTubes * efficiency_NotCorrected(sd, rd) / (double)numTubes;
        }

        /// <summary>
        /// Calculates the detector efficiency using only the tubes. This is not corrected for the number of tubes.
        /// </summary>
        /// <param name="sd"></param>
        /// <param name="rd"></param>
        /// <returns></returns>
        private double efficiency_NotCorrected(double sd, double rd)
        {
            return eff_notcorrected = A * (Math.Exp(-lambda1 * sd) + B * Math.Exp(-lambda2 * sd)) * Math.Exp(-lambda3 * rd);
        }

        /// <summary>
        /// A wrapper function around efficiency(double sd, double rd) so that it can be used in Amoeba.<para/>
        /// You will need to define rd before using this.<para/>
        /// </summary>
        /// <param name="vecDoub"></param>
        /// <returns></returns>
        //private double efficiency_Amoeba(NumRec.VecDoub vecDoub)
        //{
        //    return efficiency(vecDoub[0], rd);
        //}

        /// <summary>
        /// Calculates coeff[0] * (1.0 + coeff[1] / Math.Pow(value, 1) + .. coeff[k] / Math.Pow(value, k))
        /// </summary>
        /// <param name="coeff"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        private double Expansion(double[] coeff, double value)
        {
            double sum = 1.0;
            for(int i = 1; i < coeff.Length; ++i)
            {
                sum += coeff[i] / Math.Pow(value, (double)i);
            }
            return coeff[0]*sum;
        }

        public void CreateName()
        {
            name = string.Format("{0}-tubes {1} Cd",
                numTubes,
                cd_present);
        }

        /// <summary>
        /// Sets the positions of each tube in the MC-15. Units are in cm.
        /// </summary>
        public void SetMc15Positions()
        {
            position = new PointF[] {
                new PointF(-15.24f, 1.60f),
                new PointF(-10.16f, 1.60f),
                new PointF(-5.08f, 1.60f),
                new PointF(0.00f, 1.60f),
                new PointF(5.08f, 1.60f),
                new PointF(10.16f, 1.60f),
                new PointF(15.24f, 1.60f),
                new PointF(-12.70f, 5.41f),
                new PointF(-7.62f, 5.41f),
                new PointF(-2.54f, 5.41f),
                new PointF(-2.54f, 5.41f),
                new PointF(-7.62f, 5.41f),
                new PointF(-12.70f, 5.41f),
                new PointF(-5.08f, 8.92f),
                new PointF(-5.08f, 8.92f)
            };
        }

        /// <summary>
        /// Sets the dose conversion factors to convert from count rate to dose.<para/>
        /// Units are in mrem / hour.
        /// </summary>
        public void SetMc15Dose()
        {
            // Using Hadyns values
            dose = new double[] {
                0.0,
                0.0,
                0.0,
                0.0,
                0.0,
                0.0,
                0.0,
                0.0,
                0.0,
                0.0,
                0.0,
                0.0,
                0.0,
                8.230452E-3,
                8.230452E-3,
                0.0
            };
        }

        /// <summary>
        /// Returns the geometric center of the counts in the MC-15.<para/>
        /// X is in the lateral direction and is from the center of the MC-15. 
        /// Negative values are to the left and positive values are to the right.
        /// Y is the depth measured from the front face of the detector.<para/>
        /// Units are in cm.<para/>
        /// PointF[0] is the primary.<para/>
        /// PointF[1] is the secondary.<para/>
        /// </summary>
        /// <param name="channelCounts"></param>
        /// <returns></returns>
        public PointF[] CountCenter(uint[] channelCounts)
        {
            if(position == null)
            {
                return new PointF[0];
            }
            PointF[] centers = new PointF[2];
            uint[] sums = new uint[2];
            if (channelCounts.Length > 31)
            {
                for (int i = 0; i < 15; ++i)
                {
                    centers[0].X += position[i].X * (float)channelCounts[i];
                    centers[0].Y += position[i].Y * (float)channelCounts[i];
                    sums[0] += channelCounts[i];
                    centers[1].X += position[i].X * (float)channelCounts[i + 16];
                    centers[1].Y += position[i].Y * (float)channelCounts[i + 16];
                    sums[1] += channelCounts[i + 1];
                }
                centers[0].X /= (float)sums[0];
                centers[0].Y /= (float)sums[0];
                centers[1].X /= (float)sums[1];
                centers[1].Y /= (float)sums[1];
            }

            return centers;
        }

        /// <summary>
        /// Calculates the dose measurement.<para/>
        /// Dose[0] is the primary.<para/>
        /// Dose[1] is the secondary.<para/>
        /// Units are in mrem / hr.<para/>
        /// </summary>
        /// <param name="channelCounts"></param>
        /// <returns></returns>
        public double[] Dose(uint[] channelCounts)
        {
            if(dose == null)
            {
                return [];
            }
            double[] sum = new double[2];
            if (channelCounts.Length > 31)
            {
                for (int i = 0; i < 16; ++i)
                {
                    sum[0] += dose[i] * channelCounts[i];
                    sum[1] += dose[i] * channelCounts[i + 16];
                }
            }
            return sum;
        }

        /// <summary>
        /// Finds the source to detector distance when given the detector efficincy and rd values.
        /// </summary>
        /// <param name="eff"></param>
        /// <param name="rd"></param>
        /// <returns>double[] = double[0] = sd, double[1] = eff</returns>
        public double[] SourceToDector(double eff, double rd)
        {
            this.rd = rd;
            Func<double, double> f = delegate(double sd)
            {
                return efficiency(sd, rd);
            };

            double left = 1.0E1;
            double right = 4.0E3;
            double tolerance = 1.0E-7;

            double eff_left = efficiency(left, rd);
            double eff_right = efficiency(right, rd);
            if (eff_left < eff)
            {
                // Out of range on the left side.
                eff = eff_left;
                sd = left;
                return new double[] { left, eff_left };
            }
            if (eff_right > eff)
            {
                // Out of range on the right side.
                eff = eff_right;
                sd = right;
                return new double[] { right, eff_right };
            }
            //return RF.RootFinding.Brent(f, left, right, tolerance, eff);
            sd = RF.RootFinding.Bisect(f, left, right, tolerance, eff);
            this.eff = eff;
            return new double[] { sd, eff };
         }

 
        

    }
}
