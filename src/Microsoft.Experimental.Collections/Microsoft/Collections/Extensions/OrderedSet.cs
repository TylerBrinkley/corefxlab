// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;

namespace Microsoft.Collections.Extensions
{
    // used for set checking operations (using enumerables) that rely on counting
    internal struct ElementCount
    {
        public int UniqueCount;
        public int UnfoundCount;
    }

    /// <summary>
    /// Represents an ordered set of values with the same performance as <see cref="HashSet{T}"/> with O(1) lookups and adds but with O(n) inserts and removes.
    /// </summary>
    /// <typeparam name="T">The type of elements in the set.</typeparam>
    public class OrderedSet<T> : IList<T>, IReadOnlyList<T>, ISet<T>
    {
        private struct Slot
        {
            public int HashCode; // lower 31 bits of the hash code
            public T Value;
            public int Next; // the index of the next item in the same bucket, -1 if last
        }
        
        // We want to initialize without allocating arrays. We also want to avoid null checks.
        // Array.Empty would give divide by zero in modulo operation. So we use static one element arrays.
        // The first add will cause a resize replacing these with real arrays of three elements.
        // Arrays are wrapped in a class to avoid being duplicated for each <T>
        private static readonly Slot[] InitialSlots = new Slot[1];
        // 1-based index into _slots; 0 means empty
        private int[] _buckets = HashHelpers.SizeOneIntArray;
        // remains contiguous and maintains order
        private Slot[] _slots = InitialSlots;
        private int _count;
        private int _version;
        // is null when comparer is EqualityComparer<TKey>.Default so that the GetHashCode method is used explicitly on the object
        private readonly IEqualityComparer<T> _comparer;

        public int Count => _count;

        public IEqualityComparer<T> Comparer => _comparer ?? EqualityComparer<T>.Default;

        public T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index), Strings.ArgumentOutOfRange_Index);
                }

                return _slots[index].Value;
            }
            set
            {
                if ((uint)index >= (uint)_count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index), Strings.ArgumentOutOfRange_Index);
                }

                int foundIndex = IndexOf(value, out int hashCode);
                // value does not exist in set thus replace value at index
                if (foundIndex < 0)
                {
                    RemoveSlotFromBucket(index);
                    Slot slot = new Slot { HashCode = hashCode, Value = value };
                    AddSlotToBucket(ref slot, index, _buckets);
                    _slots[index] = slot;
                    ++_version;
                }
                // value already exists in set at the specified index thus just replace the value as hashCode remains the same
                else if (foundIndex == index)
                {
                    _slots[index].Value = value;
                }
                // value already exists in set but not at the specified index thus throw exception as this method shouldn't affect the indices of other items
                else
                {
                    throw new ArgumentException(Strings.Argument_AddingDuplicate);
                }
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
                int newSize = HashHelpers.GetPrime(capacity);
                _buckets = new int[newSize];
                _slots = new Slot[newSize];
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
            if (_count > 0)
            {
                Array.Clear(_buckets, 0, _buckets.Length);
                Array.Clear(_slots, 0, _count);
                _count = 0;
                ++_version;
            }
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
            
            // this is already the empty set; return
            if (_count == 0)
            {
                return;
            }

            // special case if other is this; a set minus itself is the empty set
            if (other == this)
            {
                Clear();
                return;
            }

            ExceptWithEnumerable(other);
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

            // intersection of anything with empty set is empty set, so return if count is 0
            if (_count == 0)
            {
                return;
            }

            // set intersecting with itself is the same set
            if (other == this)
            {
                return;
            }

            // if other is empty, intersection is empty set; remove all elements and we're done
            // can only figure this out if implements ICollection<T>. (IEnumerable<T> has no count)
            if (other is ICollection<T> otherAsCollection && otherAsCollection.Count == 0)
            {
                Clear();
                return;
            }

            IntersectWithEnumerable(other);
        }

        public bool IsProperSubsetOf(IEnumerable<T> other)
        {
            if (other == null)
            {
                throw new ArgumentNullException(nameof(other));
            }

            // the empty set isn't a proper superset of any set.
            if (_count == 0)
            {
                return false;
            }

            // a set is never a strict superset of itself
            if (other == this)
            {
                return false;
            }

            if (other is ICollection<T> otherAsCollection)
            {
                // if other is the empty set then this is a superset
                if (otherAsCollection.Count == 0)
                {
                    // note that this has at least one element, based on above check
                    return true;
                }
                // faster if other is a hashset with the same equality comparer
                if ((other is HashSet<T> otherAsSet && AreEqualityComparersEqual(this, otherAsSet)) ||
                    (other is OrderedSet<T> otherAsOrderedSet && AreEqualityComparersEqual(this, otherAsOrderedSet)))
                {
                    if (otherAsCollection.Count >= _count)
                    {
                        return false;
                    }
                    // now perform element check
                    return ContainsAllElements(other);
                }
            }
            // couldn't fall out in the above cases; do it the long way
            ElementCount result = CheckUniqueAndUnfoundElements(other, true);
            return result.UniqueCount < _count && result.UnfoundCount == 0;
        }

        public bool IsProperSupersetOf(IEnumerable<T> other)
        {
            if (other == null)
            {
                throw new ArgumentNullException(nameof(other));
            }

            // no set is a proper subset of itself.
            if (other == this)
            {
                return false;
            }

            if (other is ICollection<T> otherAsCollection)
            {
                // no set is a proper subset of an empty set
                if (otherAsCollection.Count == 0)
                {
                    return false;
                }

                // the empty set is a proper subset of anything but the empty set
                if (_count == 0)
                {
                    return otherAsCollection.Count > 0;
                }
                // faster if other is a hashset (and we're using same equality comparer)
                if ((other is HashSet<T> otherAsSet && AreEqualityComparersEqual(this, otherAsSet)) ||
                    (other is OrderedSet<T> otherAsOrderedSet && AreEqualityComparersEqual(this, otherAsOrderedSet)))
                {
                    if (_count >= otherAsCollection.Count)
                    {
                        return false;
                    }
                    // this has strictly less than number of items in other, so the following
                    // check suffices for proper subset.
                    return IsSubsetOfHashSetWithSameEC(otherAsCollection);
                }
            }

            ElementCount result = CheckUniqueAndUnfoundElements(other, false);
            return result.UniqueCount == _count && result.UnfoundCount > 0;
        }

        public bool IsSubsetOf(IEnumerable<T> other)
        {
            if (other == null)
            {
                throw new ArgumentNullException(nameof(other));
            }

            // The empty set is a subset of any set
            if (_count == 0)
            {
                return true;
            }

            // Set is always a subset of itself
            if (other == this)
            {
                return true;
            }

            // faster if other has unique elements according to this equality comparer; so check 
            // that other is a hashset using the same equality comparer.
            if (other is ICollection<T> otherAsCollection &&
                ((other is HashSet<T> otherAsSet && AreEqualityComparersEqual(this, otherAsSet)) ||
                (other is OrderedSet<T> otherAsOrderedSet && AreEqualityComparersEqual(this, otherAsOrderedSet))))
            {
                // if this has more elements then it can't be a subset
                if (_count > otherAsCollection.Count)
                {
                    return false;
                }

                // already checked that we're using same equality comparer. simply check that 
                // each element in this is contained in other.
                return IsSubsetOfHashSetWithSameEC(otherAsCollection);
            }
            ElementCount result = CheckUniqueAndUnfoundElements(other, false);
            return result.UniqueCount == _count && result.UnfoundCount >= 0;
        }

        public bool IsSupersetOf(IEnumerable<T> other)
        {
            if (other == null)
            {
                throw new ArgumentNullException(nameof(other));
            }

            // a set is always a superset of itself
            if (other == this)
            {
                return true;
            }

            // try to fall out early based on counts
            if (other is ICollection<T> otherAsCollection)
            {
                // if other is the empty set then this is a superset
                if (otherAsCollection.Count == 0)
                {
                    return true;
                }
                // try to compare based on counts alone if other is a hashset with
                // same equality comparer
                if ((other is HashSet<T> otherAsSet && AreEqualityComparersEqual(this, otherAsSet)) ||
                    (other is OrderedSet<T> otherAsOrderedSet && AreEqualityComparersEqual(this, otherAsOrderedSet)))
                {
                    if (otherAsCollection.Count > _count)
                    {
                        return false;
                    }
                }
            }

            return ContainsAllElements(other);
        }

        public void Move(int fromIndex, int toIndex)
        {
            if ((uint)fromIndex >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(fromIndex), Strings.ArgumentOutOfRange_Index);
            }
            if ((uint)toIndex >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(toIndex), Strings.ArgumentOutOfRange_Index);
            }

            if (fromIndex == toIndex)
            {
                return;
            }

            Slot[] slots = _slots;
            Slot temp = slots[fromIndex];
            RemoveSlotFromBucket(fromIndex);
            int direction = fromIndex < toIndex ? 1 : -1;
            for (int i = fromIndex; i != toIndex; i += direction)
            {
                slots[i] = slots[i + direction];
                UpdateBucketIndex(i + direction, -direction);
            }
            AddSlotToBucket(ref temp, toIndex, _buckets);
            slots[toIndex] = temp;
            ++_version;
        }

        public void MoveRange(int fromIndex, int toIndex, int count)
        {
            if (count == 1)
            {
                Move(fromIndex, toIndex);
                return;
            }

            if ((uint)fromIndex >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(fromIndex), Strings.ArgumentOutOfRange_Index);
            }
            if ((uint)toIndex >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(toIndex), Strings.ArgumentOutOfRange_Index);
            }
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), Strings.ArgumentOutOfRange_NeedNonNegNum);
            }
            if (fromIndex + count > _count)
            {
                throw new ArgumentException(Strings.Argument_InvalidOffLen);
            }
            if (toIndex + count > _count)
            {
                throw new ArgumentException(Strings.Argument_InvalidOffLen);
            }

            if (fromIndex == toIndex || count == 0)
            {
                return;
            }

            Slot[] slots = _slots;
            // Make a copy of the slots to move. Consider using ArrayPool instead to avoid allocations?
            Slot[] slotsToMove = new Slot[count];
            for (int i = 0; i < count; ++i)
            {
                slotsToMove[i] = slots[fromIndex + i];
                RemoveSlotFromBucket(fromIndex + i);
            }

            // Move slots in between
            int direction = 1;
            int amount = count;
            int start = fromIndex;
            int end = toIndex;
            if (fromIndex > toIndex)
            {
                direction = -1;
                amount = -count;
                start = fromIndex + count - 1;
                end = toIndex + count - 1;
            }
            for (int i = start; i != end; i += direction)
            {
                slots[i] = slots[i + amount];
                UpdateBucketIndex(i + amount, -amount);
            }

            int[] buckets = _buckets;
            // Copy slots to destination
            for (int i = 0; i < count; ++i)
            {
                Slot temp = slotsToMove[i];
                AddSlotToBucket(ref temp, toIndex + i, buckets);
                slots[toIndex + i] = temp;
            }
            ++_version;
        }

        public bool Overlaps(IEnumerable<T> other)
        {
            if (other == null)
            {
                throw new ArgumentNullException(nameof(other));
            }

            if (_count == 0)
            {
                return false;
            }

            // set overlaps itself
            if (other == this)
            {
                return true;
            }

            foreach (T element in other)
            {
                if (Contains(element))
                {
                    return true;
                }
            }
            return false;
        }

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
            int count = _count;
            if (index < 0 || index >= count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), Strings.ArgumentOutOfRange_Index);
            }

            // Remove the slot from the bucket
            RemoveSlotFromBucket(index);

            // Decrement the indices > index
            for (int i = index + 1; i < count; ++i)
            {
                UpdateBucketIndex(i, -1);
            }
            Slot[] slots = _slots;
            Array.Copy(slots, index + 1, slots, index, count - index - 1);
            --_count;
            slots[_count] = default;
            ++_version;
        }

        public bool SetEquals(IEnumerable<T> other)
        {
            if (other == null)
            {
                throw new ArgumentNullException(nameof(other));
            }

            // a set is equal to itself
            if (other == this)
            {
                return true;
            }

            if (other is ICollection<T> otherAsCollection)
            {
                // if this count is 0 but other contains at least one element, they can't be equal
                if (_count == 0 && otherAsCollection.Count > 0)
                {
                    return false;
                }
                // faster if other is a hashset and we're using same equality comparer
                if ((other is HashSet<T> otherAsSet && AreEqualityComparersEqual(this, otherAsSet)) ||
                    (other is OrderedSet<T> otherAsOrderedSet && AreEqualityComparersEqual(this, otherAsOrderedSet)))
                {
                    // attempt to return early: since both contain unique elements, if they have 
                    // different counts, then they can't be equal
                    if (_count != otherAsCollection.Count)
                    {
                        return false;
                    }

                    // already confirmed that the sets have the same number of distinct elements, so if
                    // one is a superset of the other then they must be equal
                    return ContainsAllElements(other);
                }
            }
            ElementCount result = CheckUniqueAndUnfoundElements(other, true);
            return result.UniqueCount == _count && result.UnfoundCount == 0;
        }

        public void SymmetricExceptWith(IEnumerable<T> other)
        {
            if (other == null)
            {
                throw new ArgumentNullException(nameof(other));
            }

            // if set is empty, then symmetric difference is other
            if (_count == 0)
            {
                UnionWith(other);
                return;
            }

            // special case this; the symmetric difference of a set with itself is the empty set
            if (other == this)
            {
                Clear();
                return;
            }

            SymmetricExceptWithEnumerable(other);
        }

        public void TrimExcess() => TrimExcess(_count);

        public void TrimExcess(int capacity)
        {
            if (capacity < _count)
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

        #region Explicit Interface Implementation
        bool ICollection<T>.IsReadOnly => false;

        void ICollection<T>.Add(T item) => Add(item);

        void ICollection<T>.CopyTo(T[] array, int arrayIndex)
        {
            if (array == null)
            {
                throw new ArgumentNullException(nameof(array));
            }
            if ((uint)arrayIndex > (uint)array.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(arrayIndex), Strings.ArgumentOutOfRange_NeedNonNegNum);
            }
            int count = _count;
            if (array.Length - arrayIndex < count)
            {
                throw new ArgumentException(Strings.Arg_ArrayPlusOffTooSmall);
            }

            Slot[] slots = _slots;
            for (int i = 0; i < count; ++i)
            {
                array[i + arrayIndex] = slots[i].Value;
            }
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        #endregion

        private Slot[] Resize(int newSize)
        {
            int[] newBuckets = new int[newSize];
            Slot[] newSlots = new Slot[newSize];

            Array.Copy(_slots, newSlots, _count);
            ReinitializeBuckets(newBuckets, newSlots);

            _buckets = newBuckets;
            _slots = newSlots;
            return newSlots;
        }

        private void ReinitializeBuckets(int[] buckets, Slot[] slots)
        {
            int count = _count;
            for (int i = 0; i < count; ++i)
            {
                AddSlotToBucket(ref slots[i], i, buckets);
            }
        }

        private int IndexOf(T item, out int hashCode)
        {
            IEqualityComparer<T> comparer = _comparer;
            hashCode = item != null ? (comparer?.GetHashCode(item) ?? item.GetHashCode()) & 0x7FFFFFFF : 0;
            int[] buckets = _buckets;
            int index = buckets[hashCode % buckets.Length] - 1;
            if (index >= 0)
            {
                if (comparer == null)
                {
                    comparer = EqualityComparer<T>.Default;
                }
                Slot[] slots = _slots;
                int collisionCount = 0;
                do
                {
                    Slot slot = slots[index];
                    if (slot.HashCode == hashCode && comparer.Equals(slot.Value, item))
                    {
                        break;
                    }
                    index = slot.Next;
                    if (collisionCount >= slots.Length)
                    {
                        // The chain of slots forms a loop; which means a concurrent update has happened.
                        // Break out of the loop and throw, rather than looping forever.
                        throw new InvalidOperationException(Strings.InvalidOperation_ConcurrentOperationsNotSupported);
                    }
                    ++collisionCount;
                } while (index >= 0);
            }
            return index;
        }

        private int TryInsert(int? index, T item, bool throwOnExisting)
        {
            int i = IndexOf(item, out int hashCode);
            if (i >= 0)
            {
                if (throwOnExisting)
                {
                    throw new ArgumentException(Strings.Argument_AddingDuplicate);
                }
                else
                {
                    return ~i;
                }
            }

            // Check if resize is needed
            Slot[] slots = _slots;
            int count = _count;
            if (slots.Length == count || count == 1)
            {
                slots = Resize(HashHelpers.ExpandPrime(slots.Length));
            }

            // Increment indices >= index;
            int actualIndex = index ?? count;
            for (i = actualIndex; i < count; ++i)
            {
                UpdateBucketIndex(i, 1);
            }
            Array.Copy(slots, actualIndex, slots, actualIndex + 1, count - actualIndex);

            Slot slot = new Slot { HashCode = hashCode, Value = item };
            AddSlotToBucket(ref slot, actualIndex, _buckets);
            slots[actualIndex] = slot;
            ++_count;
            ++_version;
            return actualIndex;
        }

        // Returns the index of the next slot in the bucket
        private void AddSlotToBucket(ref Slot slot, int slotIndex, int[] buckets)
        {
            ref int b = ref buckets[slot.HashCode % buckets.Length];
            slot.Next = b - 1;
            b = slotIndex + 1;
        }

        private void RemoveSlotFromBucket(int slotIndex)
        {
            Slot[] slots = _slots;
            Slot slot = slots[slotIndex];
            ref int b = ref _buckets[slot.HashCode % _buckets.Length];
            // Bucket was pointing to removed slot. Update it to point to the next in the chain
            if (b == slotIndex + 1)
            {
                b = slot.Next + 1;
            }
            else
            {
                // Start at the slot the bucket points to, and walk the chain until we find the slot with the index we want to remove, then fix the chain
                int i = b - 1;
                int collisionCount = 0;
                while (true)
                {
                    ref Slot e = ref slots[i];
                    if (e.Next == slotIndex)
                    {
                        e.Next = slot.Next;
                        return;
                    }
                    i = e.Next;
                    if (collisionCount >= slots.Length)
                    {
                        // The chain of slots forms a loop; which means a concurrent update has happened.
                        // Break out of the loop and throw, rather than looping forever.
                        throw new InvalidOperationException(Strings.InvalidOperation_ConcurrentOperationsNotSupported);
                    }
                    ++collisionCount;
                }
            }
        }

        private void UpdateBucketIndex(int slotIndex, int incrementAmount)
        {
            Slot[] slots = _slots;
            Slot slot = slots[slotIndex];
            ref int b = ref _buckets[slot.HashCode % _buckets.Length];
            // Bucket was pointing to slot. Increment the index by incrementAmount.
            if (b == slotIndex + 1)
            {
                b += incrementAmount;
            }
            else
            {
                // Start at the slot the bucket points to, and walk the chain until we find the slot with the index we want to increment.
                int i = b - 1;
                int collisionCount = 0;
                while (true)
                {
                    ref Slot e = ref slots[i];
                    if (e.Next == slotIndex)
                    {
                        e.Next += incrementAmount;
                        return;
                    }
                    i = e.Next;
                    if (collisionCount >= slots.Length)
                    {
                        // The chain of slots forms a loop; which means a concurrent update has happened.
                        // Break out of the loop and throw, rather than looping forever.
                        throw new InvalidOperationException(Strings.InvalidOperation_ConcurrentOperationsNotSupported);
                    }
                    ++collisionCount;
                }
            }
        }

        private void SymmetricExceptWithEnumerable(IEnumerable<T> other)
        {
            // Utilizes the unused 32nd bit of the hashCode to avoid allocating a BitArray

            int count = _count;
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
            count = _count;
            Slot[] slots = _slots;
            for (int i = 0; i < count; ++i)
            {
                Slot slot = slots[i];
                // checks if 32nd bit is unset
                if ((slot.HashCode & unchecked((int)0x80000000)) == 0)
                {
                    slots[nextIndex++] = slot;
                }
            }

            // Clears removed slots
            for (int i = nextIndex; i < count; ++i)
            {
                slots[i] = default;
            }

            _count = nextIndex;
            int[] buckets = _buckets;
            Array.Clear(buckets, 0, buckets.Length);
            ReinitializeBuckets(buckets, slots);
            ++_version;
        }

        private void IntersectWithEnumerable(IEnumerable<T> other)
        {
            // Utilizes the unused 32nd bit of the hashCode to avoid allocating a BitArray

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

            int count = _count;
            int nextIndex = 0;
            for (int i = 0; i < count; ++i)
            {
                ref Slot slot = ref slots[i];
                // checks if 32nd bit is set
                if ((slot.HashCode & unchecked((int)0x80000000)) != 0)
                {
                    // unsets the 32nd bit
                    slot.HashCode &= 0x7FFFFFFF;
                    slots[nextIndex++] = slot;
                }
            }

            // Clears removed slots
            for (int i = nextIndex; i < count; ++i)
            {
                slots[i] = default;
            }

            _count = nextIndex;
            int[] buckets = _buckets;
            Array.Clear(buckets, 0, buckets.Length);
            ReinitializeBuckets(buckets, slots);
            ++_version;
        }

        private void ExceptWithEnumerable(IEnumerable<T> other)
        {
            // Utilizes the unused 32nd bit of the hashCode to avoid allocating a BitArray

            int count = _count;
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
                    slots[nextIndex++] = slot;
                }
            }

            // Clears removed slots
            for (int i = nextIndex; i < count; ++i)
            {
                slots[i] = default;
            }

            _count = nextIndex;
            int[] buckets = _buckets;
            Array.Clear(buckets, 0, buckets.Length);
            ReinitializeBuckets(buckets, slots);
            ++_version;
        }

        /// <summary>
        /// Checks if equality comparers are equal. This is used for algorithms that can
        /// speed up if it knows the other item has unique elements. I.e. if they're using 
        /// different equality comparers, then uniqueness assumption between sets break.
        /// </summary>
        /// <param name="set1"></param>
        /// <param name="set2"></param>
        /// <returns></returns>
        private static bool AreEqualityComparersEqual(OrderedSet<T> set1, HashSet<T> set2)
        {
            return set1.Comparer.Equals(set2.Comparer);
        }

        /// <summary>
        /// Checks if equality comparers are equal. This is used for algorithms that can
        /// speed up if it knows the other item has unique elements. I.e. if they're using 
        /// different equality comparers, then uniqueness assumption between sets break.
        /// </summary>
        /// <param name="set1"></param>
        /// <param name="set2"></param>
        /// <returns></returns>
        private static bool AreEqualityComparersEqual(OrderedSet<T> set1, OrderedSet<T> set2)
        {
            return set1.Comparer.Equals(set2.Comparer);
        }

        private bool ContainsAllElements(IEnumerable<T> other)
        {
            foreach (T element in other)
            {
                if (!Contains(element))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Implementation Notes:
        /// If other is a hashset and is using same equality comparer, then checking subset is 
        /// faster. Simply check that each element in this is in other.
        /// 
        /// Note: if other doesn't use same equality comparer, then Contains check is invalid,
        /// which is why callers must take are of this.
        /// 
        /// If callers are concerned about whether this is a proper subset, they take care of that.
        ///
        /// </summary>
        /// <param name="other"></param>
        /// <returns></returns>
        private bool IsSubsetOfHashSetWithSameEC(ICollection<T> other)
        {
            Debug.Assert((other is HashSet<T> otherAsSet && AreEqualityComparersEqual(this, otherAsSet)) ||
                (other is OrderedSet<T> otherAsOrderedSet && AreEqualityComparersEqual(this, otherAsOrderedSet)));
            foreach (T item in this)
            {
                if (!other.Contains(item))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Determines counts that can be used to determine equality, subset, and superset. This
        /// is only used when other is an IEnumerable and not a HashSet. If other is a HashSet
        /// these properties can be checked faster without use of marking because we can assume 
        /// other has no duplicates.
        /// 
        /// The following count checks are performed by callers:
        /// 1. Equals: checks if unfoundCount = 0 and uniqueFoundCount = _count; i.e. everything 
        /// in other is in this and everything in this is in other
        /// 2. Subset: checks if unfoundCount >= 0 and uniqueFoundCount = _count; i.e. other may
        /// have elements not in this and everything in this is in other
        /// 3. Proper subset: checks if unfoundCount > 0 and uniqueFoundCount = _count; i.e
        /// other must have at least one element not in this and everything in this is in other
        /// 4. Proper superset: checks if unfound count = 0 and uniqueFoundCount strictly less
        /// than _count; i.e. everything in other was in this and this had at least one element
        /// not contained in other.
        /// 
        /// An earlier implementation used delegates to perform these checks rather than returning
        /// an ElementCount struct; however this was changed due to the perf overhead of delegates.
        /// </summary>
        /// <param name="other"></param>
        /// <param name="returnIfUnfound">Allows us to finish faster for equals and proper superset
        /// because unfoundCount must be 0.</param>
        /// <returns></returns>
        private ElementCount CheckUniqueAndUnfoundElements(IEnumerable<T> other, bool returnIfUnfound)
        {
            ElementCount result;

            // need special case in case this has no elements. 
            if (_count == 0)
            {
                int numElementsInOther = 0;
                foreach (T item in other)
                {
                    numElementsInOther++;
                    // break right away, all we want to know is whether other has 0 or 1 elements
                    break;
                }
                result.UniqueCount = 0;
                result.UnfoundCount = numElementsInOther;
                return result;
            }

            BitArray bitArray = new BitArray(_count);

            // count of items in other not found in this
            int unfoundCount = 0;
            // count of unique items in other found in this
            int uniqueFoundCount = 0;

            foreach (T item in other)
            {
                int index = IndexOf(item);
                if (index >= 0)
                {
                    if (!bitArray[index])
                    {
                        // item hasn't been seen yet
                        bitArray.Set(index, true);
                        uniqueFoundCount++;
                    }
                }
                else
                {
                    unfoundCount++;
                    if (returnIfUnfound)
                    {
                        break;
                    }
                }
            }

            result.UniqueCount = uniqueFoundCount;
            result.UnfoundCount = unfoundCount;
            return result;
        }

        public struct Enumerator : IEnumerator<T>
        {
            private readonly OrderedSet<T> _orderedSet;
            private readonly int _version;
            private int _index;
            private T _current;

            public T Current => _current;

            object IEnumerator.Current => _current;

            internal Enumerator(OrderedSet<T> orderedSet)
            {
                _orderedSet = orderedSet;
                _version = orderedSet._version;
                _index = 0;
                _current = default;
            }

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                if (_version != _orderedSet._version)
                {
                    throw new InvalidOperationException(Strings.InvalidOperation_EnumFailedVersion);
                }

                if (_index < _orderedSet._count)
                {
                    _current = _orderedSet._slots[_index].Value;
                    ++_index;
                    return true;
                }
                _current = default;
                return false;
            }

            void IEnumerator.Reset()
            {
                if (_version != _orderedSet._version)
                {
                    throw new InvalidOperationException(Strings.InvalidOperation_EnumFailedVersion);
                }

                _index = 0;
                _current = default;
            }
        }
    }
}
