namespace Read2Me.Services.Llm
{
    /// <summary>
    /// Incrementally scans streamed LLM content for the completion of the first top-level
    /// JSON value of the expected kind ('{' object or '[' array). Lets the caller stop
    /// reading the response stream once the answer has closed — reasoning models sometimes
    /// keep generating (more "thinking") after the JSON instead of emitting EOS.
    /// Text before the first opening bracket is skipped; brackets inside JSON strings
    /// (including escaped quotes) are ignored.
    /// </summary>
    public sealed class JsonCompletionScanner
    {
        private readonly char _open;
        private readonly char _close;
        private int _depth;
        private bool _started;
        private bool _inString;
        private bool _escaped;

        private JsonCompletionScanner(char open, char close)
        {
            _open = open;
            _close = close;
        }

        public static JsonCompletionScanner ForObject() => new('{', '}');
        public static JsonCompletionScanner ForArray() => new('[', ']');

        public bool Completed { get; private set; }

        /// <summary>Feeds the next content chunk. Returns true once the value has closed.</summary>
        public bool Append(string chunk)
        {
            if (Completed)
                return true;

            foreach (var c in chunk)
            {
                if (!_started)
                {
                    if (c == _open)
                    {
                        _started = true;
                        _depth = 1;
                    }
                    continue;
                }

                if (_inString)
                {
                    if (_escaped) _escaped = false;
                    else if (c == '\\') _escaped = true;
                    else if (c == '"') _inString = false;
                    continue;
                }

                if (c == '"')
                {
                    _inString = true;
                }
                else if (c == _open)
                {
                    _depth++;
                }
                else if (c == _close && --_depth == 0)
                {
                    Completed = true;
                    return true;
                }
            }
            return false;
        }
    }
}
