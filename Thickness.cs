using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;

namespace Vf61Gui
{
    public class Thickness
    {
        public string name = "";
        public double[] amp = [];
        public double[] offset = [];
        public double[] mu = [];      // units are in cm
        public double[] sigma = [];   // units are in cm
        public double[] thicknessMin = [];
        public double[] thicknessMax = [];
        public uint[] channelCounts = [];

        /// <summary>
        /// <para>Which channels are used in determining the row ratio.</para>
        /// <para>channels[x][y] = x is which row e.g. row 0 or row 1</para>
        /// <para>y is what channels. e.g. channels 2, 3, 5, 6 </para>
        /// <para>This needs to be a jagged array beceause the number of channels in each row may not be the same.</para>
        /// </summary>
        public int[][] channels = { [] };

        /// <summary>
        /// The row ratios calculated from the channelCounts.
        /// </summary>
        public ValueStdev[] rowRatios = [];

        /// <summary>
        /// The last result for calculating the thickness of the moderator.
        /// </summary>
        public ValueStdev[][] thickness = { [] };

        /// <summary>
        /// <para>What is the interpretive thickness of the moderator on a scale of 0.0 to 1.0.</para>
        /// <para>0.0 is no moderation, 0.5 is medium moderation, and 1.0 is highly moderated.</para>
        /// </summary>
        public double[][] relativeThickness = { [] };

        public bool cdPresent;
        public bool simulated;
        public DetectorFormat detectorFormat;

        /// <summary>
        /// What thickness is considered moderately thick.
        /// </summary>
        public double muRelativeThickness = 3.0;

        /// <summary>
        /// What is the width of the relative thickness.
        /// </summary>
        public double sigmaRelativeThickness = 1.25;

        public Thickness()
        {
        }

        /// <summary>
        /// Returns the RowRatio for a given thickness.
        /// </summary>
        /// <param name="thickness">The thickness of the moderator (cm).</param>
        /// <param name="index">The index of the desired row-ratio. e.g. row12 (index = 0), row13 (index = 1), or row23 (index = 2).</param>
        /// <returns>Returns the RowRatio associated of the input thickness and index.</returns>
        public double RowRatio(double thickness, int index)
        {
            if (index < mu.Length)
            {
                return Functions.LogNormalWithBOffset(
                    thickness.Clamp(thicknessMin[index], thicknessMax[index]), 
                    amp[index], 
                    mu[index], 
                    sigma[index], 
                    offset[index]);
            }
            else
            {
                return 0.0;
            }
        }

        /// <summary>
        /// <para>Returns all of the rowratio for all of the constants.</para>
        /// <para>The number of results is dependentu upon how many different row ratios there are.</para>
        /// <para>e.g. result[0] will be row1/2, result[1] will be row 1/3, etc.</para>
        /// </summary>
        /// <param name="thickness"></param>
        /// <returns></returns>
        public double[] RowRatio(double thickness)
        {
            double[] rowRatio = new double[mu.Length];
            for (int i = 0; i < mu.Length; ++i)
            {
                rowRatio[i] = Functions.LogNormalWithBOffset(
                    thickness, 
                    amp[i],
                    mu[i],
                    sigma[i],
                    offset[i]
                    );
            }
            return rowRatio;
        }

        /// <summary>
        /// 
        /// <para> Calculates the thickness of the moderator based on the channelCounts. </para>
        /// <para> The resulting jagged array is arraigned as: </para>
        /// <para> ValueStdev[]   =&gt; the row ratios. e.g. ValueStdev[0] = row 1/2, ValueStdev[1] = row 1/3...</para>
        /// <para> ValueStdev[][] =&gt; the actual values. There may be 0, 1, or 2 solutions. You will need to check how many solutions are present.</para>
        /// </summary>
        /// <param name="channelCounts"></param>
        /// <returns></returns>
        public ValueStdev[][] CalculateThickness(uint[] channelCounts)
        {
            this.channelCounts = channelCounts;
            rowRatios = channelCounts.GetRowRatios(channels);
            thickness = new ValueStdev[rowRatios.Length][];

            for (int i = 0; i < rowRatios.Length; ++i)
            {
                List<ValueStdev> results = new List<ValueStdev>();
                if (rowRatios[i].value < Functions.LogNormalWithBOffset_Min(offset[i]))
                {
                    // The row ratio is below the minimum.
                    // This is a bare source.
                    results.Add(new ValueStdev(offset[i], rowRatios[i].name));
                }
                else if (rowRatios[i].value > Functions.LogNormalWithBOffset_Max(amp[i], offset[i])) 
                {
                    // The row ratio is above the maximum
                    // Set the moderator thickness to the max thickness.
                    results.Add(new ValueStdev(Math.Exp(mu[i]).Clamp(thicknessMin[i], thicknessMax[i]), rowRatios[i].name)); 
                }
                else
                {
                    // Get all the solutions
                    // Most likely there's two solutions.
                    List<double> temp = Functions.LogNormalWithBOffset_Inverse(rowRatios[i].value, amp[i], mu[i], sigma[i], offset[i]).ToList();
                    // Cull the solutions.
                    if (temp.Count < 1)
                    {
                        // No solutions.
                        // Set this to a bare source.
                        // This really shouldn't happen.
                        results.Add(new ValueStdev(rowRatios[i].name)); 
                    }
                    else if (temp.Count.Equals(1))
                    {
                        results.Add(new ValueStdev(temp[0].Clamp(thicknessMin[i], thicknessMax[i]), rowRatios[i].name));
                    }
                    else
                    {
                        // We've taken care of the row ratios being to high and to low.
                        // There's 2 solutions.
                        //temp = temp.Where(x => x.InRangeII(thicknessMin[i], thicknessMax[i])).ToList();
                        double tmin = thicknessMin[i];
                        double tmax = thicknessMax[i];
                        if (temp[0].InRangeII(tmin, tmax) && temp[1].InRangeII(tmin, tmax))
                        {
                            // Both are in range
                            // Add both of them right now.
                            // I need to figure out what to do here.
                            results.Add(new ValueStdev(temp[0], rowRatios[i].name + " (low)"));
                            results.Add(new ValueStdev(temp[1], rowRatios[i].name + " (high)"));
                        }
                        else if (temp[0].InRangeII(tmin, tmax) && !temp[1].InRangeII(tmin, tmax))
                        {
                            // The first solution is in range, the second is not.
                            // Only add the first solution.
                            results.Add(new ValueStdev(temp[0], rowRatios[i].name));
                        }
                        else if (!temp[0].InRangeII(tmin, tmax) && temp[1].InRangeII(tmin, tmax))
                        {
                            // The second solution is in range, the first is not.
                            // Only add the second solution.
                            results.Add(new ValueStdev(temp[1], rowRatios[i].name));
                        }
                        else
                        {
                            // None are in range.
                        }
                    }
                }
                thickness[i] = results.ToArray();
            }

            // Determine the relative thickness for each point.
            CalculateRelativeThickness();

            return thickness;
        }


        private void CalculateRelativeThickness()
        {
            relativeThickness = new double[thickness.Length][];
            for (int i = 0; i < relativeThickness.Length; ++i)
            {
                double[] rt = new double[thickness[i].Length];
                for (int j = 0; j < rt.Length; ++j)
                {
                    rt[j] = Functions.Gaussian_Cdf(thickness[i][j].value, muRelativeThickness, sigmaRelativeThickness);
                }
                relativeThickness[i] = rt;
            }
        }
    }
}
