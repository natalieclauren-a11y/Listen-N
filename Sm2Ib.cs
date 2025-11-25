using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Drawing;
using System.Windows.Forms;

namespace Vf61Gui
{
    /// <summary>
    /// Stores that data from the command Sm2Ib
    /// </summary>
    /// 
    public class Sm2Ib
    {
        // From MC-15 / mc15_control / main_control.c 
        //    sprintf(rtncmd, "Sm2Ib = %d, %e,%e,%e,%e,%e,%e,%e,%e,%e,%e,%e,%e,%e",
        //              elapsedTime, R1, dR1, R2, dR2, R3, dR3, Sm2, dSm2, InvBeta, dInvBeta, lambda1, lambda2, fraction);
        //     replies  [1]          [2] [3]  [4] [5]  [6] [7]  [8]  [9]   [10]     [11]      [12]     [13]     [14]
        //              Note, InvBeta is actually Beta.

        public ValueStdev R1 = new ValueStdev();
        public ValueStdev R2 = new ValueStdev();
        public ValueStdev R3 = new ValueStdev();
        public ValueStdev Sm2 = new ValueStdev();
        public ValueStdev InvBeta = new ValueStdev();
        public ValueStdev Beta = new ValueStdev();
        public double lambda1;
        public double lambda2;
        public double fraction;
        public uint elapsedTime;
        public DateTime dateTime;   // The clock tick of the instantiation.
        //public Snm snmPu240 = new Snm("Pu240");
        //public Snm snmPu239 = new Snm("Pu239");
        //public Snm snmAlphaN = new Snm("AlphaN");
        //public Snm snmU235 = new Snm("U235");
        //public Snm snmU238 = new Snm("U238");
        //public Snm snmCf252 = new Snm("Cf252");

        /// <summary>
        /// Returns the lifetime in nanoseconds
        /// </summary>
        public double lifetime1
        {
            get { return (lambda1 == 0.0) ? 0.0 : 1.0 / lambda1; }
        }

        /// <summary>
        /// Returns the lifetime in nanoseconds
        /// </summary>
        public double lifetime2
        {
            get { return (lambda2 == 0.0) ? 0.0 : 1.0 / lambda2; }
        }

        /// <summary>
        /// Returns the lifetime in microseconds
        /// </summary>
        public double lifetime1_us
        {
            get { return (lambda1 == 0.0) ? 0.0 : 1.0E-3 / lambda1; }
        }

        /// <summary>
        /// Returns the lifetime in microseconds
        /// </summary>
        public double lifetime2_us
        {
            get { return (lambda2 == 0.0) ? 0.0 : 1.0E-3 / lambda2; }
        }

        public Sm2Ib()
        {
            dateTime = DateTime.Now;
            // I only want 1 sec precision.
            this.dateTime.AddMilliseconds(this.dateTime.Millisecond - 1000);
        }
        public Sm2Ib(string[] replies)
        {
            dateTime = DateTime.Now;
            // I only want 1 sec precision.
            this.dateTime.AddMilliseconds(this.dateTime.Millisecond - 1000);

            if (replies.Length > 1)
                this.elapsedTime = replies[1].ToUInt32(0);
            else
                return;

            if (replies.Length > 2)
                this.R1.value = replies[2].ToDouble(0.0);
            else
                return;

            if (replies.Length > 3)
                this.R1.stdev = replies[3].ToDouble(0.0);
            else
                return;

            if (replies.Length > 4)
                this.R2.value = replies[4].ToDouble(0.0);
            else
                return;

            if (replies.Length > 5)
                this.R2.stdev = replies[5].ToDouble(0.0);
            else
                return;

            if (replies.Length > 6)
                this.R3.value = replies[6].ToDouble(0.0);
            else
                return;

            if (replies.Length > 7)
                this.R3.stdev = replies[7].ToDouble(0.0);
            else
                return;

            if (replies.Length > 8)
                this.Sm2.value = replies[8].ToDouble(0.0);
            else
                return;

            if (replies.Length > 9)
                this.Sm2.stdev = replies[9].ToDouble(0.0);
            else
                return;

            if (replies.Length > 10)
            {
                this.InvBeta.value = replies[10].ToDouble(0.0);
                this.Beta.value = (this.InvBeta.value == 0.0) ? 0.0 : 1.0 / this.InvBeta.value ;
            }
            else
                return;

            if (replies.Length > 11)
            {
                this.InvBeta.stdev = replies[11].ToDouble(0.0);
                this.Beta.stdev = (this.InvBeta.value == 0.0) ? 0.0 : this.InvBeta.stdev / this.InvBeta.value / this.InvBeta.value;
            }
            else
                return;

           if (replies.Length > 12)
               this.lambda1 = replies[12].ToDouble(0.0);
            else
                return;

           if (replies.Length > 13)
               this.lambda2 = replies[13].ToDouble(0.0);
            else
                return;

           if (replies.Length > 14)
               this.fraction = replies[14].ToDouble(0.0);
           else
               return;

        }

        public string Print()
        {
            string message = "";
            message += this.elapsedTime.ToString() + " sec";
            message += Environment.NewLine;
            message += string.Format("{0, -5} = {1}", "R1", this.R1.Print());
            message += Environment.NewLine;
            message += string.Format("{0, -5} = {1}", "R2", this.R2.Print());
            message += Environment.NewLine;
            message += string.Format("{0, -5} = {1}", "R3", this.R3.Print());
            message += Environment.NewLine;
            message += string.Format("{0, -5} = {1}", "β", this.Beta.Print());
            message += Environment.NewLine;
            message += string.Format("{0, -5} = {1}", "¹/λ1", this.lifetime1_us.ToString("0.000") + " µs");
            message += Environment.NewLine;
            message += string.Format("{0, -5} = {1}", "¹/λ2", this.lifetime2_us.ToString("0.000") + " µs");
            message += Environment.NewLine;
            message += string.Format("{0, -5} = {1}", "f2", this.fraction.ToString("0.000"));
            message += Environment.NewLine;

            return message;
        }

        /// <summary>
        /// Prints only the values for the associated output values.
        /// Note: Tahoma is not a monospace font so the text boxes need to be aligned on the top.
        /// </summary>
        /// <returns></returns>
        public string PrintValues()
        {
            string message = "";
            message += Environment.NewLine; // This is for seconds
            message += this.R1.Print();
            message += Environment.NewLine;
            message += this.R2.Print();
            message += Environment.NewLine;
            message += this.R3.Print();
            message += Environment.NewLine;
            message += this.Beta.Print();
            message += Environment.NewLine;
            message += this.lifetime1_us.ToString("#,##0.0") + " µs";
            message += Environment.NewLine;
            message += this.lifetime2_us.ToString("#,##0.0") + " µs";
            message += Environment.NewLine;
            message += this.fraction.ToString("0.000");
            message += Environment.NewLine;
            return message;
        }

        /// <summary>
        /// Prints only the variable names for the associated output values.
        /// Note: Tahoma is not a monospace font so the text boxes need to be aligned on the top.
        /// </summary>
        /// <returns></returns>
        public string PrintNames()
        {
            string message = "";
            message += this.elapsedTime.ToString() + " sec";
            message += Environment.NewLine;
            message += string.Format("R1");
            message += Environment.NewLine;
            message += string.Format("R2");
            message += Environment.NewLine;
            message += string.Format("R3");
            message += Environment.NewLine;
            message += string.Format("β");
            message += Environment.NewLine;
            message += string.Format("¹/λ1");
            message += Environment.NewLine;
            message += string.Format("¹/λ2");
            message += Environment.NewLine;
            message += string.Format("f2");
            message += Environment.NewLine;
            return message;
        }

        public string[][] GetStringArrayFormat01()
        {
            string[][] items = new string[][] {
                new string[] {"time", " = ", this.elapsedTime.ToString("#,##0") + " sec"},
                new string[] {"R1", " = ", this.R1.Eng()},
                new string[] {"R2", " = ", this.R2.Eng()},
                new string[] {"R3", " = ", this.R3.Eng()},
                new string[] {"β", " = ", this.Beta.Eng()},
                new string[] {"Sm2", " = ", this.Sm2.Eng()},
                new string[] {"¹/λ1", " = ", this.lifetime1_us.ToString("#,##0.00") + " µs"},
                new string[] {"¹/λ2", " = ", this.lifetime2_us.ToString("#,##0.00") + " µs"},
                new string[] {"f2", " = ", this.fraction.ToString("0.00")}
            };
            return items;
        }

        public string[][] GetStringArrayFormat02()
        {
            string[] R1Str = R1.EngArray();
            string[] R2Str = R2.EngArray();
            string[] R3Str = R3.EngArray();
            string[] BetaStr = Beta.EngArray();
            string[] Sm2Str = Sm2.EngArray();

            string[][] items = new string[][] {
                new string[] {"time", " = ", this.elapsedTime.ToString("#,##0") + " sec", "", ""},
                new string[] {"R1", " = ", R1Str[0], R1Str[1], R1Str[2]},
                new string[] {"R2", " = ", R2Str[0], R2Str[1], R2Str[2]},
                new string[] {"R3", " = ", R3Str[0], R3Str[1], R3Str[2]},
                new string[] {"Sm2", " = ", Sm2Str[0], Sm2Str[1], Sm2Str[2]},
                new string[] {"β", " = ", BetaStr[0], BetaStr[1], BetaStr[2]},
                new string[] {"¹/λ1", " = ", this.lifetime1_us.ToString("#,##0.00") + " µs", "", ""},
                new string[] {"¹/λ2", " = ", this.lifetime2_us.ToString("#,##0.00") + " µs", "", ""},
                new string[] {"f2", " = ", this.fraction.ToString("0.00"), "", ""}
            };
            return items;
        }

        public ListViewItem[] GetListViewItems()
        {
            string[][] itemsArray = GetStringArrayFormat02();
            ListViewItem[] items = new ListViewItem[itemsArray.Length];

            for (int i = 0; i < itemsArray.Length; ++i)
            {
                items[i] = new ListViewItem(itemsArray[i]);
            }
 
            return items;
        }

        /// <summary>
        /// Returns a bitmap where the desired values are in an aligned table.
        /// </summary>
        /// <param name="size">The size of the bitmap.</param>
        /// <param name="colorText">The color of the text.</param>
        /// <param name="font"></param>
        /// <returns></returns>
        public Bitmap DrawTable(Size size, Color colorText, Font font)
        {
            /// Typical fonts are:
            /// Font("Tahoma", 12, FontStyle.Regular)
            string[][] itemsArray = GetStringArrayFormat02();
            StringFormat[] formats = new StringFormat[] {
                new StringFormat() {Alignment = StringAlignment.Near},      // Name
                new StringFormat() {Alignment = StringAlignment.Center},    // =
                new StringFormat() {Alignment = StringAlignment.Far},       // Value
                new StringFormat() {Alignment = StringAlignment.Center},    // ±
                new StringFormat() {Alignment = StringAlignment.Far}        // Stdev
            };
            Color[] colorTextArray = new Color[5].Fill(colorText);
            Color[] colorBackArray = new Color[5].Fill(Color.White);
            Font[] fonts = new Font[5].Fill(font);
            return itemsArray.DrawTable(colorTextArray, colorBackArray, formats, fonts, size);
        }


    }
}
