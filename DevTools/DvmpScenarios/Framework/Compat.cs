using System.Collections.Generic;

namespace Dvmp.Scenarios
{
    // What newer frameworks have and net48 lacks.
    internal static class Compat
    {
        public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
        {
            key = pair.Key;
            value = pair.Value;
        }
    }
}
