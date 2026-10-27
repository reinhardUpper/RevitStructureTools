using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace RevitStructureTools
{
    // Revit geometry wrappers are cached by identity: two equal-looking objects
    // are not interchangeable when they came from different geometry options.
    internal sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
    {
        public static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();
        public bool Equals(T x, T y) { return ReferenceEquals(x, y); }
        public int GetHashCode(T value) { return RuntimeHelpers.GetHashCode(value); }
    }

    internal sealed class ReferencePairCache<TLeft, TRight, TValue>
        where TLeft : class where TRight : class
    {
        private readonly Dictionary<TLeft, Dictionary<TRight, TValue>> values
            = new Dictionary<TLeft, Dictionary<TRight, TValue>>(ReferenceComparer<TLeft>.Instance);

        public bool TryGet(TLeft left, TRight right, out TValue value)
        {
            if (values.TryGetValue(left, out Dictionary<TRight, TValue> row))
                return row.TryGetValue(right, out value);
            value = default(TValue);
            return false;
        }

        public void Add(TLeft left, TRight right, TValue value)
        {
            if (!values.TryGetValue(left, out Dictionary<TRight, TValue> row))
                values.Add(left, row = new Dictionary<TRight, TValue>(ReferenceComparer<TRight>.Instance));
            row[right] = value;
        }

        public void Clear() { values.Clear(); }
    }
}
