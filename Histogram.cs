using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;

namespace Vf61Gui
{
    public class Histogram
    {
        public ulong[] bin = [];
        public double[] m = [];  // m[0] is total number of events.
        public int gap_size = 0;

        public Histogram()
        {
            bin = new ulong[0];
            m = new double[9];
        }

        /// <summary>
        /// Initializes Feynman assuming replies[0] == Feynman.
        /// This is initializing Feynman as a histogram.
        /// </summary>
        /// <param name="replies"></param>
        public Histogram(string[] replies)
        {
            Assign(replies);
        }

        public void Assign(string[] replies)
        {
            bin = new ulong[replies.Length - 1];
            m = new double[9];

            for (int i = 1; i < replies.Length; ++i)
            {
                bin[i - 1] = replies[i].ToUInt32(0);
            };
            CalculateMoments();
        }

        public void CalculateMoments()
        {

            Array.Clear(m, 0, m.Length);
            if (m.Length < 1)
            {
                return;
            }
            long[] c = new long[m.Length];

            c[0] = (long)bin[0];        // c[0] is the total number of counts in the histogram.
            for (int i = 1; i < bin.Length; ++i)
            {
                c[0] += (long)bin[i];
                long coeff = i;
                for (int j = 1; j < c.Length; ++j)
                {
                    c[j] += (coeff--) * (long)bin[i];
                }
            }
            m[0] = c[0];
            long denom = c[0];
            if (c[0] > 0)
            {
                for (int i = 1; i < c.Length; ++i)
                {
                    denom *= i;
                    m[i] = ((double)c[i] / (double)denom);
                }
            }
        }

        /// <summary>
        /// Sets the values in bin[] to zero and m[] to zero. This does not change the size of bin[]
        /// </summary>
        public void Clear()
        {
            bin = new ulong[0];
            Array.Clear(m, 0, m.Length);
        }

        /// <summary>
        /// Returns the sum the height of each of the bins in the histogram.
        /// </summary>
        /// <returns></returns>
        public ulong totalCycles()
        {
            int I = FindHistogramEnd();
            return bin.Sum(0, I);
        }

        /// <summary>
        /// Returns the total number of counts in the histogram.
        /// </summary>
        /// <returns></returns>
        public ulong totalCounts()
        {
            //total number of events in the histogram
            int I = FindHistogramEnd();
            ulong sum = 0;
            for (int i = 1; i < I; ++i)
            {
                sum += (ulong)i * bin[i];
            }
            return sum;
        }

        /// <summary>
        /// Determines when the first sequence of all zeros in bin that is &gt; gap_size occurs.
        /// This is to reduce issues with outlyers.
        /// </summary>
        /// <param name="gap_size">How big of a gap of 0's to search for. 
        /// If gap_size == 0 then no gap will be found &amp; histogram_end == bin.size().
        /// </param>
        /// <returns>Sets histogram_end.</returns>
        public int FindHistogramEnd()
        {
            if (bin.Length == 0)
            {
                return 0;
            }

            if (gap_size == 0)
            {
                return bin.Length;
            }

            int max_ele = bin.MaxIndex();
            if (max_ele == bin.Length)
            {
                return bin.Length;
            }
            int gap_length = 0;
            for (int i = max_ele; i < bin.Length; ++i)
            {
                if (bin[i] == 0ul)
                {
                    ++gap_length;
                    if (gap_length >= gap_size)
                    {
                        return i;
                    }
                }
                else
                {
                    gap_length = 0;
                }
            }
            return bin.Length;
        }
    }
}
