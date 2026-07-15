using System;
using System.Collections.Generic;
using System.Linq;
using TextTemplateManager.Common;

namespace TextTemplateManager.Services.Pasting.Strategies
{
    public static class StrategyRegistry
    {
        private static readonly Dictionary<PasteMode, IPasteStrategy> _strategies = new();

        static StrategyRegistry()
        {
            var strategyType = typeof(IPasteStrategy);
            var implementations = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(s => s.GetTypes())
                .Where(p => strategyType.IsAssignableFrom(p) && !p.IsInterface);

            foreach (var type in implementations)
            {
                // Logic: If class name is "JiraStrategy", match to PasteMode.Jira
                string modeName = type.Name.Replace("Strategy", "");
                if (Enum.TryParse<PasteMode>(modeName, true, out var mode))
                {
                    _strategies[mode] = (IPasteStrategy)Activator.CreateInstance(type)!;
                }
            }
        }

        public static IPasteStrategy Get(PasteMode mode)
        {
            if (_strategies.TryGetValue(mode, out var strategy))
                return strategy;

            // Fallback to Plaintext if a new enum is added but no strategy is defined yet
            return _strategies[PasteMode.Plaintext];
        }
    }
}