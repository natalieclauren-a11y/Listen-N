using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;

namespace Vf61Gui
{
    public class FeynmanHistogram : Histogram
    {
        public uint gatewidth;

        public FeynmanHistogram() : base() {}
        public FeynmanHistogram(string[] replies) : base(replies) {}

        public static double Y1(double gate_width, double m1)
        {
            return gate_width <= 0.0 ? 0.0 : m1 * 1.0E9 / (double)gate_width;
        }
        public static double Y2(double gate_width, double m1, double m2)
        {
            return gate_width <= 0.0 ? 0.0 : (m2 - 0.5 * m1 * m1) * 1.0E9 / (double)gate_width;
        }
        public static double Y3(double gate_width, double m1, double m2, double m3)
        {
            return gate_width <= 0.0 ? 0.0 : (m3 - m2 * m1 + m1 * m1 * m1 / 3.0) * 1.0E9 / (double)gate_width;
        }
        public static double Y4(double gate_width, double m1, double m2, double m3, double m4)
        {
            return gate_width <= 0.0 ? 0.0 : (m4 - m3 * m1 + m2 * m1 * m1 - 0.5 * m2 * m2 - 0.25 * m1 * m1 * m1 * m1) * 1.0E9 / (double)gate_width;
        }
        public static double Ym(double m1, double m2)
        {
            return m1 <= 0.0 ? 0.0 : 2.0 * m2 / m1 - m1;
        }
        public static double Y2f(double m1, double m2)
        {
            return m1 <= 0.0 ? 0.0 : m2 / m1 - 0.5 * m1;
        }
        public static double Y3f(double m1, double m2, double m3)
        {
            return m1 <= 0.0 ? 0.0 : m3 / m1 - m2 + m1 * m1 / 3.0;
        }
        public static double Y4f(double m1, double m2, double m3, double m4)
        {
            return m1 <= 0.0 ? 0.0 : m4 / m1 - m3 + m2 * m1 - 0.5 * Math.Pow(m2, 2.0) / m1 - 0.25 * Math.Pow(m1, 3.0);
        }
        public static double Ysm2(double gate_width, double m1, double m2)
        {	//Y2 / Y1^2
            double y1 = Y1(gate_width, m1);
            double y2 = Y2(gate_width, m1, m2);
            if (y1 == 0.0)
            {
                return 0.0;
            }
            return y2 / Math.Pow(y1, 2.0);
        }

        public static double Ysm3(double gate_width, double m1, double m2, double m3)
        { //Y3 / Y1^3
            double y1 = Y1(gate_width, m1);
            double y3 = Y3(gate_width, m1, m2, m3);
            if (y1 == 0.0)
            {
                return 0.0;
            }
            return y3 / Math.Pow(y1, 3.0);

        }

        public static double Ysm4(double gate_width, double m1, double m2, double m3, double m4)
        {	//Y4 / Y1^4
            double y1 = Y1(gate_width, m1);
            double y4 = Y4(gate_width, m1, m2, m3, m4);
            if (y1 == 0.0)
            {
                return 0.0;
            }
            return y4 / Math.Pow(y1, 4.0);
        }

        public static double Ybeta(double gate_width, double m1, double m2, double m3)
        {	// R2^2 / R3 * R1;
            if ((m1 <= 0.0)
                || (m2 <= 0.0)
                || (m3 <= 0.0))
            {
                return 0.0;
            }

            double numer = 3.0 * Math.Pow(m1 * m1 - 2.0 * m2, 2.0);
            double denom = 4.0 * (Math.Pow(m1, 3.0) - 3.0 * m2 * m1 + 3.0 * m3) * m1;
            if (denom == 0.0)
            {
                return 0.0;
            }
            return numer / denom;
        }
        public static double DivideSigma_By_Cycles_Gatewidth(uint gate_width, ulong cycles, double sigma)
        {
            if ((cycles < 2) || (gate_width < 1))
            {
                return -1.0;
            }
            if (sigma < 0.0)
            {
                return 0.0;
            }
            return Math.Sqrt(sigma) / (double)(cycles - 1) / (double)gate_width * 1.0E9;
        }

        public static double DivideSigma_By_Cycles(ulong cycles, double sigma)
        {
            if (cycles < 2)
            {
                return -1.0;
            }
            if (sigma < 0.0)
            {
                return 0.0;
            }
            return Math.Sqrt(sigma) / (double)(cycles - 1);
        }

        public static double DivideSigma_By_SqrtCycles_Gatewidth(uint gate_width, ulong cycles, double sigma)
        {
            if ((cycles < 2) || (gate_width < 1))
            {
                return -1.0;
            }
            if (sigma < 0.0)
            {
                return 0.0;
            }
            return Math.Sqrt(sigma) / Math.Sqrt((double)(cycles - 1)) / (double)gate_width * 1.0E9;
        }

        public static double DivideSigma_By_SqrtCycles(ulong cycles, double sigma)
        {
            if (cycles < 2)
            {
                return -1.0;
            }
            if (sigma < 0.0)
            {
                return 0.0;
            }
            return Math.Sqrt(sigma) / Math.Sqrt((double)(cycles - 1));
        }
        public static double Y1_stdev(uint gate_width, ulong cycles, double m1, double m2)
        {
            double stdev = sigma_Y1_Y1(m1, m2);
            return DivideSigma_By_SqrtCycles_Gatewidth(gate_width, cycles, stdev);
        }

        public static double Y2_stdev(uint gate_width, ulong cycles, double m1, double m2, double m3, double m4)
        {
            double stdev = sigma_Y2_Y2(m1, m2, m3, m4);
            return DivideSigma_By_SqrtCycles_Gatewidth(gate_width, cycles, stdev);
        }

        double Y3_stdev(uint gate_width, ulong cycles, double m1, double m2, double m3, double m4, double m5, double m6)
        {
            double stdev = sigma_Y3_Y3(m1, m2, m3, m4, m5, m6);
            return DivideSigma_By_SqrtCycles_Gatewidth(gate_width, cycles, stdev);
        }

        public static double Y4_stdev(uint gate_width, ulong cycles, double m1, double m2, double m3, double m4, double m5, double m6, double m7, double m8)
        {
            double stdev = sigma_Y4_Y4(m1, m2, m3, m4, m5, m6, m7, m8);
            return DivideSigma_By_SqrtCycles_Gatewidth(gate_width, cycles, stdev);
        }

        public static double Ym_stdev(ulong cycles, double m1, double m2, double m3, double m4)
        {
            double sigma = 4.0 * sigma_Y2f_Y2f(m1, m2, m3, m4);
            return DivideSigma_By_SqrtCycles(cycles, sigma);
        }

        public static double Y2f_stdev(ulong cycles, double m1, double m2, double m3, double m4)
        {
            double sigma = sigma_Y2f_Y2f(m1, m2, m3, m4);
            return DivideSigma_By_SqrtCycles(cycles, sigma);
        }

        public static double Y3f_stdev(ulong cycles, double m1, double m2, double m3, double m4, double m5, double m6)
        {
            double sigma = sigma_Y3f_Y3f(m1, m2, m3, m4, m5, m6);
            return DivideSigma_By_SqrtCycles(cycles, sigma);

        }
        public static double Y4f_stdev(ulong cycles, double m1, double m2, double m3, double m4, double m5, double m6, double m7, double m8)
        {
            double sigma = sigma_Y4f_Y4f(m1, m2, m3, m4, m5, m6, m7, m8);
            return DivideSigma_By_SqrtCycles(cycles, sigma);
        }
        public static double YSm2_stdev(uint gate_width, ulong cycles, double m1, double m2, double m3, double m4)
        {	//R2 / R1^2  -- uncertainty
            double sigma = sigma_SM2_SM2(m1, m2, m3, m4);
            double time_normalizer = (double)gate_width * 1.0E-9;
            return DivideSigma_By_SqrtCycles(cycles, sigma) * time_normalizer;
        }

        public static double YSm3_stdev(uint gate_width, ulong cycles, double m1, double m2, double m3, double m4, double m5, double m6)
        {	//R3 / R1^3  -- uncertainty
            double sigma = sigma_SM3_SM3(m1, m2, m3, m4, m5, m6);
            //double time_normalizer = std::pow((double)gate_width * 1.0E-9, 2.0);
            double time_normalizer = (double)gate_width * 1.0E-9;
            // Square the value time_normalizer
            time_normalizer = time_normalizer * time_normalizer;
            return DivideSigma_By_SqrtCycles(cycles, sigma) * time_normalizer;
        }

        public static double YSm4_stdev(uint gate_width, ulong cycles, double m1, double m2, double m3, double m4, double m5, double m6, double m7, double m8)
        {	//R4 / R1^3  -- uncertainty
            double sigma = sigma_SM4_SM4(m1, m2, m3, m4, m5, m6, m7, m8);
            //double time_normalizer = std::pow((double)gate_width * 1.0E-9, 3.0);
            double time_normalizer = (double)gate_width * 1.0E-9;
            // Cube the value time_normalizer
            time_normalizer = time_normalizer * time_normalizer * time_normalizer;
            return DivideSigma_By_SqrtCycles(cycles, sigma) * time_normalizer;
        }

        public static double YBeta_stdev(uint gate_width, ulong cycles, double m1, double m2, double m3, double m4, double m5, double m6)
        {
            //Y2^2 / Y3 / Y1  -- uncertainty
            // Note: The gate_widths cancel out. You do not need to do a time_normalizer like in the previous functions.

            double sigma = sigma_Beta_Beta(m1, m2, m3, m4, m5, m6);
            return DivideSigma_By_SqrtCycles(cycles, sigma);
        }
        public static double sigma_M1_M1(double m1, double m2)
        {
            //Not normalized by the number of cycles
            double num = 2.0 * m2 + m1 - m1 * m1;
            return num;
        }
        public static double sigma_M2_M2(double m1, double m2, double m3, double m4)
        {
            //Not normalized by the number of cycles
            double num = 6.0 * m4 + 6.0 * m3 + m2 - m2 * m2;
            return num;
        }
        public static double sigma_M3_M3(double m1, double m2, double m3, double m4, double m5, double m6)
        {
            //Not normalized by the number of cycles
            double num = 20.0 * m6 + 30.0 * m5 + 12.0 * m4 + m3 - m3 * m3;
            return num;
        }
        public static double sigma_M4_M4(double m1, double m2, double m3, double m4, double m5, double m6, double m7, double m8)
        {
            //Not normalized by the number of cycles
            double num = 70.0 * m8 + 140.0 * m7 + 90.0 * m6 + 20.0 * m5 + m4 - m4 * m4;
            return num;
        }

        public static double sigma_M1_M2(double m1, double m2, double m3)
        {
            //Not normalized by the number of cycles
            double num = 3.0 * m3 + 2.0 * m2 - m1 * m2;
            return num;
        }
        public static double sigma_M1_M3(double m1, double m2, double m3, double m4)
        {
            //Not normalized by the number of cycles
            double num = 4.0 * m4 + 3.0 * m3 - m1 * m3;
            return num;
        }
        public static double sigma_M1_M4(double m1, double m2, double m3, double m4, double m5)
        {
            //Not normalized by the number of cycles
            double num = 5.0 * m5 + 4.0 * m4 - m1 * m4;
            return num;
        }

        public static double sigma_M2_M3(double m1, double m2, double m3, double m4, double m5)
        {
            //Not normalized by the number of cycles
            double num = 10.0 * m5 + 12.0 * m4 + 3.0 * m3 - m2 * m3;
            return num;
        }
        public static double sigma_M2_M4(double m1, double m2, double m3, double m4, double m5, double m6)
        {
            //Not normalized by the number of cycles
            double num = 15.0 * m6 + 20.0 * m5 + 6.0 * m4 - m2 * m4;
            return num;
        }

        public static double sigma_M3_M4(double m1, double m2, double m3, double m4, double m5, double m6, double m7)
        {
            //Not normalized by the number of cycles
            double num = 35.0 * m7 + 60.0 * m6 + 30.0 * m5 + 4.0 * m4 - m3 * m4;
            return num;
        }
        public static double sigma_Y1_Y1(double m1, double m2)	//Not normalized by the number of cycles
        {
            double value = -m1 * m1 + m1 + 2.0 * m2;
            return value;
        }

        public static double sigma_Y2_Y2(double m1, double m2, double m3, double m4)
        {	//Not normalized by the number of cycles
            double _diff_Y2_m1 = diff_Y2_m1(m1, m2);
            double _diff_Y2_m2 = diff_Y2_m2(m1, m2);

            double value = Math.Pow(_diff_Y2_m1, 2.0) * sigma_M1_M1(m1, m2)
                + Math.Pow(_diff_Y2_m2, 2.0) * sigma_M2_M2(m1, m2, m3, m4);
            double covariance = 2.0 * _diff_Y2_m1 * _diff_Y2_m2 * sigma_M1_M2(m1, m2, m3);

            return value + covariance;
        }
        public static double sigma_Y3_Y3(double m1, double m2, double m3, double m4, double m5, double m6)
        {	//Not normalized by the number of cycles
            double _diff_Y3_m1 = diff_Y3_m1(m1, m2, m3);
            double _diff_Y3_m2 = diff_Y3_m2(m1, m2, m3);
            double _diff_Y3_m3 = diff_Y3_m3(m1, m2, m3);

            double value = Math.Pow(_diff_Y3_m1, 2.0) * sigma_M1_M1(m1, m2)
                + Math.Pow(_diff_Y3_m2, 2.0) * sigma_M2_M2(m1, m2, m3, m4)
                + Math.Pow(_diff_Y3_m3, 2.0) * sigma_M3_M3(m1, m2, m3, m4, m5, m6);
            double covariance = 2.0 * _diff_Y3_m1 * _diff_Y3_m2 * sigma_M1_M2(m1, m2, m3)
                + 2.0 * _diff_Y3_m1 * _diff_Y3_m3 * sigma_M1_M3(m1, m2, m3, m4)
                + 2.0 * _diff_Y3_m2 * _diff_Y3_m3 * sigma_M2_M3(m1, m2, m3, m4, m5);

            return value + covariance;
        }

        public static double sigma_Y4_Y4(double m1, double m2, double m3, double m4, double m5, double m6, double m7, double m8)
        {	//Not normalized by the number of cycles
            double _diff_Y4_m1 = diff_Y4_m1(m1, m2, m3, m4);
            double _diff_Y4_m2 = diff_Y4_m2(m1, m2, m3, m4);
            double _diff_Y4_m3 = diff_Y4_m3(m1, m2, m3, m4);
            double _diff_Y4_m4 = diff_Y4_m4(m1, m2, m3, m4);

            double value = Math.Pow(_diff_Y4_m1, 2.0) * sigma_M1_M1(m1, m2)
                + Math.Pow(_diff_Y4_m2, 2.0) * sigma_M2_M2(m1, m2, m3, m4)
                + Math.Pow(_diff_Y4_m3, 2.0) * sigma_M3_M3(m1, m2, m3, m4, m5, m6)
                + Math.Pow(_diff_Y4_m4, 2.0) * sigma_M4_M4(m1, m2, m3, m4, m5, m6, m7, m8);
            double covariance = 2.0 * _diff_Y4_m1 * _diff_Y4_m2 * sigma_M1_M2(m1, m2, m3)
                + 2.0 * _diff_Y4_m1 * _diff_Y4_m3 * sigma_M1_M3(m1, m2, m3, m4)
                + 2.0 * _diff_Y4_m1 * _diff_Y4_m4 * sigma_M1_M4(m1, m2, m3, m4, m5)
                + 2.0 * _diff_Y4_m2 * _diff_Y4_m3 * sigma_M2_M3(m1, m2, m3, m4, m5)
                + 2.0 * _diff_Y4_m2 * _diff_Y4_m4 * sigma_M2_M4(m1, m2, m3, m4, m5, m6)
                + 2.0 * _diff_Y4_m3 * _diff_Y4_m4 * sigma_M3_M4(m1, m2, m3, m4, m5, m6, m7);

            return value + covariance;
        }

        public static double sigma_Y2f_Y2f(double m1, double m2, double m3, double m4)
        {	//Not normalized by the number of cycles
            if (m1 == 0.0)
            {
                return 0.0;
            }

            double _diff_Y2f_m1 = diff_Y2f_m1(m1, m2);
            double _diff_Y2f_m2 = diff_Y2f_m2(m1, m2);

            double value = Math.Pow(_diff_Y2f_m1, 2.0) * sigma_M1_M1(m1, m2)
                + Math.Pow(_diff_Y2f_m2, 2.0) * sigma_M2_M2(m1, m2, m3, m4);
            double covariance = 2.0 * _diff_Y2f_m1 * _diff_Y2f_m2 * sigma_M1_M2(m1, m2, m3);

            return value + covariance;
        }

        public static double sigma_Y3f_Y3f(double m1, double m2, double m3, double m4, double m5, double m6)
        {
            //Not normalized by the number of cycles
            double _diff_Y3f_m1 = diff_Y3f_m1(m1, m2, m3);
            double _diff_Y3f_m2 = diff_Y3f_m2(m1, m2, m3);
            double _diff_Y3f_m3 = diff_Y3f_m3(m1, m2, m3);

            double value = Math.Pow(_diff_Y3f_m1, 2.0) * sigma_M1_M1(m1, m2)
                + Math.Pow(_diff_Y3f_m2, 2.0) * sigma_M2_M2(m1, m2, m3, m4)
                + Math.Pow(_diff_Y3f_m3, 2.0) * sigma_M3_M3(m1, m2, m3, m4, m5, m6);
            double covariance = 2.0 * _diff_Y3f_m1 * _diff_Y3f_m2 * sigma_M1_M2(m1, m2, m3)
                + 2.0 * _diff_Y3f_m1 * _diff_Y3f_m3 * sigma_M1_M3(m1, m2, m3, m4)
                + 2.0 * _diff_Y3f_m2 * _diff_Y3f_m3 * sigma_M2_M3(m1, m2, m3, m4, m5);

            return value + covariance;
        }

        public static double sigma_Y4f_Y4f(double m1, double m2, double m3, double m4, double m5, double m6, double m7, double m8)
        {	//Not normalized by the number of cycles
            double _diff_Y4f_m1 = diff_Y4f_m1(m1, m2, m3, m4);
            double _diff_Y4f_m2 = diff_Y4f_m2(m1, m2, m3, m4);
            double _diff_Y4f_m3 = diff_Y4f_m3(m1, m2, m3, m4);
            double _diff_Y4f_m4 = diff_Y4f_m4(m1, m2, m3, m4);

            double value = Math.Pow(_diff_Y4f_m1, 2.0) * sigma_M1_M1(m1, m2)
                + Math.Pow(_diff_Y4f_m2, 2.0) * sigma_M2_M2(m1, m2, m3, m4)
                + Math.Pow(_diff_Y4f_m3, 2.0) * sigma_M3_M3(m1, m2, m3, m4, m5, m6)
                + Math.Pow(_diff_Y4f_m4, 2.0) * sigma_M4_M4(m1, m2, m3, m4, m5, m6, m7, m8);
            double covariance = 2.0 * _diff_Y4f_m1 * _diff_Y4f_m2 * sigma_M1_M2(m1, m2, m3)
                + 2.0 * _diff_Y4f_m1 * _diff_Y4f_m3 * sigma_M1_M3(m1, m2, m3, m4)
                + 2.0 * _diff_Y4f_m1 * _diff_Y4f_m4 * sigma_M1_M4(m1, m2, m3, m4, m5)
                + 2.0 * _diff_Y4f_m2 * _diff_Y4f_m3 * sigma_M2_M3(m1, m2, m3, m4, m5)
                + 2.0 * _diff_Y4f_m2 * _diff_Y4f_m4 * sigma_M2_M4(m1, m2, m3, m4, m5, m6)
                + 2.0 * _diff_Y4f_m3 * _diff_Y4f_m4 * sigma_M3_M4(m1, m2, m3, m4, m5, m6, m7);

            return value + covariance;
        }

        public static double sigma_SM2_SM2(double m1, double m2, double m3, double m4)
        {	//Not normalized by the number of cycles
            double _diff_Sm2_m1 = diff_Sm2_m1(m1, m2);
            double _diff_Sm2_m2 = diff_Sm2_m2(m1, m2);

            double value = Math.Pow(_diff_Sm2_m1, 2.0) * sigma_M1_M1(m1, m2)
                + Math.Pow(_diff_Sm2_m2, 2.0) * sigma_M2_M2(m1, m2, m3, m4);
            double covariance = 2.0 * _diff_Sm2_m1 * _diff_Sm2_m2 * sigma_M1_M2(m1, m2, m3);

            return value + covariance;
        }

        public static double sigma_SM3_SM3(double m1, double m2, double m3, double m4, double m5, double m6)
        {	//Not normalized by the number of cycles
            double _diff_Sm3_m1 = diff_Sm3_m1(m1, m2, m3);
            double _diff_Sm3_m2 = diff_Sm3_m2(m1, m2, m3);
            double _diff_Sm3_m3 = diff_Sm3_m3(m1, m2, m3);

            double value = Math.Pow(_diff_Sm3_m1, 2.0) * sigma_M1_M1(m1, m2)
                + Math.Pow(_diff_Sm3_m2, 2.0) * sigma_M2_M2(m1, m2, m3, m4)
                + Math.Pow(_diff_Sm3_m3, 2.0) * sigma_M3_M3(m1, m2, m3, m4, m5, m6);
            double covariance = 2.0 * _diff_Sm3_m1 * _diff_Sm3_m2 * sigma_M1_M2(m1, m2, m3)
                + 2.0 * _diff_Sm3_m1 * _diff_Sm3_m3 * sigma_M1_M3(m1, m2, m3, m4)
                + 2.0 * _diff_Sm3_m2 * _diff_Sm3_m3 * sigma_M2_M3(m1, m2, m3, m4, m5);

            return value + covariance;
        }

        public static double sigma_SM4_SM4(double m1, double m2, double m3, double m4, double m5, double m6, double m7, double m8)
        {
            //Not normalized by the number of cycles
            double _diff_Sm4_m1 = diff_Sm4_m1(m1, m2, m3, m4);
            double _diff_Sm4_m2 = diff_Sm4_m2(m1, m2, m3, m4);
            double _diff_Sm4_m3 = diff_Sm4_m3(m1, m2, m3, m4);
            double _diff_Sm4_m4 = diff_Sm4_m4(m1, m2, m3, m4);

            double value = Math.Pow(_diff_Sm4_m1, 2.0) * sigma_M1_M1(m1, m2)
                + Math.Pow(_diff_Sm4_m2, 2.0) * sigma_M2_M2(m1, m2, m3, m4)
                + Math.Pow(_diff_Sm4_m3, 2.0) * sigma_M3_M3(m1, m2, m3, m4, m5, m6)
                + Math.Pow(_diff_Sm4_m3, 2.0) * sigma_M4_M4(m1, m2, m3, m4, m5, m6, m7, m8);
            double covariance = 2.0 * _diff_Sm4_m1 * _diff_Sm4_m2 * sigma_M1_M2(m1, m2, m3)
                + 2.0 * _diff_Sm4_m1 * _diff_Sm4_m3 * sigma_M1_M3(m1, m2, m3, m4)
                + 2.0 * _diff_Sm4_m1 * _diff_Sm4_m4 * sigma_M1_M4(m1, m2, m3, m4, m5)
                + 2.0 * _diff_Sm4_m2 * _diff_Sm4_m3 * sigma_M2_M3(m1, m2, m3, m4, m5)
                + 2.0 * _diff_Sm4_m2 * _diff_Sm4_m4 * sigma_M2_M4(m1, m2, m3, m4, m5, m6)
                + 2.0 * _diff_Sm4_m3 * _diff_Sm4_m4 * sigma_M3_M4(m1, m2, m3, m4, m5, m6, m7);

            return value + covariance;
        }

        public static double sigma_Beta_Beta(double m1, double m2, double m3, double m4, double m5, double m6)
        {	//Not normalized by the number of cycles
            double _diff_Beta_m1 = diff_Beta_m1(m1, m2, m3);
            double _diff_Beta_m2 = diff_Beta_m2(m1, m2, m3);
            double _diff_Beta_m3 = diff_Beta_m3(m1, m2, m3);

            //double value = Math.Pow(_diff_Beta_m1, 2.0) * sigma_M1_M1(m1, m2)
            //	+ Math.Pow(_diff_Beta_m2, 2.0) * sigma_M2_M2(m1, m2, m3, m4)
            //	+ Math.Pow(_diff_Beta_m3, 2.0) * sigma_M3_M3(m1, m2, m3, m4, m5, m6);
            double value = _diff_Beta_m1 * _diff_Beta_m1 * sigma_M1_M1(m1, m2)
                + _diff_Beta_m2 * _diff_Beta_m2 * sigma_M2_M2(m1, m2, m3, m4)
                + _diff_Beta_m3 * _diff_Beta_m3 * sigma_M3_M3(m1, m2, m3, m4, m5, m6);
            double covariance = 2.0 * _diff_Beta_m1 * _diff_Beta_m2 * sigma_M1_M2(m1, m2, m3)
                + 2.0 * _diff_Beta_m1 * _diff_Beta_m3 * sigma_M1_M3(m1, m2, m3, m4)
                + 2.0 * _diff_Beta_m2 * _diff_Beta_m3 * sigma_M2_M3(m1, m2, m3, m4, m5);
            return value + covariance;
        }
        public static double diff_Sm2_m1(double m1, double m2)
        {
            return -0.1e1 / m1 - 0.2e1 * (m2 - m1 * m1 / 0.2e1) * Math.Pow(m1, -0.3e1);
        }
        public static double diff_Sm2_m2(double m1, double m2)
        {
            return Math.Pow(m1, -0.2e1);
        }
        public static double diff_Sm3_m1(double m1, double m2, double m3)
        {
            return (m1 * m1 - m2) * Math.Pow(m1, -0.3e1) - 0.3e1 * (m3 - m2 * m1 + Math.Pow(m1, 0.3e1) / 0.3e1) * Math.Pow(m1, -0.4e1);
        }
        public static double diff_Sm3_m2(double m1, double m2, double m3)
        {
            return -Math.Pow(m1, -0.2e1);
        }
        public static double diff_Sm3_m3(double m1, double m2, double m3)
        {
            return Math.Pow(m1, -0.3e1);
        }
        public static double diff_Sm4_m1(double m1, double m2, double m3, double m4)
        {
            return (-Math.Pow(m1, 0.3e1) + 0.2e1 * m2 * m1 - m3) * Math.Pow(m1, -0.4e1) - 0.4e1 * (m4 - m3 * m1 + m2 * m1 * m1 - m2 * m2 / 0.2e1 - Math.Pow(m1, 0.4e1) / 0.4e1) * Math.Pow(m1, -0.5e1);
        }
        public static double diff_Sm4_m2(double m1, double m2, double m3, double m4)
        {
            return (m1 * m1 - m2) * Math.Pow(m1, -0.4e1);
        }
        public static double diff_Sm4_m3(double m1, double m2, double m3, double m4)
        {
            return -Math.Pow(m1, -0.3e1);
        }
        public static double diff_Sm4_m4(double m1, double m2, double m3, double m4)
        {
            return Math.Pow(m1, -0.4e1);
        }

        public static double diff_Beta_m1(double m1, double m2, double m3)
        {
            double numerator = 3.0 * (m1 * m1 - 2.0 * m2) * (2.0 * Math.Pow(m1, 3.0) * m2 + 9.0 * m1 * m1 * m3 - 12.0 * m1 * m2 * m2 + 6.0 * m2 * m3);
            double denom = 4.0 * Math.Pow(Math.Pow(m1, 3.0) - 3.0 * m2 * m1 + 3.0 * m3, 2.0) * m1 * m1;
            if (denom == 0.0)
            {
                return Double.PositiveInfinity;
            }
            return numerator / denom;
        }

        public static double diff_Beta_m2(double m1, double m2, double m3)
        {
            double numerator = -3.0 * (m1 * m1 - 2.0 * m2) * (Math.Pow(m1, 3.0) - 6.0 * m2 * m1 + 12.0 * m3);
            double denom = 4.0 * m1 * Math.Pow(Math.Pow(m1, 3.0) - 3.0 * m2 * m1 + 3.0 * m3, 2.0);
            if (denom == 0.0)
            {
                return Double.PositiveInfinity;
            }
            return numerator / denom;
        }

        public static double diff_Beta_m3(double m1, double m2, double m3)
        {
            double numerator = -9.0 * Math.Pow(m1 * m1 - 2.0 * m2, 2.0);
            double denom = 4.0 * m1 * Math.Pow(Math.Pow(m1, 3.0) - 3.0 * m2 * m1 + 3.0 * m3, 2.0);
            if (denom == 0.0)
            {
                return Double.PositiveInfinity;
            }
            return numerator / denom;
        }
        public static double diff_Y2_m1(double m1, double m2)
        {
            return -m1;
        }
        public static double diff_Y2_m2(double m1, double m2)
        {
            return 1.0;
        }
        public static double diff_Y3_m1(double m1, double m2, double m3)
        {
            return m1 * m1 - m2;
        }
        public static double diff_Y3_m2(double m1, double m2, double m3)
        {
            return -m1;
        }
        public static double diff_Y3_m3(double m1, double m2, double m3)
        {
            return 1.0;
        }
        public static double diff_Y4_m1(double m1, double m2, double m3, double m4)
        {
            return -Math.Pow(m1, 0.3e1) + 0.2e1 * m2 * m1 - m3;
        }
        public static double diff_Y4_m2(double m1, double m2, double m3, double m4)
        {
            return m1 * m1 - m2;
        }
        public static double diff_Y4_m3(double m1, double m2, double m3, double m4)
        {
            return -m1;
        }
        public static double diff_Y4_m4(double m1, double m2, double m3, double m4)
        {
            return 1.0;
        }

        public static double diff_Y2f_m1(double m1, double m2)
        {
            return -0.1e1 - (m2 - m1 * m1 / 0.2e1) * Math.Pow(m1, -0.2e1);
        }
        public static double diff_Y2f_m2(double m1, double m2)
        {
            return 0.1e1 / m1;
        }
        public static double diff_Y3f_m1(double m1, double m2, double m3)
        {
            return (m1 * m1 - m2) / m1 - (m3 - m2 * m1 + Math.Pow(m1, 0.3e1) / 0.3e1) * Math.Pow(m1, -0.2e1);
        }
        public static double diff_Y3f_m2(double m1, double m2, double m3)
        {
            return -1.0;
        }
        public static double diff_Y3f_m3(double m1, double m2, double m3)
        {
            return 0.1e1 / m1;
        }
        public static double diff_Y4f_m1(double m1, double m2, double m3, double m4)
        {
            return (-Math.Pow(m1, 0.3e1) + 0.2e1 * m2 * m1 - m3) / m1 - (m4 - m3 * m1 + m2 * m1 * m1 - m2 * m2 / 0.2e1 - Math.Pow(m1, 0.4e1) / 0.4e1) * Math.Pow(m1, -0.2e1);
        }
        public static double diff_Y4f_m2(double m1, double m2, double m3, double m4)
        {
            return (m1 * m1 - m2) / m1;
        }
        public static double diff_Y4f_m3(double m1, double m2, double m3, double m4)
        {
            return -1.0;
        }
        public static double diff_Y4f_m4(double m1, double m2, double m3, double m4)
        {
            return 0.1e1 / m1;
        }
    }
}
