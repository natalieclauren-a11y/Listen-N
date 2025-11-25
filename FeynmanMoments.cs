using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;

namespace Vf61Gui
{
    public class FeynmanMoments
    {
        public double[] m; // Note: m[0] = 0.0, m[1] -> first moment
        public uint gatewidth;  // in nano seconds

        public FeynmanMoments()
        {
            m = new double[9];
        }

        /// <summary>
        /// Initializes Feynman assuming replies[0] == FeynmanMoments.
        /// This is initializing Feynman with the moments calculated by lmc_control
        /// </summary>
        /// <param name="replies"></param>
        public FeynmanMoments(string[] replies) : this()
        {
            Assign(replies);
        }

        public void Assign(string[] replies)
        {
            m = new double[replies.Length - 1];

            gatewidth = Convert.ToUInt32(replies[1]);

            for (int i = 2; i < replies.Length; ++i)
            {
                if (!replies[i].Equals("nan"))
                {
                    m[i - 1] = Convert.ToDouble(replies[i]);
                }
            }
        }

        /// <summary>
        /// Sets all of the moments to zero. This does not change the gatewidth.
        /// </summary>
        public void Clear()
        {
            Array.Clear(m, 0, m.Length);
        }
    }
}
