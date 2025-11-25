using System;
using System.Drawing;
using System.Linq;
using System.Collections.Generic;
using System.Text;

namespace Vf61Gui
{
    public class BatteryStatus
    {
        public int chargeLevel = 0;
        public string name;
        public string printed = "";
        public Bitmap bitmapVertical = new Bitmap(10, 10);
        public Bitmap bitmapHorizontal = new Bitmap(10, 10);
        public bool isPresent;
        public bool isCharging;

        public BatteryStatus(string name = "")
        {
            this.chargeLevel = 0;
            this.name = name;
            this.isPresent = false;
            this.isCharging = false;
            this.Print();
            this.Draw();
        }

        public void Clear()
        {
            this.chargeLevel = 0;
            this.isPresent = false;
            this.isCharging = false;
            this.Print();
            this.Draw();
        }

        public void SetCharge(int chargeLevel, int isCharging)
        {
            this.chargeLevel = chargeLevel.Clamp(-1, 100);
            this.isPresent = (this.chargeLevel != -1);
            this.isCharging = Convert.ToBoolean(isCharging);
            this.Print();
            this.Draw();
        }

        public void Draw()
        {
            BitmapBattery bitmapBattery = new BitmapBattery(false);
            bitmapBattery.Draw(chargeLevel, Convert.ToInt32(isCharging));
            if(bitmapBattery.bitmap == null)
            {
                return;
            }
            bitmapVertical = new Bitmap(bitmapBattery.bitmap);
            Bitmap? bitmapBattery_Sideways = bitmapBattery.TurnSideways();
            if (bitmapBattery_Sideways != null)
            {
                bitmapHorizontal = new Bitmap(bitmapBattery_Sideways);
            }
        }

        public string Print()
        {
            printed = name + Environment.NewLine;
            printed += (isPresent) ? "  Present": "  Not Present";
            printed += Environment.NewLine;
            printed += "  Charge = " + chargeLevel.Clamp(0, 100).ToString() + Environment.NewLine;
            printed += isCharging ? "  Charging" : "";
            printed += Environment.NewLine;
            return printed;
        }
    }
}
