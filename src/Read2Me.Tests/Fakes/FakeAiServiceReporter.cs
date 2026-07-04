using System;
using System.Collections.Generic;
using Read2Me.Services.Health;

namespace Read2Me.Tests.Fakes
{
    /// <summary>
    /// Capturing <see cref="IAiServiceReporter"/> for tests. <see cref="Managed"/> controls whether
    /// <see cref="ReportFailure"/> reports and returns true (registry hit) or false (remote miss).
    /// </summary>
    public sealed class FakeAiServiceReporter : IAiServiceReporter
    {
        public List<string> Successes { get; } = new();
        public List<(string BaseUrl, Exception Ex)> Failures { get; } = new();

        /// <summary>When true, ReportFailure behaves as a managed hit (reports + returns true). Default false.</summary>
        public bool Managed { get; set; }

        public void ReportSuccess(string baseUrl) => Successes.Add(baseUrl);

        public bool ReportFailure(string baseUrl, Exception ex)
        {
            Failures.Add((baseUrl, ex));
            return Managed;
        }
    }
}
