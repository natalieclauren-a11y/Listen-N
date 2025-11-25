using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;

namespace Vf61Gui
{
    public class Feynman : IComparable
    {
        public FeynmanMoments feynmanMoments;       // moments m[], and gatewidth (ns)
        public FeynmanResults feynmanResults;       // Ym, cBar, and c2Bar
        public FeynmanHistogram feynmanHistogram;   // FeynmanHistograms

        // The moments that are returned with the
        //   Send Command> Moments = <index>
        //   Return      > FeynmanMoments = <gate width>, <m1>, <m2>, <m3>, <m4>, <m5>, <m6>, <m7>, <m8>
        //public double[] m; // Note: m[0] = 0.0, m[1] -> first moment

        public Feynman()
        {
            feynmanMoments = new FeynmanMoments();
            feynmanResults = new FeynmanResults();
            feynmanHistogram = new FeynmanHistogram();
        }

        public Feynman(string[] replies)
        {
            if (replies.Length < 2)
            {
                feynmanMoments = new FeynmanMoments();
                feynmanResults = new FeynmanResults();
                feynmanHistogram = new FeynmanHistogram();
                return;
            }
            if (replies[0].Equals("FeynmanMoments"))
            {
                feynmanMoments = new FeynmanMoments(replies);
                feynmanResults = new FeynmanResults();
                feynmanHistogram = new FeynmanHistogram();
            }
            else if (replies[0].Equals("FeynmanResults"))
            {
                feynmanMoments = new FeynmanMoments();
                feynmanResults = new FeynmanResults(replies);
                feynmanHistogram = new FeynmanHistogram();
            }
            else if (replies[0].Equals("Feynman"))
            {
                feynmanMoments = new FeynmanMoments();
                feynmanResults = new FeynmanResults();
                feynmanHistogram = new FeynmanHistogram(replies);
            }
            else
            {
                feynmanMoments = new FeynmanMoments();
                feynmanResults = new FeynmanResults();
                feynmanHistogram = new FeynmanHistogram();
            }
        }

        /// <summary>
        /// Returns the gatewidth in nano seconds.
        /// </summary>
        public uint gatewidth
        {
            get { return this.feynmanMoments.gatewidth; }
            set { this.feynmanMoments.gatewidth = value; }
        }

        public double countRate
        {
            get
            {
                if (this.feynmanMoments.gatewidth == 0)
                {
                    return 0.0;
                }
                else
                {
                    return 1.0E9 * this.feynmanResults.cBar / this.feynmanMoments.gatewidth;
                }
            }
        }

        /// <summary>
        /// Sets all the values to zero. Except for the gatewidth.
        /// </summary>
        public void Clear()
        {
            feynmanMoments.Clear();
            feynmanResults.Clear();
            feynmanHistogram.Clear();
        }

        public int CompareTo(object feynman)
        {
            Feynman? f = feynman as Feynman;
            if (f == null)
            {
                throw new ArgumentException("Feynman.CompareTo(object) -> object is not a Feynman");
            }
            return this.feynmanMoments.gatewidth.CompareTo(f.feynmanMoments.gatewidth);
        }
    }


    /// <summary>
    /// This is the comparator to find a Feynman based on the gatewidth.
    /// </summary>
    public class FeynmanSearch
    {
        Feynman feynman = new Feynman();
        public FeynmanSearch(uint gatewidth)
        {
            feynman.gatewidth = gatewidth;
        }
        public bool Gatewidth(Feynman feynman)
        {
            return this.feynman.Equals(feynman.gatewidth);
        }
    }

    public static partial class Utilities
    {

    }
}
