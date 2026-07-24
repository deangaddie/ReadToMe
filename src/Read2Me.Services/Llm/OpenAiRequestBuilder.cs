using System.Text;
using System.Text.Json;
using Read2Me.AppData.Entities;

namespace Read2Me.Services.Llm
{
    /// <summary>
    /// Builds JSON request bodies for OpenAI-compatible chat completion endpoints.
    /// Pure: no I/O, no state.
    /// </summary>
    public static class OpenAiRequestBuilder
    {
        public static string BuildChatBody(
            LlmServerConfig config, string prompt, bool stream, string? jsonSchema = null,
            bool disableThinking = false)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();

                if (!string.IsNullOrWhiteSpace(config.Model))
                    writer.WriteString("model", config.Model);

                writer.WriteBoolean("stream", stream);

                // Ask llama.cpp to measure for us. These have different gates: `timings` rides the
                // final chunk ungated, but `timings_per_token` is the only source of a mid-stream
                // rate and the only way an aborted request retains a measurement. `usage` is opt-in
                // via `stream_options` and is what makes TokensIn a real prompt_tokens.
                writer.WriteBoolean("timings_per_token", true);

                if (stream)
                {
                    writer.WritePropertyName("stream_options");
                    writer.WriteStartObject();
                    writer.WriteBoolean("include_usage", true);
                    writer.WriteEndObject();
                }

                writer.WriteStartArray("messages");
                writer.WriteStartObject();
                writer.WriteString("role", "user");
                writer.WriteString("content", prompt);
                writer.WriteEndObject();
                writer.WriteEndArray();

                // Optional params: emit only when set so the server default applies otherwise.
                if (config.Temperature is { } temp) writer.WriteNumber("temperature", temp);
                if (config.TopP is { } topP) writer.WriteNumber("top_p", topP);
                if (config.MaxTokens is { } maxTokens) writer.WriteNumber("max_tokens", maxTokens);
                if (config.FrequencyPenalty is { } freq) writer.WriteNumber("frequency_penalty", freq);
                if (config.PresencePenalty is { } pres) writer.WriteNumber("presence_penalty", pres);

                // Emitted only when requested: chat_template_kwargs is a llama.cpp extension and
                // strict OpenAI-compatible servers may reject unknown top-level fields.
                if (disableThinking)
                {
                    writer.WritePropertyName("chat_template_kwargs");
                    writer.WriteStartObject();
                    writer.WriteBoolean("enable_thinking", false);
                    writer.WriteEndObject();
                }

                if (!string.IsNullOrWhiteSpace(jsonSchema))
                {
                    writer.WritePropertyName("response_format");
                    writer.WriteStartObject();
                    writer.WriteString("type", "json_schema");
                    writer.WritePropertyName("json_schema");
                    writer.WriteStartObject();
                    writer.WriteString("name", "response");
                    writer.WritePropertyName("schema");
                    using (var doc = JsonDocument.Parse(jsonSchema))
                        doc.RootElement.WriteTo(writer);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }
}
