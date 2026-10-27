using System.Collections.Generic;
using System.Linq;

namespace RevitStructureTools
{
    internal static class DecisionRules
    {
        public static string ChooseGroup(IDictionary<string, int> votes, out string reason)
        {
            if (votes.Count == 0)
            {
                reason = "Все группы пустые или параметр недоступен";
                return null;
            }
            int maximum = votes.Values.Max();
            string[] winners = votes.Where(pair => pair.Value == maximum).Select(pair => pair.Key).ToArray();
            reason = winners.Length == 1 ? null : "Одинаковая максимальная частота разных групп";
            return winners.Length == 1 ? winners[0] : null;
        }

        public static T ChooseHost<T>(IEnumerable<T> candidates, bool lowestFirst,
            System.Func<T, double> score, System.Func<T, double?> minimumZ) where T : class
        {
            T[] items = candidates.ToArray();
            if (lowestFirst)
            {
                T[] withZ = items.Where(item => minimumZ(item).HasValue).ToArray();
                if (withZ.Length != 0)
                    return withZ.OrderBy(item => minimumZ(item).Value).ThenByDescending(score).First();
            }
            return items.OrderByDescending(score).FirstOrDefault();
        }
    }
}
