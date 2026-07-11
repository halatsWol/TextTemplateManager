using System;
using System.Collections.Generic;

namespace TextTemplateManager.Helpers
{
    public static class EnumerableExtensions
    {
        public static IEnumerable<T> SelectManyRecursive<T>(
            this IEnumerable<T> source,
            Func<T, IEnumerable<T>> childSelector)
        {
            foreach (var item in source)
            {
                yield return item;
                var children = childSelector(item);
                if (children != null)
                {
                    foreach (var child in children.SelectManyRecursive(childSelector))
                        yield return child;
                }
            }
        }
    }
}
