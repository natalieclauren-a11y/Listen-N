using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Windows.Forms;

namespace Vf61Gui
{
    public class BitmapBorderOnly
    {
        public Bitmap bitmap;
        private Rectangle rect;

        public BitmapBorderOnly(PictureBox pictureBox) : this(pictureBox.Width, pictureBox.Height) { }

        public BitmapBorderOnly(Size size) : this(size.Width, size.Height) {}

        public BitmapBorderOnly(int width, int height)
        {
            rect = new Rectangle(0, 0, width, height);
            Rectangle rectBorder = new Rectangle(0, 0, width - 1, height - 1);
            bitmap = new Bitmap(width, height);
            Graphics graphics = Graphics.FromImage(bitmap);
            graphics.FillRectangle(new SolidBrush(Color.White), rect);
            graphics.DrawRectangle(new Pen(Color.Black), rectBorder);
            graphics.Dispose();
        }

    }
}
