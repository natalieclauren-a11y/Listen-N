using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Drawing.Imaging;
//using Microsoft.WindowsCE.Forms;

namespace Vf61Gui
{
    public class BitmapBattery
    {
        public Bitmap? bitmap;

        public int batteryLevel = 50;
        public int isCharging = 0;
        public bool isPresent = false;

        public Color colorCharge = Color.Blue;
        public Color colorCharging = Color.Lime;
        public Color colorEmpty = Color.Azure;
        public Color colorBorder = Color.Black;
        public Color colorBackground = Color.Transparent;
        public Color colorNub = Color.FromArgb(64, 64, 64);
        public Color colorLowBat = Color.Red;

        // Create the drawing areas
        private Rectangle positiveNub;      // The positive pole on top of the battery bitmap.
        private Rectangle battery;          // The area inside the battery border. This is the combined area of charging and empty.
        private Rectangle batteryBorder;    // The border around the battery areas of charging + empty.
        private Rectangle charging;         // The section of the battery that is charged. i.e. the bottom part of the battery area that is filled.
        private Rectangle empty;            // The empty portion of the battery. i.e. the top part of the battery area that is not filled.

        // Create the pens
        private Pen pen;

        // Create the brushes
        private SolidBrush brush;

        /// <summary>
        /// Creates the bitmap of a battery.
        /// The size of the PictureBox for a battery should be (14, 58)
        /// </summary>
        /// <param name="battery_level">This is between 0 and 100 and represents the battery level.</param>
        /// <param name="charging">0 = not charging 1 = charging</param>
        public BitmapBattery(bool drawDefault)
        {
            // Setup the Bitmap
            positiveNub = new Rectangle(3, 0, 8, 5);
            battery = new Rectangle(1, 6, 12, 51);
            batteryBorder = new Rectangle(0, 5, 13, 52);

            // Create Pens
            pen = new Pen(colorBorder, 1);

            // Create Brushes
            brush = new SolidBrush(colorCharge);

            if (drawDefault)
            {
                Draw(batteryLevel, isCharging);
            }
            else
            {
                bitmap = null;
            }
        }

        /// <summary>
        /// Call this to draw a new bitmap of the battery.
        /// </summary>
        /// <param name="BatteryLevel"></param>
        /// <param name="IsCharging"></param>
        public void Draw(int BatteryLevel,
            int IsCharging)
        {
            // Set the properties of BitmapBattery
            this.batteryLevel = BatteryLevel.Clamp(0, 100);
            this.isCharging = IsCharging;
            this.isPresent = (BatteryLevel != -1);

            // Create and initialize Graphics
            bitmap = new Bitmap(14, 58, PixelFormat.Format32bppRgb);
            Graphics graphics = Graphics.FromImage(bitmap);

            // Set up the boundaries
            empty.X = 1;
            empty.Y = positiveNub.Height;
            empty.Width = battery.Width;
            empty.Height = (int)((double)battery.Height * (1.0 - ((double)batteryLevel - 0.5) / 100.0));  // Round down. This will make a battery charge of 99 equal to 100.
            charging.X = 1;
            charging.Y = empty.Bottom;
            charging.Width = battery.Width;
            charging.Height = battery.Bottom - empty.Bottom;

            // Color the background 
            brush.Color = colorBackground;
            graphics.FillRectangle(brush, 0, 0, bitmap.Width, bitmap.Height);


            // Draw positive the positive pole nub
            brush.Color = colorNub;
            graphics.FillRectangle(brush, positiveNub);

            if (isPresent)
            {
                // Battery is present

                // Draw battery fill
                if (isCharging == 1)
                {
                    brush.Color = colorCharging;
                }
                else if (batteryLevel < 26)
                {
                    brush.Color = colorLowBat;
                }
                else
                {
                    brush.Color = colorCharge;
                }
                graphics.FillRectangle(brush, charging);
                // Draw empty fill
                brush.Color = colorEmpty;
                graphics.FillRectangle(brush, empty);
            }
            else
            {
                // There is no battery
                // Just draw grey
                // Draw positive the positive pole nub
                brush.Color = Color.Gray;
                graphics.FillRectangle(brush, battery);
            }
            // Draw the battery border
            pen.Color = Color.Black;
            graphics.DrawRectangle(pen, batteryBorder);
        }

        /// <summary>
        /// Turns the battery bitmap 90 degrees clockwise.
        /// This does not change the bitmap itself, rather it returns a new bitmap.
        /// </summary>
        /// <returns>Returns a new bitmap that is turned 90 degrees clockwise.</returns>
        public Bitmap? TurnSideways()
        {
            return bitmap?.Rotate270();
            //PixelFormat pixelFormat = PixelFormat.Format32bppRgb;
            //Rectangle rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);

            //Bitmap bitmapRotated = new Bitmap(bitmap.Height, bitmap.Width, pixelFormat);

            //for (int i = 0; i < bitmap.Width; i++)
            //{
            //    for (int j = 0; j < bitmap.Height; j++)
            //    {
            //        int newX = j;
            //        int newY = bitmap.Width - i - 1;
            //        Color color = bitmap.GetPixel(i, j);
            //        bitmapRotated.SetPixel(newX, newY, color);
            //    }
            //}
            //return bitmapRotated;
        }
    }

}
