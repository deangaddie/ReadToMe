using Read2Me.App.Api;
using Read2Me.AppData.Entities;
using Xunit;

namespace Read2Me.Tests.App
{
    /// <summary>
    /// What every provider schema owes the settings form, whatever the provider: a label and a kind
    /// the form can render, a recommended default or an explicit "server default" (nullable), and no
    /// per-config knob (connection, chunking) masquerading as a per-voice one. Each provider's own
    /// field list is its *ProviderSettingsTests class.
    /// </summary>
    public class ProviderSettingsSchemaTests
    {
        private static readonly string[] Kinds = ["number", "boolean", "enum", "string", "text"];

        public static TheoryData<string, SettingsSchemaDto> AllSchemas()
        {
            var data = new TheoryData<string, SettingsSchemaDto>();
            foreach (var type in Enum.GetValues<ParagraphTtsServiceType>())
                data.Add($"paragraph-tts/{type}", ProviderSettingsSchema.ParagraphTts(type));
            foreach (var type in Enum.GetValues<VoiceDesignServiceType>())
                data.Add($"voice-design/{type}", ProviderSettingsSchema.VoiceDesign(type));
            return data;
        }

        [Theory]
        [MemberData(nameof(AllSchemas))]
        public void Every_field_renders_has_a_default_or_is_nullable_and_is_per_voice(string name, SettingsSchemaDto schema)
        {
            Assert.NotEmpty(schema.Fields);
            foreach (var field in schema.Fields)
            {
                Assert.False(string.IsNullOrEmpty(field.Label), $"{name}.{field.Key} has no label");
                Assert.Contains(field.Kind, Kinds);
                Assert.True(field.Default is not null || field.Nullable, $"{name}.{field.Key} has no default and is not nullable");
            }
            var keys = schema.Fields.Select(f => f.Key).ToList();
            Assert.DoesNotContain("baseUrl", keys);
            Assert.DoesNotContain("maxChunkChars", keys);
            Assert.Equal(keys.Count, keys.Distinct().Count());
        }
    }
}
