using System.Text.Json;

namespace Read2Me.Services.AiServers
{
    /// <summary>One model a server lists on <c>GET /v1/models</c>, and whether it is the one resident.</summary>
    public sealed record ListedModel(string Id, bool Loaded);

    /// <summary>
    /// What an OpenAI-style server lists on <c>GET /v1/models</c>: llama's autoload router and
    /// audio.cpp. Pure: each caller owns its GET, client, timeout, auth and error contract and hands
    /// the parsed body here. Both server variants are read without being told which one answered:
    /// an item is loaded when llama's <c>status.value</c> is <c>loaded</c> or audio.cpp's
    /// <c>loaded</c> is true.
    /// </summary>
    public sealed class ModelListing
    {
        private ModelListing(IReadOnlyList<ListedModel> models) => Models = models;

        /// <summary>The listed models, in server order.</summary>
        public IReadOnlyList<ListedModel> Models { get; }

        /// <summary>The first loaded model's id; null when none is loaded.</summary>
        public string? LoadedModel => Models.FirstOrDefault(m => m.Loaded)?.Id;

        /// <summary>The listed model with this id; null when the server doesn't list it.</summary>
        public ListedModel? Find(string? id) => Models.FirstOrDefault(m => m.Id == id);

        /// <summary>True only when the server lists the model and reports it loaded.</summary>
        public bool IsLoaded(string? id) => Find(id) is { Loaded: true };

        /// <summary>
        /// Reads <c>data[]</c>. A root that isn't an object or has no <c>data</c> array gives an empty
        /// listing; an item that isn't an object or has no non-blank string <c>id</c> is skipped.
        /// </summary>
        public static ModelListing Parse(JsonElement root)
        {
            var models = new List<ListedModel>();
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object
                        || !item.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
                        continue;

                    var id = idElement.GetString();
                    if (!string.IsNullOrWhiteSpace(id))
                        models.Add(new ListedModel(id, IsLoadedItem(item)));
                }
            }
            return new ModelListing(models);
        }

        private static bool IsLoadedItem(JsonElement item) =>
            (item.TryGetProperty("loaded", out var loaded) && loaded.ValueKind == JsonValueKind.True)
            || (item.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object
                && status.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() == "loaded");
    }
}
