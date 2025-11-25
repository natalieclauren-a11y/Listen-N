using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Listen_N;

namespace Vf61Gui
{
    public class BitmapHageAlphaCalcs
    {
        public Bitmap? bitmapMultiplicationCalcs;

        public double rd = 30.0;
        public double sd = 27.3;
        public double eff = 0.01;

        public int leftPadding = 15;
        public int rightPadding = 10;
        public int bottomPadding = 0;
        public int topPadding = 0;

        Rectangle rectPlot;
        Rectangle rect;

        public BitmapHageAlphaCalcs(PictureBox pictureBox) : this(pictureBox.Width, pictureBox.Height) { }

        public BitmapHageAlphaCalcs(int width, int height)
        {
            rect = new Rectangle(0, 0, width, height);
            rectPlot = new Rectangle(leftPadding, topPadding, width - leftPadding - rightPadding, height - topPadding - bottomPadding);
            //DrawBetaBackground();
        }
        /// <summary>
        /// Returns a table to be used in DrawTable. Use this format for general isotopes..
        /// </summary>
        /// <param name="hageSolutionAlpha"></param>
        /// <returns></returns>
        public static string[][] GetStringArrayFormat00(HageSolutionAlpha hageSolutionAlpha)
        {
            string[] isotopeStrVi = new string[5].Fill("");
            string[] isotopeStrVs1 = new string[5].Fill("");
            string[] isotopeStrVs2 = new string[5].Fill("");

            // Use GetVi(), GetVs1(), etc. to access Cdf and then .Name
            isotopeStrVi[2] = hageSolutionAlpha.vi.GetVi().Name;
            isotopeStrVs1[2] = hageSolutionAlpha.vs1.GetVs1().Name;
            isotopeStrVs2[2] = hageSolutionAlpha.vs2.GetVs2().Name;

            // Use the new mass calculation method based on Cdf data
            ValueStdev mass = new ValueStdev(hageSolutionAlpha.vs1.GetVs1().MassFromFissionRate(hageSolutionAlpha.Fs.value));

            string[] multStr = (new string[] { "MT", " = " }).Concat(hageSolutionAlpha.MT.EngArray()).ToArray();
            string[] effStr = (new string[] { "eff", " = " }).Concat(hageSolutionAlpha.eff.Percent(3)).ToArray();
            string[] massStr = (new string[] { "mass", " = " }).Concat(mass.EngArray()).Concat(new string[] { " g" }).ToArray();
            string[] alphaStr = (new string[] { "alpha", " = " }).Concat(hageSolutionAlpha.alpha.EngArray()).ToArray();

            return new string[][] {
        isotopeStrVi,
        isotopeStrVs1,
        isotopeStrVs2,
        multStr,
        effStr,
        massStr,
        alphaStr
    };
        }


        /// <summary>
        /// Returns a table to be used in DrawTable. Use this format for U and Pu.
        /// </summary>
        /// <param name="hageSolutionAlpha"></param>
        /// <returns></returns>
        public static string[][] GetStringArrayFormat01(HageSolutionAlpha hageSolutionAlpha)
        {
            string[] isotopeStr = new string[5].Fill("");
            string[] massStrHeader = new string[5].Fill("");

            string vs1Name = hageSolutionAlpha.vs1.GetVs1().Name;

            // Match based on Cdf.Name instead of enum
            if (vs1Name == "U-238")
            {
                isotopeStr[2] = "Uranium";
                massStrHeader[0] = "U-238";
            }
            else if (vs1Name == "Pu-240")
            {
                isotopeStr[2] = "Plutonium";
                massStrHeader[0] = "Pu-240";
            }

            // Compute mass using CDF-based model
            double massVal = hageSolutionAlpha.vs1.GetVs1().MassFromFissionRate(hageSolutionAlpha.Fs.value);
            ValueStdev mass = new ValueStdev(massVal);  // Wrap in ValueStdev for formatting

            string[] multStr = (new string[] { "MT", " = " }).Concat(hageSolutionAlpha.MT.EngArray()).ToArray();
            string[] massStr = (new string[] { "mass", " = " }).Concat(mass.EngArray()).Concat(new string[] { " g" }).ToArray();
            string[] effStr = (new string[] { "eff", " = " }).Concat(hageSolutionAlpha.eff.Percent(3)).ToArray();

            List<string[]> result = new List<string[]>
    {
        isotopeStr,
        multStr,
        massStrHeader,
        massStr,
        effStr
    };

            // Add row ratio if it exists
            if (hageSolutionAlpha.rowRatios != null)
            {
                string[] rowRatioStr = hageSolutionAlpha.rowRatios.Length == 0
                    ? new string[] { "row1/2", " = " }
                    : new string[] { "row1/2", " = ", hageSolutionAlpha.rowRatios[0].value.ToString("0.00") };
                result.Add(rowRatioStr);
            }

            // Add thickness if it exists
            if (hageSolutionAlpha.thickness != null && hageSolutionAlpha.thickness.Length > 0)
            {
                string[] thickStr = hageSolutionAlpha.thickness[0].Length == 0
                    ? new string[] { "thick", " = " }
                    : new string[] { "thick", " = ", hageSolutionAlpha.thickness[0][0].value.ToString("0.00") + " cm" };
                result.Add(thickStr);
            }

            return result.ToArray();
        }


        /// <summary>
        /// Returns a table to be used in DrawTable. Use this format for Cf-252.
        /// </summary>
        /// <param name="hageSolutionAlpha"></param>
        /// <returns></returns>
        public static string[][] GetStringArrayFormat02(HageSolutionAlpha hageSolutionAlpha)
        {
            string[] isotopeStr = new string[5].Fill("");
            //string[] massStrHeader = new string[5].Fill("");

            isotopeStr[2] = "Californium";

            string[] nssStr = (new string[] { "nss", " = " }).Concat(hageSolutionAlpha.NSS.EngArray()).Concat(new string[] {" nps"}).ToArray();
            string[] effStr = (new string[] { "eff", " = " }).Concat(hageSolutionAlpha.eff.Percent(3)).ToArray();

            List<string[]> result = new List<string[]>()
            {
                isotopeStr,
                nssStr,
                effStr
            };
            if (hageSolutionAlpha.thickness != null)
            {
                if(hageSolutionAlpha.thickness.Length > 0) 
                {
                    string[] thickStr = hageSolutionAlpha.thickness[0].Length.Equals(0) ?
                        (new string[] { "thick", " = " }) :
                        (new string[] { "thick", " = ",  hageSolutionAlpha.thickness[0][0].value.ToString("0.00") + " cm" });
                    //result.Add(thickStr);
                    result.Add(new string[] { "thick", " = " });
                }
            }

            return result.ToArray();
        }

        public static Bitmap DrawTable(RingBuffer.RingBuffer<HageSolutionAlpha> hageSolutionAlpha, Size size, Color colorText, Font font)
        {
            if (hageSolutionAlpha.Size.Equals(0))
            {
                return new BitmapBorderOnly(size).bitmap;
            }

            HageSolutionAlpha? solution = hageSolutionAlpha.Front();
            if (solution == null)
            {
                return new BitmapBorderOnly(size).bitmap;
            }

            string[][] itemsArray;

            string vs1Name = solution.vs1.GetVs1().Name;

            // Dispatch formatting logic based on isotope name
            if (vs1Name == "U-238" || vs1Name == "Pu-240")
            {
                itemsArray = GetStringArrayFormat01(solution);
            }
            else if (vs1Name == "Cf-252")
            {
                itemsArray = GetStringArrayFormat02(solution);
            }
            else
            {
                itemsArray = GetStringArrayFormat00(solution);
            }

            StringFormat[] formats = new StringFormat[] {
        new() { Alignment = StringAlignment.Near },
        new() { Alignment = StringAlignment.Center },
        new() { Alignment = StringAlignment.Far },
        new() { Alignment = StringAlignment.Center },
        new() { Alignment = StringAlignment.Far },
        new() { Alignment = StringAlignment.Center }
    };

            Color[] colorTextArray = new Color[6].Fill(colorText);
            Color[] colorBackArray = new Color[6].Fill(Color.White);
            Font[] fonts = new Font[6].Fill(font);

            return itemsArray.DrawTable(colorTextArray, colorBackArray, formats, fonts, size);
        }



    }
}
