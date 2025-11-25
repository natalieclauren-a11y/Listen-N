using System;
using System.Drawing;
using System.Linq;
using System.Collections.Generic;
using System.Text;
//using Microsoft.WindowsCE.Forms;
using System.Drawing.Imaging;
using static System.Net.Mime.MediaTypeNames;
using MultiPass;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Diagnostics.Eventing.Reader;



namespace Vf61Gui
{
    public static class UtilitiesVf61Gui
    {
        /// <summary>
        /// A null coalescing operator extension to invoke an event.
        /// </summary>
        /// <remarks>
        /// <para> .Net Framework 3.5 does not have the null coalescing operators ?. or ??. This is a work-around extension.</para>
        /// <para> If this is moved back to Vf61Gui you will need to replace the null coalescing operator with Solicit.</para>
        /// </remarks>
        /// <param name="eventHandler"></param>
        /// <param name="sender"></param>
        public static void Solicit(this EventHandler<EventArgs> eventHandler, object sender, EventArgs e)
        {
            if (eventHandler != null)
            {
                eventHandler.Invoke(sender, e);
            }
        }

        /// <summary>
        /// A null coalescing operator extension to invoke an event.
        /// </summary>
        /// <remarks>
        /// <para> .Net Framework 3.5 does not have the null coalescing operators ?. or ??. This is a work-around extension.</para>
        /// <para> If this is moved back to Vf61Gui you will need to replace the null coalescing operator with Solicit.</para>
        /// </remarks>
        /// <param name="eventHandler"></param>
        /// <param name="sender"></param>
        public static void Solicit(this EventHandler<EventArgs> eventHandler, object sender)
        {
            if ((eventHandler != null) && (sender != null))
            {
                eventHandler.Solicit(sender, new EventArgs());
            }
        }

        //public static void Solicit(this Vf61Gui.DelegateVf61Gui delegateVf61Gui, object sender, EventArgs e)
        //{
        //    if (delegateVf61Gui != null)
        //    {
        //        delegateVf61Gui(sender, e);
        //    }
        //}

        //public static void Solicit(this Vf61Gui.DelegateVf61Gui delegateVf61Gui, object sender)
        //{
        //    if (delegateVf61Gui != null)
        //    {
        //        delegateVf61Gui(sender, null);
        //    }
        //}


        /// <summary>
        ///     Create the rotated font using a LOGFONT structure.  
        ///     Important notes:
        ///         a) rotation angle is in 1/10's of a degree
        ///         b) orientation and escapement values are the same on
        ///            mobile devices.
        /// </summary>
        /// <seealso cref="https://learn.microsoft.com/en-us/previous-versions/847yht23(v=vs.100)"/>
        //public static Font CreateRotatedFont(string faceName, int height, int degrees)
        //{
        //    LogFont lf = new LogFont();

        //    // scale a 18 point font for current screen DPI
        //    //lf.Height = (int)(-18f * g.DpiY / 112);
        //    //lf.Height = (int)(-16f * g.DpiY / POINTSPERINCH);
        //    lf.Height = height;
        //    lf.Width = 0;

        //    // rotation angle in tenths of degrees
        //    lf.Escapement = degrees * 10;

        //    // Orientation == Escapement for mobile device OS
        //    lf.Orientation = lf.Escapement;
        //    lf.Weight = LogFontWeight.Normal;
        //    //lf.Weight = 0;
        //    //lf.Italic = 0;
        //    //lf.Underline = 0;
        //    //lf.StrikeOut = 0;
        //    lf.CharSet = LogFontCharSet.Default;
        //    lf.OutPrecision = LogFontPrecision.Default;
        //    lf.ClipPrecision = LogFontClipPrecision.Default;
        //    lf.Quality = LogFontQuality.ClearType;
        //    lf.PitchAndFamily = LogFontPitchAndFamily.Default;
        //    //lf.FaceName = "Tahoma";
        //    lf.FaceName = faceName;

        //    return Font.FromLogFont(lf);
        //}

        /// <summary>
        /// Returns nice gridline intervals.
        /// </summary>
        /// <example>
        /// static void Main()
        /// {
        ///     // Dummy code to show a usage example.
        ///     var minimumValue = data.Min();
        ///     var maximumValue = data.Max();
        ///     var results = GetScaleDetails(minimumValue, maximumValue);
        ///     chart.YAxis.MinValue = results.Item1;
        ///     chart.YAxis.MaxValue = results.Item2;
        ///     chart.YAxis.Step = results.Item3;
        /// }
        /// </example>
        /// <seealso cref="https://stackoverflow.com/questions/237220/tickmark-algorithm-for-a-graph-axis"/>
        /// <param name="value_min"></param>
        /// <param name="value_max"></param>
        /// <param name="value_min_positive">value_min must be zero or a positive number.</param>
        /// <param name="use_min_maxs_as_range">If true then the value_min and value_max will be used as the range.</param>
        /// <param name="step_count">Target number of values to be displayed on the Y axis.</param>
        /// <returns>std::tuple&lt;min_scale_value, max_scale_value, gridline_step_size&gt;</returns>
        public static void CalculateScaleDetails(double value_min,
            double value_max,
            bool value_min_positive,
            bool use_min_maxs_as_range,
            int step_count,
            out double min_scale_value,
            out double max_scale_value,
            out double gridline_step_size)
        {
            if (value_min == value_max)
            {
                min_scale_value = 0.95 * value_min;
                max_scale_value = 1.05 * value_max;
                gridline_step_size = 0.11 * value_min;
                return;
            }

            if (use_min_maxs_as_range)
            {

            }
            else
            {
                // Minimal increment to avoid round extreme values to be on the edge of the chart
                double epsilon = (value_max - value_min) / 1e6;
                value_max += epsilon;
                value_min -= epsilon;
                if (value_min_positive)
                {
                    //value_min = std::max<double>(0.0, value_min);
                    value_min = Math.Max(0.0, value_min);
                }
            }

            double range = value_max - value_min;

            // First approximation
            double roughStep = range / (double)(step_count - 1);

            // Set best step for the range
            //choose one of the following setpsizes
            //const double goodNormalizedSteps[] = { 1, 1.5, 2, 2.5, 5, 7.5, 10 }; // keep the 10 at the end
            // const double goodNormalizedSteps[] = { 1, 2, 5, 10 }; // keep the 10 at the end
            double[] goodNormalizedSteps = new double[] { 1, 1.5, 2, 2.5, 5, 7.5, 10 };

            // Normalize rough step to find the normalized one that fits best
            //double stepPower = std::pow(10, -std::floor(std::log10(std::abs(roughStep))));
            double stepPower = Math.Pow(10.0, -Math.Floor(Math.Log10(Math.Abs(roughStep))));
            double normalizedStep = roughStep * stepPower;
            //const size_t I = std::extent < decltype(goodNormalizedSteps) >::value;
            //size_t i = 0;
            int I = goodNormalizedSteps.Length;
            int i = 0;
            for (i = 0; i < I; ++i)
            {
                if (goodNormalizedSteps[i] >= normalizedStep)
                {
                    break;
                }
            }
            i = Math.Min(i, I - 1);
            double goodNormalizedStep = goodNormalizedSteps[i];
            double step = goodNormalizedStep / stepPower;

            // Determine the scale limits based on the chosen step.
            double scale_max = Math.Ceiling(value_max / step) * step;
            double scale_min = Math.Floor(value_min / step) * step;

            min_scale_value = scale_min;
            max_scale_value = scale_max;
            gridline_step_size = step;

            //return std::tuple<double, double, double>(scale_min, scale_max, step);
        }

        public static bool IsOdd(this int value)
        {
            return (value % 2 == 0);
        }

        public static bool IsEven(this int value)
        {
            return (value % 2 == 1);
        }

        /// <summary>
        /// Returns the middle in (X, Y) of the supplied Rectangle.
        /// </summary>
        /// <param name="point"></param>
        /// <returns></returns>
        public static Point Center(this Rectangle rect)
        {
            return new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        }
        public static bool ToBool(this int value)
        {
            return (value == 0) ? false : true;
        }

        public static bool ToBool(this string str)
        {
            try
            {
                return Convert.ToBoolean(str);
            }
            catch
            {
                return false;
            }
        }
        public static double Line(double x, double m, double b)
        {
            return m * x + b;
        }

        //public static bool InRange<T>(T value, T min, T max)
        //    where T : System.IComparable<T>
        //{
        //    if (value.CompareTo(min) < 0)
        //    {
        //        return false;
        //    }
        //    else if (value.CompareTo(max) > 0)
        //    {
        //        return false;
        //    }
        //    return true;
        //}

        static public Size Transform(this Size size)
        {
            return new Size(size.Height, size.Width);
        }

        static public void Swap<T>(ref T a, ref T b)
        {
            T temp = a;
            a = b;
            b = temp;
        }

        /// <summary>
        /// Clamps the value to be within [min, max].
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="value"></param>
        /// <param name="min"></param>
        /// <param name="max"></param>
        /// <returns></returns>
        static public T Clamp<T>(this T value, T min, T max) where T : System.IComparable<T>
        {
            T result = value;
            if (min.CompareTo(max) > 0)
            {
                Swap(ref min, ref max);
            }
            if (value.CompareTo(max) > 0)
            {
                return max;
            }
            else if (value.CompareTo(min) < 0)
            {
                return min;
            }
            return result;
        }

        /// <summary>
        /// Determines if min &lt; value &lt; max.
        /// [exclusive, exclusive]
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="value"></param>
        /// <param name="min"></param>
        /// <param name="max"></param>
        /// <returns></returns>
        static public bool InRangeEE<T>(this T value, T min, T max) where T : System.IComparable<T>
        {
            if (min.CompareTo(max) > 0)
            {
                Swap(ref min, ref max);
            }
            if (value.CompareTo(max) > 0)
            {
                return false;
            }
            if (value.CompareTo(min) < 0)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Determines if min &lt; value &gt; max. If value &lt; min returns -1, if value &gt; max returns 1, if min &lt; value &gt; max returns 0;
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="value"></param>
        /// <param name="min"></param>
        /// <param name="max"></param>
        /// <returns></returns>
        static public int CompareEE<T>(this T value, T min, T max) where T : System.IComparable<T>
        {
            if (min.CompareTo(max) > 0)
            {
                Swap(ref min, ref max);
            }
            if (value.CompareTo(max) > 0)
            {
                return 1;
            }
            if (value.CompareTo(min) < 0)
            {
                return -1;
            }
            return 0;
        }
        /// <summary>
        /// Determines if min &lt;= value &lt; max.
        /// (inclusive, exclusive]
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="value"></param>
        /// <param name="min"></param>
        /// <param name="max"></param>
        /// <returns></returns>
        static public bool InRangeIE<T>(this T value, T min, T max) where T : System.IComparable<T>
        {
            if (min.CompareTo(max) > 0)
            {
                Swap(ref min, ref max);
            }
            if (value.CompareTo(max) > 0)
            {
                return false;
            }
            if (value.CompareTo(min) <= 0)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Determines if min &lt; value &lt;= max.
        /// (inclusive, exclusive]
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="value"></param>
        /// <param name="min"></param>
        /// <param name="max"></param>
        /// <returns></returns>
        static public bool InRangeEI<T>(this T value, T min, T max) where T : System.IComparable<T>
        {
            if (min.CompareTo(max) > 0)
            {
                Swap(ref min, ref max);
            }
            if (value.CompareTo(max) >= 0)
            {
                return false;
            }
            if (value.CompareTo(min) < 0)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Determines if min &lt;= value &lt;= max.
        /// (inclusive, exclusive]
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="value"></param>
        /// <param name="min"></param>
        /// <param name="max"></param>
        /// <returns></returns>
        static public bool InRangeII<T>(this T value, T min, T max) where T : System.IComparable<T>
        {
            if (min.CompareTo(max) > 0)
            {
                Swap(ref min, ref max);
            }
            if (value.CompareTo(max) >= 0)
            {
                return false;
            }
            if (value.CompareTo(min) <= 0)
            {
                return false;
            }
            return true;
        }

        static public bool CheckIndex<T>(this IList<T> list, int index)
        {
            return (index < list.Count) && (index > -1);
        }


        static public Point MidPoint(this Rectangle rect)
        {
            return new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        }

        /// <summary>
        /// Finds the point (X only) where to place a second rectangle so that the centers are aligned
        /// </summary>
        /// <param name="rect"></param>
        /// <param name="widht"></param>
        /// <returns></returns>
        static public Point AlignCenterX(this Rectangle primary, Rectangle secondary)
        {
            return new Point()
            {
                X = primary.X + (primary.Width - secondary.Width) / 2,
                Y = secondary.Y
            };
        }
        static public double Normal(double x)
        {
            return Normal(x, 0.0, 1.0);
        }
        static public double Normal(double x, double mu)
        {
            return Normal(x, mu, 1.0);
        }
        static public double Normal(double x, double mu, double sigma)
        {
            return Math.Exp(-Math.Pow((mu - x) / sigma, 2.0));
        }
        static public void DrawLine(this Graphics graphics, Pen pen, Point pt1, Point pt2)
        {
            graphics.DrawLine(pen, pt1.X, pt1.Y, pt2.X, pt2.Y);
        }

        public static string[] SplitAtFirstDelim(this string str, char[] delimitors)
        {
            // Work with a string initially because it's more efficient for adding values.
            List<string> result = new List<string>();
            int index = str.IndexOfAny(delimitors);
            if ((index < 0) || (index >= str.Length))
            {
                return new string[] {
                    str,
                    ""
                };
            }
            result.Add(str.Substring(0, index));
            result.Add(str.Substring(index + 1));
            // Convert to an array
            return result.ToArray();
        }

        /// <summary>
        /// Returns the index of all bytes that equal b in the array.
        /// </summary>
        /// <param name="array"></param>
        /// <param name="b"></param>
        /// <returns></returns>
        public static int[] IndexOfAll(this byte[] array, byte b)
        {
            List<int> splits = new List<int>();
            for(int i = 0; i < array.Length; i++)
            {
                if(array[i] == b)
                {
                    splits.Add(i);
                }
            }
            return splits.ToArray();
        }

        /// <summary>
        /// Splits a byte array at each byte b.
        /// </summary>
        /// <param name="array"></param>
        /// <param name="b"></param>
        /// <returns></returns>
        public static byte[][] Split(this byte[] array, byte b)
        {
            int[] indices = array.IndexOfAll(b);
            List<byte[]> splits = new List<byte[]>();
            if(indices.Length == 0)
            {
                // There's nothing to split on.
                // Just return the array.
                // It needs all of this fluff to ensure Trim() works.
                byte[] buffer = new byte[array.Length];
                Buffer.BlockCopy(array, 0, buffer, 0, buffer.Length);
                splits.Add(buffer.Trim());
                return splits.ToArray();
            }

            int oldI = 0;
            foreach(int i in indices)
            {
                byte[] buffer = new byte[i - oldI];
                Buffer.BlockCopy(array, oldI, buffer, 0, buffer.Length);
                splits.Add(buffer.Trim());
                //splits.Add(buffer);
                oldI = i + 1;
            }
            // Copy from the last delimiter to the end
            if(oldI < array.Length)
            {
                byte[] buffer = new byte[array.Length - oldI];
                Buffer.BlockCopy(array, oldI, buffer, 0, buffer.Length);
                splits.Add(buffer.Trim());
                //splits.Add(buffer);
            }

            return splits.ToArray();
        }

        public static byte[][] ToByteArray(this List<string> array)
        {
            byte[][] result = new byte[array.Count][];
            for (int i = 0; i < array.Count; ++i) 
            {
                result[i] = Encoding.ASCII.GetBytes(array[i]);
            }
            return result;
        }
        public static byte[][][] ToByteArray(this List<List<string>> array)
        {
            byte[][][] result = new byte[array.Count][][];
            for(int i = 0; i < array.Count; ++i)
            {
                result[i] = ToByteArray(array[i]);
            }
            return result;
        }
        public static List<string> ToStringList(this byte[][] array)
        {
            List<string> list = new List<string>();
            for (int i = 0; i < array.Length; ++i)
            {
                list.Add(Encoding.ASCII.GetString(array[i]));
            }
            return list;
        }

        public static List<List<string>> ToStringList(this byte[][][] array)
        {
            List<List<string>> list = new List<List<string>>();
            for(int i = 0; i < array.Length; ++i)
            {
                list.Add(array[i].ToStringList());
            }
            return list;
        }

        /// <summary>
        /// Searches an array to determine if a search array is in it.
        /// </summary>
        /// <param name="array"></param>
        /// <param name="search"></param>
        /// <returns></returns>
        //public static bool Contains(this byte[] data, byte[] pattern, int bytesRead)
        //{
        //    return data.IndexOf(pattern, 0, bytesRead) > -1;
        //}

        public static int GetLongestKeyLength<T>(this Dictionary<string, T> dictionary)
        {
            int length = 0;
            foreach (var dict in dictionary)
            {
                if (dict.Key.Length > length)
                {
                    length = dict.Key.Length;
                }
            }
            return length;
        }

        public static int GetShortestKeyLength<T>(this Dictionary<string, T> dictionary)
        {
            if (dictionary.Count < 1)
            {
                return 0;
            }
            int length = int.MaxValue;
            foreach (var dict in dictionary) {
                if (length < dict.Key.Length)
                {
                    length = dict.Key.Length;
                }
            }
            return length;
        }

        public static int GetLongestReplyLength(this Dictionary<string,ExpectedReply> dictionary)
        {
            int length = 0;
            foreach(var dict in dictionary)
            {
                foreach (string reply in dict.Value.replies)
                {
                    if (reply.Length > length)
                    {
                        length = reply.Length;
                    }
                }
            }
            return length;
        }
        public static byte[] commandsSeparator = Encoding.ASCII.GetBytes(" =");
        public static byte[] runDescription = Encoding.ASCII.GetBytes("runDescription");
        public static byte[] unrecognizedResult = Encoding.ASCII.GetBytes("unrecognized");

        public static byte[][] ReturnCommands(this List<byte[]> data)
        {
            byte[][] results = new byte[data.Count][];
            for(int i = 0; i < data.Count; ++i )
            {
                results[i] = new byte[data[i].Length];
                Buffer.BlockCopy(data[i], 0, results[i], 0, data[i].Length);
            }
            return results;
        }

        public static int IndexOfInReverse(this byte[] data, byte search, int index)
        {
            for(int i = index; i > -1; --i)
            {
                if (data[i] == search)
                {
                    return i;
                }
            }
            return -1;
        }

        public static byte[][] GetCommands(this byte[] data, int bytesRead)
        {
            // splits contains all of the comma seperated commands
            // The first one should have a byte pattern " = ".
            // ***** Things to watch out for *****
            // 1) A command coming at the end of NetSaveFile
            //      This shouldn't happen here.
            //      This should be taken care of when processing NetSavefile - Transferring data
            // 2) a NetSaveFile being a command in the list.
            //      Maybe not? This should get processed normally?
            //      Probably need to add "ListModeDataFileVersion" to accepted responses?
            // 3) runDescription
            //      The results from this will 

            int iEnd = data.IndexOfPattern(commandsSeparator, 0, bytesRead);
            int iStart = 0;
            if (iEnd < 0)
            {
                // There is no commandsSeparator
                // Assume this is only binary data
                return Array.Empty<byte[]>();
            }

#if DEBUG
            string debug_string = Encoding.ASCII.GetString(data);
#endif
            // I see the sequence " = " in the reply.
            // I'm going to assume this is only replies and not binary data
            if (Array.IndexOf(data, (byte)',', iStart, bytesRead - iStart) < 0)
            {
                // No comma was found.
                // This is the only command to send back.
                return ReturnCommands(new List<byte[]> { data });
            }


            // Need to figure out which commands can give headaches.
            //  e.g. Maybe get confused with " ="
            // 1) NetSaveFile => Possibly with " = " in the filename?
            //      Not likely: 
            // 2) runDescription => Absolutely
            // 3) unknown => doesn't have a " =" in the response.
            //      Could also be in runDescription


            List<byte[]> results = new List<byte[]>();
            // Look for additional " = "
            while (iEnd < bytesRead)
            {
                int length = bytesRead - iStart;
                int iRunDescription = data.IndexOfPattern(runDescription, iStart, length);
                if (iRunDescription > -1)
                {
                    // runDescription was found
                }
                else
                {
                    // This is not runDescription
                    iEnd = Math.Min(iEnd + commandsSeparator.Length, bytesRead);   // compensate for the size of " = ".
                    iEnd = data.IndexOfPattern(commandsSeparator, iEnd, bytesRead - iEnd);
                    if (iEnd < 0)
                    {
                        // End of the data.
                        iEnd = bytesRead;
                        length = iEnd - iStart;
                        byte[] buffer = new byte[length];
                        Buffer.BlockCopy(data, iStart, buffer, 0, length);
#if DEBUG
                        string str_Temp = Encoding.ASCII.GetString(buffer);
#endif
                        results.Add(buffer);
                        iStart = iEnd;
                    }
                    else
                    {
                        // There's another command.
                        //int i = data.IndexOfInReverse((byte)',', iEnd);   // Look for a comma
                        int i = data.IndexOfInReverse((byte)' ', iEnd);     // Look for a space
                        if (i < 0)
                        {
                            // No comma/space was found.
                            // Not sure what to do right now.
                            int test_test = 0;
                        }
                        length = i - iStart;
                        if (length < 1)
                        {
                            throw new Exception("GetCommands:Invalid Index");
                        }
                        byte[] buffer = new byte[length];
                        Buffer.BlockCopy(data, iStart, buffer, 0, length);
#if DEBUG
                        string str_Temp = Encoding.ASCII.GetString(buffer);
#endif
                        results.Add(buffer);
                        // iEnd is incremented at the start of the loop.
                        iStart = Math.Min(++i, bytesRead);  // Increment iStart to compensate for the size of ','
                    }

                }
            }
            return ReturnCommands(results);
        }

        public static string GetRunDescription(this byte[] data, int start, int bytesRead, ref int end)
        {
            return "";
        }
        /// <summary>
        /// Removes the white spaces from each line in a jagged array of bytes[][].
        /// </summary>
        /// <param name="array"></param>
        /// <returns></returns>
        public static byte[][] Trim(this byte[][] array)
        {
            byte[][] result = new byte[array.Length][];
            for(int i = 0; i<array.Length; ++i)
            {
                result[i] = array[i].Trim();
            }
            return result;
        }

        public static byte[] whitespaces = new byte[] { (byte)' ', (byte)'\n', (byte)'\r', (byte)'\t' };

        //Removes the whitespaces from an array of bytes.
        public static byte[] Trim(this byte[] array)
        {
            // Find the first non whitespace
            int start;
            int stop;
            for(start = 0; start < array.Length; ++start)
            {
                if(!array[start].IsWhiteSpace())
                {
                    break;
                }
            }
            // Find the last non whitespace
            if (array[array.Length - 1].IsWhiteSpace())
            {
                for (stop = array.Length - 1; stop > start; --stop)
                {
                    if (!array[stop].IsWhiteSpace())
                    {
                        ++stop;
                        break;
                    }
                }
            }
            else
            {
                stop = array.Length;
            }

            if(start < stop)
            {
                int length = stop - start;
                byte[] result = new byte[length];
                Buffer.BlockCopy(array, start, result, 0, length);
                return result;
            } else
            {
                return Array.Empty<byte>();
            }
        }

        /// <summary>
        /// Determines if the byte is considered a whitespace.
        /// </summary>
        /// <param name="b"></param>
        /// <returns></returns>
        public static bool IsWhiteSpace(this byte b)
        {
            foreach (byte ws in whitespaces)
            {
                if (b == ws)
                {
                    return true;
                }
            }
            return false;
        }

        public static int LastIndexOf(this byte[] data, byte[] pattern)
        {
            // https://gist.github.com/ramonsmits/0837dc27ffcf5b73eec53ed7965068f4
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (pattern == null) throw new ArgumentNullException(nameof(pattern));
            if (pattern.Length > data.Length) return -1;

            int cycles = data.Length - pattern.Length + 1;
            int patternIndex;
            for (int dataIndex = cycles; dataIndex > 0; dataIndex--)
            {
                if (data[dataIndex] != pattern[0]) continue;
                for (patternIndex = pattern.Length - 1; patternIndex >= 1; patternIndex--) if (data[dataIndex + patternIndex] != pattern[patternIndex]) break;
                if (patternIndex == 0) return dataIndex;
            }
            return -1;
        }


        /// <summary>
        /// Searchs the byte array data for the given pattern.
        /// </summary>
        /// <param name="data">byte[] the data to be searched.</param>
        /// <param name="pattern">byte[] The pattern to be searched for.</param>
        /// <param name="startIndex">int The starting index in data to start searching.</param>
        /// <param name="length">int The length of data to search</param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        public static int IndexOfPattern(this byte[] data, byte[] pattern, int startIndex, int length)
        {
            // https://gist.github.com/ramonsmits/0837dc27ffcf5b73eec53ed7965068f4
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (pattern == null) throw new ArgumentNullException(nameof(pattern));
            //if (pattern.Length > data.Length) return -1;
            length = Math.Min(length, data.Length); // Ensure there's no accidental overruns.
            if (pattern.Length > length) return -1;

            //int cycles = data.Length - pattern.Length + 1;
            int cycles = length - pattern.Length + 1;
            int patternIndex;
            for (int dataIndex = startIndex; dataIndex < cycles; dataIndex++)
            {
                if (data[dataIndex] != pattern[0]) continue;
                for (patternIndex = pattern.Length - 1; patternIndex >= 1; patternIndex--)
                {
                    if (data[dataIndex + patternIndex] != pattern[patternIndex])
                    {
                        break;
                    }
                }
                if (patternIndex == 0) return dataIndex;
            }
            return -1;
        }

        public static string TrimAtNull(this string path)
        {
            if (path.Length == 0)
            {
                return "";
            }
            int index = path.IndexOf('\0');
            if (index < 0)
            {
                // A null terminated character is not found.
                return path;
            }

            return path.Substring(0, index);
        }

        /// <summary>
        /// Sums all of the values in the array values.
        /// Array.Sum() does not have an overloaded function for uint[].
        /// </summary>
        /// <param name="values"></param>
        /// <returns></returns>
        public static uint Sum(this uint[] values)
        {
            if (values == null)
            {
                return 0u;
            }
            uint sum = 0u;
            foreach (uint value in values)
            {
                sum += value;
            }
            return sum;
        }

        /// <summary>
        /// Sums all of the values in the array values starting at startIndex and going to the end of the array.
        /// Array.Sum() does not have an overloaded function for uint[].
        /// </summary>
        /// <param name="values"></param>
        /// <returns></returns>
        public static uint Sum(this uint[] values, int startIndex)
        {
            return values.Sum(startIndex, values.Length);
        }

        /// <summary>
        /// Sums all of the values in the array values starting at startIndex and ending at endIndex.
        /// Array.Sum() does not have an overloaded function for uint[].
        /// </summary>
        /// <param name="values"></param>
        /// <returns></returns>
        public static uint Sum(this uint[] values, int startIndex, int endIndex)
        {
            if (values == null)
            {
                return 0u;
            }
            uint sum = 0u;
            for (int index = startIndex; index < endIndex; ++index)
            {
                sum += values[index];
            }
            return sum;
        }



        /// <summary>
        /// Sums all of the values in the array values.
        /// Array.Sum() does not have an overloaded function for ulong[].
        /// </summary>
        /// <param name="values"></param>
        /// <returns></returns>
        public static ulong Sum(this ulong[] values)
        {
            if (values == null)
            {
                return 0u;
            }
            ulong sum = 0u;
            foreach (ulong value in values)
            {
                sum += value;
            }
            return sum;
        }

        /// <summary>
        /// Sums all of the values in the array values starting at startIndex and going to the end of the array.
        /// Array.Sum() does not have an overloaded function for ulong[].
        /// </summary>
        /// <param name="values"></param>
        /// <returns></returns>
        public static ulong Sum(this ulong[] values, int startIndex)
        {
            return values.Sum(startIndex, values.Length);
        }

        /// <summary>
        /// Sums all of the values in the array values starting at startIndex and ending at endIndex.
        /// Array.Sum() does not have an overloaded function for ulong[].
        /// </summary>
        /// <param name="values"></param>
        /// <returns></returns>
        public static ulong Sum(this ulong[] values, int startIndex, int endIndex)
        {
            if (values == null)
            {
                return 0u;
            }
            ulong sum = 0u;
            for (int index = startIndex; index < endIndex; ++index)
            {
                sum += values[index];
            }
            return sum;
        }

        /// <summary>
        /// Sums all of the values in the array values.
        /// Array.Sum() does not have an overloaded function for long[].
        /// </summary>
        /// <param name="values"></param>
        /// <returns></returns>
        public static long Sum(this long[] values)
        {
            if (values == null)
            {
                return 0u;
            }
            long sum = 0u;
            foreach (long value in values)
            {
                sum += value;
            }
            return sum;
        }

        /// <summary>
        /// Sums all of the values in the array values starting at startIndex and going to the end of the array.
        /// Array.Sum() does not have an overloaded function for long[].
        /// </summary>
        /// <param name="values"></param>
        /// <returns></returns>
        public static long Sum(this long[] values, int startIndex)
        {
            return values.Sum(startIndex, values.Length);
        }

        /// <summary>
        /// Sums all of the values in the array values starting at startIndex and ending at endIndex.
        /// Array.Sum() does not have an overloaded function for ulong[].
        /// </summary>
        /// <param name="values"></param>
        /// <returns></returns>
        public static long Sum(this long[] values, int startIndex, int endIndex)
        {
            if (values == null)
            {
                return 0u;
            }
            long sum = 0u;
            for (int index = startIndex; index < endIndex; ++index)
            {
                sum += values[index];
            }
            return sum;
        }





        /// <summary>
        /// Sums all of the values in the array values.
        /// Array.Sum() does not have an overloaded function for uint[].
        /// </summary>
        /// <param name="values"></param>
        /// <returns></returns>
        public static int Sum(this int[] values)
        {
            if (values == null)
            {
                return 0;
            }
            int sum = 0;
            foreach (int value in values)
            {
                sum += value;
            }
            return sum;
        }
        /// <summary>
        /// Sums all of the values in the array values starting at startIndex and going to the end of the array.
        /// Array.Sum() does not have an overloaded function for uint[].
        /// </summary>
        /// <param name="values"></param>
        /// <returns></returns>
        public static int Sum(this int[] values, int startIndex)
        {
            return values.Sum(startIndex, values.Length);
        }
        /// <summary>
        /// Sums all of the values in the array values starting at startIndex and ending at endIndex.
        /// Array.Sum() does not have an overloaded function for uint[].
        /// </summary>
        /// <param name="values"></param>
        /// <returns></returns>
        public static int Sum(this int[] values, int startIndex, int endIndex)
        {
            if (values == null)
            {
                return 0;
            }
            int sum = 0;
            for (int index = startIndex; index < endIndex; ++index)
            {
                sum += values[index];
            }
            return sum;
        }


        /// <summary>
        /// Converts a value to 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="value"></param>
        /// <returns></returns>
        public static string ToFileSizeString(this long value)
        {
            if (value == 0)
            {
                return "0 B";
            }
            ulong magnitude = (uint)Math.Floor(Math.Log((double)value) / Math.Log(1024.0));
            magnitude = magnitude.Clamp(0ul, 8ul);
            long scale = (long)Math.Pow(1024.0, (double)magnitude);
            long value_scaled = value / scale;
            string message = value_scaled.ToString("#,##0");
            switch (magnitude)
            {
                case 0:
                    message += " B";
                    break;
                case 1:
                    message += " KiB";
                    break;
                case 2:
                    message += " MiB";
                    break;
                case 3:
                    message += " GiB";
                    break;
                case 4:
                    message += " TiB";
                    break;
                case 5:
                    message += " PiB";
                    break;
                case 6:
                    message += " EiB";
                    break;
                case 7:
                    message += " ZiB";
                    break;
                default:
                case 8:
                    message += " YiB";
                    break;
            }
            return message;
        }

        public static string SiUnit(this int exponent, string unit)
        {
            return SiUnit(exponent) + unit;
        }

        public static string SiUnit(this int exponent)
        {
            int magnitude = ((exponent < 0) ? (exponent - 2) : exponent) / 3;
            switch (magnitude)
            {
                case -8: return "y";
                case -7: return "z";
                case -6: return "a";
                case -5: return "f";
                case -4: return "p";
                case -3: return "n";
                case -2: return "µ";
                case -1: return "m";
                case 0: return "";
                case 1: return "k";
                case 2: return "M";
                case 3: return "G";
                case 4: return "T";
                case 5: return "P";
                case 6: return "E";
                case 7: return "Z";
                case 8: return "Y";
            }
            return (magnitude < 0) ? "y" : "Y";
        }

        public static string[] SiUnitArray(this double value, int places, string unit)
        {
            string[] text = SiUnitArray(value, 0.0, places, unit);
            return new string[] { text[0], text[3] };
        }

        public static string SiUnit(this double value, int places, string unit)
        {
            string[] text = SiUnitArray(value, places, unit);
            return string.Join(" ", text);
        }

        public static string[] SiUnitArray(this double value, double stdev, int places, string unit)
        {
            if (double.IsNaN(value) ||
                double.IsInfinity(value))
            {
                return new string[] { value.ToString(), "±", value.ToString(), unit };
            }

            if (places < 1)
            {
                places = 1;
            }
            value = Math.Abs(value);
            if (value > 0.0)
            {
                int exponent = (int)Math.Floor(Math.Log10(value));
                int magnitude = ((exponent < 0) ? (exponent - 2) : exponent) / 3 * 3;
                double scale = Math.Pow(10.0, (double)(-magnitude));
                string format = "0";
                int n = magnitude - exponent - 1 + places;
                if (n > 0)
                {
                    format = format + "." + new string('0', n);
                }

                string[] text = new string[] {
                    (value * scale).ToString(format),
                    "±",
                    (stdev * scale).ToString(format),
                    SiUnit(exponent, unit)
                };
                return text;
            }
            else
            {
                string text = "0." + new string('0', places - 1);
                return new string[] {
                    text,
                    "±",
                    text,
                    unit
                };
            }
        }

        public static string SiUnit(this double value, double stdev, int places, string unit)
        {
            string[] text = SiUnitArray(value, stdev, places, unit);
            return string.Join(" ", text);
        }



        /// <summary>
        /// Returns a subarray of array starting at offset and going until the end of the array.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="array"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        public static T[] SubArray<T>(this T[] array, int offset)
        {
            return array.SubArray(offset, int.MaxValue);
        }
        /// <summary>
        /// Returns a subarray of array starting at offset with a size defined as length.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="array"></param>
        /// <param name="offset"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        public static T[] SubArray<T>(this T[] array, int offset, int length)
        {
            if (length >= array.Length - offset)
            {
                length = array.Length - offset;
            }
            T[] result = new T[length];
            Array.Copy(array, offset, result, 0, length);
            return result;
        }

        public static Color ChangeAlpha(this Color color, int alpha)
        {
            return Color.FromArgb((color.ToArgb() & 0x00FFFFFF) | (alpha <<= 12));
        }

        public static void Dispose(ref System.Threading.Timer? timer)
        {
            if (timer == null)
            {
                return;
            }
            timer.Dispose();
            timer = null;
        }
        /// <summary>
        /// Checks to see if the pictureBox != null and if it's visible. 
        /// If both are true then draw the picture.
        /// </summary>
        /// <param name="pictureBox"></param>
        /// <param name="bitmap"></param>
        public static void SolicitDraw(this PictureBox pictureBox, Bitmap bitmap)
        {
            if ((pictureBox == null) || (bitmap == null))
            {
                return;
            }
            if (pictureBox.Visible)
            {
                pictureBox.Image = (System.Drawing.Image)bitmap;
            }
        }

        /// <summary>
        /// Sets the background color of a TextBox depending on if text is present.
        /// </summary>
        /// <param name="textBox"></param>
        /// <param name="colorNotEmpty"></param>
        /// <param name="colorEmpty"></param>
        /// <returns>true if text is present, false if no text is present</returns>
        public static bool SetBackgroundColor(this TextBox textBox, Color colorNotEmpty, Color colorEmpty)
        {
            if ((textBox.Text == null)
                || textBox.Text.Equals(""))
            {
                textBox.BackColor = colorEmpty;
                return false;
            }
            else
            {
                textBox.BackColor = colorNotEmpty;
                return true;
            }
        }

        public static Color Fade(this Color color, int pixelStart, int pixelStop, int pixelCurrent)
        {
            double m = 1.0 / (double)(pixelStart - pixelStop);
            double b = (-m * (double)pixelStop).Clamp(0.0, 1.0);
            double fraction = m * pixelCurrent + b;
            int red = (int)(fraction * (double)color.R);
            int green = (int)(fraction * (double)color.G);
            int blue = (int)(fraction * (double)color.B);
            return Color.FromArgb(red, green, blue);
        }

        public static Color FadeToBlack(this Color color, double fade)
        {
            int red = ((int)(fade * (double)color.R)).Clamp(0, 255);
            int green = ((int)(fade * (double)color.G)).Clamp(0, 255);
            int blue = ((int)(fade * (double)color.B)).Clamp(0, 255);
            return Color.FromArgb(red, green, blue);
        }

        public static void GetSlope(System.Drawing.Point p1, System.Drawing.Point p2, out double m, out double b)
        {
            m = (float)(p1.Y - p2.Y) / (float)(p1.X - p2.X);
            b = p1.Y - m * p1.X;
        }

        public static void GetSlope(PointF p1, PointF p2, out float m, out float b)
        {
            m = (p1.Y - p2.Y) / (p1.X - p2.X);
            b = p1.Y - m * p1.X;
        }
        public static void GetSlope(float x1, float y1, float x2, float y2, out float m, out float b)
        {
            m = (y1 - y2) / (x1 - x2);
            b = y1 - m * x1;
        }
        public static void GetSlope(double x1, double y1, double x2, double y2, out double m, out double b)
        {
            m = (y1 - y2) / (x1 - x2);
            b = y1 - m * x1;
        }

        /// <summary>
        /// Gets the Y value (float) of a line for a given x, m, and b. i.e. y = m * x + b.
        /// </summary>
        /// <remarks>
        /// This is the float version.
        /// </remarks>
        /// <param name="x">float</param>
        /// <param name="m">float</param>
        /// <param name="b">float</param>
        /// <returns>float</returns>
        public static float GetY(this float x, float m, float b)
        {
            return m * x + b;
        }

        /// <summary>
        /// Gets the Y value (double) of a line for a given x, m, and b. i.e. y = m * x + b.
        /// </summary>
        /// <remarks>
        /// This is the double version.
        /// </remarks>
        /// <param name="x">double</param>
        /// <param name="m">double</param>
        /// <param name="b">double</param>
        /// <returns>double</returns>
        public static double GetY(this double x, double m, double b)
        {
            return m * x + b;
        }

        /// <summary>
        /// Gets the X value (float) of a line for a given x, m, and b. i.e. y = m * x + b.
        /// </summary>
        /// <remarks>
        /// This is the float version.
        /// </remarks>
        /// <param name="x">float</param>
        /// <param name="m">float</param>
        /// <param name="b">float</param>
        /// <returns>float</returns>
        public static float GetX(this float y, float m, float b)
        {
            return (y - b) / m;
        }

        /// <summary>
        /// Gets the X value (double) of a line for a given y, m, and b. i.e. y = m * x + b.
        /// </summary>
        /// <remarks>
        /// This is the double version.
        /// </remarks>
        /// <param name="x">double</param>
        /// <param name="m">double</param>
        /// <param name="b">double</param>
        /// <returns>double</returns>
        public static double GetX(this double y, double m, double b)
        {
            return (y - b) / m;
        }



        /// <summary>
        /// Tries to convert the string to a uint. If it can't it returns 0 and gives you a message.
        /// </summary>
        /// <param name="str"></param>
        /// <returns></returns>
        public static uint ToUInt32(this string str)
        {
            try
            {
                return Convert.ToUInt32(str);
            }
            catch (Exception ex)
            {
                MessageBox.Show("UtilitiesVf61Gui.ToUint(this string) -> " + ex.Message);
                return 0u;
            }
        }

        /// <summary>
        /// Tries to convert the string to a uint. If it can't it will return the defaultValue.
        /// </summary>
        /// <param name="str"></param>
        /// <param name="defaultValue"></param>
        /// <returns></returns>
        public static uint ToUInt32(this string str, uint defaultValue)
        {
            try
            {
                return Convert.ToUInt32(str);
            }
            catch (Exception)
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// Tries to convert the string to a uint. If it can't it will return the defaultValue.
        /// </summary>
        /// <param name="str"></param>
        /// <param name="defaultValue"></param>
        /// <returns></returns>
        public static uint ToUInt32(this string str, uint defaultValue, int specified_base)
        {
            try
            {
                return Convert.ToUInt32(str, specified_base);
            }
            catch (Exception)
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// Tries to convert the string to a uint. If it can't it returns 0 and gives you a message.
        /// </summary>
        /// <param name="str"></param>
        /// <returns></returns>
        public static ulong ToUInt64(this string str)
        {
            try
            {
                return Convert.ToUInt64(str);
            }
            catch (Exception ex)
            {
                MessageBox.Show("UtilitiesVf61Gui.ToUint(this string) -> " + ex.Message);
                return 0u;
            }
        }

        /// <summary>
        /// Tries to convert the string to a uint. If it can't it will return the defaultValue.
        /// </summary>
        /// <param name="str"></param>
        /// <param name="defaultValue"></param>
        /// <returns></returns>
        public static ulong ToUInt64(this string str, ulong defaultValue)
        {
            try
            {
                return Convert.ToUInt64(str);
            }
            catch (Exception)
            {
                return defaultValue;
            }
        }
        /// <summary>
        /// Tries to convert the string to an Int32. If it can't it returns 0 and gives you a message.
        /// </summary>
        /// <param name="str"></param>
        /// <returns></returns>
        public static int ToInt32(this string str)
        {
            try
            {
                return Convert.ToInt32(str);
            }
            catch (Exception ex)
            {
                MessageBox.Show("UtilitiesVf61Gui.ToUint(this string) -> " + ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// Tries to convert the boolean to an Int32. If it can't it will return the defaultValue.
        /// </summary>
        /// <param name="str"></param>
        /// <param name="defaultValue"></param>
        /// <returns></returns>
        public static int ToInt32(this bool value, int defaultValue)
        {
            try
            {
                return Convert.ToInt32(value);
            }
            catch (Exception)
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// Tries to convert the string to an Int32. If it can't it will return the defaultValue.
        /// </summary>
        /// <param name="str"></param>
        /// <param name="defaultValue"></param>
        /// <returns></returns>
        public static int ToInt32(this string str, int defaultValue)
        {
            try
            {
                return Convert.ToInt32(str);
            }
            catch (Exception)
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// Tries to convert the string to an Int32. If it can't it will return the defaultValue.
        /// </summary>
        /// <param name="str"></param>
        /// <param name="defaultValue"></param>
        /// <returns></returns>
        public static int ToInt32(this string str, int defaultValue, int desired_base)
        {
            try
            {
                return Convert.ToInt32(str, desired_base);
            }
            catch (Exception)
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// Tries to convert the string to a Int64. If it can't it will return the defaultValue.
        /// </summary>
        /// <param name="str"></param>
        /// <param name="defaultValue"></param>
        /// <returns></returns>
        public static long ToInt64(this string str, int defaultValue)
        {
            try
            {
                return Convert.ToInt64(str);
            }
            catch (Exception)
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// Tries to convert the string to a double. If it can't it returns 0 and gives you a message.
        /// </summary>
        /// <param name="str"></param>
        /// <returns></returns>
        public static double ToDouble(this string str)
        {
            try
            {
                return Convert.ToDouble(str);
            }
            catch (Exception ex)
            {
                MessageBox.Show("UtilitiesVf61Gui.ToDouble(this string) -> " + ex.Message);
                return 0.0;
            }
        }

        /// <summary>
        /// Tries to convert the string to a double. If it can't it will return the defaultValue.
        /// </summary>
        /// <param name="str"></param>
        /// <param name="defaultValue"></param>
        /// <returns></returns>
        public static double ToDouble(this string str, double defaultValue)
        {
            try
            {
                return Convert.ToDouble(str);
            }
            catch (Exception)
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// Tries to convert the string to a double. If it can't it will return the defaultValue.
        /// </summary>
        /// <param name="str"></param>
        /// <param name="defaultValue"></param>
        /// <returns></returns>
        public static float ToDouble(this string str, float defaultValue)
        {
            try
            {
                return (float)Convert.ToDouble(str);
            }
            catch (Exception)
            {
                return defaultValue;
            }
        }

        public static string AddPreValue(this string str, int size, string value)
        {
            while (str.Length < size)
            {
                str = str.Insert(0, value);
            }
            return str;
        }

        public static FrontPanelStatus ToFrontPanelStatus(this int index)
        {
            try
            {
                switch (index)
                {
                    default:
                    case -1:
                        return FrontPanelStatus.Undefined;
                    case 0:
                        return FrontPanelStatus.NothingAttached;
                    case 1:
                        return FrontPanelStatus.CableAttached;
                    case 2:
                        return FrontPanelStatus.FrontPanelAttached;
                    case 3:
                        return FrontPanelStatus.CableAttachedToFrontPanel;
                }
            }
            catch (Exception)
            {
                return FrontPanelStatus.Undefined;
            }
        }

        public static FrontPanelStatus ToFrontPanelStatus(this string str)
        {
            try
            {
                return str.ToInt32(-1).ToFrontPanelStatus();
            }
            catch (Exception)
            {
                return FrontPanelStatus.Undefined;
            }
        }

        public static VetoTrueState ToVetoTrueState(this string str)
        {
            // public enum VetoTrueState : int
            // {
            //    Undefined = -1,
            //    Low,
            //    High
            // }        
            try
            {
                return str.ToInt32(-1).ToVetoTrueState();
            }
            catch (Exception)
            {
                return VetoTrueState.Undefined;
            }
        }

        public static VetoTrueState ToVetoTrueState(this int index)
        {
            // public enum VetoTrueState : int
            // {
            //    Undefined = -1,
            //    Low,
            //    High
            // }        
            try
            {
                switch (index)
                {
                    default:
                    case -1:
                        return VetoTrueState.Undefined;
                    case 0:
                        return VetoTrueState.Low;
                    case 1:
                        return VetoTrueState.High;
                }
            }
            catch (Exception)
            {
                return VetoTrueState.Undefined;
            }

        }

        public static PodConfig ToPodConfig(this int index)
        {
            switch (index)
            {
                default:
                case -1:
                    return PodConfig.Undefined;
                case 0:
                    return PodConfig.Single;
                case 1:
                    return PodConfig.Primary;
                case 2:
                    return PodConfig.Secondary;
            }
        }

        public static PodConfig ToPodConfig(this string str)
        {
            try
            {
                return str.ToInt32(-1).ToPodConfig();
            }
            catch (Exception)
            {
                return PodConfig.Undefined;
            }
        }

        public static MeasurementType ToMeasurementType(this string str)
        {
            try
            {
                return str.ToInt32(-1).ToMeasurementType();
            }
            catch (Exception)
            {
                return MeasurementType.Undefined;
            }
        }

        public static MeasurementType ToMeasurementType(this int index)
        {
            if (Enum.IsDefined(typeof(MeasurementType), index))
            {
                return (MeasurementType)index;
            }
            else
            {
                return MeasurementType.Undefined;
            }
        }

        public static InstrumentStorageLocation ToInstrumentStorageLocation(this string str)
        {
            return str.ToInt32(-1).ToInstrumentStorageLocation();
        }

        public static InstrumentStorageLocation ToInstrumentStorageLocation(this int index)
        {
            if (Enum.IsDefined(typeof(InstrumentStorageLocation), index))
            {
                return (InstrumentStorageLocation)index;
            }
            else
            {
                return InstrumentStorageLocation.UNDEFINED;
            }
        }

        public static UserMode ToUserMode(this string str)
        {
            return str.ToInt32(-1).ToUserMode();
        }

        public static UserMode ToUserMode(this int index)
        {
            if (Enum.IsDefined(typeof(UserMode), index))
            {
                return (UserMode)index;
            }
            else
            {
                return UserMode.Undefined;
            }
        }

        public static UpdateSystemStatus ToUpdateSystemStatus(this string str)
        {
            return str.ToInt32(-1).ToUpdateSystemStatus();
        }

        public static UpdateSystemStatus ToUpdateSystemStatus(this int index)
        {
            if (Enum.IsDefined(typeof(UpdateSystemStatus), index))
            {
                return (UpdateSystemStatus)index;
            }
            else
            {
                return UpdateSystemStatus.Undefined;
            }
        }

        public static NetworkState ToNetworkState(this string str)
        {
            return str.ToInt32(-1).ToNetworkState();
        }

        public static NetworkState ToNetworkState(this int index)
        {
            if (Enum.IsDefined(typeof(NetworkState), index))
            {
                return (NetworkState)index;
            }
            else
            {
                return NetworkState.Undefined;
            }
        }

        public static AudioState ToAudioState(this string str)
        {
            return str.ToInt32(-1).ToAudioState();
        }

        public static AudioState ToAudioState(this int index)
        {
            if (Enum.IsDefined(typeof(AudioState), index))
            {
                return (AudioState)index;
            }
            else
            {
                return AudioState.Undefined;
            }
        }

        /// <summary>
        /// Gets a list of all of the controls that are of Type. Even the nested controls.
        /// </summary>
        /// <example> List&lt;Control&gt; text_boxes = GetAllControls(this, typeof(TextBox)).ToList();</example>
        /// <param name="control"></param>
        /// <param name="type"></param>
        /// <returns></returns>
        public static List<Control> GetAllControls(this Control control, Type type)
        {
            //https://stackoverflow.com/questions/3419159/how-to-get-all-child-controls-of-a-windows-forms-form-of-a-specific-type-button
            IEnumerable<Control> controls = control.Controls.Cast<Control>();

            List<Control> control_list = controls.SelectMany(ctrl => GetAllControls(ctrl, type))
                                        .Concat(controls)
                                        .Where(c => c.GetType() == type).ToList();
            control_list.OrderBy(c => c.Name);
            return control_list;
        }

        /// <summary>
        /// Does a reverse search on a ComboBox to set SelectedIndex associated with the value.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="combobox"></param>
        /// <param name="value"></param>
        public static void LookupAndSetValue<T>(this ComboBox combobox, T value)
        {
            if (combobox.Items.Count > 0)
            {
                combobox.SelectedValue = value;
                combobox.SelectedIndex = combobox.Items.IndexOf(value);
            }
        }

        /// <summary>
        /// Determines if the string can be converted into a valid double.
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public static bool IsDouble(this string value)
        {
            try
            {
                Convert.ToDouble(value);
                return true;
            }
            catch (Exception)
            {
                //There really is not other way except to catch an exception.
                // New versions of .NET Framework have Double.TryParse()
            }
            return false;
        }

        public static bool IsDouble(this string value, out double result)
        {
            try
            {
                result = Convert.ToDouble(value);
                return true;
            }
            catch (Exception)
            {
                result = 0.0;
                return false;
            }
        }

        public static bool IsInt(this string value)
        {
            try
            {
                Convert.ToInt32(value);
                return true;
            }
            catch (Exception)
            {
                //There really is not other way except to catch an exception.
                // New versions of .NET Framework have Double.TryParse()
            }
            return false;
        }

        public static bool IsInt32(this string value, out int result)
        {
            try
            {
                result = Convert.ToInt32(value);
                return true;
            }
            catch (Exception)
            {
                result = 0;
                return false;
            }
        }

        /// <summary>
        /// Returns the dimensions of a string in pixels based on the Font and TextBox.
        /// </summary>
        /// <param name="message"></param>
        /// <param name="textbox"></param>
        /// <param name="font"></param>
        /// <returns></returns>
        public static SizeF GetTextSize(this string message, TextBox textbox, System.Drawing.Font font)
        {
            Bitmap bitmap = new Bitmap(textbox.Width, textbox.Height);
            Graphics graphics = Graphics.FromImage(bitmap);
            return graphics.MeasureString(message, font);
        }

        // Returns the dimensions of the string in pixels inside a TextBox
        public static SizeF GetTextSize(this TextBox textbox)
        {
            Bitmap bitmap = new Bitmap(textbox.Width, textbox.Height);
            Graphics graphics = Graphics.FromImage(bitmap);
            return graphics.MeasureString(textbox.Text, textbox.Font);
        }

        public static TabPage? SelectedTab(this TabControl tabControl)
        {
            int index = tabControl.SelectedIndex;
            return (index < 0) ? null : tabControl.TabPages[index];
        }

        //public static TKey? GetKey<TKey>(this Dictionary<TKey, string>? dictionary, string value)
        //{
        //    //foreach (KeyValuePair<TKey, string> keyValuePair in dictionary)
        //    //{
        //    //    if (keyValuePair.Value.Equals(value))
        //    //    {
        //    //        return keyValuePair.Key;
        //    //    }
        //    //}
        //    TKey? t = default(TKey);
        //    return t;
        //}

        /// <summary>
        ///  Sets the minimum, maximum, and value so that there are no exceptions thrown.
        /// </summary>
        /// <param name="progressBar"></param>
        /// <param name="minimum"></param>
        /// <param name="maximum"></param>
        /// <param name="value"></param>
        public static int SetValues(this ProgressBar progressBar, int minimum, int maximum, int value)
        {
            try
            {
                progressBar.SuspendLayout();
                progressBar.Minimum = Int32.MinValue;
                progressBar.Maximum = Int32.MaxValue;
                progressBar.Value = value.Clamp(minimum, maximum);
                progressBar.Minimum = minimum;
                progressBar.Maximum = maximum;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "UtilitiesVf61Gui.SetValues()",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Exclamation,
                    MessageBoxDefaultButton.Button1);
            }
            finally
            {
                progressBar.ResumeLayout();
            }
            return progressBar.Value;
        }

        public static uint Factorial(this int index)
        {
            if (index.InRangeIE(0, Factorials.Length))
            {
                return Factorials[index];
            }
            return 0u;
        }
        public static uint[] Factorials = new uint[]
        {
            1,  1,  2,  6,  24, 120, 720, 5040, 40320, 362880,
            3628800, 39916800, 479001600
        };


        public static void Start(this System.Windows.Forms.Timer timer)
        {
            timer.Enabled = true;
        }

        public static void Stop(this System.Windows.Forms.Timer timer)
        {
            timer.Enabled = false;
        }
        /// <summary>
        /// Turns the battery bitmap 90 degrees clockwise.
        /// This does not change the bitmap itself, rather it returns a new bitmap.
        /// </summary>
        /// <returns>Returns a new bitmap that is turned 90 degrees clockwise.</returns>
        public static Bitmap Rotate270(this Bitmap bitmap)
        {
            PixelFormat pixelFormat = PixelFormat.Format32bppRgb;
            Rectangle rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);

            Bitmap bitmapRotated = new Bitmap(bitmap.Height, bitmap.Width, pixelFormat);

            for (int i = 0; i < bitmap.Width; i++)
            {
                for (int j = 0; j < bitmap.Height; j++)
                {
                    //int newX = bitmap.Height - j - 1;
                    int newX = j;
                    int newY = bitmap.Width - i - 1;
                    Color color = bitmap.GetPixel(i, j);
                    bitmapRotated.SetPixel(newX, newY, color);
                }
            }
            return bitmapRotated;
        }

        public static Size GetMaxDimensions(Size a, Size b)
        {
            return new Size()
            {
                Width = Math.Max(a.Width, b.Width),
                Height = Math.Max(a.Height, b.Height)
            };
        }

        public static SizeF GetMaxDimensions(SizeF a, SizeF b)
        {
            return new SizeF()
            {
                Width = Math.Max(a.Width, b.Width),
                Height = Math.Max(a.Height, b.Height)
            };
        }

        public static RectangleF ToRectangleF(this Rectangle rect)
        {
            return new RectangleF()
            {
                X = (float)rect.X,
                Y = (float)rect.Y,
                Width = (float)rect.Width,
                Height = (float)rect.Height
            };
        }

        public static int MaxLengthOfRows<T>(this T[][] table)
        {
            int length = 0;
            foreach (T[] row in table)
            {
                length = Math.Max(row.Length, length);
            }
            return length;
        }

        /// <summary>
        /// Draws a bitmap of a table where each element in itemsArray is aligned in a column.
        /// </summary>
        /// <remarks>
        /// itemsArray[]   = the rows of the strings to be drawn<para />
        /// itemsArray[][] = the columns of the strings to be drawn<para />
        /// </remarks>
        /// <param name="itemsArray"></param>
        /// <param name="colorText"></param>
        /// <param name="colorBack"></param>
        /// <param name="format"></param>
        /// <param name="font"></param>
        /// <param name="size"></param>
        /// <returns></returns>
        public static Bitmap DrawTable(this string[][] itemsArray,
            Color[] colorText,
            Color[] colorBack,
            StringFormat[] format,
            System.Drawing.Font[] font,
            Size size)
        {
            Bitmap bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
            Graphics graphics = Graphics.FromImage(bitmap);
            SolidBrush colorTrans = new SolidBrush(Color.Transparent);
            graphics.FillRectangle(colorTrans, new Rectangle() { Size = bitmap.Size });

            // Get the max string size for each column.
            Size[] sizeMax = new Size[itemsArray.MaxLengthOfRows()];
            for (int row = 0; row < itemsArray.Length; ++row)
            {
                int Cols = itemsArray[row].Length;
                for (int col = 0; col < Cols; ++col)
                {
                    if (itemsArray != null)
                    {
                        sizeMax[col] = GetMaxDimensions(sizeMax[col], graphics.MeasureString(itemsArray[row][col], font[col]).ToSize());
                    }
                }
            }

            // Set the drawing rectangles
            Rectangle[] cells = new Rectangle[sizeMax.Length];
            int X = 0;
            for (int col = 0; col < sizeMax.Length; ++col)
            {
                cells[col].Height = sizeMax[col].Height;
                cells[col].Width = sizeMax[col].Width;
                cells[col].X = X;
                X += cells[col].Width;
            }

            // Draw the strings
            int Y = 0;
            for (int row = 0; row < itemsArray.Length; ++row)
            {
                int Cols = itemsArray[row].Length;
                for (int col = 0; col < Cols; ++col)
                {
                    SolidBrush brushBackground = new SolidBrush(colorBack[col]);
                    SolidBrush brushText = new SolidBrush(colorText[col]);
                    cells[col].Y = Y;
                    graphics.FillRectangle(brushBackground, cells[col]);
                    string test = itemsArray[row][col];
                    graphics.DrawString(itemsArray[row][col], font[col], brushText, cells[col].ToRectangleF(), format[col]);
                    //cells[col].Y += cells[col].Height;
                    brushBackground.Dispose();
                    brushText.Dispose();
                }
                Y += cells[0].Height;
            }

            graphics.Dispose();
            colorTrans.Dispose();
            return bitmap;
        }

        public static bool CheckState(this InstrumentState instrumentState, InstrumentState checkState)
        {
            return (instrumentState & checkState).Equals(checkState);
        }

        public static InstrumentState SetState(this InstrumentState instrumentState, InstrumentState setState)
        {
            return (instrumentState |= setState);
        }

        public static InstrumentState ClearState(this InstrumentState instrumentState, InstrumentState clearState)
        {
            return (instrumentState &= ~clearState);
        }

        public static InstrumentState ToggleState(this InstrumentState instrumentState, InstrumentState toggleState)
        {
            return (instrumentState ^= toggleState);
        }

        //public static string Print(this CircularBuffer<DebuggingMessage> buffer)
        //{
        //    string message = "";
        //    foreach (DebuggingMessage value in buffer)
        //    {
        //        message += value.Print() + Environment.NewLine;
        //    }
        //    return message;
        //}

        //public static string PrintReverse(this CircularBuffer<DebuggingMessage> buffer)
        //{
        //    string message = "";
        //    foreach (DebuggingMessage value in buffer.Reverse())
        //    {
        //        message += value.Print() + Environment.NewLine;
        //    }
        //    return message;
        //}

        public static string Print(this RingBuffer.RingBuffer<DebuggingMessage> buffer)
        {
            string message = "";
            foreach (DebuggingMessage value in buffer)
            {
                message += value.Print() + Environment.NewLine;
            }
            return message;
        }

        public static string PrintReverse(this RingBuffer.RingBuffer<DebuggingMessage> buffer)
        {
            string message = "";
            foreach (DebuggingMessage value in buffer.Reverse())
            {
                message += value.Print() + Environment.NewLine;
            }
            return message;
        }


        /// <summary>
        /// Clears the 2d arrays in the list. This is used for the rowRatio Lists.
        /// </summary>
        /// <param name="list"></param>
        public static void Clear(ref List<double[,]> list)
        {
            foreach (double[,] arr in list)
            {
                Array.Clear(arr, 0, arr.Length);
            }
        }

        public static string PrintRowRatios(this double[,] rowRatios)
        {
            string message = "";
            for (int i = 0; i < rowRatios.GetLength(0); ++i)
            {
                for (int j = i + 1; j < rowRatios.GetLength(1); ++j)
                {
                    //double rr = rowRatios[i, j];
                    //message += string.Format("Row {0}/{1} = {3}", i + 1, j + 1, rr);
                    message += "Row " + (i + 1).ToString() + "/" + (j + 1).ToString() + " = " + rowRatios[i, j].ToString("0.000");
                    message += Environment.NewLine;
                }
            }
            return message;
        }

        /// <summary>
        /// Returns the previously displayed TabPage. i.e. RingBuffer&lt;TabPage&gt;.At(Size - 2). This does not change the size of the RingBuffer.
        /// </summary>
        /// <param name="ringBuffer"></param>
        /// <returns></returns>
        public static TabPage? Previous(this RingBuffer.RingBuffer<TabPage> ringBuffer)
        {
            if (ringBuffer.Size < 2)
            {
                return null;
            }
            return ringBuffer.At(ringBuffer.Size - 2);
        }

        /// <summary>
        /// Returns the last char in str as a string. i.e. str.Substring(str.Length - 1).
        /// </summary>
        /// <param name="str"></param>
        /// <returns></returns>
        public static string Last(this string str)
        {
            if ((str == null)
                || (str.Length == 0))
            {
                return String.Empty;
            }
            return str.Substring(str.Length - 1);
        }

        public static int MaxIndex<T>(this IEnumerable<T> source)
        {
            IComparer<T> comparer = Comparer<T>.Default;
            using (var iterator = source.GetEnumerator())
            {
                if (!iterator.MoveNext())
                {
                    throw new InvalidOperationException("Empty sequence");
                }
                int maxIndex = 0;
                T maxElement = iterator.Current;
                int index = 0;
                while (iterator.MoveNext())
                {
                    index++;
                    T element = iterator.Current;
                    if (comparer.Compare(element, maxElement) > 0)
                    {
                        maxElement = element;
                        maxIndex = index;
                    }
                }
                return maxIndex;
            }
        }

        /// <summary>
        /// Squares the value. This is faster than Math.Pow(value, 2.0).
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public static double Squared(this double value)
        {
            return value * value;
        }

        /// <summary>
        /// Squares the value. This is faster than Math.Pow(value, 3.0).
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public static double Cubed(this double value)
        {
            return value * value * value;
        }

        /// <summary>
        /// Squares the value. This is faster than Math.Pow(value, 4.0).
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public static double Quad(this double value)
        {
            return value * value * value * value;
        }

        public static double Hypot(this double[] x)
        {
            double result = 0;
            foreach (double _x in x)
            {
                result += _x * _x;
            }
            return Math.Sqrt(result);
        }

        public static double Hypot(double x, double y)
        {
            return Math.Sqrt(x * x + y * y);
        }

        public static double Hypot(double x, double y, double z)
        {
            return Math.Sqrt(x * x + y * y + z * z);
        }

        public static void AddException(this List<Exception> list, Exception exception)
        {
            if (list.Count > 100)
            {
                list.RemoveAt(0);
            }
            list.Add(exception);
        }


        /// <summary>
        /// Returns the Sqrt(a[0]*a[0] + ... + a[n-1]*a[n-1]).
        /// </summary>
        /// <param name="a"></param>
        /// <returns></returns>
        public static double SumInQuadrature(this double[] a)
        {
            double a_sum = a.Sum(x => { return x.IsANumber() ? x * x : 0.0; });
            return Math.Sqrt(a_sum);
        }

        /// <summary>
        /// Returns the Sqrt(a[0]*a[0] + ... + a[n-1]*a[n-1]).
        /// </summary>
        /// <param name="a"></param>
        /// <returns></returns>
        public static float SumInQuadrature(this float[] a)
        {
            double a_sum = a.Sum(x => { return x.IsANumber() ? x * x : 0.0; });
            return (float)Math.Sqrt(a_sum);
        }

        /// <summary>
        /// Checks to see if x is a number. Returns false if x is Infinity 
        /// </summary>
        /// <param name="x"></param>
        public static bool IsANumber(this double x)
        {
            return !(double.IsInfinity(x) || double.IsNaN(x));
        }

        /// <summary>
        /// Checks to see if x is a number. Returns false if x is Infinity 
        /// </summary>
        /// <param name="x"></param>
        public static bool IsANumber(this float x)
        {
            return !(float.IsInfinity(x) || float.IsNaN(x));
        }

        public static void DrawString90(this Graphics graphics, string str, System.Drawing.Font font, Brush brush, Point point)
        {
            Size sizeText = graphics.MeasureString(str, font).ToSize();
            SolidBrush brushWhite = new SolidBrush(Color.White);

            Rectangle rectText = new Rectangle()
            {
                Size = sizeText
            };
            StringFormat format = new StringFormat()
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            Bitmap bitmapText = new Bitmap(sizeText.Width, sizeText.Height, PixelFormat.Format32bppRgb);

            Graphics graphicsText = Graphics.FromImage(bitmapText);
            graphicsText.FillRectangle(new SolidBrush(Color.Transparent), rectText);
            graphicsText.DrawString(str, font, brush, rectText, format);
            Point pointNew = new Point(point.X - bitmapText.Height / 2, point.Y - bitmapText.Width / 2);
            graphics.DrawImage(bitmapText.Rotate270(), pointNew.X, pointNew.Y);
        }

        public static void AddBitmaps(this Bitmap bitmap, Bitmap bitmapAdd, int X, int Y, Color transparent)
        {
            for (int x = 0; x < bitmapAdd.Width; ++x)
            {
                for (int y = 0; y < bitmapAdd.Height; ++y)
                {
                    Color pixel = bitmapAdd.GetPixel(x, y);
                    if (pixel != transparent)
                    {
                        bitmap.SetPixel(x + X, y + Y, pixel);
                    }
                }
            }
        }

        public static T[] Fill<T>(this T[] arr, T value)
        {
            for (int i = 0; i < arr.Length; ++i)
            {
                arr[i] = value;
            }
            return arr;
        }

        /// <summary>
        /// Calculates the sum of n + (n-1) + (n-2) ... 0.
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public static int DecreasingSum(this int value)
        {
            if (value < 1)
            {
                return 0;
            }
            else
            {
                return (int)Math.Round((double)(value * (value + 1)) * 0.5);
            }
        }

        //public static ValueStdev[] GetRowRatios(this uint[] channelCounts, int[][] channels)
        //{
        //    string[] rowRatiosStr;
        //    return GetRowRatios(channelCounts, channels, out rowRatiosStr);
        //}

        public static ValueStdev[] GetRowRatios(this uint[] channelCounts, int[][] channels)
        {
            ValueStdev[] rowRatios = new ValueStdev[(channels.Length - 1).DecreasingSum()];
            try
            {
                uint[] rowSums = new uint[channels.Length];

                for (int row = 0; row < channels.Length; ++row)
                {
                    for (int col = 0; col < channels[row].Length; ++col)
                    {
                        int index = channels[row][col];
                        if (index < channelCounts.Length)
                        {
                            rowSums[row] += channelCounts[index];
                        }
                    }
                }

                int K = 1;
                for (int i = 0, j = 0, k = 1; i < rowRatios.Length; ++i)
                {
                    double upper = (double)rowSums[j];
                    double lower = (double)rowSums[k];

                    //double value = lower.Equals(0) ? 0.0 : upper / lower;
                    //double stdev = lower.Equalsvalue * Math.Sqrt(upper * upper + lower * lower); ;

                    double value;
                    double stdev;
                    if (lower.Equals(0))
                    {
                        value = 0;
                        stdev = 0;
                    }
                    else
                    {
                        value = upper / lower;
                        stdev = value * Math.Sqrt(upper * upper + lower * lower);
                    };
                    rowRatios[i] = new ValueStdev(value,
                        stdev,
                        string.Format("Row {0}/{1}", j + 1, k + 1)
                        );
                    if (++k >= channels.Length)
                    {
                        ++j;
                        k = ++K;
                    }
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                MessageBox.Show(ex.Message, "UtilitiesVf61Gui.GetRowRatios", MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1);
#endif
            }
            return rowRatios;

        }

        public static ValueStdev[][] DeepCopy(this ValueStdev[][] value)
        {
            ValueStdev[][] result = new ValueStdev[value.Length][];
            for (int row = 0; row < value.Length; ++row)
            {
                result[row] = value[row].DeepCopy();
            }
            return result;
        }

        public static ValueStdev[] DeepCopy(this ValueStdev[] value)
        {
            ValueStdev[] result = new ValueStdev[value.Length];
            for (int i = 0; i < result.Length; ++i)
            {
                result[i] = new ValueStdev(value[i]);
            }
            return result;
        }

    }
}
