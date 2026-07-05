using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Llm
{
    public class CharacterAttributionParserTests
    {
        [Fact]
        public void CleanJson_ParsesCharacterAndVoice()
        {
            var raw = """{ "character": "Alice", "voice_instructions": "calm, measured" }""";
            Assert.True(CharacterAttributionParser.TryParse(raw, out var result));
            Assert.Equal("Alice", result.Character);
            Assert.Equal("calm, measured", result.VoiceInstructions);
        }

        [Fact]
        public void JsonWithReasoning_ParsesAndCapturesReasoning()
        {
            var raw = """{ "reasoning": "tag after quote", "character": "Alice", "voice_instructions": "calm" }""";
            Assert.True(CharacterAttributionParser.TryParse(raw, out var result));
            Assert.Equal("Alice", result.Character);
            Assert.Equal("tag after quote", result.Reasoning);
        }

        [Fact]
        public void JsonInCodeFence_Parses()
        {
            var raw = "```json\n{ \"character\": \"Bob\", \"voice_instructions\": \"gruff\" }\n```";
            Assert.True(CharacterAttributionParser.TryParse(raw, out var result));
            Assert.Equal("Bob", result.Character);
        }

        [Fact]
        public void JsonWithLeadingProse_Parses()
        {
            var raw = "Here is the result: { \"character\": \"Narrator\", \"voice_instructions\": \"neutral\" } That's it.";
            Assert.True(CharacterAttributionParser.TryParse(raw, out var result));
            Assert.Equal("Narrator", result.Character);
        }

        [Fact]
        public void CharacterIsUnknown_StillParses()
        {
            var raw = """{ "character": "unknown", "voice_instructions": "" }""";
            Assert.True(CharacterAttributionParser.TryParse(raw, out var result));
            Assert.Equal("unknown", result.Character);
        }

        [Fact]
        public void Garbage_ReturnsFalse()
        {
            Assert.False(CharacterAttributionParser.TryParse("not json at all", out _));
        }

        [Fact]
        public void EmptyString_ReturnsFalse()
        {
            Assert.False(CharacterAttributionParser.TryParse(string.Empty, out _));
        }

        [Fact]
        public void MissingCharacterField_ReturnsFalse()
        {
            var raw = """{ "voice_instructions": "calm" }""";
            Assert.False(CharacterAttributionParser.TryParse(raw, out _));
        }

        [Fact]
        public void EmptyCharacterField_ReturnsFalse()
        {
            var raw = """{ "character": "", "voice_instructions": "calm" }""";
            Assert.False(CharacterAttributionParser.TryParse(raw, out _));
        }

        [Fact]
        public void JsonInPlainFence_Parses()
        {
            var raw = "```\n{ \"character\": \"Eve\", \"voice_instructions\": \"whisper\" }\n```";
            Assert.True(CharacterAttributionParser.TryParse(raw, out var result));
            Assert.Equal("Eve", result.Character);
        }

        [Fact]
        public void CaseInsensitiveKeys_Parse()
        {
            var raw = """{ "Character": "Alice", "Voice_Instructions": "loud" }""";
            Assert.True(CharacterAttributionParser.TryParse(raw, out var result));
            Assert.Equal("Alice", result.Character);
        }
    }
}
