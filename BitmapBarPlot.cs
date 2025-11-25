using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Windows.Forms;

namespace Vf61Gui
{
    public class BitmapBarPlot : MCImages
    {
        // Yeah, I know I should do these with { get, set }
        // but this does the same thing with less code
        public int numberOfGridlines = 0;
        public int lengthOfTickmark = 10;
        public Color[] colorRow = new Color[] { Color.DarkBlue, Color.DodgerBlue, Color.Cyan, Color.Blue };
        public Color colorGridlines = Color.DarkGray;
        public Color colorBorderChannel = Color.DarkGray;
        public Pen penTicks = new Pen(Color.Black, 2);

        public BitmapBarPlot(PictureBox pictureBox, DetectorFormat detectorFormat) : this(pictureBox.Width, pictureBox.Height, detectorFormat) { }

        public BitmapBarPlot(int width,
            int height,
            DetectorFormat detectorFormat)
        {
            // Initialize the MCImages values
            this.bitmap = new Bitmap(width,
                height,
                PixelFormat.Format32bppRgb);
            this.detectorFormat = detectorFormat;
            this.sizeOfUndefined = 16;
            this.widthPercentRow1 = 0.8;
            this.widthPercentRow2 = 0.7;
            this.widthPercentRow3 = 0.45;
            this.widthPercentChannel16 = 0.5;
            this.borderPaddingLeft = 14;
            this.borderPaddingRight = 4;
            this.borderPaddingTop = 4;
            this.borderPaddingBottom = 4;
            this.plotType = PlotType.BarPlot;

            //border = new Rectangle(borderLeft, borderTop, widthBorder, heightBorder);

            border = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            InitializeDistribution(detectorFormat);
        }

        public void DrawBarPlot(uint[] channelCounts)
        {
            CopyChannelCounts(channelCounts);
            DrawBarPlot();
        }

        public void DrawBarPlot()
        {
            if(bitmap == null)
            {
                return;
            }
            graphics = Graphics.FromImage(bitmap);

            solidBrush.Color = colorPlotFill;
            graphics.FillRectangle(solidBrush, border);

            minScale = 0;
            maxScale = MaxCount();
            double minScaleValue;
            double maxScaleValue;
            double gridLineStepSize;
            UtilitiesVf61Gui.CalculateScaleDetails((double)minScale,
                (double)maxScale,
                true,
                false,
                5,
                out minScaleValue,
                out maxScaleValue,
                out gridLineStepSize);

            minScaleValue = 0.0;    // Force the plot to start at 0
            gridStepSize = Math.Max(1, (int)gridLineStepSize);
            numberOfGridlines = Math.Max(2, (int)((maxScaleValue - minScaleValue) / gridLineStepSize));
            minScale = (uint)minScaleValue;
            maxScale = (uint)((double)maxScaleValue * gridLineStepSize);

            DrawGridlines();

            int range = (int)(maxScaleValue - minScaleValue);
            int color_index = 0;
            foreach (Channel[] row in channels)
            {
                solidBrush.Color = colorRow[color_index];
                pen.Color = colorBorderChannel;
                foreach (Channel channel in row)
                {
                    channel.Height = (int)((double)channel.counts * (double)border.Height / (double)range);
                    channel.Y = border.Bottom - channel.Height;
                    if (channel.enabled)
                    {
                        graphics.FillRectangle(solidBrush, channel.rectangle);
                        graphics.DrawRectangle(pen, channel.rectangle);
                    }
                }
                ++color_index;
            }

            DrawBarPlotBorder();
        }

        private void DrawGridlines()
        {
            int stepSize = border.Height / numberOfGridlines;

            // Draw the gridlines
            int y = border.Bottom - stepSize;
            for (int i = 1; i < numberOfGridlines; ++i)
            {
                // This doesn't draw the top most or bottom most gridline.
                // Draw the gridline
                graphics?.DrawLine(pen,
                    border.Left + lengthOfTickmark,
                    y,
                    border.Right - 1,
                    y);
                //Draw the tickmarks
                DrawTickMark(border.Left, y);
                y -= stepSize;
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
            SolidBrush brush = new SolidBrush(penTicks.Color);
            Rectangle rect = new Rectangle(start.X, start.Y, lengthOfTickmark, (int)penTicks.Width);
            graphics?.FillRectangle(brush, rect);
        }

        public void DrawBarPlotBorder()
        {
            // Draw the border
            graphics?.DrawLine(pen, new Point(0, 0), new Point(border.Right, 0));
            graphics?.DrawLine(pen, new Point(border.Right - 1, 0), new Point(border.Right - 1, border.Bottom));

            // Draw the top tickmark
            DrawTickMark(border.Left, border.Top);

            // Draw the bottom border
            graphics?.DrawLine(penTicks,
                border.Left,
                border.Bottom,
                border.Right,
                border.Bottom);

            // Draw the y axis
            graphics?.DrawLine(penTicks,
                border.Left,
                border.Top,
                border.Left,
                border.Bottom);
        }
    }
}
