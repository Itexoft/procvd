// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using Procvd.Configuration;

namespace Procvd.Runtime;

public sealed class ProcessDependencyGraph
{
    private ProcessDependencyGraph(
        IReadOnlyList<string> startOrder,
        IReadOnlyDictionary<string, IReadOnlyList<string>> dependents)
    {
        this.StartOrder = startOrder;
        this.Dependents = dependents;
    }

    public IReadOnlyList<string> StartOrder { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Dependents { get; }

    public static ProcessDependencyGraph Build(ResolvedProcessConfig config)
    {
        var groups = config.Groups;
        var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var name in groups.Keys)
        {
            inDegree[name] = 0;
            dependents[name] = [];
        }

        foreach (var (groupName, group) in groups)
        {
            foreach (var dependency in group.Dependencies)
            {
                if (!groups.ContainsKey(dependency))
                    throw new ProcessConfigException($"dependency '{dependency}' for '{groupName}' not found");

                dependents[dependency].Add(groupName);
                inDegree[groupName]++;
            }
        }

        var order = new List<string>(groups.Count);
        var ready = new SortedSet<string>(inDegree.Where(x => x.Value == 0).Select(x => x.Key), StringComparer.Ordinal);

        while (ready.Count != 0)
        {
            var current = ready.Min!;
            ready.Remove(current);
            order.Add(current);

            foreach (var dependent in dependents[current])
            {
                inDegree[dependent]--;

                if (inDegree[dependent] == 0)
                    ready.Add(dependent);
            }
        }

        if (order.Count != groups.Count)
            throw new ProcessConfigException("cycle detected in group dependencies");

        var readOnlyDependents = dependents.ToDictionary(
            x => x.Key,
            IReadOnlyList<string> (x) => x.Value.OrderBy(y => y, StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);

        return new ProcessDependencyGraph(order, readOnlyDependents);
    }
}
