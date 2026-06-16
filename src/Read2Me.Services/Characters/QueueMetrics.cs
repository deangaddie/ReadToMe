using System.Threading;

namespace Read2Me.Services.Characters
{
    internal sealed class QueueMetrics
    {
        private int _completedCount;
        private double _averageSecondsPerParagraph;
        private readonly Lock _lock = new();

        public int CompletedCount
        {
            get { lock (_lock) return _completedCount; }
        }

        public double AverageSecondsPerParagraph
        {
            get { lock (_lock) return _averageSecondsPerParagraph; }
        }

        public void RecordCompletion(double elapsedSeconds)
        {
            lock (_lock)
            {
                _completedCount++;
                _averageSecondsPerParagraph = _completedCount == 1
                    ? elapsedSeconds
                    : (_averageSecondsPerParagraph * (_completedCount - 1) + elapsedSeconds) / _completedCount;
            }
        }

        public (int completed, double avg) Read()
        {
            lock (_lock)
                return (_completedCount, _averageSecondsPerParagraph);
        }
    }
}
