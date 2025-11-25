using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;

namespace Vf61Gui
{
    public static class Functions
    {
        public static double Gaussian(double x, double amp, double mu, double sigma)
        {
            double top = (mu - x) / sigma;
            return amp * Math.Exp(-top * top);
        }

        public static double Gaussian_Cdf(double x, double mu, double sigma)
        {
            return 0.5 * (1.0 + Erf(sqrt2Inverse * (x - mu) / sigma));
        }

        public static double Cauchy(double x, double amp, double mu, double sigma)
        {
            double top = Math.Abs((mu - x) / sigma);
            return amp / (1.0 + Math.Pow(top, 2.0));
        }
        public static double CauchySetAtOne(double x, double mu, double sigma)
        {
            double top = Math.Abs((mu - x) / sigma);
            double amp = 1.0 + Math.Pow(Math.Abs(mu / sigma), 2.0);
            return amp / (1.0 + Math.Pow(top, 2.0));
        }
        public static double CauchyModified01(double x, double amp, double mu, double sigma, double power)
        {
            double top = Math.Abs((mu - x) / sigma);
            return amp / (1.0 + Math.Pow(top, power));
        }
        public static double CauchyModified01SetAtOne(double x, double mu, double sigma, double power)
        {
            double top = Math.Abs((mu - x) / sigma);
            double amp = 1.0 + Math.Pow(Math.Abs(mu / sigma), power);
            return amp / (1.0 + Math.Pow(top, power));
        }

        public static double CauchyModified02(double x, double amp, double mu, double sigma, double offset)
        {
            double top = Math.Abs((mu - x) / sigma);
            return amp / (offset + Math.Pow(top, 2.0));
        }

        public static double CauchyModified03(double x, double amp, double mu, double sigma, double power, double offset)
        {
            double top = Math.Abs((mu - x) / sigma);
            return amp / (offset + Math.Pow(top, power));
        }

        public static double GaussianModified(double x, double amp, double mu, double sigma, double power)
        {
            double top = Math.Abs((mu - x) / sigma);
            return amp * Math.Exp(-Math.Pow(top, power));
        }

        public static double LogNormal(double x, double amp, double mu, double sigma)
        {
            if (x <= 0.0)
            {
                return 0;
            }
            double top = (Math.Log(x) - mu) / sigma;
            return amp * Math.Exp(-top * top);
        }

        public static double LogNormalWithBOffset(double x, double amp, double mu, double sigma, double b)
        {
            if (x <= 0.0)
            {
                return b;
            }
            double top = (Math.Log(x) - mu) / sigma;
            return amp * Math.Exp(-top * top) + b;
        }

        public static double OneExponential(double t, double a, double b, double lambda)
        {
            double lt = lambda * t;
            return a - b * Math.Exp(-lt);
        }

        public static double TwoExponential(double t, double a, double b, double lambda_1, double c, double lambda_2)
        {
            double lt_1 = lambda_1 * t;
            double lt_2 = lambda_2 * t;
            return a - b * Math.Exp(-lt_1) - c * Math.Exp(-lt_2);
        }

        public static double W2WithBOffset(double x, double amp, double lambda, double offset)
        {
            if (x == 0.0) { return offset; };
            double lambda_x = lambda * x;
            return amp * (1.0 - (1.0 - Math.Exp(-lambda_x)) / lambda_x) + offset;
        }

        public static double W3WithBOffset(double x, double amp, double lambda, double offset)
        {
            if (x == 0.0) { return offset; };
            double lambda_x = lambda * x;
            return amp * (1.0 - 0.5 * (3.0 - 4.0 * Math.Exp(-lambda_x) + Math.Exp(-2.0 * lambda_x)) / lambda_x) + offset;
        }

        public static double W4WithBOffset(double x, double amp, double lambda, double offset)
        {
            if (x == 0.0) { return offset; };
            double lt = lambda * x;
            return amp * (1.0 - (11.0 - 2.0 * Math.Exp(-3.0 * lt) + 9.0 * Math.Exp(-2.0 * lt) - 18.0 * Math.Exp(-lt)) / 6.0 / lt) + offset;
        }

        public static double ArcTanWithBOffset(double x, double amp, double mu, double sigma, double offset)
        {
            double input = (x - mu) / sigma;
            return offset + amp * Math.Atan(input);
        }

        /// <summary>
        /// <para>Calculates the inverse of a LogNormalWithBOffset.</para>
        /// <para>If the length of the return is 0 then there are no answers.</para>
        /// <para>The return values are sorted by value.</para>
        /// </summary>
        /// <param name="y"></param>
        /// <param name="amp"></param>
        /// <param name="mu"></param>
        /// <param name="sigma"></param>
        /// <param name="b"></param>
        /// <returns></returns>
        public static double[] LogNormalWithBOffset_Inverse(double y, double amp, double mu, double sigma, double b)
        {
            double f = (y - b) / amp;
            if (f.Equals(0.0))
            {
                // The solutions equal 0
                return new double[] { 0.0 };
            }
            if (f < 0.0)
            {
                // There are no valid solutions
                return new double[0];
            }

            double g = -Math.Log(f);
            if (g.Equals(0.0))
            {
                // There is only one solution
                return new double[] { Math.Exp(mu) };
            }
            if (g < 0.0)
            {
                // There are no solutions
                return new double[0];
            }

            double h = sigma * Math.Sqrt(g);
            // There are two solutions
            return new double[] {
                Math.Exp(mu - h),
                Math.Exp(mu + h)
            };
        }

        /// <summary>
        /// The minimum value (y-value) of the log-normal distribution.
        /// </summary>
        /// <param name="amp"></param>
        /// <param name="b"></param>
        /// <returns></returns>
        public static double LogNormalWithBOffset_Min(double b)
        {
            return b;
        }

        /// <summary>
        /// The maximum value (y-value) of the log-normal distribution.
        /// </summary>
        /// <param name="amp"></param>
        /// <param name="b"></param>
        /// <returns></returns>
        public static double LogNormalWithBOffset_Max(double amp, double b)
        {
            return amp + b;
        }

        /// <summary>
        /// Constants used in Erf.
        /// </summary>
        public static double a1 = 0.254829592;
        public static double a2 = -0.284496736;
        public static double a3 = 1.421413741;
        public static double a4 = -1.453152027;
        public static double a5 = 1.061405429;
        public static double p = 0.3275911;

        public static double sqrt2 = 1.4142135623731;
        public static double sqrt2Inverse = 0.707106781186547;

        public static double Erf(double x)
        {
            // constants
            //double a1 = 0.254829592;
            //double a2 = -0.284496736;
            //double a3 = 1.421413741;
            //double a4 = -1.453152027;
            //double a5 = 1.061405429;
            //double p = 0.3275911;

            // Save the sign of x
            int sign = 1;
            if (x < 0)
                sign = -1;
            x = Math.Abs(x);

            // A&S formula 7.1.26
            double t = 1.0 / (1.0 + p * x);
            double y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);

            return sign * y;
        }

    }
}
