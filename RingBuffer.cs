#region License
/* Copyright 2015 Joe Osborne
 * 
 * This file is part of RingBuffer.
 *
 *  RingBuffer is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  RingBuffer is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with RingBuffer. If not, see <http://www.gnu.org/licenses/>.
 */
#endregion
using System;
using System.Collections;
using System.Collections.Generic;

namespace RingBuffer
{
    /// <summary>
    /// A generic ring buffer with fixed capacity.
    /// </summary>
    /// <typeparam name="T">The type of data stored in the buffer</typeparam>
    public class RingBuffer<T> : IEnumerable<T>, IEnumerable, ICollection<T>,
        ICollection        
    {
        protected int head = 0;
        protected int tail = 0;
        protected int size = 0;

        protected T[] buffer;

        private uint numAdditions = 0;
        public uint NumAdditions { get { return numAdditions; } }

        private bool allowOverflow;
        public bool AllowOverflow { get { return allowOverflow; } }

        /// <summary>
        /// The total number of elements the buffer can store (grows).
        /// </summary>
        public int Capacity { get { return buffer.Length; } }

        /// <summary>
        /// The number of elements currently contained in the buffer.
        /// </summary>
        public int Size { get { return size; } }

        /// <summary>
        /// Retrieve the first (oldest) item from the buffer.
        /// </summary>
        /// <returns>The oldest item added to the buffer.</returns>
        public T Get()
        {
            if (size == 0) throw new System.InvalidOperationException("Buffer is empty.");
            T _item = buffer[head];
            head = (head + 1) % Capacity;
            --size;
            return _item;
        }

        /// <summary>
        /// Retrieves the last (newest) item from the buffer.
        /// </summary>
        /// <returns>The newest item added to the buffer.</returns>
        public T Pop()
        {
            if (size == 0) throw new System.InvalidOperationException("Buffer is empty.");
            tail = (tail == 0) ? Capacity - 1 : tail - 1;
            --size;
            T _item = buffer[tail];
            return _item;
        }

        /// <summary>
        /// Adds an item to the end of the buffer.
        /// </summary>
        /// <param name="item">The item to be added.</param>
        public void Add(T item)
        {
            // If tail & head are equal and the buffer is not empty, assume
            // that it will overflow and throw an exception.
            ++numAdditions;
            if (tail == head && size != 0)
            {
                if (allowOverflow)
                {
                    addToBuffer(item, true);
                }
                else
                {
                    throw new System.InvalidOperationException("The RingBuffer is full");
                }
            }
            // If the buffer will not overflow, just add the item.
            else
            {
                addToBuffer(item, false);
            }
        }

        /// <summary>
        /// Looks at the oldest item in the ring buffer. This does not change the size of the buffer.
        /// </summary>
        /// <returns></returns>
        public T? Back()
        {
            if (size < 1)
            {
                return default;
            }
            return buffer[head];
        }

        /// <summary>
        /// Looks at the newest item in the ring buffer. This does not change the size of the buffer.
        /// </summary>
        /// <returns></returns>
        public T? Front()
        {
            if (size == 0)
            {
                return default;
            }
            int index = (tail < 1) ? Capacity - 1 : tail - 1;
            return buffer[index];
        }

        /// <summary>
        /// Returns the value at index. Index is referenced from the head. This does not change the size of the buffer.
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public T? At(int index)
        {
            if((size == 0) 
                || (index > buffer.Length))
            {
                return default;
            }
            int i = (head + index) % buffer.Length;
            return buffer[i];
        }

        protected void addToBuffer(T toAdd, bool overflow)
        {
            if (overflow)
            {
                head = (head + 1) % Capacity;
            }
            else
            {
                size++;
            }
            buffer[tail] = toAdd;
            tail = (tail + 1) % Capacity;
        }

        #region Constructors
        // Default capacity is 4, default overflow behavior is false.
        public RingBuffer() : this(4, false) { }

        public RingBuffer(int capacity) : this(capacity, false) { }

        public RingBuffer(int capacity, bool overflow)
        {
            buffer = new T[capacity];
            allowOverflow = overflow;
        }
        #endregion

        #region IEnumerable Members
        public IEnumerator<T> GetEnumerator()
        {
            int _index = head;
            for (int i = 0; i < size; i++, _index = (_index + 1) % Capacity)
            {
                yield return buffer[_index];
            }
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            return GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return (IEnumerator)GetEnumerator();
        }
        #endregion

        #region ICollection<T> Members

        /// <summary>
        /// The number of elements currently contained in the buffer.
        /// </summary>
        public int Count { get { return size; } }
        
        public bool IsReadOnly { get { return false; } }

        /// <summary>
        /// Determines whether the RingBuffer contains a specific value.
        /// </summary>
        /// <param name="item">The value to check the RingBuffer for.</param>
        /// <returns>True if the RingBuffer contains <paramref name="item"/>
        /// , false if it does not.
        /// </returns>
        public bool Contains(T item)
        {
            EqualityComparer<T> comparer = EqualityComparer<T>.Default;
            int _index = head;
            for (int i = 0; i < size; i++, _index = (_index + 1) % Capacity)
            {
                if (comparer.Equals(item, buffer[_index])) return true;
            }
            return false;
        }

        /// <summary>
        /// Sets all the values in the RingBuffer to default values and sets the head and tail to the beginning of the buffer.
        /// </summary>
        public void Clear()
        {
            Array.Clear(buffer, 0, buffer.Length);
            head = 0;
            tail = 0;
            size = 0;
        }

        /// <summary>
        /// Copies the contents of the RingBuffer to <paramref name="array"/>
        /// starting at <paramref name="arrayIndex"/>.
        /// </summary>
        /// <param name="array">The array to be copied to.</param>
        /// <param name="arrayIndex">The index of <paramref name="array"/>
        /// where the buffer should begin copying to. <paramref name="array"/> needs to be sized correctly before this is called.</param>
        public void CopyTo(T[] array, int arrayIndex)
        {
            int _index = head;
            for (int i = 0; i < size; i++, arrayIndex++, _index = (_index + 1) %
                Capacity)
            {
                array[arrayIndex] = (T)buffer[_index];
            }
        }

        /// <summary>
        /// Removes <paramref name="item"/> from the buffer.
        /// </summary>
        /// <param name="item"></param>
        /// <returns>True if <paramref name="item"/> was found and 
        /// successfully removed. False if <paramref name="item"/> was not
        /// found or there was a problem removing it from the RingBuffer.
        /// </returns>
        public bool Remove(T item)
        {
            int _index = head;
            int _removeIndex = 0;
            bool _foundItem = false;
            EqualityComparer<T> _comparer = EqualityComparer<T>.Default;
            for (int i = 0; i < size; i++, _index = (_index + 1) % Capacity)
            {
                if (_comparer.Equals(item, buffer[_index]))
                {
                    _removeIndex = _index;
                    _foundItem = true;
                    break;
                }
            }
            if (_foundItem)
            {
                T[] _newBuffer = new T[size - 1];
                _index = head;
                bool _pastItem = false;
                for (int i = 0; i < size - 1; i++, _index = (_index + 1) % Capacity)
                {
                    if (_index == _removeIndex)
                    {
                        _pastItem = true;
                    }
                    if (_pastItem)
                    {
                        _newBuffer[_index] = buffer[(_index + 1) % Capacity];
                    }
                    else
                    {
                        _newBuffer[_index] = buffer[_index];
                    }
                }
                size--;
                buffer = _newBuffer;
                return true;
            }
            return false;
        }
        #endregion

        #region ICollection Members
        /// <summary>
        /// Gets an object that can be used to synchronize access to the
        /// RingBuffer.
        /// </summary>
        public Object SyncRoot { get { return this; } }

        /// <summary>
        /// Gets a value indicating whether access to the RingBuffer is 
        /// synchronized (thread safe).
        /// </summary>
        public bool IsSynchronized { get { return false; } }

        /// <summary>
        /// Copies the elements of the RingBuffer to <paramref name="array"/>, 
        /// starting at a particular Array <paramref name="index"/>.
        /// </summary>
        /// <param name="array">The one-dimensional Array that is the 
        /// destination of the elements copied from RingBuffer. The Array must 
        /// have zero-based indexing.</param>
        /// <param name="index">The zero-based index in 
        /// <paramref name="array"/> at which copying begins.</param>
        void ICollection.CopyTo(Array array, int index)
        {
            CopyTo((T[])array, index);
        }
        #endregion
    }

}
