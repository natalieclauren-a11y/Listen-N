using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace Vf61Gui
{
    public class DebuggingMessage

    {
        public DateTime dateTime = DateTime.Now;
        public string message = "";

        public DebuggingMessage(string message)
        {
            dateTime = DateTime.Now;
            this.message = message;
        }
        public DebuggingMessage()
        {

        }

        public string Print()
        {
            string str = dateTime.ToString("hh:mm:ss.fff");
            str += "   -    ";
            str += message;
            return str;
        }
        public ListViewItem ToListViewItem()
        {
            string[] subitems = new string[] {
                dateTime.ToString("hh:mm:ss.fff"),
                message
            };
            return new ListViewItem(subitems);
        }

        public static ListViewItem ToListViewItem_Empty()
        {
            string[] subitems = new string[] { "", "" };
            return new ListViewItem(subitems);
        }

    }
}
