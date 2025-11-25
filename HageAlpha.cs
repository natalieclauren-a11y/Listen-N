using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using Listen_N;

namespace Vf61Gui
{
    /// <summary>
    /// Solves the Hage equations for eff, M, Fs_1, and alpha.
    /// </summary>
    public class HageAlpha : Multiplicity
    {
        /// <summary>
        /// These are the valid solutions.
        /// If solutions.Count() == 0 then there are no valid solutions.
        /// </summary>
        public HageSolutionAlpha[] solutions = [];

        public ValueStdev ML = new();
        public ValueStdev eff = new();
        public ValueStdev Fs_1 = new();
        public ValueStdev alpha = new();
        public int mask;    // Stores the last mask used for calculations.

        //public HageAlpha()
        //{
        //}

        public ValueStdev MT // Property
        {
            get { return ML.MTotalFromMLeakage(vi.GetVi().Moment(1)); }
        }

        public ValueStdev Keff // Property
        {
            get { return ML.KFromMLeakage(vi.GetVi().Moment(1)); }
        }

        /// <summary>
        /// Solves HageSolutionAlpha based on the mask.<para />
        /// mask[0b0001] = (0x01) solve for alpha ratio.<para/>
        /// mask[0b0010] = (0xsolve for Fs_1.<para/>
        /// mask[0b0100] = solve for eff.<para/>
        /// mask[0b1000] = solve for ML.<para.>
        /// </summary>
        /// <param name="hageSolutionAlpha"></param>
        /// <param name="mask"></param>
        public void Solve(ref HageSolutionAlpha hageSolutionAlpha, int mask)
        {
            hageSolutionAlpha.vi = this.vi;
            hageSolutionAlpha.vs1 = this.vs1;
            hageSolutionAlpha.vs2 = this.vs2;

            this.mask = mask;
            switch (mask & 0x0F)
            {
                case 0x01:
                    // 0b0001       (int)01
                    // solve for alpha
                    Solve(hageSolutionAlpha.ML, hageSolutionAlpha.eff, hageSolutionAlpha.Fs, out hageSolutionAlpha.alpha);
                    break;
                case 0x02:
                    // 0b0010       (int)02
                    // solve for Fs_1
                    Solve(hageSolutionAlpha.ML, hageSolutionAlpha.eff, out hageSolutionAlpha.Fs, hageSolutionAlpha.alpha);
                    break;
                case 0x03:
                    // 0b0011       (int)03
                    // solve for Fs_1 and alpha
                    Solve(hageSolutionAlpha.ML, hageSolutionAlpha.eff, out hageSolutionAlpha.Fs, out hageSolutionAlpha.alpha);
                    break;
                case 0x04:
                    // 0b0100       (int)04
                    // solve for eff
                    Solve(hageSolutionAlpha.ML, out hageSolutionAlpha.eff, hageSolutionAlpha.Fs, hageSolutionAlpha.alpha);
                    break;
                case 0x05:
                    // 0b0101       (int)05
                    // solve for eff and alpha
                    Solve(hageSolutionAlpha.ML, out hageSolutionAlpha.eff, hageSolutionAlpha.Fs, out hageSolutionAlpha.alpha);
                    break;
                case 0x06:
                    // 0b0110       (int)06
                    // solve for eff and Fs_1
                    Solve(hageSolutionAlpha.ML, out hageSolutionAlpha.eff, out hageSolutionAlpha.Fs, hageSolutionAlpha.alpha);
                    break;
                case 0x07:
                    // 0b0111       (int)07
                    // solve for eff, Fs_1, and alpha
                    // not implemented
                    // Solve(hageSolutionAlpha.ML, out hageSolutionAlpha.eff, out hageSolutionAlpha.Fs, out hageSolutionAlpha.alpha);
                    break;
                case 0x08:
                    // 0b1000       (int)08
                    // solve for ML
                    Solve(out hageSolutionAlpha.ML, hageSolutionAlpha.eff, hageSolutionAlpha.Fs, hageSolutionAlpha.alpha);
                    break;
                case 0x09:
                    // 0b1001       (int)09
                    // solve for ML and alpha
                    Solve(out hageSolutionAlpha.ML, hageSolutionAlpha.eff, hageSolutionAlpha.Fs, out hageSolutionAlpha.alpha);
                    break;
                case 0x0A:
                    // 0b1010       (int)10
                    // solve for ML and Fs_1
                    Solve(out hageSolutionAlpha.ML, hageSolutionAlpha.eff, out hageSolutionAlpha.Fs, hageSolutionAlpha.alpha);
                    break;
                case 0x0B:
                    // 0b1011       (int)11
                    // solve for ML, Fs_1, and alpha
                    // not implemented
                    // Solve(out hageSolutionAlpha.ML, hageSolutionAlpha.eff, out  hageSolutionAlpha.Fs, out hageSolutionAlpha.alpha);
                    break;
                case 0x0C:
                    // 0b1100       (int)12
                    // solve for ML and eff
                    // not implemented
                    // Solve(hageSolutionAlpha.ML, hageSolutionAlpha.eff, hageSolutionAlpha.Fs, hageSolutionAlpha.alpha);
                    break;
                case 0x0D:
                    // 0b1101       (int)13
                    // solve for ML, eff, and alpha
                    // not implemented
                    // Solve(out hageSolutionAlpha.ML, out hageSolutionAlpha.eff, hageSolutionAlpha.Fs, out hageSolutionAlpha.alpha);
                    break;
                case 0x0E:
                    // 0b1110       (int)14
                    // solve for ML, eff, and Fs_1
                    // not implemented
                    // Solve(out hageSolutionAlpha.ML, out hageSolutionAlpha.eff, out hageSolutionAlpha.Fs, hageSolutionAlpha.alpha);
                    break;
                case 0x0F:
                    // 0b1111       (int)15
                    // solve for ML, eff, Fs_1, and alpha
                    // not implemented
                    // Solve(out hageSolutionAlpha.ML, out hageSolutionAlpha.eff, out hageSolutionAlpha.Fs, out hageSolutionAlpha.alpha);
                    break;
            }
        }

        public void AssignResults(ValueStdev _ML, ValueStdev _eff, ValueStdev _Fs, ValueStdev _alpha, int _mask)
        {
            ML = new ValueStdev(_ML);
            eff = new ValueStdev(_eff);
            Fs_1 = new ValueStdev(_Fs);
            alpha = new ValueStdev(_alpha);
            mask = _mask;
        }
        /// <summary>
        /// Solve for ML.
        /// There is only one solution.
        /// </summary>
        /// <param name="_ML"></param>
        /// <param name="_eff"></param>
        /// <param name="_Fs_1"></param>
        /// <param name="_alpha"></param>
        /// <returns></returns>
        public void Solve(out ValueStdev _ML, ValueStdev _eff, ValueStdev _Fs, ValueStdev _alpha)
        {
            ValueStdev ap1 = _alpha.AlphaPlusOne();
            double vs_1 = vs1.GetVs1().Moment(1);
            double value = R1.value / _Fs.value / _eff.value / ap1.value / vs_1;
            double stdev = Math.Abs(value * R1.FractionalError());

            if (value >= 1.0)
            {
                // This is a valid solution
                _ML = new ValueStdev(value, stdev);
                solutions = new HageSolutionAlpha[] {
                    new HageSolutionAlpha(_ML, _eff, _Fs, _alpha, R1, R2, R3, R4)
                };
            }
            else
            {
                _ML = new ValueStdev();
                solutions = new HageSolutionAlpha[0];
            }

            AssignResults(_ML, _eff, _Fs, _alpha, this.mask);
        }

        /// <summary>
        /// Calculates the value K = (ML - 1.0) / (vi1 - 1.0).<para/>
        /// This is not Keff.<para/>
        /// </summary>
        /// <param name="ML">Leakage Multiplication.</param>
        public ValueStdev K(ValueStdev ML)
        {
            return ML.KFromMLeakage(vi.GetVi().Moment(1));
        }

        /// <summary>
        /// Solves the equation K = (ML - 1) / (vi1 - 1) for ML.<para/>
        /// This is not Keff.<para/>
        /// </summary>
        /// <param name="K"></param>
        public ValueStdev MLFromK(ValueStdev K)
        {
            return K.MLeakageFromK(vi.GetVi().Moment(1));
        }

        /// <summary>
        /// Solve for efficiency.
        /// There is only one solution.
        /// </summary>
        /// <param name="_ML"></param>
        /// <param name="_eff"></param>
        /// <param name="_Fs"></param>
        /// <param name="_alpha"></param>
        /// <returns></returns>
        public void Solve(ValueStdev _ML, out ValueStdev _eff, ValueStdev _Fs, ValueStdev _alpha)
        {
            ValueStdev ap1 = _alpha.AlphaPlusOne();            
            double vs_1 = vs1.GetVs1().Moment(1);
            double value = R1.value / _Fs.value / _ML.value / ap1.value / vs_1;
            double stdev = Math.Abs(value * R1.FractionalError());
            if (value.InRangeEI(0.0, 1.0))
            {
                _eff = new ValueStdev(value, stdev);
                solutions = new HageSolutionAlpha[] {
                    new HageSolutionAlpha(_ML, _eff, _Fs, _alpha, R1, R2, R3, R4)
                };
            }
            else
            {
                _eff = new ValueStdev();
                solutions = new HageSolutionAlpha[0];
            }
            AssignResults(_ML, _eff, _Fs, _alpha, this.mask);
        }

        /// <summary>
        /// Solve for Fs.
        /// There is only one solution.
        /// </summary>
        /// <param name="_ML"></param>
        /// <param name="_eff"></param>
        /// <param name="_Fs_1"></param>
        /// <param name="_alpha"></param>
        /// <returns></returns>
        public void Solve(ValueStdev _ML, ValueStdev _eff, out ValueStdev _Fs, ValueStdev _alpha)
        {
            ValueStdev ap1 = _alpha.AlphaPlusOne();
            double vs_1 = vs1.GetVs1().Moment(1);
            double value = R1.value / _ML.value / _eff.value / ap1.value / vs_1;
            double stdev = Math.Abs(value * R1.FractionalError());
            if (value > 0.0)
            {
                _Fs = new ValueStdev(value, stdev);
                solutions = new HageSolutionAlpha[] {
                    new HageSolutionAlpha(_ML, _eff, _Fs, _alpha, R1, R2, R3, R4)
                };
            }
            else
            {
                _Fs = new ValueStdev();
                solutions = new HageSolutionAlpha[0];
            }
            AssignResults(_ML, _eff, _Fs, _alpha, this.mask);
        }

        /// <summary>
        /// Solve for alpha.
        /// There is only one solution.
        /// </summary>
        /// <param name="_ML"></param>
        /// <param name="_eff"></param>
        /// <param name="_Fs_1"></param>
        /// <param name="_alpha"></param>
        /// <returns></returns>
        public void Solve(ValueStdev _ML, ValueStdev _eff, ValueStdev _Fs, out ValueStdev _alpha)
        {
            double vs_1 = vs1.GetVs1().Moment(1);
            double value = R1.value / _ML.value / _eff.value / _Fs.value / vs_1;
            double stdev = Math.Abs(value * R1.FractionalError());
            if (value > 1.0)
            {
                ValueStdev ap1 = new ValueStdev(value, stdev);
                _alpha = ap1.Alpha();
                solutions = new HageSolutionAlpha[] {
                    new HageSolutionAlpha(_ML, _eff, _Fs, _alpha, R1, R2, R3, R4)
                };
            }
            else
            {
                _alpha = new ValueStdev();
                solutions = new HageSolutionAlpha[0];
            }
            AssignResults(_ML, _eff, _Fs, _alpha, this.mask);
        }

        /// <summary>
        /// Solves for ML and eff.
        /// There is only one solution.
        /// </summary>
        /// <param name="_ML"></param>
        /// <param name="_eff"></param>
        /// <param name="_Fs_1"></param>
        /// <param name="_alpha"></param>
        /// <returns></returns>
        public void Solve(out ValueStdev _ML, out ValueStdev _eff, ValueStdev _Fs, ValueStdev _alpha)
        {
            // Solve for ML first.
            double vs_1 = vs1.GetVs1().Moment(1);
            double vs_2 = vs1.GetVs1().Moment(2);
            double vi_2 = vi.GetVi().Moment(2);
            ValueStdev ap1 = _alpha.AlphaPlusOne();
            ValueStdev sm2 = Sm2();

            double vs_1_ap1 = vs_1 * ap1.value;
            double value_K = (sm2.value * vs_1_ap1.Squared() * _Fs.value - vs_2) / vs_1_ap1 / vi_2;
            double stdev_K = sm2.stdev * _Fs.value * ap1.value * vs_1 / vi_2;

            if (value_K >= 0.0)
            {
                // Now solve for eff
                ValueStdev k = new ValueStdev(value_K, stdev_K);
                _ML = MLFromK(k);
                // Solve will automatically set solutions because there is only one solution.
                Solve(_ML, out _eff, _Fs, _alpha);
            }
            else
            {
                _ML = new ValueStdev();
                _eff = new ValueStdev();
                solutions = new HageSolutionAlpha[0];
            }
            AssignResults(_ML, _eff, _Fs, _alpha, this.mask);
        }

        /// <summary>
        /// Solves for ML and Fs.
        /// There are potentially two solutions.
        /// </summary>
        /// <param name="_ML"></param>
        /// <param name="_eff"></param>
        /// <param name="_Fs_1"></param>
        /// <param name="_alpha"></param>
        /// <returns></returns>
        public void Solve(out ValueStdev _ML, ValueStdev _eff, out ValueStdev _Fs, ValueStdev _alpha)
        {
            // Solve for ML first.
            double vs_1 = vs1.GetVs1().Moment(1);
            double vs_2 = vs1.GetVs1().Moment(2);
            double vi_2 = vi.GetVi().Moment(2);
            double vi1m1 = vi.GetVi().Moment(1) - 1.0;

            ValueStdev ap1 = new ValueStdev(_alpha.value - 1.0, _alpha.stdev);
            ValueStdev r2f = R2f();

            ValueStdev a = new ValueStdev(_eff.value * ap1.value * vi_2 * vs_1);
            ValueStdev b = new ValueStdev(-_eff.value * ap1.value * vi_2 * vs_1 + _eff.value * vi1m1 * vs_2);
            ValueStdev c = new ValueStdev(-r2f.value * vi1m1 * vs_1 * ap1.value);

            // I'm only interested in the uncertainties associated with the rates.
            // Thus
            //  a.stdev = 0.0
            //  b.stdev = 0.0
             c.stdev = c.value * r2f.FractionalError();

            Quadratic quadratic = new Quadratic(0.0, a, b, c);
            quadratic.Solve();

            List<HageSolutionAlpha> solutionsHage = new List<HageSolutionAlpha>();
            foreach (ValueStdev valueStdev in quadratic.Solutions())
            {
                if (valueStdev.value >= 1.0)
                {
                    solutionsHage.Add(new HageSolutionAlpha(valueStdev, _eff, new ValueStdev(), _alpha, R1, R2, R3, R4));
                }
            }
 
            // Check to see if there are any valid solutions. 
            // If there are not then assign _ML to 1.0 +- 0.0
            if (solutionsHage.Count().Equals(0))
            {
                ValueStdev ml = new ValueStdev(1.0);
                ValueStdev fs = new ValueStdev();
                solutionsHage.Add(new HageSolutionAlpha(ml, _eff, fs, _alpha, R1, R2, R3, R4));
            }

            // Solve for Fs
            foreach (HageSolutionAlpha solution in solutionsHage)
            {
                Solve(solution.ML, solution.eff, out solution.Fs, solution.alpha);
            }

            // Look for valid solutions.
            solutionsHage.Sort((left, right) => left.ML.value.CompareTo(right.ML.value));
            solutions = solutionsHage.Where(s => s.ML.value >= 1.0 && s.Fs.value > 0.0).ToArray();
            if (solutions.Count().Equals(0))
            {
                // No solutions
                _ML = new ValueStdev();
                _Fs = new ValueStdev();
            }
            else
            {
                _ML = solutions[0].ML;
                _Fs = solutions[0].Fs;
            }
            AssignResults(_ML, _eff, _Fs, _alpha, this.mask);
        }
        

        /// <summary>
        /// Solves for ML and alpha.
        /// There are two solutions.
        /// </summary>
        /// <param name="_ML"></param>
        /// <param name="_eff"></param>
        /// <param name="_Fs_1"></param>
        /// <param name="_alpha"></param>
        /// <returns></returns>
        public void Solve(out ValueStdev _ML, ValueStdev _eff, ValueStdev _Fs, out ValueStdev _alpha)
        {
            // Solve for ML first.
            double vs_1 = vs1.GetVs1().Moment(1);
            double vs_2 = vs1.GetVs1().Moment(2);
            double vi_2 = vi.GetVi().Moment(2);
            double vi1m1 = vi.GetVi().Moment(1) - 1.0;
            
            // Solve for ML first
            ValueStdev a = new ValueStdev(_Fs.value * _eff.value.Squared() * vi1m1 * vs_2 + _eff.value * R1.value * vi_2);
            ValueStdev b = new ValueStdev(-_eff.value * R1.value * vi_2);
            ValueStdev c = new ValueStdev(-R2.value * vi1m1);

            // I'm only interested in the uncertainties associated with the rates.
            // Thus
            //  a.stdev = 0.0
            b.stdev = b.value * R1.FractionalError();
            c.stdev = R2.stdev * vi1m1;

            Quadratic quadratic = new Quadratic(0.0, a, b, c);
            quadratic.Solve();

            List<HageSolutionAlpha> solutionsHage = new List<HageSolutionAlpha>();
            //Remember, these are ML
            foreach (ValueStdev valueStdev in quadratic.Solutions())
            {
                if (valueStdev.value >= 1.0)
                {
                    ValueStdev alpha = new ValueStdev();
                    solutionsHage.Add(new HageSolutionAlpha(valueStdev, _eff, _Fs, alpha, R1, R2, R3, R4));
                }
            }

            if (solutionsHage.Count().Equals(0))
            {
                ValueStdev ML = new ValueStdev(1.0);
                ValueStdev alpha = new ValueStdev();
                solutionsHage.Add(new HageSolutionAlpha(ML, _eff, _Fs, alpha, R1, R2, R3, R4));
            }

            // Solve for alpha.
            foreach (HageSolutionAlpha sol in solutionsHage)
            {
                Solve(sol.ML, sol.eff, sol.Fs, out sol.alpha);
            }

            solutionsHage.Sort((left, right) => left.ML.value.CompareTo(right.ML.value));
            solutions = solutionsHage.Where(s => s.ML.value >= 1.0 && s.alpha.value >= 0.0).ToArray();
            if (solutions.Count().Equals(0))
            {
                _ML = new ValueStdev();
                _alpha = new ValueStdev();
            }
            else
            {
                _ML = solutions[0].ML;
                _alpha = solutions[0].alpha;
            }
            AssignResults(_ML, _eff, _Fs, _alpha, this.mask);
        }

        /// <summary>
        /// Solve for eff and Fs.
        /// There is only one solution.
        /// </summary>
        /// <param name="_ML"></param>
        /// <param name="_eff"></param>
        /// <param name="_Fs"></param>
        /// <param name="_alpha"></param>
        public void Solve(ValueStdev _ML, out ValueStdev _eff, out ValueStdev _Fs, ValueStdev _alpha)
        {
            // Solve for Fs first.
            double vs_1 = vs1.GetVs1().Moment(1);
            double vs_2 = vs1.GetVs1().Moment(2);
            double vi_2 = vi.GetVi().Moment(2);
            double vi1m1 = vi.GetVi().Moment(1) - 1.0;
            double ap1 = _alpha.value + 1.0;
            double k = K(_ML).value;

            ValueStdev sm2 = Sm2();

            // Solve for Fs first
            double denom = (ap1 * vs_1).Squared() * sm2.value;
            if (denom <= 0.0)
            {
                // There's really no way out. Just return a blank solution.
                _Fs = new ValueStdev();
                _eff = new ValueStdev();
                solutions = new HageSolutionAlpha[0];
                return;
            }

            double value = (k * ap1 * vi_2 * vs_1 + vs_2) / denom;
            double stdev = value * sm2.FractionalError();
            
            if (value < 0.0)
            {
                // There's really no way out. Just return a blank solution.
                _Fs = new ValueStdev();
                _eff = new ValueStdev();
                solutions = new HageSolutionAlpha[0];
                return;
            }
            else
            {
                _Fs = new ValueStdev(value, stdev);
                Solve(_ML, out _eff, _Fs, _alpha);  // Because there is only one solution, solutions is updated in Solve().
            }
            AssignResults(_ML, _eff, _Fs, _alpha, this.mask);
        }

        /// <summary>
        /// Solve for eff &amp; alpha.
        /// There are potentially 2 solutions.
        /// </summary>
        /// <param name="_ML"></param>
        /// <param name="_eff"></param>
        /// <param name="_Fs"></param>
        /// <param name="_alpha"></param>
        public void Solve(ValueStdev _ML, out ValueStdev _eff, ValueStdev _Fs, out ValueStdev _alpha)
        {
            // Solve for eff first.
            double vs_1 = vs1.GetVs1().Moment(1);
            double vs_2 = vs1.GetVs1().Moment(2);
            double vi_2 = vi.GetVi().Moment(2);
            double vi1m1 = vi.GetVi().Moment(1) - 1.0;
            ValueStdev k = K(_ML);
            //ValueStdev sm2 = Sm2();


            ValueStdev a = new ValueStdev(_Fs.value * vs_2);
            ValueStdev b = new ValueStdev(R1.value * k.value * vi_2);
            ValueStdev c = new ValueStdev(-R2.value);    

            //a.stdev = 0.0;
            b.stdev = R1.stdev * k.value * vi_2;
            c.stdev = R2.stdev;

            // The solutions need to be divided by ML.
            Quadratic quadratic = new Quadratic(0, a, b, c);
            quadratic.Solve();
            List<HageSolutionAlpha> solutionsHage = new List<HageSolutionAlpha>();
            foreach (ValueStdev valueStdev in quadratic.Solutions())
            {
                // We are solving for eff
                // The solutions need to be divided by ML.
                if ((valueStdev.value / _ML.value).InRangeEI(0.0, 1.0))
                {
                    ValueStdev eff = new ValueStdev(valueStdev);
                    eff.value /= _ML.value;
                    eff.stdev /= _ML.value;
                    ValueStdev alpha = new ValueStdev();
                    solutionsHage.Add(new HageSolutionAlpha(_ML, eff, _Fs, alpha, R1, R2, R3, R4));
                }
            }
            if (solutionsHage.Count().Equals(0))
            {
                // There's no viable solutions for eff.
                // See what you get if you set alpha to 0.0
                _alpha = new ValueStdev();
                Solve(_ML, out _eff, _Fs, _alpha);    // This will set solutions
                return;
            }
            foreach (HageSolutionAlpha sol in solutionsHage)
            {
                Solve(sol.ML, sol.eff, sol.Fs, out sol.alpha);
            }
            solutionsHage.Sort((left, right) => left.eff.value.CompareTo(right.eff.value));
            solutions = solutionsHage.Where(s => s.eff.value.InRangeEI(0.0, 1.0) && s.alpha.value >= 0.0).ToArray();
            if (solutions.Count().Equals(0))
            {
                _eff = new ValueStdev();
                _alpha = new ValueStdev();
            }
            else
            {
                _eff = solutions[0].eff;
                _alpha = solutions[0].alpha;
            }
            AssignResults(_ML, _eff, _Fs, _alpha, this.mask);
        }

        /// <summary>
        /// Solve for Fs &amp; alpha.
        /// There is only one solution.
        /// </summary>
        /// <param name="_ML"></param>
        /// <param name="_eff"></param>
        /// <param name="_Fs"></param>
        /// <param name="_alpha"></param>
        public void Solve(ValueStdev _ML, ValueStdev _eff, out ValueStdev _Fs, out ValueStdev _alpha)
        {
            double vs_1 = vs1.GetVs1().Moment(1);
            double vs_2 = vs1.GetVs1().Moment(2);
            double vi_2 = vi.GetVi().Moment(2);
            double vi1m1 = vi.GetVi().Moment(1) - 1.0;
            double k = K(_ML).value;

            // Solve for Fs first.
            double denom = (_ML.value * _eff.value).Squared() * vs_2;
            double value = denom.Equals(0.0) ? 0.0 : (R2.value - k * _ML.value * _eff.value * R1.value * vi_2) / denom;

            double stdev_denom = _ML.value * _eff.value * vs_2;
            double stdev = 0.0;
            if (!stdev_denom.Equals(0.0))
            {
                double[] stdevs = new double[] {
                    R1.stdev / stdev_denom,
                    R2.stdev / stdev_denom / _ML.value / _eff.value
                };
                stdev = stdevs.SumInQuadrature();
            }
            _Fs = new ValueStdev(value, stdev);

            // Next, solve for alpha
            Solve(_ML, _eff, _Fs, out _alpha);  // This will set solutions and call AssignResults(...)
        }

        public string Print()
        {
            if (solutions == null)
            {
                return "No solutions exist.\n";
            }
            string text = string.Format("Number of solutions = {0}\n", solutions.Count());
            int i = 0;
            foreach (HageSolutionAlpha solution in solutions)
            {
                text += string.Format("solution {0}\n", i);
                text += solution.Print();
                text += "\n";
                ++i;
            }

            return text;
        }

        public string Compare(ValueStdev ML, ValueStdev eff, ValueStdev Fs, ValueStdev alpha)
        {
            if (solutions == null)
            {
                return "No solutions exist.\n";
            }
            string text = string.Format("Number of solutions = {0}\n", solutions.Count());
            int i = 0;
            foreach (HageSolutionAlpha solution in solutions)
            {
                text += string.Format("solution {0}\n", i);
                text += string.Format("R1 = {0}\n", solution.R1.Print());
                text += string.Format("R2 = {0}\n", solution.R2.Print());
                text += string.Format("R3 = {0}\n", solution.R3.Print());
                text += string.Format("R4 = {0}\n", solution.R4.Print());
                //text += solution.Print();
                text += string.Format("ML = {0}\n", solution.ML.Print());
                text += string.Format("     {0}\n", ML.Print());
                text += string.Format("     {0} +- {1}\n", solution.ML.value / ML.value, solution.ML.stdev / ML.stdev);
                text += "\n";
                //text += solution.Print();
                text += string.Format("eff = {0}\n", solution.eff.Print());
                text += string.Format("      {0}\n", eff.Print());
                text += string.Format("      {0} +- {1}\n", solution.eff.value / eff.value, solution.eff.stdev / eff.stdev);
                text += "\n";
                //text += solution.Print();
                text += string.Format("Fs = {0}\n", solution.Fs.Print());
                text += string.Format("     {0}\n", Fs.Print());
                text += string.Format("     {0} +- {1}\n", solution.Fs.value / Fs.value, solution.Fs.stdev / Fs.stdev);
                text += "\n";
                //text += solution.Print();
                text += string.Format("alpha = {0}\n", solution.alpha.Print());
                text += string.Format("        {0}\n", alpha.Print());
                text += string.Format("        {0} +- {1}\n", solution.alpha.value / alpha.value, solution.alpha.stdev / alpha.stdev);
                text += "\n";
                ++i;
            }

            return text;
        }

        /// <summary>
        /// Solves HageSolutionAlpha based on the mask.<para />
        /// mask[0b0001] = (0x01) solve for alpha ratio.<para/>
        /// mask[0b0010] = (0xsolve for Fs_1.<para/>
        /// mask[0b0100] = solve for eff.<para/>
        /// mask[0b1000] = solve for ML.<para.>
        /// </summary>
        /// <param name="hageSolutionAlpha"></param>
        /// <param name="mask"></param>
        public string MaskString(int mask)
        {
            List<string> results = new List<string>();
            if (mask.IsBitSetTo1(3))
            {
                results.Add("Multiplication");
            }
            if (mask.IsBitSetTo1(2))
            {
                results.Add("efficiency");
            }
            if (mask.IsBitSetTo1(1))
            {
                results.Add("Fs_1");
            }
            if (mask.IsBitSetTo1(0))
            {
                results.Add("alpha");
            }
            if (results.Count < 1)
            {
                return string.Format("Mask 0x{0:X4} not recognized.", mask);
            }

            StringBuilder str = new StringBuilder("Solving for ");
            if(results.Count.Equals(1)) {
                return str.Append(results[0]).ToString();
            }
            for(int i = 0; i < results.Count - 1; ++i) {
                str.Append(results[0]).Append(", ");
            }
            str.Append("and ").Append(results.Last());


            switch (mask & 0x0F)
            {
                 case 0x07:
                    // 0b0111       (int)07
                    // solve for eff, Fs_1, and alpha
                    // not implemented
                    // Solve(hageSolutionAlpha.ML, out hageSolutionAlpha.eff, out hageSolutionAlpha.Fs, out hageSolutionAlpha.alpha);
                case 0x0B:
                    // 0b1011       (int)11
                    // solve for ML, Fs_1, and alpha
                    // not implemented
                    // Solve(out hageSolutionAlpha.ML, hageSolutionAlpha.eff, out  hageSolutionAlpha.Fs, out hageSolutionAlpha.alpha);
                case 0x0C:
                    // 0b1100       (int)12
                    // solve for ML and eff
                    // not implemented
                    // Solve(hageSolutionAlpha.ML, hageSolutionAlpha.eff, hageSolutionAlpha.Fs, hageSolutionAlpha.alpha);
                case 0x0D:
                    // 0b1101       (int)13
                    // solve for ML, eff, and alpha
                    // not implemented
                    // Solve(out hageSolutionAlpha.ML, out hageSolutionAlpha.eff, hageSolutionAlpha.Fs, out hageSolutionAlpha.alpha);
                case 0x0E:
                    // 0b1110       (int)14
                    // solve for ML, eff, and Fs_1
                    // not implemented
                    // Solve(out hageSolutionAlpha.ML, out hageSolutionAlpha.eff, out hageSolutionAlpha.Fs, hageSolutionAlpha.alpha);
                case 0x0F:
                    // 0b1111       (int)15
                    // solve for ML, eff, Fs_1, and alpha
                    // not implemented
                    // Solve(out hageSolutionAlpha.ML, out hageSolutionAlpha.eff, out hageSolutionAlpha.Fs, out hageSolutionAlpha.alpha);
                    str.Append(" (Not Implemented)");
                    break;
            }
            return str.Append(".").ToString();
        }

        //public ValueStdev b1_1 // Property
        //{
        //    get { return UtilitiesVf61Gui.b1(ML, vs1, vi); }
        //}
        //public ValueStdev b1_2 // Property
        //{
        //    get { return ML.b1(vs2, vi); }
        //}

        //public ValueStdev b2_1 // Property
        //{
        //    get { return ML.b2(vs1, vi); }
        //}
        //public ValueStdev b2_2 // Property
        //{
        //    get { return ML.b2(vs2, vi); }
        //}

        //public ValueStdev b3_1 // Property
        //{
        //    get { return ML.b3(vs1, vi); }
        //}
        //public ValueStdev b3_2 // Property
        //{
        //    get { return ML.b3(vs2, vi); }
        //}

        //public ValueStdev b4_1 // Property
        //{
        //    get { return ML.b4(vs1, vi); }
        //}
        //public ValueStdev b4_2 // Property
        //{
        //    get { return ML.b4(vs2, vi); }
        //}

    }
}
