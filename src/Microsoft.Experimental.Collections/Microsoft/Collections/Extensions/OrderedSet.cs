// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;

namespace Microsoft.Collections.Extensions
{
    public class OrderedSet<T>// : IList<T>, IReadOnlyList<T>, ISet<T>
    {
        private struct Slot
        {
            public int HashCode; // lower 31 bits of the hash code
            public T Value;
            public int Next; // the index of the next item in the same bucket, -1 if last
        }

        private int[] _buckets = Array.Empty<int>();
        private Slot[] _slots = Array.Empty<Slot>();
        private int _version;
        private readonly IEqualityComparer<T> _comparer;

        public int Count { get; private set; }

        public IEqualityComparer<T> Comparer => _comparer ?? EqualityComparer<T>.Default;

        public T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index), "Index was out of range. Must be non-negative and less than the size of the collection.");
                }

                return _slots[index].Value;
            }
            set
            {
                if ((uint)index >= (uint)Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index), "Index was out of range. Must be non-negative and less than the size of the collection.");
                }

                // TODO: implement logic
            }
        }

        public OrderedSet()
            : this(0, null)
        {
        }

        public OrderedSet(int capacity)
            : this(capacity, null)
        {
        }

        public OrderedSet(IEqualityComparer<T> comparer)
            : this(0, comparer)
        {
        }

        public OrderedSet(int capacity, IEqualityComparer<T> comparer)
        {
            if (capacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }
            if (capacity > 0)
            {
                Resize(HashHelpers.GetPrime(capacity));
            }
            if (comparer != EqualityComparer<T>.Default)
            {
                _comparer = comparer;
            }
        }

        public OrderedSet(IEnumerable<T> collection)
            : this(collection, null)
        {
        }

        public OrderedSet(IEnumerable<T> collection, IEqualityComparer<T> comparer)
            : this((collection as ICollection<T>)?.Count ?? 0, comparer)
        {
            if (collection == null)
            {
                throw new ArgumentNullException(nameof(collection));
            }

            foreach (T item in collection)
            {
                Add(item);
            }
        }

        public bool Add(T item) => TryInsert(null, item, false) >= 0;

        public void Clear()
        {
            if (Count > 0)
            {
                Array.Clear(_buckets, 0, _buckets.Length);
                Array.Clear(_slots, 0, Count);
                Count = 0;
            }
            ++_version;
        }

        public bool Contains(T item) => IndexOf(item) >= 0;

        public int EnsureCapacity(int capacity)
        {
            if (capacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            if (_slots.Length >= capacity)
            {
                return _slots.Length;
            }
            int newSize = HashHelpers.GetPrime(capacity);
            Resize(newSize);
            ++_version;
            return newSize;
        }

        public void ExceptWith(IEnumerable<T> other)
        {
            if (other == null)
            {
                throw new ArgumentNullException(nameof(other));
            }

            ++_version;
            int count = Count;
            if (count == 0)
            {
                return;
            }

            if (other == this)
            {
                Clear();
                return;
            }

            bool anyToRemove = false;
            Slot[] slots = _slots;
            foreach (T item in other)
            {
                int index = IndexOf(item);
                if (index >= 0)
                {
                    // sets the 32nd bit to indicate that the item was found
                    slots[index].HashCode |= unchecked((int)0x80000000);
                    anyToRemove = true;
                }
            }

            if (!anyToRemove)
            {
                return;
            }

            int nextIndex = 0;
            for (int i = 0; i < count; ++i)
            {
                Slot slot = slots[i];
                // checks if 32nd bit is unset
                if ((slot.HashCode & unchecked((int)0x80000000)) == 0)
                {
                    slots[nextIndex] = slot;
                    ++nextIndex;
                }
            }

            // Clears removed slots
            for (int i = nextIndex; i < count; ++i)
            {
                slots[i] = default;
            }

            Count = nextIndex;
            int[] buckets = _buckets;
            Array.Clear(buckets, 0, buckets.Length);
            ReinitializeBuckets(buckets, slots);
        }

        public Enumerator GetEnumerator() => new Enumerator(this);

        public int IndexOf(T item) => IndexOf(item, out _);

        public void Insert(int index, T item) => TryInsert(index, item, true);

        public void IntersectWith(IEnumerable<T> other)
        {
            if (other == null)
            {
                throw new ArgumentNullException(nameof(other));
            }

            ++_version;
            int count = Count;
            if (count == 0)
            {
                return;
            }

            if (other == this)
            {
                return;
            }

            bool anyToKeep = false;
            Slot[] slots = _slots;
            foreach (T item in other)
            {
                int index = IndexOf(item);
                if (index >= 0)
                {
                    // sets the 32nd bit to indicate that the item was found
                    slots[index].HashCode |= unchecked((int)0x80000000);
                    anyToKeep = true;
                }
            }

            if (!anyToKeep)
            {
                Clear();
                return;
            }

            int nextIndex = 0;
            for (int i = 0; i < count; ++i)
            {
                ref Slot slot = ref slots[i];
                // checks if 32nd bit is set
                if ((slot.HashCode & unchecked((int)0x80000000)) != 0)
                {
                    // unsets the 32nd bit
                    slot.HashCode &= 0x7FFFFFFF;
                    slots[nextIndex] = slot;
                    ++nextIndex;
                }
            }

            // Clears removed slots
            for (int i = nextIndex; i < count; ++i)
            {
                slots[i] = default;
            }

            Count = nextIndex;
            int[] buckets = _buckets;
            Array.Clear(buckets, 0, buckets.Length);
            ReinitializeBuckets(buckets, slots);
        }

        public bool IsProperSubsetOf(IEnumerable<T> other)
        {
            if (other == null)
            {
                throw new ArgumentNullException(nameof(other));
            }

            if (other is ICollection<T> otherAsCollection)
            {
                if (Count == 0)
                {
                    return otherAsCollection.Count > 0;
                }
                if (Count >= otherAsCollection.Count)
                {
                    return false;
                }
            }

            foreach (T item in other)
            {
                if (Contains(item))
                {

                }
            }
        }

        public bool IsProperSupersetOf(IEnumerable<T> other);

        public bool IsSubsetOf(IEnumerable<T> other);

        public bool IsSupersetOf(IEnumerable<T> other);

        public bool Overlaps(IEnumerable<T> other);

        public bool Remove(T item)
        {
            int index = IndexOf(item);
            if (index >= 0)
            {
                RemoveAt(index);
                return true;
            }
            return false;
        }

        public void RemoveAt(int index)
        {
            int count = Count;
            if (index < 0 || index >= count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "Index was out of range. Must be non-negative and less than the size of the collection.");
            }

            // Remove the entry from the bucket
            UpdateBucket(index, true);

            // Decrement the indices > index
            for (int i = index + 1; i < count; ++i)
            {
                UpdateBucket(i, false, -1);
            }
            Slot[] slots = _slots;
            Array.Copy(slots, index + 1, slots, index, count - index - 1);
            --Count;
            slots[Count] = default;
            ++_version;
        }

        public bool SetEquals(IEnumerable<T> other);

        public void SymmetricExceptWith(IEnumerable<T> other)
        {
            if (other == null)
            {
                throw new ArgumentNullException(nameof(other));
            }

            ++_version;
            int count = Count;
            if (count == 0)
            {
                UnionWith(other);
                return;
            }

            if (other == this)
            {
                Clear();
                return;
            }

            bool anyToRemove = false;
            foreach (T item in other)
            {
                int index = TryInsert(null, item, false);
                if (index < 0)
                {
                    index = ~index;
                    if (index < count)
                    {
                        // sets the 32nd bit to indicate that the item was found
                        _slots[index].HashCode |= unchecked((int)0x80000000);
                        anyToRemove = true;
                    }
                }
            }

            if (!anyToRemove)
            {
                return;
            }

            int nextIndex = 0;
            count = Count;
            Slot[] slots = _slots;
            for (int i = 0; i < count; ++i)
            {
                Slot slot = slots[i];
                // checks if 32nd bit is unset
                if ((slot.HashCode & unchecked((int)0x80000000)) == 0)
                {
                    slots[nextIndex] = slot;
                    ++nextIndex;
                }
            }

            // Clears removed slots
            for (int i = nextIndex; i < count; ++i)
            {
                slots[i] = default;
            }

            Count = nextIndex;
            int[] buckets = _buckets;
            Array.Clear(buckets, 0, buckets.Length);
            ReinitializeBuckets(buckets, slots);
        }

        public void TrimExcess() => TrimExcess(Count);

        public void TrimExcess(int capacity)
        {
            if (capacity < Count)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            int newSize = HashHelpers.GetPrime(capacity);
            if (newSize < _slots.Length)
            {
                Resize(newSize);
                ++_version;
            }
        }

        public bool TryGetValue(T equalValue, out T actualValue)
        {
            int index = IndexOf(equalValue);
            if (index >= 0)
            {
                actualValue = _slots[index].Value;
                return true;
            }
            actualValue = default;
            return false;
        }

        public void UnionWith(IEnumerable<T> other)
        {
            if (other == null)
            {
                throw new ArgumentNullException(nameof(other));
            }

            foreach (T item in other)
            {
                Add(item);
            }
        }

        private void Resize(int newSize)
        {
            int[] newBuckets = new int[newSize];
            Slot[] newSlots = new Slot[newSize];

            Array.Copy(_slots, newSlots, Count);
            ReinitializeBuckets(newBuckets, newSlots);

            _buckets = newBuckets;
            _slots = newSlots;
        }

        private void ReinitializeBuckets(int[] buckets, Slot[] slots)
        {
            int bucketCount = buckets.Length;
            int count = Count;
            for (int i = 0; i < count; ++i)
            {
                ref Slot entry = ref slots[i];
                ref int next = ref buckets[entry.HashCode % bucketCount];
                entry.Next = next - 1;
                next = i + 1;
            }
        }

        private int IndexOf(T item, out int hashCode)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            IEqualityComparer<T> comparer = _comparer;
            hashCode = (comparer?.GetHashCode(item) ?? item.GetHashCode()) & 0x7FFFFFFF;
            int[] buckets = _buckets;
            int index = -1;
            if (buckets.Length > 0)
            {
                int bucket = hashCode % buckets.Length;
                index = buckets[bucket] - 1;
                comparer = comparer ?? EqualityComparer<T>.Default;
                Slot[] slots = _slots;
                while (index >= 0)
                {
                    Slot slot = slots[index];
                    if ((slot.HashCode & 0x7FFFFFFF) == hashCode && comparer.Equals(slot.Value, item))
                    {
                        break;
                    }
                    index = slot.Next;
                }
            }
            return index;
        }

        private int TryInsert(int? index, T item, bool throwOnExisting)
        {
            int i = IndexOf(item, out int hashCode);
            ++_version;
            if (i >= 0)
            {
                if (throwOnExisting)
                {
                    throw new ArgumentException("An item with the same key has already been added.");
                }
                else
                {
                    return ~i;
                }
            }

            // Check if resize is needed
            Slot[] slots = _slots;
            int count = Count;
            if (slots.Length == count)
            {
                Resize(HashHelpers.ExpandPrime(slots.Length));
                slots = _slots;
            }

            // Increment indices >= index;
            int actualIndex = index ?? count;
            for (i = actualIndex; i < count; ++i)
            {
                UpdateBucket(i, false, 1);
            }
            Array.Copy(slots, actualIndex, slots, actualIndex + 1, count - actualIndex);

            Debug.Assert(_buckets.Length > 0);
            ref int bucket = ref _buckets[hashCode % _buckets.Length];
            Slot newEntry = new Slot { HashCode = hashCode, Value = item, Next = bucket - 1 };
            slots[actualIndex] = newEntry;
            bucket = actualIndex + 1;
            ++Count;
            return actualIndex;
        }

        private void UpdateBucket(int i, bool setToNext, int incrementAmount = 0)
        {
            Debug.Assert(setToNext ^ (incrementAmount != 0), "setToNext and incrementAmount should be mutually exclusive");
            Slot[] slots = _slots;
            Slot slot = slots[i];
            Debug.Assert(_buckets.Length > 0);
            ref int b = ref _buckets[slot.HashCode % _buckets.Length];
            if (b == i + 1)
            {
                b = setToNext ? slot.Next + 1 : b + incrementAmount;
            }
            else
            {
                int j = b - 1;
                do
                {
                    ref Slot e = ref slots[j];
                    if (e.Next == i)
                    {
                        e.Next = setToNext ? slot.Next : e.Next + incrementAmount;
                        j = -1;
                    }
                    else
                    {
                        j = e.Next;
                    }
                } while (j >= 0);
            }
        }

        public struct Enumerator : IEnumerator<T>
        {
            private readonly OrderedSet<T> _orderedSet;
            private readonly int _version;
            private int _index;

            public T Current { get; private set; }

            object IEnumerator.Current => Current;

            internal Enumerator(OrderedSet<T> orderedSet)
            {
                _orderedSet = orderedSet;
                _version = orderedSet._version;
                _index = 0;
                Current = default;
            }

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                if (_version != _orderedSet._version)
                {
                    throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
                }
                if (_index < _orderedSet.Count)
                {
                    Slot slot = _orderedSet._slots[_index];
                    Current = slot.Value;
                    ++_index;
                    return true;
                }
                Current = default;
                return false;
            }

            void IEnumerator.Reset()
            {
                if (_version != _orderedSet._version)
                {
                    throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
                }
                _index = 0;
                Current = default;
            }
        }
    }
}
