using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Windows.Forms;

namespace Vf61Gui
{
    public class BitmapSm2Ib
    {
        /// <summary>
        /// The bitmap for Beta vs time. This is a log10-linear plot.
        /// </summary>
        public Bitmap? bitmapBetaTime;

        /// <summary>
        /// The bitmap for the backgrouind of Beta vs time. This is a log10-linear plot.
        /// </summary>
        public Bitmap? bitmapBetaTimeBackground; // Draw this only once and then use it everytime a new plot is made.

        /// <summary>
        /// The bitmap for Beta vs Sm2. This is a log10-log10 plot.
        /// </summary>
        public Bitmap? bitmapBetaSm2;

        /// <summary>
        /// The bitmap for the background of Beta vs Sm2. This is a log10-log10 plot.
        /// </summary>
        public Bitmap? bitmapBetaSm2Background; // Draw this only once and then use it everytime a new plot is made.

        public int leftPadding = 15;
        public int rightPadding = 10;
        public int bottomPadding = 0;
        public int topPadding = 0;

        // These colors are for the plot itselft.
        public Color colorLowM = Color.LightGreen;
        public Color colorHighMUpper = Color.LightPink;
        public Color colorHighMLower = Color.LightPink;
        public Color colorAlphaN = Color.LightGreen;

        // Colors for the plotting of the lines
        Pen penValue = new Pen(Color.DarkBlue);
        Pen penStdev = new Pen(Color.Blue);

        // Colors for the indication of current values.
        Pen penEndCircle = new Pen(Color.Red);
        Pen penEndBox = new Pen(Color.Red);

        public double minScaleY = 0.01;
        public double maxScaleY = 5.0;
        public double minScaleX = 1.0E-12; // Make the exponenent a factor of 3
        public double maxScaleX =   1.0;   // Make the exponenent a factor of 3
        public double highM = 0.5;
        public double cf = 1.8;

        /// <summary>
        /// The slope on the X axis for the Beta vs Sm2 plot. Note: input is log10(value). output is pixel.
        /// </summary>
        public double mX;   // The slope on the X axis for the Beta vs Sm2 plot.

        /// <summary>
        /// The intercept on the X axis for the Beta vs Sm2 plot. Note: input is log10(value). output is pixel.
        /// </summary>
        public double bX;   // The intercept on the X axis for the Beta vs Sm2 plot.

        public double mY;
        public double bY;

        Rectangle rectPlot;
        Rectangle rect;

        private double mGridY;
        private double bGridY;

        /// <summary>
        /// The grid lines for the x axis for bitmapBetaSm2 & bitmapBetaSm2Background
        /// </summary>
        List<Line> gridLinesX = new List<Line>();

        public BitmapSm2Ib(PictureBox pictureBox) : this(pictureBox.Width, pictureBox.Height) { }

        public BitmapSm2Ib(int width, int height)
        {
            rect = new Rectangle(0, 0, width, height);
            rectPlot = new Rectangle(leftPadding, topPadding, width - leftPadding - rightPadding, height - topPadding - bottomPadding);
            DrawBetaBackground();
        }

        public void DrawBetaBackground()
        {
            bitmapBetaTimeBackground = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppRgb);
            Graphics graphicsBetaTime = Graphics.FromImage(bitmapBetaTimeBackground);
            Font font = new Font("Tahoma", 10, FontStyle.Regular);
            SolidBrush brushBlack = new SolidBrush(Color.Black);
            Pen penBlack = new Pen(Color.Black);

            System.Drawing.SolidBrush brush = new SolidBrush(Color.White);
            graphicsBetaTime.FillRectangle(brush, rect);

            //double maxScaleLog = Math.Log10(maxScaleY);
            //double minScaleLog = Math.Log10(minScaleY);
            UtilitiesVf61Gui.GetSlope(Math.Log10(maxScaleY),
                rectPlot.Top,
                Math.Log10(minScaleY),
                rectPlot.Bottom,
                out mGridY,
                out bGridY);
            int rowTop = rectPlot.Top;
            int rowCf = (int)Math.Log10(cf).GetY(mGridY, bGridY);
            int rowHighM = (int)Math.Log10(highM).GetY(mGridY, bGridY);
            int row100 = (int)(Math.Log10(1.0).GetY(mGridY, bGridY));
            int row010 = (int)(Math.Log10(0.1).GetY(mGridY, bGridY));
            int rowBottom = rectPlot.Bottom;

            graphicsBetaTime.DrawGradientTopDown(new Rectangle(rectPlot.Left, rowTop, rect.Width, rowCf - rowTop), colorLowM, Color.White);
            graphicsBetaTime.DrawGradientBottomUp(new Rectangle(rectPlot.Left, rowCf, rect.Width, rowHighM - rowCf), Color.White, colorHighMUpper);
            graphicsBetaTime.DrawGradientTopDown(new Rectangle(rectPlot.Left, rowHighM, rect.Width, row010 - rowHighM), colorHighMLower, Color.White);
            graphicsBetaTime.DrawGradientTopDown(new Rectangle(rectPlot.Left, row010, rect.Width, rectPlot.Bottom - row010), Color.White, colorAlphaN);

            // Draw the text
            int XText = 5;
            graphicsBetaTime.DrawString90("High M", font, brushBlack, new Point(XText, rowHighM));
            graphicsBetaTime.DrawString90("Cf", font, brushBlack, new Point(XText, rowCf));
            graphicsBetaTime.DrawString90("(a,n)", font, brushBlack, new Point(XText, row010));

            // Copy the bitmap to bitmapBetaSm2Background
            bitmapBetaSm2Background = new Bitmap(bitmapBetaTimeBackground);
            Graphics graphicsBetaSm2 = Graphics.FromImage(bitmapBetaSm2Background);

            // Draw the horizontal gridlines for Beta vs Time
            graphicsBetaTime.DrawLine(new Pen(Color.Orange), rectPlot.Left + 1, rowCf, rectPlot.Right - 1, rowCf);
            graphicsBetaTime.DrawLine(new Pen(Color.Red), rectPlot.Left + 1, rowHighM, rectPlot.Right - 1, rowHighM);
            graphicsBetaTime.DrawLine(new Pen(Color.Gray), rectPlot.Left + 1, row100, rectPlot.Right - 1, row100);
            graphicsBetaTime.DrawLine(new Pen(Color.Gray), rectPlot.Left + 1, row010, rectPlot.Right - 1, row010);

            // Draw the horizontal gridlines for Beta vs Sm2
            graphicsBetaSm2.DrawLine(new Pen(Color.Orange), rectPlot.Left + 1, rowCf, rect.Width, rowCf);
            graphicsBetaSm2.DrawLine(new Pen(Color.Red), rectPlot.Left + 1, rowHighM, rect.Width, rowHighM);
            graphicsBetaSm2.DrawLine(new Pen(Color.Gray), rectPlot.Left + 1, row100, rect.Width, row100);
            graphicsBetaSm2.DrawLine(new Pen(Color.Gray), rectPlot.Left + 1, row010, rect.Width, row010);

            // Draw the border for Beta vs Time.
            // Note: This has a little section on the right that isn't bounded by penBlack.
            Rectangle rectBorder = new Rectangle()
            {
                Location = rectPlot.Location,
                Width = rectPlot.Width,
                Height = rectPlot.Height - 1
            };
            graphicsBetaTime.DrawRectangle(penBlack, rectBorder);

            // Calculate the locations of the vertical gridlines for Beta vs Sm2
            gridLinesX.Clear();
            UtilitiesVf61Gui.GetSlope(Math.Log10(minScaleX), rectPlot.Left, Math.Log10(maxScaleX), rect.Right, out mX, out bX);    // Yes, it's rectPlot.Left & rect.Right.
            UtilitiesVf61Gui.GetSlope(Math.Log10(minScaleY), rect.Bottom, Math.Log10(maxScaleY), rect.Top, out mY, out bY);
            int I = (int)Math.Log10(maxScaleX);
            for (int i = (int)Math.Log10(minScaleX); i <= I; i += 3)
            {
                // Note: i is essentially Math.Log10(x value for a grid line)
                int x = (int)UtilitiesVf61Gui.GetY((double)i, mX, bX);
                Line line = new Line()
                {
                    P1 = new Point() { X = x, Y = rectPlot.Top },
                    P2 = new Point() { X = x, Y = rectPlot.Bottom }
                };
                gridLinesX.Add(line);
                graphicsBetaSm2.DrawLine(new Pen(Color.Gray), line); 
            }

            graphicsBetaTime.Dispose();
            graphicsBetaSm2.Dispose();
        }

        public void DrawBetaVsTime(Sm2Ib[] sm2Ib, int numSteps)
        {
            if (bitmapBetaTimeBackground != null)
            {
                bitmapBetaTime = new Bitmap(bitmapBetaTimeBackground);
                Graphics? graphics = Graphics.FromImage(bitmapBetaTime);

                int stepLength = rectPlot.Width / numSteps;

                int Steps = Math.Min(numSteps, sm2Ib.Length);
                int index = Math.Max(0, sm2Ib.Length - Steps);

                Point valueOld = new Point();
                Point upperOld = new Point();
                Point lowerOld = new Point();

                for (int step = 0; step < Steps; ++step)
                {
                    Sm2Ib sm2 = sm2Ib[index++];
                    int y;
                    int yUpper;
                    int yLower;

                    if (sm2.Beta.value < 0.01)
                    {
                        y = rectPlot.Height;
                        yUpper = rectPlot.Height - 2;
                        yLower = 0;
                    }
                    else
                    {
                        y = (int)Math.Log10(sm2.Beta.value).GetY(mGridY, bGridY);
                        yUpper = (int)Math.Log10(sm2.Beta.upper).GetY(mGridY, bGridY);
                        yLower = (int)Math.Log10(sm2.Beta.lower).GetY(mGridY, bGridY);
                    }
                    Point value1 = new Point()
                    {
                        X = step * stepLength + rectPlot.Left,
                        Y = y
                    };
                    Point value2 = new Point()
                    {
                        X = value1.X + stepLength,
                        Y = value1.Y
                    };
                    Point upper1 = new Point()
                    {
                        X = value1.X,
                        Y = yUpper
                    };

                    Point upper2 = new Point()
                    {
                        X = value2.X,
                        Y = yUpper
                    };
                    Point lower1 = new Point()
                    {
                        X = value1.X,
                        Y = yLower
                    };

                    Point lower2 = new Point()
                    {
                        X = value2.X,
                        Y = yLower
                    };

                    if (!valueOld.IsEmpty)
                    {
                        graphics.DrawLine(penValue, valueOld, value1);
                    }
                    if (!upperOld.IsEmpty)
                    {
                        graphics.DrawLine(penStdev, upperOld, upper1);
                    }
                    if (!lowerOld.IsEmpty)
                    {
                        graphics.DrawLine(penStdev, lowerOld, lower1);
                    }

                    graphics.DrawLine(penStdev, lower1, lower2);
                    graphics.DrawLine(penStdev, value1, value2);
                    graphics.DrawLine(penValue, upper1, upper2);    // Draw last.

                    valueOld = value2;
                    upperOld = upper2;
                    lowerOld = lower2;
                }
                // Draw the endpoint circle
                int rectSize = 2;
                Rectangle rectEnd = new Rectangle(valueOld.X - rectSize, valueOld.Y - rectSize, 2 * rectSize, 2 * rectSize);
                graphics.DrawEllipse(penEndCircle, rectEnd);

                int rectHeight = 5;
                Rectangle rectValue = new Rectangle(rectPlot.Right + 1, valueOld.Y - rectHeight, rect.Right - rectPlot.Right - 2, 2 * rectHeight);
                graphics.DrawRectangle(penEndBox, rectValue);

                graphics.Dispose();
                graphics = null;
            }
        }

        public void DrawBetaVsSm2(Sm2Ib[] sm2Ib, int numItems)
        {
            if(bitmapBetaSm2Background == null)
            {
                return;
            }
            bitmapBetaSm2 = new Bitmap(bitmapBetaSm2Background);
            Graphics graphics = Graphics.FromImage(bitmapBetaSm2);
            int I = (int)Math.Max(0, sm2Ib.Length - numItems);
            SolidBrush brush = new SolidBrush(Color.DarkBlue);
            double mSize;
            double bSize;
            UtilitiesVf61Gui.GetSlope((double)I, 1.0, (double)(sm2Ib.Length - 1), 5.0, out mSize, out bSize);
            
            for (int i = I; i < sm2Ib.Length; ++i)
            {
                // I want different sizes for each point. The most recent points are the biggest.
                int size = (int)((double)i).GetY(mSize, bSize);
                Sm2Ib value = sm2Ib[i];
                if((value.Sm2.value <= 0.0)
                    || (value.Beta.value <= 0.0))
                {
                    return;
                }

                int sm2;
                if (value.Sm2.value < 0.0000001)
                {
                    sm2 = rectPlot.Left;
                }
                else 
                {
                    sm2 = (int)Math.Log10(value.Sm2.value).GetY(mX, bX);
                }

                int beta;

                if (value.Beta.value < 0.01)
                {
                    beta = rectPlot.Bottom;
                }
                else
                {
                    beta = (int)Math.Log10(value.Beta.value).GetY(mY, bY);
                }
                Rectangle rectDraw = new Rectangle()
                {
                    X = (sm2 - size / 2).Clamp(rectPlot.Left, rectPlot.Right),
                    Y = (beta - size / 2).Clamp(rectPlot.Bottom, rectPlot.Top),
                    Width = size,
                    Height = size
                };
                graphics.FillEllipse(brush, rectDraw);
            }

            graphics.Dispose();
        }
    }
    public static partial class Utilities
    {
 
        public static void DrawGradientTopDown(this Graphics graphics, Rectangle rect, Color colorTop, Color colorBottom)
        {
            double mRed;
            double mGreen;
            double mBlue;
            double bRed;
            double bGreen;
            double bBlue;

            int vRed;
            int vGreen;
            int vBlue;

            Pen pen = new Pen(colorTop);
            Point p1 = new Point(rect.Left, rect.Top);
            Point p2 = new Point(rect.Right, rect.Top);

            UtilitiesVf61Gui.GetSlope((double)rect.Top, (double)colorTop.R, (double)rect.Bottom, (double)colorBottom.R, out mRed, out bRed);
            UtilitiesVf61Gui.GetSlope((double)rect.Top, (double)colorTop.G, (double)rect.Bottom, (double)colorBottom.G, out mGreen, out bGreen);
            UtilitiesVf61Gui.GetSlope((double)rect.Top, (double)colorTop.R, (double)rect.Bottom, (double)colorBottom.B, out mBlue, out bBlue);

            for (int row = rect.Top; row < rect.Bottom; ++row)
            {
                vRed = ((int)((double)row).GetY(mRed, bRed)).Clamp( 0, 255);
                vGreen = ((int)((double)row).GetY(mGreen, bGreen)).Clamp(0, 255);
                vBlue = ((int)((double)row).GetY(mBlue, bBlue)).Clamp(0, 255);

                pen.Color = Color.FromArgb(vRed, vGreen, vBlue);
                p1.Y = row;
                p2.Y = row;
                graphics.DrawLine(pen, p1, p2);
            }
        }

        public static void DrawGradientBottomUp(this Graphics graphics, Rectangle rect, Color colorTop, Color colorBottom)
        {
            double mRed;
            double mGreen;
            double mBlue;
            double bRed;
            double bGreen;
            double bBlue;

            int vRed;
            int vGreen;
            int vBlue;

            Pen pen = new Pen(colorTop);
            Point p1 = new Point(rect.Left, rect.Top);
            Point p2 = new Point(rect.Right, rect.Top);

            UtilitiesVf61Gui.GetSlope((double)rect.Bottom, (double)colorBottom.R, (double)rect.Top, (double)colorTop.R, out mRed, out bRed);
            UtilitiesVf61Gui.GetSlope((double)rect.Bottom, (double)colorBottom.G, (double)rect.Top, (double)colorTop.G, out mGreen, out bGreen);
            UtilitiesVf61Gui.GetSlope((double)rect.Bottom, (double)colorBottom.R, (double)rect.Top, (double)colorTop.B, out mBlue, out bBlue);

            for (int row = rect.Top; row < rect.Bottom; ++row)
            {
                vRed = ((int)((double)row).GetY(mRed, bRed)).Clamp(0, 255);
                vGreen = ((int)((double)row).GetY(mGreen, bGreen)).Clamp(0, 255);
                vBlue = ((int)((double)row).GetY(mBlue, bBlue)).Clamp(0, 255);

                pen.Color = Color.FromArgb(vRed, vGreen, vBlue);
                p1.Y = row;
                p2.Y = row;
                graphics.DrawLine(pen, p1, p2);
            }
        }
    }
}
