namespace MimeSpy;

// Aho-Corasick over bytes: finds every occurrence of every pattern in one left-to-right
// pass, however many patterns there are. Stored as a sparse trie with failure links,
// plus a full 256-entry row for the root, where a scan spends most of its time.
internal sealed class PronomAhoCorasick
{
    private readonly int[] _rootNext;

    // State s's edges are _edgeBytes/_edgeTargets[_edgeStart[s].._edgeStart[s + 1]],
    // and its matches (its own patterns plus those of every state its failure links
    // reach) _matches[_matchStart[s].._matchStart[s + 1]].
    private readonly int[] _edgeStart;
    private readonly byte[] _edgeBytes;
    private readonly int[] _edgeTargets;
    private readonly int[] _failure;
    private readonly int[] _matchStart;
    private readonly int[] _matches;

    // patterns[i] is reported as i. Duplicate patterns are fine - each is reported.
    public PronomAhoCorasick(IReadOnlyList<byte[]> patterns)
    {
        // The trie: next[(s << 8) | value] is the state after s on value. State 0 is the
        // root.
        var next = new Dictionary<int, int>();
        var ownMatches = new List<List<int>?> { null };
        for (var i = 0; i < patterns.Count; i++)
        {
            if (patterns[i].Length == 0)
            {
                throw new ArgumentException("Patterns must not be empty.", nameof(patterns));
            }

            var state = 0;
            foreach (var value in patterns[i])
            {
                if (!next.TryGetValue((state << 8) | value, out var child))
                {
                    child = ownMatches.Count;
                    next[(state << 8) | value] = child;
                    ownMatches.Add(null);
                }

                state = child;
            }

            (ownMatches[state] ??= []).Add(i);
        }

        // Sorting the keys groups each state's edges together, in byte order.
        var stateCount = ownMatches.Count;
        var edgeKeys = new int[next.Count];
        next.Keys.CopyTo(edgeKeys, 0);
        Array.Sort(edgeKeys);
        _edgeStart = new int[stateCount + 1];
        _edgeBytes = new byte[edgeKeys.Length];
        _edgeTargets = new int[edgeKeys.Length];
        for (var e = 0; e < edgeKeys.Length; e++)
        {
            _edgeBytes[e] = (byte)edgeKeys[e];
            _edgeTargets[e] = next[edgeKeys[e]];
            _edgeStart[(edgeKeys[e] >> 8) + 1]++;
        }

        for (var state = 0; state < stateCount; state++)
        {
            _edgeStart[state + 1] += _edgeStart[state];
        }

        _rootNext = new int[256];
        for (var e = _edgeStart[0]; e < _edgeStart[1]; e++)
        {
            _rootNext[_edgeBytes[e]] = _edgeTargets[e];
        }

        // Breadth-first: a state's failure link is the longest proper suffix of its path
        // that is also a path in the trie - where its parent's link leads on the same
        // byte. A link always points to a shallower state, so its matches are complete
        // by the time they're copied onto the state that links to it.
        _failure = new int[stateCount];
        var matches = new int[stateCount][];
        matches[0] = [];
        var queue = new Queue<int>();
        queue.Enqueue(0);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            for (var e = _edgeStart[parent]; e < _edgeStart[parent + 1]; e++)
            {
                var child = _edgeTargets[e];
                queue.Enqueue(child);
                var link = parent == 0 ? 0 : Step(_failure[parent], _edgeBytes[e]);
                _failure[child] = link;
                matches[child] = ownMatches[child] is { } own ? [.. own, .. matches[link]] : matches[link];
            }
        }

        _matchStart = new int[stateCount + 1];
        var flatMatches = new List<int>();
        for (var state = 0; state < stateCount; state++)
        {
            _matchStart[state] = flatMatches.Count;
            flatMatches.AddRange(matches[state]);
        }

        _matchStart[stateCount] = flatMatches.Count;
        _matches = [.. flatMatches];
    }

    // The state after reading value in state. Start a scan at state 0.
    public int Step(int state, byte value)
    {
        while (state != 0)
        {
            var end = _edgeStart[state + 1];
            for (var i = _edgeStart[state]; i < end; i++)
            {
                if (_edgeBytes[i] == value)
                {
                    return _edgeTargets[i];
                }
            }

            state = _failure[state];
        }

        return _rootNext[value];
    }

    // Whether any pattern ends at the byte that led to state - cheaper to check on
    // every step than building MatchesAt's span.
    public bool HasMatches(int state) => _matchStart[state + 1] != _matchStart[state];

    // The patterns that end at the byte that led to state.
    public ReadOnlySpan<int> MatchesAt(int state) => _matches.AsSpan(_matchStart[state], _matchStart[state + 1] - _matchStart[state]);
}
