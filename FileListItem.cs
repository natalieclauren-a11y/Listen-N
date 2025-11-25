using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Globalization;

namespace Vf61Gui
{
    /// <summary>
    /// This stores the information foreach file on the instrument.
    /// </summary>
    public class FileListItem
    {
        public string name;
        public string sizeStr;
        public long size;
        public DateTime dateTime;

        public FileListItem()
        {
            name = "";
            size = 0;
            sizeStr = "";
            dateTime = new DateTime();
        }

        public FileListItem(string filename)
        {
            this.name = filename;
            this.size = 0L;
            this.sizeStr = (0L).ToFileSizeString();
            this.dateTime = GetDateTime(filename);
        }

        public FileListItem(string filename, long fileSize)
        {
            this.name = filename;
            this.size = fileSize;
            this.sizeStr = fileSize.ToFileSizeString();
            this.dateTime = GetDateTime(filename);
        }

        public ListViewItem CreateListViewItem()
        {
            ListViewItem listViewItem = new ListViewItem(name);
            listViewItem.SubItems.Add(sizeStr);
            listViewItem.Tag = (long)size;
            return listViewItem;
        }

        public void CreateFileSizeString()
        {
            this.sizeStr = this.size.ToFileSizeString();
        }

        private static DateTime GetDateTime(string filename)
        {
            if (filename.Length < 1)
            {
                return new DateTime();
            }

            Match match = Regex.Match(filename, @"\d{4}_\d{2}_\d{2}_\d{6}");
            string date = match.Value;

            if (!string.IsNullOrEmpty(date))
            {
                // These give errors. Not sure why.
                //DateTime dateTime = DateTime.ParseExact(date, "yyyy_MM_dd_hhmmss", CultureInfo.CurrentCulture);
                //DateTime dateTime = DateTime.ParseExact(date, "yyyy_M_d_hhmmss", CultureInfo.InvariantCulture);
                //return dateTime;

                int year = date.Substring(0, 4).ToInt32();
                int month = date.Substring(5, 2).ToInt32();
                int day = date.Substring(8, 2).ToInt32();
                int hour = date.Substring(11, 2).ToInt32();
                int min = date.Substring(13, 2).ToInt32();
                int sec = date.Substring(15, 2).ToInt32();
                return new DateTime(year, month, day, hour, min, sec);
            }

            return new DateTime();
        }

    }
}
