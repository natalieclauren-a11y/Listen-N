using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;

namespace Vf61Gui
{
    public class Efficiency
    {
        public int index = 0;   // The index of the desired efficiency calculation.

        // For these values see 
        // LA-UR-20-29036 Total Efficiency Characterization of the Next Generation Neutron Multiplicity Detector (MC-15)

        public EfficiencyConstants[] effConst = new EfficiencyConstants[] {
            new EfficiencyConstants() {
                name = "MC-15  5-tubes Yes Cd",
                detectorFormat = DetectorFormat.MC15,
                cd_present = true,
                //x = new double[] { 1.0, 1.318280E+02, 5.078558E+04, -4.993360E+05},
                //y = new double[] { 1.574392E-04, 5.832754E+00, -7.449479E+00},
                A = 0.04991535,
                B = 0.21916007,
                lambda1 = 0.11242491,
                lambda2 = 0.02408903,
                lambda3 = 0.00154409,
                numTubes = 5,
            },
            new EfficiencyConstants() {
                name =  "MC-15  9-tubes Yes Cd",
                detectorFormat = DetectorFormat.MC15,
                cd_present = true,
                A = 0.06827142,
                B = 0.22819464,
                lambda1 = 0.09226501,
                lambda2 = 0.02096024,
                lambda3 = 0.00161907,
                numTubes = 9,
            },
            new EfficiencyConstants() {
                name =  "MC-15 15-tubes Yes Cd",
                detectorFormat = DetectorFormat.MC15,
                cd_present = true,
                A = 0.09057244,
                B = 0.22630983,
                lambda1 = 0.08009251,
                lambda2 = 0.01868853,
                lambda3 = 0.00163366,
                numTubes = 15,
            },
            new EfficiencyConstants() {
                name =  "MC-15  5-tubes No Cd",
                detectorFormat = DetectorFormat.MC15,
                cd_present = false,
                A = 0.04102078,
                B = 0.20658877,
                lambda1 = 0.10046665,
                lambda2 = 0.02290527,
                lambda3 = 0.00010518,
                numTubes = 5,
            },
            new EfficiencyConstants() {
                name = "MC-15  9-tubes No Cd",
                detectorFormat = DetectorFormat.MC15,
                cd_present = false,
                A = 0.06337277,
                B = 0.20040320,
                lambda1 = 0.08996412,
                lambda2 = 0.02060502,
                lambda3 = 0.00010473,
                numTubes = 9,
            },
            new EfficiencyConstants() {
                name = "MC-15 15-tubes No Cd",
                detectorFormat = DetectorFormat.MC15,
                cd_present = false,
                A = 0.09234597,
                B = 0.23150295,
                lambda1 = 0.07871365,
                lambda2 = 0.01866987,
                lambda3 = 0.00204529,
                numTubes = 15,
            },
        };

        public Efficiency(int index) : this()
        {
            this.index = index;
        }

        public Efficiency() {
            foreach (EfficiencyConstants eff in effConst)
            {
                eff.SetMc15Positions();
                eff.SetMc15Dose();
            }
        }


        public double efficiency(double sd, double rd, int index)
        {
            this.index = index;
            return effConst[index].efficiency(sd, rd);
        }

        public double efficiency(double sd, double rd)
        {
            return effConst[index].efficiency(sd, rd);
        }

        public double[] dose(uint[] channelCounts, int index)
        {
            this.index = index;
            return effConst[index].Dose(channelCounts);
        }

        public double[] dose(uint[] channelCounts)
        {
            return effConst[index].Dose(channelCounts);
        }

        public double[] SourceToDector(double eff, double rd, int index)
        {
            this.index = index;
            return effConst[index].SourceToDector(eff, rd);
        }

        public double[] SourceToDector(double eff, double rd)
        {
            return effConst[index].SourceToDector(eff, rd);
        }

    }
}
