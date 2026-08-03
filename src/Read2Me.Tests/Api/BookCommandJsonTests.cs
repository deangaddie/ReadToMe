using System.Text.Json.Nodes;
using Read2Me.App.Api;
using Xunit;
using Read2Me.Core.Models;
using Read2Me.Data.Enums;

namespace Read2Me.Tests.Api
{
    public class BookCommandJsonTests
    {
        private static readonly ProjectFolderId Folder = new("my-folder");

        private static BookCommand Deserialize(string type, string jsonBody)
        {
            var body = JsonNode.Parse(jsonBody)!.AsObject();
            var ok = BookCommandJson.TryDeserialize(type, body, Folder, out var command, out var error);
            Assert.True(ok, error);
            return command!;
        }

        [Fact]
        public void Registry_contains_every_book_command()
        {
            // Pin the seam: every concrete BookCommand record is reachable by name.
            var expected = typeof(BookCommand).Assembly.GetTypes()
                .Where(t => !t.IsAbstract && typeof(BookCommand).IsAssignableFrom(t))
                .Select(t => t.Name.EndsWith("Command") ? t.Name[..^"Command".Length] : t.Name)
                .OrderBy(n => n)
                .ToList();

            Assert.Equal(expected, BookCommandJson.Names.OrderBy(n => n).ToList());
            Assert.Contains("SetParagraphCharacter", BookCommandJson.Names);
        }

        [Fact]
        public void Folder_id_comes_from_route_not_body()
        {
            var command = (CreateCharacterCommand)Deserialize(
                "CreateCharacter", """{ "name": "Alice", "folderId": "evil-folder" }""");

            Assert.Equal(Folder, command.FolderId);
            Assert.Equal("Alice", command.Name);
        }

        [Fact]
        public void Guids_and_nullables_bind()
        {
            var paragraphId = Guid.NewGuid();
            var characterId = Guid.NewGuid();
            var command = (SetParagraphCharacterCommand)Deserialize(
                "SetParagraphCharacter",
                $$"""{ "paragraphId": "{{paragraphId}}", "characterId": "{{characterId}}" }""");

            Assert.Equal(paragraphId, command.ParagraphId);
            Assert.Equal(characterId, command.CharacterId);
            Assert.Null(command.VoiceInstructions);
        }

        [Fact]
        public void Guid_lists_bind()
        {
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var characterId = Guid.NewGuid();
            var command = (SetParagraphsCharacterCommand)Deserialize(
                "SetParagraphsCharacter",
                $$"""{ "paragraphIds": ["{{first}}", "{{second}}"], "characterId": "{{characterId}}" }""");

            Assert.Equal([first, second], command.ParagraphIds);
            Assert.Equal(characterId, command.CharacterId);
            Assert.Equal(Folder, command.FolderId);
        }

        [Fact]
        public void Enums_bind_from_strings()
        {
            var anchorId = Guid.NewGuid();
            var command = (InsertPauseParagraphCommand)Deserialize(
                "InsertPauseParagraph",
                $$"""{ "anchorItemId": "{{anchorId}}", "position": "Before", "pauseKind": "{{PauseKind.ParagraphPause}}" }""");

            Assert.Equal(anchorId, command.AnchorItemId);
        }

        [Fact]
        public void Parameterless_command_binds_from_empty_body()
        {
            var command = Deserialize("AddChapterTitles", "{}");

            Assert.IsType<AddChapterTitlesCommand>(command);
        }

        [Fact]
        public void Project_scoped_command_binds_with_only_its_own_property()
        {
            // SetNarratorCharacter is the first BookCommand addressing the project itself.
            var characterId = Guid.NewGuid();
            var command = (SetNarratorCharacterCommand)Deserialize(
                "SetNarratorCharacter", $$"""{ "characterId": "{{characterId}}" }""");

            Assert.Equal(characterId, command.CharacterId);
            Assert.Equal(Folder, command.FolderId);
        }

        [Fact]
        public void Explicit_null_binds_as_the_unlink()
        {
            var command = (SetNarratorCharacterCommand)Deserialize(
                "SetNarratorCharacter", """{ "characterId": null }""");

            Assert.Null(command.CharacterId);
        }

        [Fact]
        public void Unknown_type_fails_with_error()
        {
            var ok = BookCommandJson.TryDeserialize(
                "NotACommand", new JsonObject(), Folder, out var command, out var error);

            Assert.False(ok);
            Assert.Null(command);
            Assert.Contains("NotACommand", error);
        }

        [Fact]
        public void Malformed_props_fail_with_error()
        {
            var body = JsonNode.Parse("""{ "paragraphId": "not-a-guid" }""")!.AsObject();
            var ok = BookCommandJson.TryDeserialize(
                "SetParagraphCharacter", body, Folder, out _, out var error);

            Assert.False(ok);
            Assert.NotNull(error);
        }
    }
}
