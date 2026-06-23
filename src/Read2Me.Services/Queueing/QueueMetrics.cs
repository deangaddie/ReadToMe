namespace Read2Me.Services.Queueing
{
    /// <summary>
    /// Queue-agnostic rolling average of seconds-per-completion. Thread-safe.
    /// </summary>
    public sealed class QueueMetrics
    {
        private int _completedCount;
        private double _averageSecondsPerCompletion;
        private readonly Lock _lock = new();

        public int CompletedCount
        {
            get { lock (_lock) return _completedCount; }
        }

        public double AverageSecondsPerCompletion
        {
            get { lock (_lock) return _averageSecondsPerCompletion; }
        }

        public void RecordCompletion(double elapsedSeconds)
        {
            lock (_lock)
            {
                _completedCount++;
                _averageSecondsPerCompletion = _completedCount == 1
                    ? elapsedSeconds
                    : (_averageSecondsPerCompletion * (_completedCount - 1) + elapsedSeconds) / _completedCount;
            }
        }

        public (int completed, double avg) Read()
        {
            lock (_lock)
                return (_completedCount, _averageSecondsPerCompletion);
        }
    }
}
