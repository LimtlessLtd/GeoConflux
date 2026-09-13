namespace Geopolitics.Infrastructure.Location;

/// <summary>
/// Finds the earliest place name a text mentions, in one pass over the text rather than one pass per
/// name.
/// <para>
/// This exists because of a limit <see cref="Gazetteer"/> measured on itself and wrote down.
/// <c>FindFirstMention</c> used to search the text once for every searchable spelling, which is
/// linear in the size of the lexicon and runs on every observation the offline enrichment provider
/// sees. At roughly 11,900 spellings that cost 0.7 ms per scan against a 5 ms guard, and
/// <see cref="GazetteerScaleTests"/> said in its failure message what would happen next: <em>"the
/// lexicon needs an index rather than a linear scan."</em> A global lexicon is an order of magnitude
/// larger, so the sevenfold headroom was gone and a faster loop would not have brought it back.
/// </para>
/// <para>
/// An Aho-Corasick automaton is the standard answer and is the one taken. Every spelling goes into a
/// trie; each node gets a fail link to the longest proper suffix of the text matched so far that is
/// also a prefix of some spelling; a scan then advances one character at a time and never revisits a
/// character. The cost becomes the length of the text plus the number of matches, and stops depending
/// on how many spellings there are at all.
/// </para>
/// <para>
/// It is specialised to this job rather than general, and the name says so. It owns the rule about
/// what counts as a mention, because that rule has to be applied <em>during</em> the scan: a match
/// that landed inside a longer word is not a mention, and letting one win because it happened to
/// start earliest is the exact bug the word-boundary handling was written to prevent.
/// </para>
/// </summary>
internal sealed class PlaceNameAutomaton
{
    /// <summary>The root, which is also the state a scan returns to when nothing matches.</summary>
    private const int Root = 0;

    /// <summary>No transition, no match, no link. One sentinel, because every array here is an index.</summary>
    private const int None = -1;

    // The goto function, held as compressed sparse rows: the children of node n are the entries from
    // ChildStart[n] to ChildStart[n + 1], ascending by character so they can be binary searched.
    //
    // A dictionary per node would be the obvious representation and is the wrong one at this size.
    // The global lexicon builds around two million nodes, and two million dictionaries is hundreds of
    // megabytes of overhead to store a couple of children each.
    private readonly int[] _childStart;
    private readonly char[] _childCharacter;
    private readonly int[] _childNode;

    private readonly int[] _fail;

    /// <summary>Which term ends at this node, or <see cref="None"/>.</summary>
    private readonly int[] _match;

    /// <summary>
    /// The nearest node reachable by fail links that ends a term, or <see cref="None"/>. This is what
    /// makes a scan report every spelling that ends here rather than only the longest: at the end of
    /// "Bab el Mandeb" both that and "Mandeb" have matched, and only one of them is the state the
    /// automaton is actually in.
    /// </summary>
    private readonly int[] _outputLink;

    private readonly int[] _termLength;
    private readonly int _longestTerm;

    private PlaceNameAutomaton(
        int[] childStart,
        char[] childCharacter,
        int[] childNode,
        int[] fail,
        int[] match,
        int[] outputLink,
        int[] termLength,
        int longestTerm)
    {
        _childStart = childStart;
        _childCharacter = childCharacter;
        _childNode = childNode;
        _fail = fail;
        _match = match;
        _outputLink = outputLink;
        _termLength = termLength;
        _longestTerm = longestTerm;
    }

    /// <summary>How many states the automaton holds. Reported so its cost can be measured rather than guessed at.</summary>
    public int States => _fail.Length;

    /// <summary>
    /// Builds the automaton over these terms, which must already be folded the way the text will be.
    /// <para>
    /// Where two terms are identical the earlier one wins, which preserves the ordering the caller
    /// established. The caller sorts longest first, so the surviving duplicate is the one the linear
    /// scan would also have found.
    /// </para>
    /// </summary>
    public static PlaceNameAutomaton Build(IReadOnlyList<string> terms)
    {
        var order = new int[terms.Count];

        for (var index = 0; index < order.Length; index++)
        {
            order[index] = index;
        }

        // Sorted so the trie can be built by appending: with the terms in ordinal order, a new child
        // is always the largest character seen at its node so far, so insertion only ever has to look
        // at the last child rather than search the whole sibling list. Ties keep the caller's order,
        // which is what lets the first of two identical terms win.
        Array.Sort(order, (first, second) =>
        {
            var comparison = string.CompareOrdinal(terms[first], terms[second]);
            return comparison != 0 ? comparison : first.CompareTo(second);
        });

        var trie = new Trie(terms.Count);
        var termLength = new int[terms.Count];
        var longestTerm = 0;

        foreach (var index in order)
        {
            var term = terms[index];
            termLength[index] = term.Length;

            if (term.Length == 0)
            {
                continue;
            }

            longestTerm = Math.Max(longestTerm, term.Length);
            trie.Add(term, index);
        }

        var (childStart, childCharacter, childNode, match) = trie.Compress();
        var (fail, outputLink) = Link(childStart, childCharacter, childNode, match);

        return new PlaceNameAutomaton(
            childStart, childCharacter, childNode, fail, match, outputLink, termLength, longestTerm);
    }

    /// <summary>
    /// The term that the earliest mention in this text names, or <see cref="None"/> when it names
    /// none.
    /// <para>
    /// Earliest wins because reporting states where something happened before it lists which other
    /// places reacted to it. Where two spellings start at the same character the longer one wins, so
    /// "Strait of Hormuz" is preferred over the "Hormuz" inside it.
    /// </para>
    /// </summary>
    public int FindFirst(string text)
    {
        var state = Root;
        var bestStart = int.MaxValue;
        var bestLength = 0;
        var bestTerm = None;

        for (var position = 0; position < text.Length; position++)
        {
            var character = text[position];

            while (state != Root && Step(state, character) == None)
            {
                state = _fail[state];
            }

            var next = Step(state, character);
            state = next == None ? Root : next;

            for (var output = _match[state] != None ? state : _outputLink[state];
                 output != None;
                 output = _outputLink[output])
            {
                var term = _match[output];
                var length = _termLength[term];
                var start = position - length + 1;

                if (start > bestStart || (start == bestStart && length <= bestLength))
                {
                    continue;
                }

                if (!IsWholeMention(text, start, length))
                {
                    continue;
                }

                bestStart = start;
                bestLength = length;
                bestTerm = term;
            }

            // Nothing further can improve on this. A match ending at any later character starts at
            // most the longest term back from it, so once the scan is a whole term's width past the
            // best start, every remaining match must start after it. Stopping here rather than at the
            // first match is what lets a longer spelling beginning at the same character still win.
            if (bestTerm != None && position >= bestStart + _longestTerm)
            {
                break;
            }
        }

        return bestTerm;
    }

    /// <summary>
    /// Every distinct term this text mentions, up to a limit, in no particular order.
    /// <para>
    /// Used for context rather than for placement: the other places a report names are evidence about
    /// which of several identically-named places it means. It is capped because a long report naming
    /// two hundred places says no more about the one being resolved than the first handful of them
    /// do, and the cap is what stops a pathological input from making resolution expensive.
    /// </para>
    /// </summary>
    public List<int> FindAll(string text, int limit)
    {
        var found = new List<int>();

        if (limit <= 0)
        {
            return found;
        }

        var seen = new HashSet<int>();
        var state = Root;

        for (var position = 0; position < text.Length; position++)
        {
            var character = text[position];

            while (state != Root && Step(state, character) == None)
            {
                state = _fail[state];
            }

            var next = Step(state, character);
            state = next == None ? Root : next;

            for (var output = _match[state] != None ? state : _outputLink[state];
                 output != None;
                 output = _outputLink[output])
            {
                var term = _match[output];
                var start = position - _termLength[term] + 1;

                if (!IsWholeMention(text, start, _termLength[term]) || !seen.Add(term))
                {
                    continue;
                }

                found.Add(term);

                if (found.Count == limit)
                {
                    return found;
                }
            }
        }

        return found;
    }

    /// <summary>The node reached from this one on this character, or <see cref="None"/>.</summary>
    private int Step(int state, char character)
    {
        var low = _childStart[state];
        var high = _childStart[state + 1] - 1;

        while (low <= high)
        {
            var middle = (int)(((uint)low + (uint)high) >> 1);
            var at = _childCharacter[middle];

            if (at == character)
            {
                return _childNode[middle];
            }

            if (at < character)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return None;
    }

    /// <summary>Computes the fail and output links by breadth-first walk, which is what makes this an automaton rather than a trie.</summary>
    private static (int[] Fail, int[] OutputLink) Link(
        int[] childStart,
        char[] childCharacter,
        int[] childNode,
        int[] match)
    {
        var states = childStart.Length - 1;
        var fail = new int[states];
        var outputLink = new int[states];

        Array.Fill(outputLink, None);

        var queue = new Queue<int>();

        // The children of the root fail to the root: there is no shorter suffix than nothing.
        for (var edge = childStart[Root]; edge < childStart[Root + 1]; edge++)
        {
            fail[childNode[edge]] = Root;
            queue.Enqueue(childNode[edge]);
        }

        while (queue.Count > 0)
        {
            var state = queue.Dequeue();

            for (var edge = childStart[state]; edge < childStart[state + 1]; edge++)
            {
                var character = childCharacter[edge];
                var child = childNode[edge];
                var candidate = fail[state];

                while (candidate != Root && Transition(childStart, childCharacter, childNode, candidate, character) == None)
                {
                    candidate = fail[candidate];
                }

                var target = Transition(childStart, childCharacter, childNode, candidate, character);
                fail[child] = target == None || target == child ? Root : target;
                outputLink[child] = match[fail[child]] != None ? fail[child] : outputLink[fail[child]];

                queue.Enqueue(child);
            }
        }

        return (fail, outputLink);
    }

    private static int Transition(int[] childStart, char[] childCharacter, int[] childNode, int state, char character)
    {
        var low = childStart[state];
        var high = childStart[state + 1] - 1;

        while (low <= high)
        {
            var middle = (int)(((uint)low + (uint)high) >> 1);
            var at = childCharacter[middle];

            if (at == character)
            {
                return childNode[middle];
            }

            if (at < character)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return None;
    }

    /// <summary>
    /// True when a match at this position is a mention rather than a fragment of a longer word.
    /// <para>
    /// Without this the two-letter aliases are catastrophic: <c>US</c> occurs inside "because",
    /// "thus", and "Russia", and because the search takes the earliest match across every term, one
    /// such hit outranks the real place name later in the sentence.
    /// </para>
    /// </summary>
    private static bool IsWholeMention(string text, int index, int length) =>
        !RunsIntoWord(text, index - 1, text[index])
        && !RunsIntoWord(text, index + length, text[index + length - 1]);

    private static bool RunsIntoWord(string text, int neighbourIndex, char termEdge)
    {
        if (neighbourIndex < 0 || neighbourIndex >= text.Length)
        {
            return false;
        }

        var neighbour = text[neighbourIndex];

        if (!char.IsLetterOrDigit(neighbour))
        {
            return false;
        }

        // Scripts written without spaces have no word edge to find. Requiring one would mean never
        // matching 美国 inside 在美国发生, or 서울 inside 서울에서 — which is to say, never matching
        // them at all, since that is how those languages are written.
        return !WritesWithoutWordBreaks(neighbour) && !WritesWithoutWordBreaks(termEdge);
    }

    /// <summary>
    /// Whether this character belongs to a script written without spaces between words.
    /// <para>
    /// Internal because <see cref="Gazetteer"/> needs the same answer for a different question. Here
    /// it decides that a match needs no word edge around it; there it decides how long a spelling has
    /// to be before it is worth hunting for. Both follow from the one fact, and two copies of this
    /// list would eventually disagree about it.
    /// </para>
    /// </summary>
    internal static bool WritesWithoutWordBreaks(char character) => character
        is (>= '぀' and <= 'ヿ')      // Hiragana and Katakana
        or (>= '㐀' and <= '䶿')      // CJK unified ideographs, extension A
        or (>= '一' and <= '鿿')      // CJK unified ideographs
        or (>= '가' and <= '힯')      // Hangul syllables, which take particles unspaced
        or (>= '豈' and <= '﫿')      // CJK compatibility ideographs
        or (>= '฀' and <= '๿');     // Thai

    /// <summary>
    /// The trie under construction, as parallel arrays rather than objects.
    /// <para>
    /// Children are held as a linked list during the build and flattened afterwards, because the two
    /// phases want different things: building wants to append a child without moving its siblings,
    /// and scanning wants siblings adjacent so they can be binary searched.
    /// </para>
    /// </summary>
    private sealed class Trie(int terms)
    {
        private int[] _firstChild = Fresh(terms);
        private int[] _lastChild = Fresh(terms);
        private int[] _nextSibling = Fresh(terms);
        private char[] _character = new char[Math.Max(16, terms)];
        private int[] _match = Fresh(terms);
        private int _count = 1;

        public void Add(string term, int index)
        {
            var node = Root;

            foreach (var character in term)
            {
                // The terms arrive in ordinal order, so a character that is not the last child added
                // here is a new child and is larger than every existing one. That is what keeps
                // insertion constant time without a per-node lookup structure.
                var last = _lastChild[node];

                node = last != None && _character[last] == character
                    ? last
                    : Append(node, character);
            }

            // First wins. Two spellings can fold to one string, and the caller's order decides which
            // of them the match belongs to.
            if (_match[node] == None)
            {
                _match[node] = index;
            }
        }

        public (int[] ChildStart, char[] ChildCharacter, int[] ChildNode, int[] Match) Compress()
        {
            var childStart = new int[_count + 1];
            var childCharacter = new char[_count - 1];
            var childNode = new int[_count - 1];
            var edge = 0;

            for (var node = 0; node < _count; node++)
            {
                childStart[node] = edge;

                for (var child = _firstChild[node]; child != None; child = _nextSibling[child])
                {
                    childCharacter[edge] = _character[child];
                    childNode[edge] = child;
                    edge++;
                }
            }

            childStart[_count] = edge;

            return (childStart, childCharacter, childNode, _match[.._count]);
        }

        private int Append(int parent, char character)
        {
            var node = _count++;

            if (node >= _firstChild.Length)
            {
                Grow(node + 1);
            }

            _firstChild[node] = None;
            _lastChild[node] = None;
            _nextSibling[node] = None;
            _match[node] = None;
            _character[node] = character;

            if (_lastChild[parent] == None)
            {
                _firstChild[parent] = node;
            }
            else
            {
                _nextSibling[_lastChild[parent]] = node;
            }

            _lastChild[parent] = node;

            return node;
        }

        private void Grow(int wanted)
        {
            var size = Math.Max(wanted, _firstChild.Length * 2);

            Array.Resize(ref _firstChild, size);
            Array.Resize(ref _lastChild, size);
            Array.Resize(ref _nextSibling, size);
            Array.Resize(ref _match, size);
            Array.Resize(ref _character, size);
        }

        private static int[] Fresh(int terms)
        {
            var array = new int[Math.Max(16, terms)];
            Array.Fill(array, None);

            return array;
        }
    }
}
