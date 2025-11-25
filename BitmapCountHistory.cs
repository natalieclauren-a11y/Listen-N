using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Windows.Forms;
using RingBuffer;

namespace Vf61Gui
{
    public class BitmapCountHistory
    {
        public Bitmap bitmap;
        //Rectangle rectPlot;
        public Rectangle rectBorder;

        public int leftPadding = 15;
        public int rightPadding = 10;
        public int bottomPadding = 0;
        public int topPadding = 0;
        public int stepCount = 5;
        public int gridStepSize;
        public int numberOfGridlines;
        public int lengthOfTickmark = 4;
        public int widthOfTickmark = 2;


        public uint minScale = 0;   // Just for good measure. I don't think this will ever get changed.
        public uint maxScale = 0;

        public uint minValue = 0;   
        public uint maxValue = 0;

        List<Line> gridLines = new();
        List<Rectangle> tickMarks = new();  // Use Rectangle here because a line with a width > 1 give wonky results.


        private int sumIndex = 0;

        private Graphics? graphics;

        public Color colorTickmark = Color.Black;

        /// <summary>
        /// The color of the hash marks on the plot.
        /// </summary>
        public Color colorValue = Color.Green;

        ChannelCountsTime[] buffer = [];
       

        public BitmapCountHistory(PictureBox pictureBox) : this(pictureBox.Width, pictureBox.Height) { }

        /// <summary>
        /// Constructor for BitmapCountHistory.
        /// </summary>
        /// <param name="width">The width of the bitmap.</param>
        /// <param name="height">The height of the bitmap.</param>
        /// <param name="arraySize"></param>
        public BitmapCountHistory(int width, int height)
        {
            rectBorder = new Rectangle(0, 0, width, height);
            //rectPlot = new Rectangle(leftPadding, topPadding, width - leftPadding - rightPadding, height - topPadding - bottomPadding);
            bitmap = new Bitmap(rectBorder.Width, rectBorder.Height, PixelFormat.Format32bppRgb);
                
        }

        /// <summary>
        /// Draws a count rate history.
        /// </summary>
        /// <param name="ringBuffer">Contains the count rate history to plot.</param>
        /// <param name="numberOfItems">How many items do you want to draw.</param>
        /// <param name="sumIndex">What count rate history do you want to draw. 0 =&gt; channels 0-15, 1 =&gt; 15-31, 2 =&gt; 0 - 31.</param>
        public void Draw(RingBuffer<ChannelCountsTime> ringBuffer, int numberOfItems, int sumIndex)
        {
            this.sumIndex = sumIndex;
            buffer = new ChannelCountsTime[ringBuffer.Size];
            ringBuffer.CopyTo(buffer, 0);
            GetMinAndMaxValues();

            graphics = Graphics.FromImage(bitmap);

            //solidBrush.Color = colorPlotFill;
            SolidBrush brushPlotFill = new SolidBrush(Color.White);
            graphics.FillRectangle(brushPlotFill, rectBorder);

            double minScaleValue;
            double maxScaleValue;
            double gridLineStepSize;
            UtilitiesVf61Gui.CalculateScaleDetails((double)minValue,
                (double)maxValue,
                true,
                false,
                5,
                out minScaleValue,
                out maxScaleValue,
                out gridLineStepSize);

            minScaleValue = 0.0;    // Force the plot to start at 0.
            gridStepSize = Math.Max(1, (int)gridLineStepSize);
            numberOfGridlines = Math.Max(2, (int)((maxScaleValue - minScaleValue) / gridLineStepSize));
            minScale = (uint)minScaleValue;
            maxScale = (uint)((double)numberOfGridlines * gridLineStepSize);

            double m;
            double b;
            UtilitiesVf61Gui.GetSlope((double)maxScale, 0.0, (double)minScale, (double)rectBorder.Bottom - 4, out m, out b);

            DrawGridlines(m, b);
            if (buffer.Length == 0)
            {
                DrawBorder();
                return;
            }

            int iStart = Math.Max(1, buffer.Length - numberOfItems);
            int tickLength = rectBorder.Width / numberOfItems;
            Pen penLine = new Pen(colorValue, 2.0f);
            Point p1 = new Point(rectBorder.Right, 0);              // Point on the left side of the hashmark
            Point p2 = new Point(rectBorder.Right + tickLength, 0); // Point on the right side of the hashmark
            Point p3 = new Point(rectBorder.Right, 0);              // Creates the vertical line connecting points.
            for (int i = buffer.Length - 1; i > iStart - 1; --i)
            {
                p1.X -= tickLength;
                p1.Y = (int)UtilitiesVf61Gui.GetY((double)buffer[i].sum[sumIndex], m, b);
                p2.X = p1.X + tickLength;
                p2.Y = p1.Y;
                graphics.DrawLine(penLine, p1, p2);
                p3.X = p1.X;
                p3.Y = (int)UtilitiesVf61Gui.GetY((double)buffer[i - 1].sum[sumIndex], m, b);
                graphics.DrawLine(penLine, p1, p3);
            }
            DrawBorder();
        }

        private void GetMinAndMaxValues()
        {
            if(buffer.Length == 0) {
                minValue = 0;
                maxValue = 0;
                return;
            }
            minValue = buffer[0].sum[sumIndex];
            maxValue = minValue;

            foreach (ChannelCountsTime item in buffer) // buffer.Skip(1) does not exist.
            {
                uint value = item.sum[sumIndex];
                minValue = Math.Min(value, minValue);
                maxValue = Math.Max(value, maxValue);
            }
        }

        private void DrawGridlines(double m, double b)
        {
            gridLines.Clear();
            tickMarks.Clear();
            int stepSize = rectBorder.Height / numberOfGridlines;
            Pen penGridline = new Pen(Color.Gray);
            SolidBrush brushTickmark = new SolidBrush(Color.Black);
            // Draw the gridlines & tickmarks
            for (int i = 0; i < numberOfGridlines + 1; ++i)
            {
                int y = (int)UtilitiesVf61Gui.GetY(i * gridStepSize, m, b);
                Line line = new Line()
                {
                    P1 = new Point() { X = rectBorder.Left + lengthOfTickmark, Y = y },
                    P2 = new Point() { X = rectBorder.Right, Y = y }
                };
                
                gridLines.Add(line);

                Rectangle rect = new Rectangle()
                {
                    X = rectBorder.Left,
                    Y = y,
                    Width = lengthOfTickmark,
                    Height = 2
                };

                tickMarks.Add(rect);

                graphics?.DrawLine(penGridline, line);
                graphics?.FillRectangle(brushTickmark, rect);
            }
        }
        

        private void DrawTickMark(int x1, int y1)
        {
            DrawTickMark(new Point(x1, y1));
        }

        /// <summary>
        /// Draws a tickmark from start to stop.
        /// </summary>
        /// <remarks>
        /// I tried using a pen of size == 2 but it gives wonky results.
        /// </remarks>
        /// <param name="start"></param>
        /// <param name="stop"></param>
        private void DrawTickMark(Point start)
        {
            SolidBrush brush = new SolidBrush(colorTickmark);
            Rectangle rect = new Rectangle(start.X, start.Y, lengthOfTickmark, widthOfTickmark);
            graphics?.FillRectangle(brush, rect);
        }

        private void DrawBorder()
        {
            // Draw the border
            Pen pen = new Pen(Color.Black);
            graphics?.DrawLine(pen, new Point(0, 0), new Point(rectBorder.Right, 0));
            graphics?.DrawLine(pen, new Point(rectBorder.Right - 1, 0), new Point(rectBorder.Right - 1, rectBorder.Bottom));

            // Draw the top tickmark
            DrawTickMark(rectBorder.Left, rectBorder.Top);

            // Draw the bottom border
            graphics?.DrawLine(pen,
                rectBorder.Left,
                rectBorder.Bottom - 1,
                rectBorder.Right,
                rectBorder.Bottom - 1);

            // Draw the y axis
            graphics?.DrawLine(pen,
                rectBorder.Left,
                rectBorder.Top,
                rectBorder.Left,
                rectBorder.Bottom);
        }

    }
}
