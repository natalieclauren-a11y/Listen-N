using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Drawing;

namespace Vf61Gui
{
    public class LineF
    {
        private PointF _p1 = new PointF();
        private PointF _p2 = new PointF();
        public PointF P1
        {
            get { return _p1; }
            set { _p1 = value; }
        }
        public PointF P2
        {
            get { return _p2; }
            set { _p2 = value; }
        }
        public static explicit operator Line(LineF lineF)
        {
            return new Line()
            {
                P1 = Point.Round(lineF.P1),
                P2 = Point.Round(lineF.P2)
            };
            //return line;
        }
        public static explicit operator LineF(Line line)
        {
            return new LineF()
            {
                P1 = (PointF)line.P1,
                P2 = (PointF)line.P2
            };
        }
    }

    public static partial class Utilities
    {
        public static void DrawLine(this Graphics graphics, Pen pen, LineF lineF)
        {
            graphics.DrawLine(pen, (int)lineF.P1.X, (int)lineF.P1.Y, (int)lineF.P2.X, (int)lineF.P2.Y);
        }
    }

}
