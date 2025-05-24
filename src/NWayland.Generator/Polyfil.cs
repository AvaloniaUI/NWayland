using System;
using System.Collections.Generic;

namespace System.Runtime.CompilerServices
{
    internal sealed class IsExternalInit { }
}

namespace NWayland.Generator
{
    static class Extensions
    {
        public static IEnumerable<T> SkipLast<T>(this IEnumerable<T> en, int count)
        {
            var q = new Queue<T>();
            foreach (var el in en)
            {
                q.Enqueue(el);
                if (q.Count > count)
                    yield return q.Dequeue();
            }

            if (q.Count < count)
                throw new InvalidOperationException($"Sequence contained {q.Count} elements");
        }
    }
}