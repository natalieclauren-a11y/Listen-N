using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace Vf61Gui
{
    /// <summary>
    /// This is used for drawing the channel counts in the bar charts.
    /// </summary>
    public class Channel
    {
        public Rectangle rectangle;
        public int index = -1;
        public uint counts = 0;
        public bool enabled = true;

        public Channel()
        {
            this.rectangle = new Rectangle(0, 0, 0, 0);
            this.counts = 0;
            this.index = 0;
            this.enabled = true;
        }

        public Channel(int X,
            int width,
            uint counts,
            int index,
            bool enabled)
        {
            this.rectangle = new Rectangle(X, 0, width, 0);
            this.counts = counts;
            this.index = index;
            this.enabled = enabled;
        }

        public Channel(uint counts,
            int index,
            bool enabled)
        {
            this.rectangle = new Rectangle();
            this.counts = counts;
            this.index = index;
            this.enabled = enabled;
        }

        public Channel(Channel channel)
        {
            this.rectangle = new Rectangle(channel.rectangle.X,
                channel.rectangle.Y,
                channel.rectangle.Width,
                channel.rectangle.Height);
            this.counts = channel.counts;
            this.index = channel.index;
            this.enabled = channel.enabled;
        }

        public int Width
        {
            get { return rectangle.Width; }
            set { rectangle.Width = value; }
        }

        public int Height
        {
            get { return rectangle.Height; }
            set { rectangle.Height = value; }
        }

        public int X
        {
            get { return rectangle.X; }
            set { rectangle.X = value; }
        }

        public int Y
        {
            get { return rectangle.Y; }
            set { rectangle.Y = value; }
        }

        public int Left
        {
            get { return rectangle.Left; }
        }

        public int Right
        {
            get { return rectangle.Right; }
        }

        public int Top
        {
            get { return rectangle.Top; }
        }

        public int Bottom
        {
            get { return rectangle.Bottom; }
        }


    }
}
