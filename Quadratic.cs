using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;

namespace Vf61Gui
{
    /// <summary>
    /// Solves the equation y = a*x^2 + b*x + c for x. 
    /// </summary>
    /// 
    public class Quadratic
    {
        public double y = 0.0;
        public double a = 0.0;	//the 'a' coefficient
        public double b = 0.0;	//the 'b' coefficient
        public double c = 0.0;	//the 'c' coefficient
        public double a_sig = 0.0;	//the uncertainty in the 'a' coefficient
        public double b_sig = 0.0;	//the uncertainty in the 'b' coefficient
        public double c_sig = 0.0;	//the uncertainty in the 'c' coefficient
        public double sol_negative = 0.0;
        public double sol_positive = 0.0;
        public double sol_negative_stdev = 0.0;
        public double sol_positive_stdev = 0.0;
        public bool sol_negative_valid = false;
        public bool sol_positive_valid = false;

        public Quadratic() : this(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0) { }
        public Quadratic(double y, double a, double b, double c) : this(y, a, b, c, 0.0, 0.0, 0.0) { }
        public Quadratic(double y, ValueStdev a, ValueStdev b, ValueStdev c) : this(y, a.value, b.value, c.value, a.stdev, b.stdev, c.stdev) { }

        public Quadratic(double y,
            double a,
            double b,
            double c,
            double a_sig,
            double b_sig,
            double c_sig)
        {
            ClearSolutions();
            this.y = y;
            this.a = a;
            this.b = b;
            this.c = c;
            this.a_sig = a_sig;
            this.b_sig = b_sig;
            this.c_sig = c_sig;
        }


        public int NumberOfValidSolutions()
        {
            return sol_negative_valid.ToInt32(0) + sol_positive_valid.ToInt32(0);
        }

        /// <summary>
        /// Solves the quadratic equation and returns the solutions as a vector.
        /// If the solution vectors are of size zero then no real solutions were found.
        /// </summary>
        /// <param name="sol">The solution</param>
        /// <param name="sigma">The uncertainty - (one sigma)</param>
        /// <returns></returns>
        public int Solve()
        {
            ClearSolutions();
            int result = Solve(y,
                a, b, c,
                a_sig, b_sig, c_sig,
                out sol_negative, out sol_positive,
                out sol_negative_stdev, out sol_positive_stdev,
                out sol_negative_valid, out sol_positive_valid);
            return result;
        }
        public int Solve(double y,
            double a,
            double b,
            double c)
        {
            Clear();
            this.y = y;
            this.a = a;
            this.b = b;
            this.c = c;
            return Solve();
        }

        public int Solve(double y,
            double a,
            double b,
            double c,
            double a_sig,
            double b_sig,
            double c_sig)
        {
            Clear();
            this.y = y;
            this.a = a;
            this.b = b;
            this.c = c;
            this.a_sig = a_sig;
            this.b_sig = b_sig;
            this.c_sig = c_sig;
            return Solve();
        }

        public static int Solve(double y,
            double a,
            double b,
            double c,
            double a_sig,
            double b_sig,
            double c_sig,
            out double sol_negative,
            out double sol_positive,
            out double sol_negative_stdev,
            out double sol_positive_stdev,
            out bool sol_negative_valid,
            out bool sol_positive_valid)
        {
            sol_negative = 0.0;
            sol_positive = 0.0;
            sol_negative_stdev = 0.0;
            sol_positive_stdev = 0.0;
            sol_negative_valid = false;
            sol_positive_valid = false;

            c -= y;	// Makes the equation 0 = a*x^2 + b*x + c

            if (CalculateVariance(a, b, c, a_sig, b_sig, c_sig, out sol_negative, out sol_negative_stdev, false))
            {
                sol_negative_valid = true;
            }
            if (CalculateVariance(a, b, c, a_sig, b_sig, c_sig, out sol_positive, out sol_positive_stdev, true))
            {
                sol_positive_valid = true;
            }

            return sol_negative_valid.ToInt32(0) + sol_positive_valid.ToInt32(0);

        }

        public void Clear()
        {
            this.y = 0.0;
            this.a = 0.0;	//the 'a' coefficient
            this.b = 0.0;	//the 'b' coefficient
            this.c = 0.0;	//the 'c' coefficient
            this.a_sig = 0.0;	//the uncertainty in the 'a' coefficient
            this.b_sig = 0.0;	//the uncertainty in the 'b' coefficient
            this.c_sig = 0.0;	//the uncertainty in the 'c' coefficient
            ClearSolutions();
        }

        public void ClearSolutions()
        {
            this.sol_negative = 0.0;
            this.sol_positive = 0.0; ;
            this.sol_negative_stdev = 0.0; ;
            this.sol_positive_stdev = 0.0; ;
            this.sol_negative_valid = false;
            this.sol_positive_valid = false;
        }

        public static bool CalculateVariance(double a,
            double b,
            double c,
            double a_sig,
            double b_sig,
            double c_sig,
            out double sol,
            out double sigma,
            bool pos_solution)
        {
            sol = 0.0;
            sigma = 0.0;

            bool solution_exists = Solve(a, b, c, out sol, pos_solution);
            if (!solution_exists)
            {
                //No solution was found
                return solution_exists;
            }

            double dy_a = 0.0;
            double dy_b = 0.0;
            double dy_c = 0.0;
            bool dy_a_valid = false;
            bool dy_b_valid = false;
            bool dy_c_valid = false;

            dYda(a, b, c, ref dy_a, pos_solution, ref dy_a_valid);
            dYdb(a, b, c, ref dy_b, pos_solution, ref dy_b_valid);
            dYdc(a, b, c, ref dy_c, pos_solution, ref dy_c_valid);

            sigma = UtilitiesVf61Gui.Hypot(dy_a * a_sig, dy_b * b_sig, dy_c * c_sig);

            return true;
        }

        public int Solve(double y,
            double a,
            double b,
            double c,
            out double sol_neg,
            out double sol_pos,
            out bool sol_neg_valid,
            out bool sol_pos_valid)
        {
            //I'm using the method outlined in Numerical Recipes in C++
            //You can get significant rounding errors if you use the standard method
            //   when a or c (or both) are small.
            sol_neg = 0.0;
            sol_pos = 0.0;
            sol_pos_valid = false;
            sol_neg_valid = false;
            c -= y;	//Make the equation 0 = a*x^2 + b*x + c

            double sol = 0.0;
            if (Solve(a, b, c, out sol, false))
            {
                sol_neg = sol;
                sol_neg_valid = true;
            }
            if (Solve(a, b, c, out sol, true))
            {
                sol_pos = sol;
                sol_pos_valid = true;
            }

            int num_solutions = sol_neg_valid.ToInt32(0) + sol_pos_valid.ToInt32(0);
            return num_solutions;
        }

        /// <summary>
        /// Solves the quadratic equation 0 = a*x^2 + b*x + c.
        /// </summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <param name="c"></param>
        /// <param name="sol"></param>
        /// <param name="pos_solution">If true then solves the positive solution (0.5*(b + sqrt(d))/a. If false solves the negative solution (0.5*(b - sqrt(d))/a.</param>
        /// <returns>How many real solutions there are. For this function it's either 0 or 1.</returns>
        public static bool Solve(double a,
            double b,
            double c,
            out double sol,
            bool pos_solution)
        {
            //I'm using the method outlined in Numerical Recipes in C++
            //You can get significant rounding errors if you use the standard method
            //   when a or c (or both) are small.
            sol = 0.0;
            double d = D(a, b, c);
            if (d < 0.0)
            {
                return false;
            }
            double q = -0.5 * (b + Math.Sign(b) * Math.Sqrt(d));
            if (pos_solution)
            {
                sol = c / q;
                return true;
            }
            else if (q == 0.0)
            {
                //Solution does not exist
                return false;
            }
            else
            {
                sol = q / a;
                return true;
            }
        }

        /// <summary>
        /// Returns list of the valid solutions.
        /// </summary>
        /// <returns></returns>
        public List<ValueStdev> Solutions()
        {
            List<ValueStdev> solutions = new List<ValueStdev>();
            if(sol_negative_valid) {
                solutions.Add(new ValueStdev(sol_negative, sol_negative_stdev));
            }
            if(sol_positive_valid) {
                solutions.Add(new ValueStdev(sol_positive, sol_positive_stdev));
            }
            solutions.Sort();
            return solutions;
        }

        public string Validate()
        {
            string message = "Quadratic.Validate()\n";
            // a = 10.0
            // b = 20.0
            // c = 5.0
            // sol_neg = -1.7071067811865 +- 0.2151882208
            // sol_pos = -0.29289321881345 +- 0.01854803526

            this.y = 0.0;
            this.a = 10.0;
            this.b = 20.0;
            this.c = 5.0;
            this.a_sig = 1.0;
            this.b_sig = 0.5;
            this.c_sig = 0.2;
            this.Solve();

            message += string.Format("a = {0, 6:0.000} ± {1, 6:0.000}\n", this.a, this.a_sig);
            message += string.Format("b = {0, 6:0.000} ± {1, 6:0.000}\n", this.b, this.b_sig);
            message += string.Format("d = {0, 6:0.000} ± {1, 6:0.000}\n", this.c, this.c_sig);

            message += string.Format("negative solution = {0, 10:0.000} ± {1, 10:0.000}\n", this.sol_negative, this.sol_negative_stdev);
            message += string.Format("positive solution = {0, 10:0.000} ± {1, 10:0.000}\n", this.sol_positive, this.sol_positive_stdev);

            return message;
        }

        /// <summary>
        /// returns std::pow(b, 2.0) - 4.0 * a * c
        /// </summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <param name="c"></param>
        /// <returns></returns>
        private static double D(double a, double b, double c)
        {
            return b.Squared() - 4.0 * a * c;
        }

        private static bool dYda(double a, double b, double c, ref double dYda, bool pos_solution, ref bool valid)
        {
            dYda = 0.0;
            valid = false;
            double d = D(a, b, c);
            double s = pos_solution ? 1.0 : -1.0;
            if ((d <= 0.0) || (a == 0.0))
            {
                return false;
            }
            double sqrt_d = Math.Sqrt(d);

            double term_1 = -s * c / sqrt_d / a;
            //double term_2 = 0.5 * (b - s * sqrt_d) * pow(a, -2.0);
            double term_2 = 0.5 * (b - s * sqrt_d) / a.Squared();
            double result = term_1 + term_2;
            dYda = result;
            return true;
        }
        private static bool dYdb(double a, double b, double c, ref double dYdb, bool pos_solution, ref bool valid)
        {
            dYdb = 0.0;
            valid = false;
            double d = D(a, b, c);
            double s = pos_solution ? 1.0 : -1.0;
            if ((d <= 0.0) || (a == 0.0))
            {
                return false;
            }
            double sqrt_d = Math.Sqrt(d);

            double result = 0.5 * (-1.0 + s * b / sqrt_d) / a;
            dYdb = result;
            return true;
        }

        private static bool dYdc(double a, double b, double c, ref double dYdc, bool pos_solution, ref bool valid)
        {
            dYdc = 0.0;
            valid = false;
            double d = D(a, b, c);
            //const double s = pos_solution ? 1.0 : -1.0;
            if (d <= 0.0)
            {
                return false;
            }
            double sqrt_d = Math.Sqrt(d);

            double result = 1.0 / sqrt_d;
            dYdc = result;
            return true;
        }
    }
}
