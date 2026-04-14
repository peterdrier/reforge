using System.Collections;

namespace Reforge;

/// <summary>
/// Macro-scale structural metrics computed from a <see cref="FileDependencyGraph"/>:
/// strongly-connected components (cycles), propagation cost, core size, betweenness
/// centrality for fix-suggestion ranking.
/// </summary>
public static class StructuralAnalysis
{
    /// <summary>
    /// Tarjan's algorithm, iterative form to avoid stack overflow on large graphs.
    /// Returns SCCs as lists of file indices, ordered so that if SCC A can reach
    /// SCC B, then A appears before B (reverse topological order of condensation).
    /// </summary>
    public static List<int[]> FindStronglyConnectedComponents(IReadOnlyList<HashSet<int>> adj)
    {
        int n = adj.Count;
        var index = new int[n];
        var lowlink = new int[n];
        var onStack = new bool[n];
        for (int i = 0; i < n; i++) index[i] = -1;

        var stack = new Stack<int>();
        var result = new List<int[]>();
        int nextIndex = 0;

        // Iterative DFS state: (node, enumerator over its successors)
        var work = new Stack<(int Node, IEnumerator<int> Succ)>();

        for (int start = 0; start < n; start++)
        {
            if (index[start] != -1) continue;

            index[start] = nextIndex;
            lowlink[start] = nextIndex;
            nextIndex++;
            stack.Push(start);
            onStack[start] = true;
            work.Push((start, adj[start].GetEnumerator()));

            while (work.Count > 0)
            {
                var (v, succ) = work.Peek();
                if (succ.MoveNext())
                {
                    int w = succ.Current;
                    if (index[w] == -1)
                    {
                        index[w] = nextIndex;
                        lowlink[w] = nextIndex;
                        nextIndex++;
                        stack.Push(w);
                        onStack[w] = true;
                        work.Push((w, adj[w].GetEnumerator()));
                    }
                    else if (onStack[w])
                    {
                        if (index[w] < lowlink[v]) lowlink[v] = index[w];
                    }
                }
                else
                {
                    // All successors visited; check if v is an SCC root.
                    if (lowlink[v] == index[v])
                    {
                        var scc = new List<int>();
                        while (true)
                        {
                            int w = stack.Pop();
                            onStack[w] = false;
                            scc.Add(w);
                            if (w == v) break;
                        }
                        result.Add(scc.ToArray());
                    }
                    work.Pop();
                    // Propagate lowlink up.
                    if (work.Count > 0)
                    {
                        var parent = work.Peek();
                        if (lowlink[v] < lowlink[parent.Node])
                            lowlink[parent.Node] = lowlink[v];
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Propagation cost = fraction of file pairs (A,B) where A transitively depends on B.
    /// Computed via SCC condensation + reverse-topo bitset union.
    /// Also returns the reachable set per file for downstream use.
    /// </summary>
    public static (double PropagationCost, BitArray[] Reachable) ComputePropagationCost(
        IReadOnlyList<HashSet<int>> adj, List<int[]> sccs)
    {
        int n = adj.Count;
        if (n == 0) return (0, Array.Empty<BitArray>());

        // Map each file to its SCC index.
        var fileToScc = new int[n];
        for (int s = 0; s < sccs.Count; s++)
            foreach (var f in sccs[s])
                fileToScc[f] = s;

        // Build condensation DAG.
        int k = sccs.Count;
        var dagAdj = new HashSet<int>[k];
        for (int i = 0; i < k; i++) dagAdj[i] = new HashSet<int>();
        for (int v = 0; v < n; v++)
        {
            int sv = fileToScc[v];
            foreach (var w in adj[v])
            {
                int sw = fileToScc[w];
                if (sv != sw) dagAdj[sv].Add(sw);
            }
        }

        // Reachable files per SCC, as a bitset of file indices.
        var sccReachable = new BitArray[k];

        // Tarjan outputs SCCs in reverse topological order — sinks first, sources last.
        // sccs[0] is a sink (its successors in the DAG, if any, have indices < 0, i.e.
        // already processed). Iterate forward so each SCC's successors are ready.
        for (int s = 0; s < k; s++)
        {
            var bits = new BitArray(n);
            // Every file inside this SCC is reachable from every file inside this SCC.
            foreach (var f in sccs[s]) bits.Set(f, true);
            // Fold in reachability of successor SCCs.
            foreach (var succ in dagAdj[s])
                bits.Or(sccReachable[succ]);
            sccReachable[s] = bits;
        }

        // Per-file reachability = reachability of its SCC, minus itself.
        var reachable = new BitArray[n];
        long reachedPairs = 0;
        for (int v = 0; v < n; v++)
        {
            var bits = sccReachable[fileToScc[v]];
            reachable[v] = bits;
            // Count set bits, then subtract 1 for self.
            int c = 0;
            for (int i = 0; i < n; i++) if (bits.Get(i)) c++;
            reachedPairs += (c - 1);
        }

        double propagationCost = n > 1 ? (double)reachedPairs / ((long)n * (n - 1)) : 0;
        return (propagationCost, reachable);
    }

    /// <summary>
    /// The "core" is the largest non-trivial SCC — the mutually-dependent heart of the codebase.
    /// Returns the indices of files in the core, or empty if the biggest SCC is size 1.
    /// </summary>
    public static int[] FindCoreScc(List<int[]> sccs)
    {
        int[] best = Array.Empty<int>();
        foreach (var scc in sccs)
        {
            if (scc.Length < 2) continue;
            if (scc.Length > best.Length) best = scc;
        }
        return best;
    }

    /// <summary>
    /// Brandes' betweenness centrality, restricted to a subgraph. Used to rank
    /// files inside the core SCC: the ones with the highest betweenness route
    /// the most dependency paths and are the highest-leverage targets for refactoring.
    /// </summary>
    public static Dictionary<int, double> ComputeBetweenness(
        int[] subgraphNodes, IReadOnlyList<HashSet<int>> adj)
    {
        var nodeSet = new HashSet<int>(subgraphNodes);
        var result = new Dictionary<int, double>();
        foreach (var v in subgraphNodes) result[v] = 0;

        foreach (var s in subgraphNodes)
        {
            var stack = new Stack<int>();
            var predecessors = new Dictionary<int, List<int>>();
            var sigma = new Dictionary<int, long>();
            var distance = new Dictionary<int, int>();
            foreach (var v in subgraphNodes)
            {
                predecessors[v] = new List<int>();
                sigma[v] = 0;
                distance[v] = -1;
            }
            sigma[s] = 1;
            distance[s] = 0;

            var queue = new Queue<int>();
            queue.Enqueue(s);
            while (queue.Count > 0)
            {
                var v = queue.Dequeue();
                stack.Push(v);
                foreach (var w in adj[v])
                {
                    if (!nodeSet.Contains(w)) continue;
                    if (distance[w] < 0)
                    {
                        distance[w] = distance[v] + 1;
                        queue.Enqueue(w);
                    }
                    if (distance[w] == distance[v] + 1)
                    {
                        sigma[w] += sigma[v];
                        predecessors[w].Add(v);
                    }
                }
            }

            var delta = new Dictionary<int, double>();
            foreach (var v in subgraphNodes) delta[v] = 0;
            while (stack.Count > 0)
            {
                var w = stack.Pop();
                foreach (var v in predecessors[w])
                {
                    delta[v] += ((double)sigma[v] / sigma[w]) * (1 + delta[w]);
                }
                if (w != s) result[w] += delta[w];
            }
        }

        return result;
    }

    /// <summary>
    /// Percentile of an integer sample (0 &lt;= p &lt;= 1).
    /// Uses nearest-rank method; returns 0 for empty input.
    /// </summary>
    public static int Percentile(IReadOnlyList<int> sortedAscending, double p)
    {
        if (sortedAscending.Count == 0) return 0;
        int rank = (int)Math.Ceiling(p * sortedAscending.Count) - 1;
        if (rank < 0) rank = 0;
        if (rank >= sortedAscending.Count) rank = sortedAscending.Count - 1;
        return sortedAscending[rank];
    }
}
