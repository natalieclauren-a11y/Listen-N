using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Drawing;

namespace Vf61Gui
{
    public class Line
    {
        private Point _p1 = new Point();
        private Point _p2 = new Point();
        public Point P1
        {
            get { return _p1; }
            set { _p1 = value; }
        }
        public Point P2
        {
            get { return _p2; }
            set { _p2 = value; }
        }
        public static explicit operator LineF(Line line)
        {
            return new LineF()
            {
                P1 = (PointF)line._p1,
                P2 = (PointF)line._p2
            };
        }
        public static explicit operator Line(LineF lineF)
        {
            return new Line()
            {
                P1 = Point.Round(lineF.P1),
                P2 = Point.Round(lineF.P2)
            };
        }
    }

    public static partial class Utilities
    {
        public static void DrawLine(this Graphics graphics, Pen pen, Line line)
        {
            graphics.DrawLine(pen, line.P1, line.P2);
        }
    }
}
