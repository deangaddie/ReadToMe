using System.Text.Json;
using Read2Me.AppData.Entities;

namespace Read2Me.Services.Llm
{
    /// <summary>
    /// What llama's autoload router says about its models, and the request that loads one. Pure: the
    /// callers own their HTTP. <see cref="ModelLoadGate"/> switches models with it; the Setup check's
    /// Full check probes each model and reloads the one llama had.
    /// </summary>
    public static class LlamaModelStatus
    {
        /// <summary>
        /// True only when the item in <c>data[]</c> whose <c>id</c> equals <paramref name="model"/>
        /// reports <c>status.value == "loaded"</c>. Any other state (unloaded/loading/absent) is false.
        /// </summary>
        public static bool IsLoaded(JsonElement root, string? model) =>
            !string.IsNullOrWhiteSpace(model) && Statuses(root).FirstOrDefault(s => s.Id == model) is { Value: "loaded" };

        /// <summary>The model whose <c>status.value</c> is <c>loaded</c>; null when none is.</summary>
        public static string? LoadedModel(JsonElement root) =>
            Statuses(root).FirstOrDefault(s => s.Value == "loaded").Id;

        /// <summary>
        /// A minimal autoload request: names the model with max_tokens=1 so the router evicts the
        /// current model and loads it with negligible generation cost.
        /// </summary>
        public static string OneTokenRequestBody(string? model, bool disableThinking = false) =>
            OpenAiRequestBuilder.BuildChatBody(
                new LlmServerConfig { Model = model, MaxTokens = 1 }, "ping", stream: false, disableThinking: disableThinking);

        /// <summary>Each <c>data[]</c> item's id and <c>status.value</c> (null when it has none), in order.</summary>
        private static IEnumerable<(string? Id, string? Value)> Statuses(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
                yield break;

            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("id", out var id) ||
                    id.ValueKind != JsonValueKind.String)
                    continue;

                var value = item.TryGetProperty("status", out var status) &&
                    status.ValueKind == JsonValueKind.Object &&
                    status.TryGetProperty("value", out var v) &&
                    v.ValueKind == JsonValueKind.String
                        ? v.GetString()
                        : null;
                yield return (id.GetString(), value);
            }
        }
    }
}
