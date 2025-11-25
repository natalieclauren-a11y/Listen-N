using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace Vf61Gui
{
    public static class EnumsConverters
    {
        public static UnitsDistance ToUnitsDistance(this string str)
        {
            try
            {
                string unitString = str.ToLower();
                if (unitString.Equals("cm"))
                {
                    return UnitsDistance.Centimeters;
                }
                else if (unitString.Equals("inches"))
                {
                    return UnitsDistance.Inches;
                }
                else if (unitString.Equals("inch"))
                {
                    return UnitsDistance.Inches;
                }
                return UnitsDistance.Undefined;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "UtilitiesVf61Gui.ToUnitsDistance(string)");
            }
            return UnitsDistance.Undefined;
        }

        public static UsbFileState ToUsbFileState(this int flag)
        {
            if (flag > 99)
            {
                return UsbFileState.SaveComplete;
            }
            if (flag > -1)
            {
                return UsbFileState.InProgress;
            }
            switch (flag)
            {
                default:
                    return UsbFileState.Undefined;
                case -3:
                    return UsbFileState.Idle;
                case -2:
                    return UsbFileState.NoMount;
                case -1:
                    return UsbFileState.NoSpace;
            }
        }


    }
}
