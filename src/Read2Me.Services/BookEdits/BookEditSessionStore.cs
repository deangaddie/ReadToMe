using System.Collections.Concurrent;
using Read2Me.Core.Models;

namespace Read2Me.Services.BookEdits
{
    public enum BookEditRunStatus { Idle, Running, Completed, Cancelled, Failed }

    /// <summary>Where a session's proposal run is: how far, and the rows it has landed.</summary>
    public sealed record BookEditRunSnapshot(
        BookEditRunStatus Status, int Done, int Total, IReadOnlyList<ProposedEdit> Rows, string? Reason);

    /// <summary>
    /// One planned AI book edit held server-side between the plan, propose and apply calls of the
    /// HTTP flow: the <see cref="EditProgram"/> the planner produced, the targets its scope resolved
    /// to, and the state of the (at most one) proposal run over them. The client only ever holds the
    /// session id, so a program never round-trips through a browser.
    /// </summary>
    /// <remarks>
    /// Run state is guarded by a lock rather than being immutable because the run reports progress
    /// from a background task while the API reads snapshots from request threads.
    /// </remarks>
    public sealed class BookEditSession
    {
        private readonly object _gate = new();
        private CancellationTokenSource? _cts;
        private BookEditRunStatus _status = BookEditRunStatus.Idle;
        private int _done;
        private IReadOnlyList<ProposedEdit> _rows = [];
        private string? _reason;

        internal BookEditSession(
            string id, ProjectFolderId folder, EditProgram program, IReadOnlyList<EditTarget> targets,
            string summary, DateTimeOffset expiresAt)
        {
            Id = id;
            Folder = folder;
            Program = program;
            Targets = targets;
            Summary = summary;
            ExpiresAt = expiresAt;
        }

        public string Id { get; }
        public ProjectFolderId Folder { get; }
        public EditProgram Program { get; }
        public IReadOnlyList<EditTarget> Targets { get; }
        public string Summary { get; }
        internal DateTimeOffset ExpiresAt { get; set; }

        public bool IsLlmTransform => Program.Transform.Kind == TransformKind.Llm;

        /// <summary>
        /// A program has one target selector, so within a session an id names at most one target.
        /// </summary>
        public EditTarget? FindTarget(Guid id) => Targets.FirstOrDefault(t => t.Id == id);

        /// <summary>
        /// Claims the session for a proposal run. False when one is already running; a finished run
        /// (completed, cancelled or failed) can be started again, which replaces its rows.
        /// </summary>
        public bool TryBeginRun(out CancellationToken ct)
        {
            lock (_gate)
            {
                if (_status == BookEditRunStatus.Running)
                {
                    ct = CancellationToken.None;
                    return false;
                }

                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                _status = BookEditRunStatus.Running;
                _done = 0;
                _rows = [];
                _reason = null;
                ct = _cts.Token;
                return true;
            }
        }

        public void ReportProgress(int done)
        {
            lock (_gate) _done = done;
        }

        /// <summary>Lands the run's rows — all of them, or the partial set a cancel left behind.</summary>
        public void CompleteRun(IReadOnlyList<ProposedEdit> rows, bool cancelled)
        {
            lock (_gate)
            {
                _rows = rows;
                _done = rows.Count;
                _status = cancelled ? BookEditRunStatus.Cancelled : BookEditRunStatus.Completed;
            }
        }

        public void FailRun(string reason)
        {
            lock (_gate)
            {
                _status = BookEditRunStatus.Failed;
                _reason = reason;
            }
        }

        /// <summary>Asks the running proposal to stop; the run then lands whatever it has computed.</summary>
        public void CancelRun()
        {
            lock (_gate) _cts?.Cancel();
        }

        public BookEditRunSnapshot Snapshot()
        {
            lock (_gate) return new(_status, _done, Targets.Count, _rows, _reason);
        }

        internal void Drop()
        {
            lock (_gate)
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
            }
        }
    }

    public interface IBookEditSessionStore
    {
        BookEditSession Create(ProjectFolderId folder, EditProgram program, IReadOnlyList<EditTarget> targets, string summary);

        /// <summary>The session, or null when unknown, expired, or created for another folder.</summary>
        BookEditSession? TryGet(ProjectFolderId folder, string id);

        /// <summary>Drops the session, cancelling any run in flight. False when there was none.</summary>
        bool Remove(ProjectFolderId folder, string id);
    }

    /// <summary>
    /// In-memory, process-wide (the HTTP API has no circuit). A session lives <see cref="DefaultTtl"/>
    /// past its last lookup — every call in the flow looks it up, so an open review keeps its
    /// session alive and an abandoned one falls away on the next create or lookup.
    /// </summary>
    public sealed class BookEditSessionStore(TimeProvider clock, TimeSpan? ttl = null) : IBookEditSessionStore
    {
        public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(2);

        private readonly ConcurrentDictionary<string, BookEditSession> _sessions = new();
        private readonly TimeSpan _ttl = ttl ?? DefaultTtl;

        public BookEditSession Create(
            ProjectFolderId folder, EditProgram program, IReadOnlyList<EditTarget> targets, string summary)
        {
            Sweep();
            var session = new BookEditSession(
                Guid.NewGuid().ToString("N"), folder, program, targets, summary, clock.GetUtcNow() + _ttl);
            _sessions[session.Id] = session;
            return session;
        }

        public BookEditSession? TryGet(ProjectFolderId folder, string id)
        {
            if (!_sessions.TryGetValue(id, out var session) || session.Folder != folder)
                return null;

            var now = clock.GetUtcNow();
            if (session.ExpiresAt <= now)
            {
                Evict(id);
                return null;
            }

            session.ExpiresAt = now + _ttl;
            return session;
        }

        public bool Remove(ProjectFolderId folder, string id)
        {
            if (!_sessions.TryGetValue(id, out var session) || session.Folder != folder)
                return false;

            Evict(id);
            return true;
        }

        private void Evict(string id)
        {
            if (_sessions.TryRemove(id, out var session))
                session.Drop();
        }

        private void Sweep()
        {
            var now = clock.GetUtcNow();
            foreach (var (id, session) in _sessions)
                if (session.ExpiresAt <= now)
                    Evict(id);
        }
    }
}
