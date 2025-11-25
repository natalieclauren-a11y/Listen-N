// Provides classes for handling cumulative distribution functions (CDFs) related to isotopic data,
// and for converting between fission rate, mass, and neutron production rate with uncertainty propagation.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Listen_N
{
    // Represents a cumulative distribution function with bins and related properties.
    public class Cdf
    {
        public string Name { get; set; } = "";
        public List<double> Bin { get; set; } = new();
        public double Npsg { get; set; } = 0.0; // Neutrons per second per gram (or similar unit)

        // Computes the n-th raw moment (e.g. mean for order=1) of the Bin data.
        public double Moment(int order)
        {
            if (Bin == null || Bin.Count == 0 || order < 1) return 0.0;
            return Bin.Select(b => Math.Pow(b, order)).Average();
        }

        // Converts a fission rate (Fs) to mass using the first moment and Npsg.
        public double MassFromFissionRate(double Fs)
        {
            double nu1 = Moment(1);
            return (Npsg == 0 || nu1 == 0) ? 0.0 : Fs * nu1 / Npsg;
        }

        // Converts mass to fission rate using the first moment and Npsg.
        public double FissionRateFromMass(double mass)
        {
            double nu1 = Moment(1);
            return (nu1 == 0) ? 0.0 : mass * Npsg / nu1;
        }
    }

    // Holds and manages sets of CDF data for different isotope types,
    // providing conversion methods and uncertainty propagation.
    public class Snm
    {
        private List<Cdf> vs1List = new();
        private List<Cdf> vs2List = new();
        private List<Cdf> viList = new();

        private Cdf vs1;
        private Cdf vs2;
        private Cdf vi;

        public Cdf GetVi() => vi;
        public Cdf GetVs1() => vs1;
        public Cdf GetVs2() => vs2;

        public Snm()
        {
            InitializeDefaults();
        }

        // Initializes default isotopes with their CDF bins and neutron production rates.
        private void InitializeDefaults()
        {
            vs1List = new List<Cdf>
            {
                new Cdf { Name = "alpha-n", Bin = new List<double> {0.000, 1.000}, Npsg = 1.0 },
                new Cdf { Name = "Cf-252", Bin = new List<double> {0.002, 0.028, 0.155, 0.428, 0.732, 0.917, 0.983, 0.998, 1.000}, Npsg = 2.34E12 },
                new Cdf { Name = "Pu-240", Bin = new List<double> {0.066, 0.298, 0.627, 0.878, 0.980, 0.998, 1.000}, Npsg = 1020.0 },
                new Cdf { Name = "U-238",  Bin = new List<double> {0.048, 0.280, 0.759, 0.961, 0.991, 0.994, 1.000}, Npsg = 1.36E-2 }
            };

            vs2List = new List<Cdf>(vs1List);

            viList = new List<Cdf>
            {
                new Cdf { Name = "Pu-239", Bin = new List<double> {0.011, 0.112, 0.387, 0.710, 0.909, 0.992, 1.000}, Npsg = 2.18E-2 },
                new Cdf { Name = "U-233",  Bin = new List<double> {0.010, 0.161, 0.487, 0.788, 0.964, 1.000}, Npsg = 8.6E-4 },
                new Cdf { Name = "Np-237 (1.00 MeV)", Bin = new List<double> {0.0222, 0.1391, 0.4370, 0.7786, 0.9548, 0.9956, 0.9998, 0.999996, 1.000}, Npsg = 2.99E-4 },
                new Cdf { Name = "U-235",  Bin = new List<double> {0.033, 0.207, 0.542, 0.846, 0.969, 0.997, 1.000}, Npsg = 2.99E-4 }
            };

            vs1 = vs1List[0];
            vs2 = vs2List[0];
            vi = viList[0];
        }

        // Returns names of available isotopes for vs1, vs2, and vi.
        public List<string> GetVs1List() => vs1List.Select(x => x.Name).ToList();
        public List<string> GetVs2List() => vs2List.Select(x => x.Name).ToList();
        public List<string> GetViList() => viList.Select(x => x.Name).ToList();

        // Selects the isotope for vs1, vs2, or vi by name. Returns true if found.
        public bool SetVs1(string name) => TrySet(name, vs1List, out vs1);
        public bool SetVs2(string name) => TrySet(name, vs2List, out vs2);
        public bool SetVi(string name) => TrySet(name, viList, out vi);

        private static bool TrySet(string name, List<Cdf> list, out Cdf selected)
        {
            selected = list.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) ?? new Cdf();
            return selected.Name != "";
        }

        // Returns the selected Cdf source by type string ("vs1", "vs2", or "vi").
        private Cdf GetSource(string type) =>
            type.ToLower() switch
            {
                "vs1" => vs1,
                "vs2" => vs2,
                "vi" => vi,
                _ => new Cdf()
            };

        // Conversion methods between fission rate, mass, and neutron production rate for selected type.

        public double GetMassFromFs(double fs, string type)
        {
            var src = GetSource(type);
            double m1 = src.Moment(1);
            return (src.Npsg == 0 || m1 == 0) ? double.MaxValue : fs * m1 / src.Npsg;
        }

        public double GetFsFromMass(double mass, string type)
        {
            var src = GetSource(type);
            double m1 = src.Moment(1);
            return (m1 == 0) ? double.MaxValue : mass * src.Npsg / m1;
        }

        public double GetNpsFromMass(double mass, string type) =>
            GetSource(type).Npsg * mass;

        public double GetMassFromNps(double nps, string type)
        {
            var src = GetSource(type);
            return (src.Npsg == 0) ? 0.0 : nps / src.Npsg;
        }

        // Uncertainty propagation methods: standard deviations for fission rate, mass, neutron production rate.

        public double GetFsStdevFromMass(double mass, double sigmaMass, string type)
        {
            var src = GetSource(type);
            double m1 = src.Moment(1);
            return (m1 == 0) ? double.MaxValue : sigmaMass * src.Npsg / m1;
        }

        public double GetMassStdevFromFs(double fs, double sigmaFs, string type)
        {
            var src = GetSource(type);
            double m1 = src.Moment(1);
            return (src.Npsg == 0) ? double.MaxValue : sigmaFs * m1 / src.Npsg;
        }

        public double GetNpsStdevFromMass(double mass, double sigmaMass, string type)
        {
            var src = GetSource(type);
            return sigmaMass * src.Npsg;
        }

        public double GetMassStdevFromNps(double nps, double sigmaNps, string type)
        {
            var src = GetSource(type);
            return (src.Npsg == 0) ? 0.0 : sigmaNps / src.Npsg;
        }

        public double GetFsStdevFromNps(double nps, double sigmaNps, string type)
        {
            var src = GetSource(type);
            double m1 = src.Moment(1);
            return (m1 == 0) ? double.MaxValue : sigmaNps / m1;
        }

        public double GetNpsStdevFromFs(double fs, double sigmaFs, string type)
        {
            var src = GetSource(type);
            return sigmaFs * src.Moment(1);
        }
    }
}