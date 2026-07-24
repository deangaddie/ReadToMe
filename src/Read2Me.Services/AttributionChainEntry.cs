using System.Text.Json.Serialization;
using Read2Me.AppData.Entities;

namespace Read2Me.Services
{
    /// <summary>
    /// One rung of the attribution escalation chain as stored: which LLM config runs it, whether that
    /// rung runs with model thinking enabled, and which attribution prompt style it asks with. The
    /// same config may appear several times (fast/thinking, full/simple) — entries are identified by
    /// the (config, thinking, style) triple, not by config alone.
    /// <para>
    /// A null <see cref="Style"/> means "inherit the config's own
    /// <see cref="LlmServerConfig.PromptStyle"/>". Every chain stored before the style was per-rung
    /// deserialises that way, so those chains keep behaving exactly as they did.
    /// </para>
    /// </summary>
    public sealed record AttributionChainEntry(
        [property: JsonPropertyName("id")] int ConfigId,
        [property: JsonPropertyName("thinking")] bool Thinking,
        [property: JsonPropertyName("style")]
        [property: JsonConverter(typeof(JsonStringEnumConverter))]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        AttributionPromptStyle? Style = null);

    /// <summary>
    /// A chain entry with its config resolved. What the walk consumes. <see cref="Style"/> is the
    /// <em>effective</em> style — the entry's own when it set one, else the config's.
    /// </summary>
    public sealed record ResolvedChainStep(
        LlmServerConfig Config, bool Thinking, AttributionPromptStyle Style);
}
