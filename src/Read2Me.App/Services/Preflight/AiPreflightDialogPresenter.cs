using Read2Me.Services.Health;

namespace Read2Me.App.Services.Preflight
{
    /// <summary>
    /// UI-agnostic state machine behind <c>AiPreflightDialog</c> and the <c>/api/preflight</c>
    /// run. Runs the plan sequentially — conflicts stopped first to free VRAM, then required
    /// services started (docker start → health poll → warm-up, one at a time so the 8 GB GPU never
    /// double-loads). The first failure aborts the rest; the razor dialog only maps rows to chrome
    /// and the API coordinator only maps <see cref="StageChanged"/> to hub messages.
    /// </summary>
    public sealed class AiPreflightDialogPresenter(IAiServiceControl control)
    {
        public enum ServiceStage { Pending, Stopping, Stopped, Starting, Ready, Failed }

        public sealed class Row(DockerAiService service, bool isConflict)
        {
            public DockerAiService Service { get; } = service;

            /// <summary>True for a running GPU service the task does not need (will be stopped).</summary>
            public bool IsConflict { get; } = isConflict;

            public ServiceStage Stage { get; internal set; } = ServiceStage.Pending;
            public string? Error { get; internal set; }
        }

        private readonly List<Row> _rows = [];

        /// <summary>Conflicts first (stop order), then the services to start.</summary>
        public IReadOnlyList<Row> Rows => _rows;

        public bool IsWorking { get; private set; }
        public bool HasFailed { get; private set; }
        public string? FailureMessage { get; private set; }

        /// <summary>Raised on every row/stage transition; the dialog calls StateHasChanged.</summary>
        public event Action? Changed;

        /// <summary>Raised with the row that just moved stage — the per-service feed the API pushes.</summary>
        public event Action<Row>? StageChanged;

        private void Move(Row row, ServiceStage stage)
        {
            row.Stage = stage;
            StageChanged?.Invoke(row);
            Changed?.Invoke();
        }

        public void Load(AiPreflightPlan plan)
        {
            _rows.Clear();
            _rows.AddRange(plan.Conflicts.Select(s => new Row(s, isConflict: true)));
            _rows.AddRange(plan.ToStart.Select(i => new Row(i.Service, isConflict: false)));
        }

        /// <summary>
        /// Stop conflicts, then start required services. Returns true when every op succeeded.
        /// A conflict that fails to stop aborts everything — its VRAM cannot be freed, so
        /// starting more GPU services would only make things worse.
        /// </summary>
        public async Task<bool> RunAsync(CancellationToken ct)
        {
            IsWorking = true;
            HasFailed = false;
            FailureMessage = null;
            Changed?.Invoke();
            try
            {
                foreach (var row in _rows)
                {
                    Move(row, row.IsConflict ? ServiceStage.Stopping : ServiceStage.Starting);

                    var result = row.IsConflict
                        ? await control.ShutdownAsync(row.Service, ct)
                        : await control.StartAsync(row.Service, ct);

                    if (!result.Succeeded)
                    {
                        row.Error = result.Error ?? "Operation failed.";
                        HasFailed = true;
                        var verb = row.IsConflict ? "stop" : "start";
                        FailureMessage = $"Failed to {verb} {row.Service.Name}: {row.Error}";
                        Move(row, ServiceStage.Failed);
                        return false;
                    }

                    Move(row, row.IsConflict ? ServiceStage.Stopped : ServiceStage.Ready);
                }

                return true;
            }
            finally
            {
                IsWorking = false;
                Changed?.Invoke();
            }
        }
    }
}
