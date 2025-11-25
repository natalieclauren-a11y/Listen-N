// Multiplicity.cs
// Purpose: Deterministic transforms among multiplicity parameters used in real-time neutron noise analysis.
// Scope: Converts between leakage multiplication ML, total multiplication MT, the convenience ratio K,
//        effective multiplication Keff, and source/induced factorial-moment coefficients (b1..b4, beta).
//
// This file assumes induced emission distributions (vi) and spontaneous emission distributions (vs),
// working with raw factorial moments. Guard checks are used for non-physical input and potential numerical issues.

using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using Listen_N;

namespace Vf61Gui
{
    /// <summary>
    /// Container for multiplicity parameters (R1..R4) and emission distributions.
    /// Provides helper functions for ratios used in neutron multiplicity analysis.
    /// </summary>
    public class Multiplicity
    {
        public ValueStdev R1 = new ValueStdev();
        public ValueStdev R2 = new ValueStdev();
        public ValueStdev R3 = new ValueStdev();
        public ValueStdev R4 = new ValueStdev();
        public Snm vi = new Snm();   // induced fission distribution moments
        public Snm vs1 = new Snm();  // spontaneous fission distribution moments
        public Snm vs2 = new Snm();  // second spontaneous source channel (alpha-n by default)

        public Multiplicity()
        {
            // Default to using alpha-n as the second spontaneous channel.
            vs2.SetVs2("alpha-n");
        }

        /// <summary>
        /// Sm2 = R2 / R1^2. Returns dimensionless ratio with propagated uncertainty.
        /// </summary>
        public ValueStdev Sm2()
        {
            if (R1.value.Equals(0.0))
                return new ValueStdev(); // guard against division by zero

            double value = R2.value / (R1.value * R1.value);
            // Propagate uncertainty using fractional errors (2σ from R1 and σ from R2)
            double[] stdevs = new double[] { 2.0 * R1.FractionalError(), R2.FractionalError() };
            double stdev = value * stdevs.SumInQuadrature();
            return new ValueStdev(value, stdev);
        }

        /// <summary>
        /// Sm3 = R3 / R1^3. Returns dimensionless ratio with propagated uncertainty.
        /// </summary>
        public ValueStdev Sm3()
        {
            if (R1.value.Equals(0.0))
                return new ValueStdev();

            double value = R3.value / Math.Pow(R1.value, 3);
            // Propagate uncertainty (3σ from R1 and σ from R3)
            double[] stdevs = new double[] { 3.0 * R1.FractionalError(), R3.FractionalError() };
            double stdev = value * stdevs.SumInQuadrature();
            return new ValueStdev(value, stdev);
        }

        /// <summary>
        /// R2f = R2 / R1. First factorial moment ratio.
        /// </summary>
        public ValueStdev R2f()
        {
            if (R1.value.Equals(0.0))
                return new ValueStdev();

            double value = R2.value / R1.value;
            double[] stdevs = new double[] { R1.FractionalError(), R2.FractionalError() };
            double stdev = value * stdevs.SumInQuadrature();
            return new ValueStdev(value, stdev);
        }

        /// <summary>
        /// R3f = R3 / R1. Second factorial moment ratio.
        /// </summary>
        public ValueStdev R3f()
        {
            if (R1.value.Equals(0.0))
                return new ValueStdev();

            double value = R3.value / R1.value;
            double[] stdevs = new double[] { R1.FractionalError(), R3.FractionalError() };
            double stdev = value * stdevs.SumInQuadrature();
            return new ValueStdev(value, stdev);
        }
    }

    public static partial class Utilities
    {
        // b1..b4 implement Hage–Cifarelli coefficients relating spontaneous and induced emissions.

        public static ValueStdev b1(this ValueStdev ML, Snm vs, Snm vi)
        {
            // b1 reduces to the first moment of spontaneous fission.
            return new ValueStdev(vs.GetVs1().Moment(1));
        }

        public static ValueStdev b2(this ValueStdev ML, Snm vs, Snm vi)
        {
            double vi1 = vi.GetVi().Moment(1);
            double vi2 = vi.GetVi().Moment(2);
            double vs1 = vs.GetVs1().Moment(1);
            double vs2 = vs.GetVs1().Moment(2);

            // Convert leakage multiplication ML into K.
            ValueStdev K = ML.KFromMLeakage(vi1);
            double vsvi = vs1 * vi2;

            // Expression is linear in K.
            return new ValueStdev()
            {
                value = (vs2 + vsvi * K.value).Clamp(0.0, 1.0E100),
                stdev = (vsvi * K.stdev).Clamp(0.0, 1.0E100)
            };
        }

        public static ValueStdev b3(this ValueStdev ML, Snm vs, Snm vi)
        {
            double vi1 = vi.GetVi().Moment(1);
            double vi2 = vi.GetVi().Moment(2);
            double vi3 = vi.GetVi().Moment(3);

            double vs1 = vs.GetVs1().Moment(1);
            double vs2 = vs.GetVs1().Moment(2);
            double vs3 = vs.GetVs1().Moment(3);

            ValueStdev K = ML.KFromMLeakage(vi1);

            // Polynomial coefficients in K.
            double a = vs3;
            double b = (2.0 * vs2 * vi2 + vs1 * vi3) + 2.0; // TODO: validate constant term.
            double c = vi2 * vi2 * vs1;

            return new ValueStdev()
            {
                value = (a + K.value * b + K.value.Squared() * c).Clamp(0.0, 1.0E100),
                stdev = ((2.0 * c * K.value + b) * K.stdev).Clamp(0.0, 1.0E100)
            };
        }

        public static ValueStdev b4(this ValueStdev ML, Snm vs, Snm vi)
        {
            double vi1 = vi.GetVi().Moment(1);
            double vi2 = vi.GetVi().Moment(2);
            double vi3 = vi.GetVi().Moment(3);
            double vi4 = vi.GetVi().Moment(4);

            double vs1 = vs.GetVs1().Moment(1);
            double vs2 = vs.GetVs1().Moment(2);
            double vs3 = vs.GetVs1().Moment(3);
            double vs4 = vs.GetVs1().Moment(4);

            ValueStdev K = ML.KFromMLeakage(vi1);

            // Polynomial in K with up to cubic term.
            double a = vs4;
            double b = 3.0 * vs3 * vi2 + 2.0 * vs2 * vi3 + vs1 * vi4;
            double c = 4.0 * vs2 * Math.Pow(vi2, 2) + 5.0 * vs1 * vi2 * vi3;
            double d = 5.0 * vs1 * Math.Pow(vi2, 3);

            return new ValueStdev()
            {
                value = (a + b * K.value + c * K.value * K.value + d * Math.Pow(K.value, 3)).Clamp(0.0, 1.0E100),
                stdev = ((b + 2.0 * c * K.value + 3.0 * d * K.value * K.value) * K.stdev).Clamp(0.0, 1.0E100)
            };
        }

        /// <summary>
        /// Convert ML to Feynman beta (variance-to-mean excess).
        /// </summary>
        public static ValueStdev BetaFromMLeakage(this ValueStdev ML, Snm vs, Snm vi)
        {
            ValueStdev _b1 = b1(ML, vs, vi);
            ValueStdev _b2 = b2(ML, vs, vi);
            ValueStdev _b3 = b3(ML, vs, vi);

            double vi2 = vi.GetVi().Moment(2);
            double vi3 = vi.GetVi().Moment(3);
            double vs1 = vs.GetVs1().Moment(1);
            double vs2 = vs.GetVs1().Moment(2);
            double vs3 = vs.GetVs1().Moment(3);

            ValueStdev K = ML.KFromMLeakage(vi2);

            // Ratio of squares defines beta.
            double numer = _b2.value.Squared();
            double denom = _b1.value * _b3.value;

            if (denom <= 0.0)
                return new ValueStdev();

            return new ValueStdev()
            {
                value = numer / denom,
                stdev = 0.0 // TODO: propagate uncertainty properly.
            };
        }

        /// <summary>
        /// Invert beta to leakage multiplication ML. Solves quadratic in K.
        /// Returns only physically valid (ML ≥ 1) solutions.
        /// </summary>
        public static List<ValueStdev> MLeakageFromBeta(this ValueStdev beta, Snm vs, Snm vi)
        {
            double vi2 = vi.GetVi().Moment(2);
            double vi3 = vi.GetVi().Moment(3);
            double vi1 = vi.GetVi().Moment(1);

            double vs1 = vs.GetVs1().Moment(1);
            double vs2 = vs.GetVs1().Moment(2);
            double vs3 = vs.GetVs1().Moment(3);

            // Quadratic coefficients (a, b, c) for equation in K.
            double vi2vs1 = vi2 * vs1;
            double a = 2.0 * beta.value * Math.Pow(vi2vs1, 2) - Math.Pow(vi2vs1, 2);
            double b = beta.value * vs1 * (2.0 * vi2 * vs2 + vi3 * vs1) - 2.0 * vi2 * vs1 * vs2;
            double c = beta.value * vs1 * vs3 - Math.Pow(vs2, 2);

            Quadratic quadratic = new Quadratic();
            quadratic.Solve(0.0, a, b, c, 0, 0, 0);

            List<ValueStdev> MLeakage = new();

            // Check negative root solution.
            if (quadratic.sol_negative > 0.0)
            {
                var ML = MLeakageFromK(new ValueStdev(quadratic.sol_negative, quadratic.sol_negative_stdev), vi1);
                if (ML.value >= 1.0) MLeakage.Add(ML);
            }

            // Check positive root solution.
            if (quadratic.sol_positive > 0.0)
            {
                var ML = MLeakageFromK(new ValueStdev(quadratic.sol_positive, quadratic.sol_positive_stdev), vi1);
                if (ML.value >= 1.0) MLeakage.Add(ML);
            }

            return MLeakage;
        }

        /// <summary>
        /// Convert K to leakage multiplication ML.
        /// </summary>
        public static ValueStdev MLeakageFromK(this ValueStdev K, double vi1)
        {
            double vi1minus1 = vi1 - 1.0;
            double ML = K.value * vi1minus1 + 1.0;
            double ML_stdev = K.stdev * vi1minus1;
            return new ValueStdev(ML, ML_stdev);
        }

        /// <summary>
        /// Convert total multiplication MT to effective multiplication Keff.
        /// </summary>
        public static ValueStdev effFromMTotal(this ValueStdev MT)
        {
            if (MT.value < 1.0) return new ValueStdev();
            double Keff = 1.0 - 1.0 / MT.value;
            double Keff_stdev = MT.stdev / MT.value.Squared();
            return new ValueStdev(Keff, Keff_stdev);
        }

        /// <summary>
        /// Convert K to total multiplication MT.
        /// </summary>
        public static ValueStdev MTotalFromK(this ValueStdev K, double vi1)
        {
            ValueStdev ML = MLeakageFromK(K, vi1);
            return MTotalFromMLeakage(ML, vi1);
        }

        /// <summary>
        /// Convert leakage multiplication ML to K.
        /// </summary>
        public static ValueStdev KFromMLeakage(this ValueStdev ML, double vi1)
        {
            double vi1minus1 = vi1 - 1.0;
            if (vi1minus1 <= 0.0) return new ValueStdev();
            double K = (ML.value - 1.0) / vi1minus1;
            double K_stdev = Math.Abs(ML.stdev / vi1minus1);
            return new ValueStdev(K, K_stdev);
        }

        /// <summary>
        /// Convert total multiplication MT to leakage multiplication ML.
        /// </summary>
        public static ValueStdev MLeakageFromMTotal(this ValueStdev MT, double vi1)
        {
            if (vi1 <= 0.0) return new ValueStdev();
            double ML = (MT.value * (vi1 - 1.0) + 1.0) / vi1;
            double ML_stdev = Math.Abs(MT.stdev * (vi1 - 1.0) / vi1);
            return new ValueStdev(ML, ML_stdev);
        }

        /// <summary>
        /// Convert leakage multiplication ML to total multiplication MT.
        /// </summary>
        public static ValueStdev MTotalFromMLeakage(this ValueStdev ML, double vi1)
        {
            double vi1minus1 = vi1 - 1.0;
            if (vi1minus1 <= 0.0) return new ValueStdev();
            double MT = (ML.value * vi1 - 1.0) / vi1minus1;
            double MT_stdev = Math.Abs(ML.stdev * vi1 / vi1minus1);
            return new ValueStdev(MT, MT_stdev);
        }

        /// <summary>
        /// Convert effective multiplication Keff to total multiplication MT.
        /// </summary>
        public static ValueStdev MTotalFromKeff(this ValueStdev Keff)
        {
            double denom = 1.0 - Keff.value;
            if (denom.InRangeEE(0.0, 1.0))
            {
                double MT = 1.0 / denom;
                double MT_stdev = Keff.stdev / denom.Squared();
                return new ValueStdev(MT, MT_stdev);
            }
            return new ValueStdev();
        }

        /// <summary>
        /// Convert effective multiplication Keff to leakage multiplication ML.
        /// </summary>
        public static ValueStdev MLeakageFromKeff(this ValueStdev Keff, double vi1)
        {
            double denom = 1.0 - Keff.value;
            if (denom.InRangeEE(0.0, 1.0))
            {
                ValueStdev MT = MTotalFromKeff(Keff);
                return MLeakageFromMTotal(MT, vi1);
            }
            return new ValueStdev();
        }

        /// <summary>
        /// Map (alpha+1) to alpha.
        /// </summary>
        public static ValueStdev Alpha(this ValueStdev alpha_plus_one)
        {
            return new ValueStdev(alpha_plus_one.value - 1.0, alpha_plus_one.stdev);
        }

        /// <summary>
        /// Map alpha to (alpha+1).
        /// </summary>
        public static ValueStdev AlphaPlusOne(this ValueStdev alpha)
        {
            return new ValueStdev(alpha.value + 1.0, alpha.stdev);
        }
    }
}
