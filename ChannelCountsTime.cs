using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;

namespace Vf61Gui
{
    /// <summary>
    /// Stores the counts in each channel (size == 32), what time the data was recorded, and the total sum of counts.
    /// </summary>
    public class ChannelCountsTime
    {
        /// <summary>
        /// What time was the data stored?
        /// </summary>
        public DateTime dateTime;

        /// <summary>
        /// What are the counts in each channel?
        /// </summary>
        public uint[] channelCounts = new uint[32];

        /// <summary>
        /// The total sum of counts in the array channelCounts[]
        /// </summary>
        public uint[] sum = new uint[3];    //sum[0] -> first 16 channels, sum[1] -> last 16 channels, sum[2] -> all channels

        /// <summary>
        /// Constructor with default values. 
        /// Time is truncated to the nearest second.
        /// </summary>
        public ChannelCountsTime() : this(DateTime.Now) {}

        /// <summary>
        /// Constructor with a given DateTime.
        /// The DateTime is not truncated to the nearest second.
        /// </summary>
        /// <param name="dateTime"></param>
        public ChannelCountsTime(DateTime dateTime)
        {
            this.dateTime = dateTime;
            RoundUpTime();
        }

        /// <summary>
        /// Constructor initializing channelCounts to values.
        /// DateTime is truncated to the nearest second.
        /// </summary>
        /// <param name="values"></param>
        public ChannelCountsTime(uint[] values) : this(DateTime.Now, values) {}

        /// <summary>
        /// Constructor initializing DateTime to the provided dateTime and the channelCounts to values.
        /// </summary>
        /// <param name="dateTime"></param>
        /// <param name="values"></param>
        public ChannelCountsTime(DateTime dateTime, uint[] values)
        {
            this.dateTime = dateTime;
            RoundUpTime();
            values.CopyTo(this.channelCounts, 0);
            this.Sum();
        }

        /// <summary>
        /// Sums up the channelCounts for first 16 channels, the second 16 channels and all the channels.
        /// </summary>
        /// <returns></returns>
        public uint[] Sum()
        {
            sum[0] = this.channelCounts.Sum(0, 16);
            sum[1] = this.channelCounts.Sum(16, 32);
            sum[2] = sum[0] + sum[1];
            return sum;
        }

        /// <summary>
        /// Rounds up the dateTime to the next second.
        /// </summary>
        public void RoundUpTime()
        {
            this.dateTime.AddMilliseconds(this.dateTime.Millisecond - 1000);
        }

    }
}
