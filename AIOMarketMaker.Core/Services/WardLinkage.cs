namespace AIOMarketMaker.Core.Services;

/// <summary>
/// Pure C# implementation of Ward's minimum variance agglomerative clustering.
/// Equivalent to scipy.cluster.hierarchy.linkage(method='ward') + fcluster.
/// </summary>
public static class WardLinkage
{
    /// <summary>
    /// Compute condensed pairwise Euclidean distance matrix.
    /// Returns array of length n*(n-1)/2 in row-major order:
    /// index = i*n - i*(i+1)/2 + j - i - 1  for i &lt; j
    /// </summary>
    public static double[] ComputeDistanceMatrix(float[][] vectors)
    {
        var n = vectors.Length;
        var condensed = new double[n * (n - 1) / 2];
        var idx = 0;
        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                var sum = 0.0;
                for (var k = 0; k < vectors[i].Length; k++)
                {
                    var diff = vectors[i][k] - vectors[j][k];
                    sum += diff * diff;
                }
                condensed[idx++] = Math.Sqrt(sum);
            }
        }
        return condensed;
    }

    /// <summary>
    /// Build Ward linkage matrix from condensed distance matrix.
    /// Uses the Lance-Williams recurrence for Ward's method.
    /// Returns array of (n-1) rows, each [cluster_i, cluster_j, distance, new_size].
    /// </summary>
    public static double[][] BuildLinkage(double[] condensedDistances, int n)
    {
        // Working copy of distances -- we'll update as clusters merge
        // Use a full n*n distance matrix for easier index manipulation
        var dist = new double[n, n];
        var idx = 0;
        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                // Ward distance = d^2 * (n_i * n_j) / (n_i + n_j)
                // Initially each cluster has size 1, so factor = 0.5
                var d = condensedDistances[idx++];
                dist[i, j] = d;
                dist[j, i] = d;
            }
        }

        var size = new int[2 * n - 1]; // cluster sizes
        for (var i = 0; i < n; i++)
        {
            size[i] = 1;
        }

        var active = new bool[2 * n - 1]; // which clusters are still active
        for (var i = 0; i < n; i++)
        {
            active[i] = true;
        }

        // Expand distance matrix to hold new clusters
        var fullDist = new double[2 * n - 1, 2 * n - 1];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                fullDist[i, j] = dist[i, j];
            }
        }

        var linkage = new double[n - 1][];

        for (var step = 0; step < n - 1; step++)
        {
            // Find the pair of active clusters with minimum Ward distance
            var minDist = double.MaxValue;
            var minI = -1;
            var minJ = -1;

            for (var i = 0; i < n + step; i++)
            {
                if (!active[i])
                {
                    continue;
                }
                for (var j = i + 1; j < n + step; j++)
                {
                    if (!active[j])
                    {
                        continue;
                    }
                    // Ward distance between clusters i and j
                    var ni = size[i];
                    var nj = size[j];
                    var wardDist = fullDist[i, j] * fullDist[i, j] * 2.0 * ni * nj / (ni + nj);
                    wardDist = Math.Sqrt(wardDist);

                    if (wardDist < minDist)
                    {
                        minDist = wardDist;
                        minI = i;
                        minJ = j;
                    }
                }
            }

            var newCluster = n + step;
            var newSize = size[minI] + size[minJ];
            size[newCluster] = newSize;
            active[minI] = false;
            active[minJ] = false;
            active[newCluster] = true;

            linkage[step] = new[] { (double)minI, (double)minJ, minDist, (double)newSize };

            // Update distances using Lance-Williams formula for Ward's method
            for (var k = 0; k < newCluster; k++)
            {
                if (!active[k])
                {
                    continue;
                }

                var ni = (double)size[minI];
                var nj = (double)size[minJ];
                var nk = (double)size[k];
                var nt = ni + nj + nk;

                var dki = fullDist[k, minI];
                var dkj = fullDist[k, minJ];
                var dij = fullDist[minI, minJ];

                // Lance-Williams recurrence for Ward
                var newDist = Math.Sqrt(
                    ((ni + nk) * dki * dki +
                     (nj + nk) * dkj * dkj -
                     nk * dij * dij) / nt);

                fullDist[k, newCluster] = newDist;
                fullDist[newCluster, k] = newDist;
            }
        }

        return linkage;
    }

    /// <summary>
    /// Cut the dendrogram at a distance threshold to produce flat cluster labels.
    /// Returns array of length n with 0-based cluster labels (no noise / -1 labels).
    /// Equivalent to scipy.cluster.hierarchy.fcluster(criterion='distance').
    /// </summary>
    public static int[] CutDendrogram(double[][] linkageMatrix, double threshold, int n)
    {
        // Each original point starts in its own cluster
        // As we replay merges, we only merge if distance <= threshold
        var parent = new int[2 * n - 1];
        for (var i = 0; i < parent.Length; i++)
        {
            parent[i] = i; // each node is its own root
        }

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]]; // path compression
                x = parent[x];
            }
            return x;
        }

        for (var step = 0; step < linkageMatrix.Length; step++)
        {
            var row = linkageMatrix[step];
            var ci = (int)row[0];
            var cj = (int)row[1];
            var dist = row[2];
            var newCluster = n + step;
            parent[newCluster] = newCluster;

            if (dist <= threshold)
            {
                // Merge: point both old clusters to the new cluster
                parent[Find(ci)] = newCluster;
                parent[Find(cj)] = newCluster;
            }
        }

        // Assign labels: find root for each original point, then renumber
        var roots = new int[n];
        for (var i = 0; i < n; i++)
        {
            roots[i] = Find(i);
        }

        // Renumber roots to consecutive 0-based labels
        var rootToLabel = new Dictionary<int, int>();
        var nextLabel = 0;
        var labels = new int[n];
        for (var i = 0; i < n; i++)
        {
            if (!rootToLabel.TryGetValue(roots[i], out var label))
            {
                label = nextLabel++;
                rootToLabel[roots[i]] = label;
            }
            labels[i] = label;
        }

        return labels;
    }

    /// <summary>
    /// Convenience method: cluster vectors using Ward linkage at the given threshold.
    /// Returns 0-based cluster labels for each input vector.
    /// </summary>
    public static int[] Cluster(float[][] vectors, double threshold)
    {
        if (vectors.Length <= 1)
        {
            return Enumerable.Range(0, vectors.Length).ToArray();
        }

        var distances = ComputeDistanceMatrix(vectors);
        var linkageMatrix = BuildLinkage(distances, vectors.Length);
        return CutDendrogram(linkageMatrix, threshold, vectors.Length);
    }
}
