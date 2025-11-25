using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;

namespace Vf61Gui
{
    /// <summary>
    /// Stores a value and it's stdev.
    /// </summary>
    /// <remarks>
    /// I would like to expand this when there is time to include addition of multiple items.
    /// </remarks>
    /// 
    public class ValueStdev : IComparable
    {
        public double value;
        public double stdev;
        public string name = "";

        /// <summary>
        /// Empty constuctor for ValueStdev.
        /// </summary>
        /// <param name="valueStdev"></param>
        public ValueStdev() : this(0.0, 0.0, "") { }
        public ValueStdev(double value) : this(value, 0.0, "") { }
        public ValueStdev(double value, double stdev) : this(value, stdev, "") { }
        public ValueStdev(string name) : this(0.0, 0.0, name) { }
        public ValueStdev(double value, string name) : this(value, 0.0, name) { }
        public ValueStdev(double value, double[] stdevs) : this(value, stdevs, "") { }

        public ValueStdev(ValueStdev valueStdev) : this(valueStdev.value, valueStdev.stdev, valueStdev.name) { }

        /// <summary>
        /// Constructor for ValueStdev.
        /// </summary>
        /// <param name="value">(float) value</param>
        /// <param name="stdev">(float) stdev</param>
        public ValueStdev(double value, double stdev, string name)
        {
            this.value = value;
            this.stdev = Math.Abs(stdev);
            this.name = name;
        }


        /// <summary>
        /// Constructor for ValueStdev. stdev is created by sqrt(summing stdevs[i] in quadrature).
        /// </summary>
        /// <param name="value"></param>
        /// <param name="stdevs"></param>
        public ValueStdev(double value, double[] stdevs, string name)
        {
            this.value = value;
            this.stdev = stdevs.SumInQuadrature();
            this.name = name;
        }

        public ValueStdev(string[] replies)
        {
            if (replies.Length > 1)
            {
                this.value = replies[0].ToDouble(0.0);
            }
            if (replies.Length > 2)
            {
                this.stdev = replies[1].ToDouble(0.0);
            }
        }

        /// <summary>
        /// Calculates the inverse value and stdev of the ValueStdev.
        /// </summary>
        /// <returns></returns>
        public ValueStdev Inverse()
        {
            ValueStdev valueStdev = new ValueStdev();
            if (this.value == 0.0)
            {
                return valueStdev;
            }

            valueStdev.value = 1.0f / this.value;
            valueStdev.stdev = this.stdev / this.value / this.value;
            return valueStdev;
        }
        /// <summary>
        /// The upper range of the point. i.e. value + stdev
        /// </summary>
        public double upper
        { get { return this.value + this.stdev; } }

        /// <summary>
        /// The lower range of the point. i.e. value - stdev
        /// </summary>
        public double lower
        { get { return this.value - this.stdev; } }

        public string Print(string format)
        {
            return value.ToString(format) + " ± " + stdev.ToString(format);
        }

        public string Print()
        {
            return Print("#,##0.00");
        }

        public string Eng()
        {
            //int mag = (value == 0.0) ? 0 : (int)Math.Log10(value) / 3;
            //if ((Math.Abs(value) < 1.0)
            //    && (value != 0.0))
            //{
            //    mag -= 3;
            //}
            //double div = Math.Pow(10.0, -mag);
            //double valueNew = value * div;
            //double stdevNew = stdev * div;
            //string magString = mag.ToString("+00;−00;+00");
            //return string.Format("{0, 6:0.00}E{1, 3} ± {2, 6:0.00}E{1, 3}",
            //    valueNew,
            //    mag.ToString("+00;−00;+00"),
            //    stdevNew);
            string[] text = EngArray();
            return string.Join(" ", text);
        }

        public string[] EngArray()
        {
            int mag = (value == 0.0) ? 0 : (int)Math.Log10(value) / 3 * 3;  // Makes this a multiple of 3.
            if ((Math.Abs(value) < 1.0)
                && (value != 0.0))
            {
                mag -= 3;
            }
            double div = Math.Pow(10, -mag);
            double valueNew = value * div;
            double stdevNew = stdev * div;
            string magString = mag.ToString("+00;−00;+00");
            return new string[] {
                string.Format("{0, 0:0.00}E{1, 3}", valueNew, magString),
                " ± ",
                string.Format("{0, 0:0.00}E{1, 3}", stdevNew, magString),
            };
        }

        /// <summary>
        /// Returns an array of size 3 in the format of &lt;value&gt; ± &lst;stdev&gt;.
        /// </summary>
        /// <param name="digits">How many digits should be displayed. e.g. if digits = 2, format = 0.00</param>
        /// <returns></returns>
        public string[] Percent(int digits)
        {
            //string format = "0." + new string('0', digits.Clamp(1, 5));
            string format = "P" + digits.Clamp(1, 5).ToString();
            return new string[] { 
                value.ToString(format), 
                " ± ", 
                stdev.ToString(format) 
            };
        }
        public string[] SiUnitArray(int places, string unit)
        {
            return UtilitiesVf61Gui.SiUnitArray(this.value, this.stdev, places, unit);
        }

        public string SiUnit(int places, string unit)
        {
            return UtilitiesVf61Gui.SiUnit(this.value, this.stdev, places, unit);
        }


        /// <summary>
        /// Returns the Sqrt(a*a + b*b)
        /// </summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <returns></returns>
        private static double SumInQuadrature(double a, double b)
        {
            return Math.Sqrt(a * a + b * b);
        }
        /// <summary>
        /// Returns the Sqrt(a[0]*a[0] + ... + a[n-1]*a[n-1]).
        /// </summary>
        /// <param name="a"></param>
        /// <returns></returns>
        private static double SumInQuadrature(double[] a)
        {
            double a_sum = a.Sum(x => { return x * x; });
            return Math.Sqrt(a_sum);
        }

        public static ValueStdev operator +(ValueStdev a, ValueStdev b)
        {
            ValueStdev valueStdev = new ValueStdev();
            valueStdev.value = a.value + b.value;
            valueStdev.stdev = SumInQuadrature(a.stdev, b.stdev);
            return valueStdev;
        }

        public static ValueStdev operator -(ValueStdev a, ValueStdev b)
        {
            ValueStdev valueStdev = new ValueStdev();
            valueStdev.value = a.value - b.value;
            valueStdev.stdev = SumInQuadrature(a.stdev, b.stdev);
            return valueStdev;
        }

        public static ValueStdev operator *(ValueStdev a, ValueStdev b)
        {
            ValueStdev valueStdev = new ValueStdev();
            valueStdev.value = a.value * b.value;
            if (a.value == 0.0)
            {
                // If a.value == 0.0 then I'm going to ignore it.
                valueStdev.stdev = a.value * b.stdev;
            }
            else if (b.value == 0.0)
            {
                // If b.vlaue == 0.0 then I'm going to ignore it.
                valueStdev.stdev = b.value * a.stdev;
            }
            else
            {
                valueStdev.stdev = valueStdev.value * SumInQuadrature(a.stdev / a.value, b.stdev / b.value);
            }

            return valueStdev;
        }

        public static ValueStdev operator *(double a, ValueStdev valueStdev)
        {
            return new ValueStdev(a * valueStdev.value, a * valueStdev.stdev);
        }

        public static ValueStdev operator *(ValueStdev valueStdev, double a)
        {
            return new ValueStdev(a * valueStdev.value, a * valueStdev.stdev);
        }

        public static ValueStdev operator /(ValueStdev a, ValueStdev b)
        {
            ValueStdev valueStdev = new ValueStdev();
            if (b.value == 0.0)
            {
                valueStdev.value = Double.NaN;
                valueStdev.stdev = Double.NaN;
                return valueStdev;
            }
            valueStdev.value = a.value / b.value;
            if (a.value == 0.0)
            {
                // If a.value == 0.0 then I'm going to ignore it.
                valueStdev.stdev = a.value * b.stdev;
            }
            else
            {
                valueStdev.stdev = valueStdev.value * SumInQuadrature(a.stdev / a.value, b.stdev / b.value);
            }

            return valueStdev;
        }

        public static ValueStdev Add(ValueStdev[] a)
        {
            double sum = 0.0;
            double stdev = 0.0;

            foreach (ValueStdev valueStdev in a)
            {
                sum += valueStdev.value;
                stdev += valueStdev.stdev * valueStdev.stdev;
            }

            return new ValueStdev(sum, Math.Sqrt(stdev));
        }

        public static ValueStdev Multiply(ValueStdev[] a)
        {
            double[] f = new double[a.Length];
            double sum = 0.0;
            double stdev = 0.0;
            foreach (ValueStdev valueStdev in a)
            {
                sum *= valueStdev.value;
                if (valueStdev.value != 0.0)
                {
                    double x = valueStdev.stdev / valueStdev.value;
                    stdev += x * x;
                }
            }

            return new ValueStdev(sum, sum * Math.Sqrt(stdev)); ;
        }

        public double FractionalError()
        {
            if (value.Equals(0.0))
            {
                return 0.0;
            }
            else
            {
                return Math.Abs(stdev / value);
            }
        }

        int IComparable.CompareTo(object obj)
        {
            if (obj == null)
            {
                throw new ArgumentNullException(nameof(obj));
            }
            ValueStdev valueStdev = (ValueStdev)obj;
            return this.value.CompareTo(valueStdev.value);
        }

    }
}
