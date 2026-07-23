using System.Text.Json.Serialization;
using Read2Me.AppData.Entities;

namespace Read2Me.Services
{
    /// <summary>
    /// One rung of the attribution escalation chain as stored: which LLM config runs it, and whether
    /// that rung runs with model thinking enabled. The same config may appear twice (a fast rung and a
    /// thinking rung) — entries are identified by the (config, thinking) pair, not by config alone.
    /// </summary>
    public sealed record AttributionChainEntry(
        [property: JsonPropertyName("id")] int ConfigId,
        [property: JsonPropertyName("thinking")] bool Thinking);

    /// <summary>
    /// A chain entry with its config resolved. What the walk consumes.
    /// </summary>
    public sealed record ResolvedChainStep(LlmServerConfig Config, bool Thinking);
}
