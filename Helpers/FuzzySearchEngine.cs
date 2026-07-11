using System;
using System.Linq;

namespace TextTemplateManager.Helpers
{
    public static class FuzzySearchEngine
    {
        public static bool IsMatch(string query, string target, bool isStrict)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            if (string.IsNullOrWhiteSpace(target)) return false;

            // Split query into words to make order irrelevant
            var queryTerms = query.ToLowerInvariant()
                                  .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var targetLower = target.ToLowerInvariant();

            // Check if EVERY word in the query finds a match in the target
            return queryTerms.All(term =>
            {
                if (isStrict)
                {
                    // Strict: Must contain the exact word (case-insensitive)
                    return targetLower.Contains(term);
                }
                else
                {
                    // Fuzzy: Characters of the word appear in order
                    return IsFuzzyMatch(term, targetLower);
                }
            });
        }

        private static bool IsFuzzyMatch(string term, string target)
        {
            // Shortcut: if it's a direct match, it's definitely a fuzzy match
            if (target.Contains(term)) return true;

            int tIdx = 0;
            int termIdx = 0;

            while (tIdx < target.Length && termIdx < term.Length)
            {
                if (target[tIdx] == term[termIdx])
                    termIdx++;

                tIdx++;
            }
            return termIdx == term.Length;
        }
    }
}
