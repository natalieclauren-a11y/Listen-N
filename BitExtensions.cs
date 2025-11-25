using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;

namespace Vf61Gui
{
    public static partial class BitExtensions
    {
        /// <summary>
        /// Set the bit at position to 1.
        /// </summary>
        /// <param name="value"></param>
        /// <param name="position"></param>
        /// <returns></returns>
        public static int SetBitTo1(this int value, int position)
        {
            return value |= (1 << position);
        }

        /// <summary>
        /// Set the bit at position to 0.
        /// </summary>
        /// <param name="value"></param>
        /// <param name="position"></param>
        /// <returns></returns>
        public static int SetBitTo0(this int value, int position)
        {
            return value & ~(1 << position);
        }

        /// <summary>
        /// Is the bit at position set to 1.
        /// </summary>
        /// <param name="value"></param>
        /// <param name="position"></param>
        /// <returns></returns>
        public static bool IsBitSetTo1(this int value, int position)
        {
            return (value & (1 << position)) != 0;
        }

        /// <summary>
        /// Is the bit at position set to 0.
        /// </summary>
        /// <param name="value"></param>
        /// <param name="position"></param>
        /// <returns></returns>
        public static bool IsBitSetTo0(this int value, int position)
        {
            return !IsBitSetTo1(value, position);
        }
    }
}
