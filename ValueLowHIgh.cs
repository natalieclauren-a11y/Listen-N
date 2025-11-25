using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;

namespace Vf61Gui
{
    /// <summary>
    /// <para> Contains three values.</para>
    /// <para> The actual value, a low value, and a high value.</para>
    /// </summary>
    public class ValueLowHigh
    {
        private double _value;
        private double _low;
        private double _high;
        private double _highMax;
        private double _lowMin;

        public ValueLowHigh(ValueLowHigh value) : this(value._value, value._low, value._high, double.MinValue, double.MaxValue) { }
        public ValueLowHigh() : this(0.0, 0.0, 0.0, double.MinValue, double.MaxValue) { }
        public ValueLowHigh(double value) : this(value, value, value, double.MinValue, double.MaxValue) { }
        public ValueLowHigh(double value, double low, double high) : this(value, low, high, double.MinValue, double.MaxValue) { }
        public ValueLowHigh(ValueStdev valueStdev, double lowMin, double highMax) : this(valueStdev.value, valueStdev.lower, valueStdev.upper, lowMin, highMax) { }
        public ValueLowHigh(ValueStdev valueStdev) : this(valueStdev.value, valueStdev.lower, valueStdev.upper, double.MinValue, double.MaxValue) { }
        public ValueLowHigh(double value, double low, double high, double lowMin, double highMAx)
        {
            _lowMin = lowMin;
            _highMax = highMAx;
            _value = value.Clamp(lowMin, highMax);
            _low = (_low < value ? _low : value).Clamp(lowMin, highMax);
            _high = (_high > value ? _high : value).Clamp(lowMin, highMax);
        }

        public double value
        {
            get { return _value; }
            set 
            {
                _value = value.Clamp(lowMin, highMax);
                if (_value < _low)
                {
                    _low = _value;
                }
                if (_value > _high)
                {
                    _high = _value;
                }
            }
        }

        public double low
        {
            get { return _low; }
            set { _low = (value < _value ? value : _value).Clamp(lowMin, highMax); }
        }
        public double high
        {
            get { return _high; }
            set { _high = (value > _value  ? value : _value).Clamp(lowMin, highMax);}
        }

        public double highMax
        {
            get { return _highMax; }
            set
            {
                _highMax = value;
                if (_high > value)
                {
                    _high = value;
                }
                if (_value > value)
                {
                    _value = value;
                }
                if (_low > value)
                {
                    _low = value;
                }
            }
        }

        public double lowMin
        {
            get { return _lowMin; }
            set
            {
                _lowMin = value;
                if (_low < value)
                {
                    _low = value;
                }
                if (_value < value)
                {
                    _value = value;
                }
                if (_high < value)
                {
                    _high = value;
                }
            }
        }

        /// <summary>
        /// <para> Returns the difference between the value and low.</para>
        /// </summary>
        /// <returns></returns>
        public double deltaLow()
        {
            return _value - _low;
        }

        /// <summary>
        /// <para> Returns the difference between the value and high.</para>
        /// </summary>
        /// <returns></returns>
        public double deltaHigh()
        {
            return _low - high;
        }

        /// <summary>
        /// <para> Returns the difference between high and low</para>
        /// </summary>
        /// <returns></returns>
        public double range()
        {
            return _high - _low;
        }

        public static ValueLowHigh operator +(ValueLowHigh value, ValueLowHigh delta)
        {
            double new_lowMin = Math.Min(value.lowMin, delta.lowMin);
            double new_highMax = Math.Max(value.highMax, delta.highMax);
            double new_value = (value._value + delta._value).Clamp(new_lowMin, new_highMax);
            double new_low = (value._low + delta._low).Clamp(new_lowMin, new_highMax);
            double new_high = (value._high + delta._high).Clamp(new_lowMin, new_highMax);
            return new ValueLowHigh()
            {
                _value = new_value, 
                _low = (new_low < new_value) ? new_low : new_value,
                _high = (new_high > new_value) ? new_high : new_value,
                _lowMin = new_lowMin,
                _highMax = new_highMax
            };
         }
        public static ValueLowHigh operator +(ValueLowHigh value, double delta)
        {
            return new ValueLowHigh(value._value += delta, value._low += delta, value._high += delta, value.lowMin, value.highMax);
        }
        public static ValueLowHigh operator -(ValueLowHigh value, double delta)
        {
            return new ValueLowHigh(value._value -= delta, value._low -= delta, value._high -= delta, value.lowMin, value.highMax);
        }
        public static ValueLowHigh operator *(ValueLowHigh value, double delta)
        {
            return new ValueLowHigh(value._value *= delta, value._low *= delta, value._high *= delta, value.lowMin, value.highMax);
        }
        public static ValueLowHigh operator /(ValueLowHigh value, double delta)
        {
            return new ValueLowHigh(value._value /= delta, value._low /= delta, value._high /= delta, value.lowMin, value.highMax);
        }
        public static ValueLowHigh operator -(ValueLowHigh value)
        {
            if (value._value.Equals(0.0))
            {
                return new ValueLowHigh(value);
            }
            return new ValueLowHigh(-value._value, -value._high, -value._low, value.highMax, value.lowMin);
        }
    }
}
