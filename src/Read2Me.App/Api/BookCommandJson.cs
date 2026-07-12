using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Read2Me.Core.Models;

namespace Read2Me.App.Api
{
    /// <summary>
    /// Maps the commands endpoint's <c>type</c> discriminator onto the concrete
    /// <see cref="BookCommand"/> records. The discriminator is the record name minus
    /// the <c>Command</c> suffix. FolderId always comes from the route: whatever the
    /// body carries under <c>folderId</c> is overwritten before binding, so a request
    /// can never write into a project other than the one in its URL.
    /// </summary>
    public static class BookCommandJson
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter(), new ProjectFolderIdJsonConverter() },
        };

        private static readonly Dictionary<string, Type> Map =
            typeof(BookCommand).Assembly.GetTypes()
                .Where(t => !t.IsAbstract && typeof(BookCommand).IsAssignableFrom(t))
                .ToDictionary(
                    t => t.Name.EndsWith("Command", StringComparison.Ordinal)
                        ? t.Name[..^"Command".Length]
                        : t.Name,
                    t => t);

        public static IReadOnlyCollection<string> Names => Map.Keys;

        public static bool TryDeserialize(
            string typeName, JsonObject body, ProjectFolderId folder,
            out BookCommand? command, out string? error)
        {
            command = null;
            if (!Map.TryGetValue(typeName, out var type))
            {
                error = $"Unknown command type '{typeName}'. Known types: {string.Join(", ", Map.Keys.OrderBy(n => n))}.";
                return false;
            }

            body.Remove("type");
            body["folderId"] = folder.Value;
            try
            {
                command = (BookCommand?)body.Deserialize(type, Options);
            }
            catch (JsonException ex)
            {
                error = $"Invalid properties for command '{typeName}': {ex.Message}";
                return false;
            }

            if (command is null)
            {
                error = $"Command '{typeName}' could not be bound.";
                return false;
            }

            error = null;
            return true;
        }

        private sealed class ProjectFolderIdJsonConverter : JsonConverter<ProjectFolderId>
        {
            public override ProjectFolderId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
                new(reader.GetString() ?? string.Empty);

            public override void Write(Utf8JsonWriter writer, ProjectFolderId value, JsonSerializerOptions options) =>
                writer.WriteStringValue(value.Value);
        }
    }
}
