using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Windows.Forms;

namespace Vf61Gui
{
    public class MCImages
    {
        // Yeah, I know I should do these with { get, set }
        // but this does the same thing with less code
        public Bitmap? bitmap;
        public Pen pen = new Pen(Color.Black, 1);
        public Channel[][] channels = { new Channel[16], new Channel[16] };    // Stores the data to be plotted. The format is channels[row][index in row].
        public bool showChannel16 = false;
        public int totalWidth = 0;
        public int gridStepSize = 0;
        public int borderPaddingLeft = 10;
        public int borderPaddingRight = 4;
        public int borderPaddingTop = 0;
        public int borderPaddingBottom = 0;
        public uint minScale = 0;    // The minimum Y value for the scale. This should be zero.
        public uint midScale = 0;
        public uint maxScale = 0;    // The maximum Y value for the scale.
        public double widthPercentRow1 = 0.8;
        public double widthPercentRow2 = 0.7;
        public double widthPercentRow3 = 0.6;
        public double widthPercentChannel16 = 0.5;

        protected Rectangle border;
        protected Graphics? graphics;
        protected int sizeOfUndefined = 16;
        protected DetectorFormat detectorFormat = DetectorFormat.Undefined;
        protected SolidBrush solidBrush = new SolidBrush(Color.White);
        public Color colorPlotFill = Color.White;

        public PlotType plotType = PlotType.Undefined;
        public ScalingType scalingType = ScalingType.Undefined;

        public MCImages()
        {
 
        }

        public DetectorFormat Format()
        {
            return this.detectorFormat;
        }

        public DetectorFormat Format(DetectorFormat detectorFormat)
        {
            this.detectorFormat = detectorFormat;
            DetectorFormat_Changed(this, new EventArgs());
            return this.detectorFormat;
        }

        public int CountNumberOfChannels()
        {
            if(channels == null)
            {
                return 0;
            }
            int sum = 0;
            foreach (Channel[] row in channels)
            {
                sum += row.Length;
            }
            return sum;
        }
        public int TotalNumberOfChannels()
        {
            if (channels == null)
            {
                return 0;
            }
            if (detectorFormat == DetectorFormat.Undefined)
            {
                return channels[0].Length;
            }
            else
            {
                return CountNumberOfChannels();
            }
        }

        public int TotalNumberOfChannels(int numberOfChannels)
        {
            sizeOfUndefined = numberOfChannels;
            if (detectorFormat == DetectorFormat.Undefined)
            {
                DetectorFormat_Changed(this, new EventArgs());
            }
            return CountNumberOfChannels();
        }


        public int NumRows()
        {
            if (channels == null)
            {
                return 0;
            }
            else
            {
                return channels.Length;
            }
        }

        public uint SumCounts()
        {
            uint sum = 0;
            if (channels == null)
            {
                return 0;
            }
            foreach (Channel[] row in channels)
            {
                if (row == null)
                {
                    continue;
                }
                foreach (Channel channel in row)
                {
                    sum += channel.counts;
                }
            }
            return sum;
        }

        /// <summary>
        /// Copies the values in channels to temp.
        /// </summary>
        /// <param name="temp"></param>

        public void CopyChannels(out Channel[] temp)
        {
            temp = new Channel[16];
            if(channels == null)
            {
                return;
            }
            int index = 0;
            foreach (Channel[] row in channels)
            {
                foreach (Channel channel in row)
                {
                    //temp[index] = new Channel(channel);
                    temp[index] = channel;
                    ++index;
                }
            }
        }

        public void CopyChannelCounts(out ulong[] temp)
        {
            temp = new ulong[CountNumberOfChannels()];
            if(channels == null)
            {
                return;
            }
            int index = 0;
            foreach (Channel[] row in channels)
            {
                foreach (Channel channel in row)
                {
                    temp[index] = (ulong)channel.counts;
                    ++index;
                }
            }
        }

        public void CopyChannelCounts(out uint[] temp)
        {
            temp = new uint[CountNumberOfChannels()];
            int index = 0;
            foreach (Channel[] row in channels)
            {
                foreach (Channel channel in row)
                {
                    temp[index] = channel.counts;
                    ++index;
                }
            }
        }
        
        public void DetectorFormat_Changed(object sender, EventArgs e)
        {
            Channel[] temp;
            CopyChannels(out temp);
            InitializeDistribution(detectorFormat);
            CopyChannelCounts(temp);
        }



        /// <summary>
        /// Copies the counts from temp to channels.
        /// It does not copy the rectangle.
        /// </summary>
        /// <param name="temp"></param>
        protected void CopyChannelCounts(Channel[] temp)
        {
            if (temp == null)
            {
                return;
            }
            int index = 0;
            for (int row = 0; row < channels.Length; ++row)
            {
                for (int col = 0; col < channels[row].Length; ++col)
                {
                    channels[row][col].counts = temp[index].counts;
                    ++index;
                }
            }
        }

        /// <summary>
        /// Copy the values in channelCounts to the internal channels jagged array.
        /// </summary>
        /// <param name="channelCounts"></param>
        protected void CopyChannelCounts(uint[] channelCounts)
        {
            int index = 0;
            int row = 0;
            int sizeTotal = Math.Min(channelCounts.Length, 16);

            while ((index < sizeTotal) && (row < channels.Length))
            {
                for (int col = 0; col < channels[row].Length; ++col)
                {
                    channels[row][col].counts = channelCounts[index];
                    ++index;
                    if (index >= sizeTotal)
                    {
                        break;
                    }
                }
                ++row;
            }
        }
        /// <summary>
        /// Copy the values in channelCounts to the internal channels jagged array.
        /// </summary>
        /// <param name="channelCounts"></param>
        protected void CopyChannelCounts(int[] channelCounts)
        {
            int index = 0;
            int row = 0;
            int sizeTotal = Math.Min(channelCounts.Length, 16);

            while ((index < sizeTotal) && (row < channels.Length))
            {
                for (int col = 0; col < channels[row].Length; ++col)
                {
                    channels[row][col].counts = (uint)channelCounts[index];
                    ++index;
                    if (index >= sizeTotal)
                    {
                        break;
                    }
                }
                ++row;
            }
        }

        public uint MinCount()
        {
            if (channels == null)
            {
                return 0;
            }
            uint minValue = uint.MaxValue;
            foreach (Channel[] row in channels)
            {
                if (row != null)
                {
                    foreach (Channel channel in row)
                    {
                        if (channel.counts < minValue)
                        {
                            minValue = channel.counts;
                        }
                    }
                }
            }
            if (minValue == uint.MaxValue)
            {
                minValue = 0;
            }
            return minValue;
        }

        public uint MaxCount()
        {
            if (channels == null)
            {
                return 0;
            }
            uint maxValue = 0;
            foreach (Channel[] row in channels)
            {
                if (row != null)
                {
                    foreach (Channel channel in row)
                    {
                        if (channel.counts > maxValue)
                        {
                            maxValue = channel.counts;
                        }
                    }
                }
            }
            return maxValue;
        }

        public virtual void InitializeDistribution(DetectorFormat detectorFormat)
        {
            this.detectorFormat = detectorFormat;

            switch (detectorFormat)
            {
                default:
                case DetectorFormat.Undefined:
                    InitializeUndefinedDistribution(this.sizeOfUndefined);
                    break;
                case DetectorFormat.Symmetric:
                    InitializeSymmetricDistribution();
                    break;
                case DetectorFormat.MC15:
                    InitializeMC15Distribution();
                    break;
                case DetectorFormat.MCSmalls:
                    InitializeMCSmallsDistribution();
                    break;
                case DetectorFormat.NPod:
                    InitializeNPodDistribution();
                    break;
            }
        }

        public void InitializeUndefinedDistribution(int size)
        {
            if (size < 16)
            {
                channels = new Channel[2][];
                channels[0] = new Channel[size];
                channels[1] = new Channel[16 - size];
                InitializeFirstRow(true);
                InitializeSecondRow(true);
            }
            else
            {
                channels = new Channel[1][];
                channels[0] = new Channel[16];
                InitializeFirstRow(true);
            }
        }
        public void InitializeSymmetricDistribution()
        {
            channels = new Channel[2][];
            channels[0] = new Channel[8]; // First row
            channels[1] = new Channel[8]; // Second row

            InitializeFirstRow(true);
            InitializeSecondRow(true);
        }
        public void InitializeMC15Distribution()
        {
            channels = new Channel[4][];
            channels[0] = new Channel[7]; // First row
            channels[1] = new Channel[6]; // Second row
            channels[2] = new Channel[2]; // Third row
            channels[3] = new Channel[1]; // Channel 16

            InitializeFirstRow(true);
            InitializeSecondRow(true);
            InitializeThirdRow(true);
            InitializeChannel16Row(true);
        }
        public void InitializeMCSmallsDistribution()
        {
            channels = new Channel[4][];
            channels[0] = new Channel[6]; // First row
            channels[1] = new Channel[5]; // Second row
            channels[2] = new Channel[4];  // Third Row - Unused channels
            channels[3] = new Channel[1]; // Channel 16

            InitializeFirstRow(true);
            InitializeSecondRow(true);
            InitializeThirdRow(false);  // Don't display the third row.
            InitializeChannel16Row(true);
        }

        public void InitializeNPodDistribution()
        {
            channels = new Channel[3][];
            channels[0] = new Channel[8]; // First row
            channels[1] = new Channel[7]; // Second row
            channels[2] = new Channel[1]; // Channel 16

            InitializeFirstRow(true);
            InitializeSecondRow(true);
            InitializeChannel16Row(true);
        }

        protected void InitializeFirstRow(bool enabled)
        {
            SetTotalWidth();

            // Calculate the padding on each side of the borders
            int plotWidth = (int)((channels[0].Length - 1.0 + widthPercentRow1) * totalWidth);
            int paddingWidth = (border.Width - plotWidth) / 2;

            // How wide are the rectangles
            int width = (int)(widthPercentRow1 * (double)totalWidth);

            // Where to place the X for each rectangle
            int x = border.X + paddingWidth + 1;

            for (int i = 0; i < channels[0].Length; ++i)
            {
                channels[0][i] = new Channel(x, width, 125, i, enabled);
                x += totalWidth;
            }
        }

        protected void InitializeSecondRow(bool enabled)
        {
            if (channels.Length < 2)
            {
                return;
            }

            int width = (int)(widthPercentRow2 * (double)totalWidth);
            int offset = channels[0].Length;
            int row = 1;
            int center = GetCenterOfRow(0);
            int rowWidth = (channels[row].Length - 1) * totalWidth + width;
            int x = center - rowWidth / 2;
            for (int i = 0; i < channels[row].Length; ++i)
            {
                channels[row][i] = new Channel(x, width, 76, i + offset, enabled);
                x += totalWidth;
            }
        }

        protected void InitializeThirdRow(bool enabled)
        {
            if (channels.Length < 3)
            {
                return;
            }
            int width = (int)(widthPercentRow3 * (double)totalWidth);
            int offset = channels[0].Length + channels[1].Length;
            int row = 2;
            if (channels[2].Length == 2)
            {
                // An MC-15
                switch (plotType)
                {
                    default:
                    case PlotType.Undefined:
                    case PlotType.BarPlot:
                        {
                            int x = (channels[1][1].Left + channels[1][1].Right) / 2 - width / 2;
                            channels[row][0] = new Channel(x, width, 76, offset, enabled);
                            ++offset;
                            x = (channels[1][4].Left + channels[1][4].Right) / 2 - width / 2;
                            channels[row][1] = new Channel(x, width, 76, offset, enabled);
                        }
                        break;
                    case PlotType.CirclePlot:
                        {
                            int x = (channels[0][2].Left + channels[0][2].Right) / 2 - width / 2;
                            channels[row][0] = new Channel(x, width, 76, offset, enabled);
                            ++offset;
                            x = (channels[0][4].Left + channels[0][4].Right) / 2 - width / 2;
                            channels[row][1] = new Channel(x, width, 76, offset, enabled);
                        }
                        break;
                }
            }
            else
            {
                // Find the center of row 1
                int center = GetCenterOfRow(0);
                int rowWidth = (channels[row].Length - 1) * totalWidth + width;
                int x = center - rowWidth / 2;
                for (int i = 0; i < channels[row].Length; ++i)
                {
                    channels[row][i] = new Channel(x, width, 76, i + offset, enabled);
                    x += totalWidth;
                }
            }
        }

        protected void InitializeChannel16Row(bool enabled)
        {
            if (channels.Length < 2)
            {
                return;
            }
            // Channel 16 is always the last row
            int row = channels.Length - 1;
            if (row < 1)
            {
                return;
            }
            int width = (int)(widthPercentChannel16 * (double)totalWidth);
            int offset = GetChannelOffset(row);

            // Find the center of row 1
            int center = GetCenterOfRow(0);
            int rowWidth = (channels[row].Length - 1) * totalWidth + width;
            int x = center - rowWidth / 2;
            for (int i = 0; i < channels[row].Length; ++i)
            {
                channels[row][i] = new Channel(x, width, 76, i + offset, enabled);
                x += totalWidth;
            }
        }

        public int GetChannelOffset(int row)
        {
            int rows = channels.Length - 2;
            if (rows < 0)
            {
                return 0;
            }
            int offset = 0;
            for (int i = 0; i < rows; ++i)
            {
                offset += channels[i].Length;
            }
            return offset;
        }
        public void EnableDisableRow(int row, bool enabled)
        {
            if (channels.Length < row)
            {
                return;
            }
            foreach (Channel channel in channels[row])
            {
                channel.enabled = enabled;
            }
        }

        public void EnableDisableChannel16(bool enabled)
        {
            int row = channels.Length - 1;
            if (row < 1)
            {
                return;
            }
            foreach (Channel channel in channels[row])
            {
                channel.enabled = enabled;
            }
        }

        protected void ClearRow(int i)
        {
            if (channels[i] == null)
            {
                return;
            }
            if (channels.Length < i + 1)
            {
                return;
            }
            foreach (Channel channel in channels[i])
            {
                channel.X = -300;
                channel.Y = -300;
                channel.Width = 0;
            }
        }

        /// <summary>
        /// Sets the total width (i.e. increment in pixels)
        /// of how far apart the channels are.
        /// </summary>
        /// <param name="numChannels"></param>
        /// <returns></returns>
        public int SetTotalWidth(int numChannels)
        {
            // What is the actual plotting area
            double numerator = (double)(border.Width - borderPaddingLeft - borderPaddingRight);

            // How wide is each bar including the space between bars
            double denom = (double)numChannels - 1.0 + widthPercentRow1;
            if (denom == 0.0)
            {
                denom = 10.0;
            }
            totalWidth = (int)(numerator / denom);
            return totalWidth;
        }

        public int SetTotalWidth()
        {
            return SetTotalWidth(channels[0].Length);
        }

        public int GetCenterOfRow(int row)
        {
            if (!(row < channels.Length))
            {
                return 0;
            }
            if (channels[row].Length < 1)
            {
                return 0;
            }
            int lastIndex = channels[row].Length - 1;
            return (channels[row][0].Left + channels[row][lastIndex].Right) / 2;
        }


        public uint MedianCount()
        {
            uint[] temp;
            CopyChannelCounts(out temp);
            Array.Sort(temp);
            return temp[temp.Length / 2];

        }
        public void AverageCounts(out double average, out double stdev)
        {
            uint sum = 0;
            uint number = 0;
            foreach (Channel[] row in channels)
            {
                foreach (Channel channel in row)
                {
                    sum += channel.counts;
                    ++number;
                }
            }
            average = (double)sum / (double)number;
            stdev = 0.0;
            foreach (Channel[] row in channels)
            {
                foreach (Channel channel in row)
                {
                    stdev += Math.Abs(average - (double)channel.counts);
                }
            }

            stdev /= (double)(number - 1);
        }

        /// <summary>
        /// Sets whether to display channel16.
        /// </summary>
        /// <param name="enable"></param>
        public void DisplayChannel16(bool enable)
        {
            showChannel16 = enable;
            int row = channels.Length - 1;
            if (row < 2)
            {
                return;
            }
            foreach (Channel channel in channels[row])
            {
                channel.enabled = enable;
            }
        }

        public bool DisplayChannel16()
        {
            return showChannel16;
        }

    }
}
