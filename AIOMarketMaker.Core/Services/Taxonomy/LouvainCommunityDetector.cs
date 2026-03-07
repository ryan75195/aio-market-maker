namespace AIOMarketMaker.Core.Services.Taxonomy;

public class LouvainCommunityDetector : ICommunityDetector
{
    public IEnumerable<Community> Detect(
        IEnumerable<WeightedEdge> edges, int nodeCount, double resolution = 2.0)
    {
        if (nodeCount == 0)
        {
            return Enumerable.Empty<Community>();
        }

        var edgeList = edges.ToList();
        if (edgeList.Count == 0)
        {
            return Enumerable.Empty<Community>();
        }

        // Build adjacency: node -> list of (neighbor, weight)
        var adjacency = new Dictionary<int, List<(int neighbor, double weight)>>();
        for (var i = 0; i < nodeCount; i++)
        {
            adjacency[i] = new List<(int, double)>();
        }

        var totalWeight = 0.0;
        foreach (var edge in edgeList)
        {
            adjacency[edge.NodeA].Add((edge.NodeB, edge.Weight));
            adjacency[edge.NodeB].Add((edge.NodeA, edge.Weight));
            totalWeight += edge.Weight;
        }

        // Precompute node degrees
        var degree = new double[nodeCount];
        for (var i = 0; i < nodeCount; i++)
        {
            degree[i] = adjacency[i].Sum(e => e.weight);
        }

        // Initial assignment: each node in its own community
        var communityOf = new int[nodeCount];
        for (var i = 0; i < nodeCount; i++)
        {
            communityOf[i] = i;
        }

        // Track sum of degrees for each community (Sigma_tot)
        var communityDegreeSum = new Dictionary<int, double>();
        for (var i = 0; i < nodeCount; i++)
        {
            communityDegreeSum[i] = degree[i];
        }

        // Phase 1: Local moves — iterate until no improvement
        var m = totalWeight;
        var improved = true;
        while (improved)
        {
            improved = false;
            for (var node = 0; node < nodeCount; node++)
            {
                var currentCommunity = communityOf[node];
                var bestCommunity = currentCommunity;
                var bestGain = 0.0;

                var ki = degree[node];

                // Compute sum of edge weights from node to each neighbor community
                var weightToCommunity = new Dictionary<int, double>();
                foreach (var (neighbor, weight) in adjacency[node])
                {
                    var nc = communityOf[neighbor];
                    weightToCommunity[nc] =
                        weightToCommunity.GetValueOrDefault(nc) + weight;
                }

                // Weight from node to its own community (excluding self-loops)
                var kiIn = weightToCommunity.GetValueOrDefault(currentCommunity);

                // Sigma_tot of current community excluding this node
                var sigmaTotCurrent = communityDegreeSum.GetValueOrDefault(currentCommunity) - ki;

                // Cost of removing node from current community
                // remove_cost = ki_in / m - resolution * ki * sigmaTotCurrent / (2 * m^2)
                var removeCost = kiIn / m - resolution * ki * sigmaTotCurrent / (2.0 * m * m);

                // Evaluate each neighbor community
                foreach (var (candidateCommunity, kiC) in weightToCommunity)
                {
                    if (candidateCommunity == currentCommunity)
                    {
                        continue;
                    }

                    var sigmaTotCandidate = communityDegreeSum.GetValueOrDefault(candidateCommunity);

                    // Gain of adding node to candidate community
                    // add_gain = kiC / m - resolution * ki * sigmaTotCandidate / (2 * m^2)
                    var addGain = kiC / m - resolution * ki * sigmaTotCandidate / (2.0 * m * m);

                    // Net gain = gain of adding - cost of removing
                    var gain = addGain - removeCost;

                    if (gain > bestGain)
                    {
                        bestGain = gain;
                        bestCommunity = candidateCommunity;
                    }
                }

                if (bestCommunity != currentCommunity)
                {
                    // Update community degree sums
                    communityDegreeSum[currentCommunity] -= ki;
                    if (communityDegreeSum[currentCommunity] < 1e-12)
                    {
                        communityDegreeSum.Remove(currentCommunity);
                    }

                    communityDegreeSum[bestCommunity] =
                        communityDegreeSum.GetValueOrDefault(bestCommunity) + ki;

                    communityOf[node] = bestCommunity;
                    improved = true;
                }
            }
        }

        // Build result communities
        var groups = new Dictionary<int, List<int>>();
        for (var i = 0; i < nodeCount; i++)
        {
            var comm = communityOf[i];
            if (!groups.TryGetValue(comm, out var list))
            {
                list = new List<int>();
                groups[comm] = list;
            }

            list.Add(i);
        }

        return groups.Values
            .Select((members, idx) => new Community(idx, members))
            .ToList();
    }
}
