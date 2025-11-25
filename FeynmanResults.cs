using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;

namespace Vf61Gui
{
    public class FeynmanResults
    {
        public double Ym;
        public double cBar;
        public double c2Bar;

        public FeynmanResults()
        {
        }

        public FeynmanResults(string[] replies)
        {
            Assign(replies);
        }

        public void Assign(string[] replies)
        {
            if (replies.Length < 2)
            {
                return;
            }
            if (replies.Length > 1)
            {
                if(!replies.Equals("nan")) {
                Ym = replies[1].ToDouble(0.0);
                }
            }
            if (replies.Length > 2)
            {
                if (!replies.Equals("nan"))
                {
                    cBar = replies[2].ToDouble(0.0);
                }
            }
            if (replies.Length > 3)
            {
                if (!replies.Equals("nan"))
                {
                    c2Bar = replies[3].ToDouble(0.0);
                }
            }
        }

        /// <summary>
        /// Sets Ym, cBar, and c2Bar to 0.0.
        /// </summary>
        public void Clear()
        {
            Ym = 0.0;
            cBar = 0.0;
            c2Bar = 0.0;
        }
    }
}
