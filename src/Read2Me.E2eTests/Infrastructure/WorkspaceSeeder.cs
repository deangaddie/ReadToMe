using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.AppData.Entities;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.E2eTests.Infrastructure.FakeAi;
using Read2Me.Services;
using Read2Me.Services.Audio.ParagraphTts.Settings;
using Read2Me.Services.Audio.SemanticSimilarity.Settings;
using Read2Me.Services.Audio.Transcription.Settings;
using Read2Me.Services.Audio.VoiceDesign.Settings;
using Read2Me.TestUtils;

namespace Read2Me.E2eTests.Infrastructure;

/// <summary>
/// Seeds app.db service configs (pointing every external service at the fake hosts)
/// and creates fixture project folders with seeded project.db content.
/// </summary>
public static class WorkspaceSeeder
{
    public static async Task SeedServiceConfigsAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        await sp.GetRequiredService<LlmSettingsService>().CreateConfigAsync(new LlmServerConfig
        {
            Name = "fake",
            BaseUrl = "http://fake-llm",
        });

        await sp.GetRequiredService<TranscriptionSettingsService>().CreateConfigAsync(new TranscriptionServiceConfig
        {
            Name = "fake",
            Type = TranscriptionServiceType.LocalWhisper,
            SettingsJson = JsonSerializer.Serialize(new LocalWhisperSettings { BaseUrl = "http://fake-whisper" }),
        });

        await sp.GetRequiredService<ParagraphTtsSettingsService>().CreateConfigAsync(new ParagraphTtsServiceConfig
        {
            Name = "fake",
            Type = ParagraphTtsServiceType.VoxCpm2,
            SettingsJson = JsonSerializer.Serialize(new VoxCpm2ParagraphTtsSettings { BaseUrl = "http://fake-tts" }),
        });

        await sp.GetRequiredService<SemanticSimilaritySettingsService>().CreateConfigAsync(new SemanticSimilarityServiceConfig
        {
            Name = "fake",
            Type = SemanticSimilarityServiceType.MiniLmL6,
            SettingsJson = JsonSerializer.Serialize(new SemanticSimilaritySettings { BaseUrl = "http://fake-similarity" }),
        });

        await sp.GetRequiredService<VoiceDesignSettingsService>().CreateConfigAsync(new VoiceDesignServiceConfig
        {
            Name = "fake",
            Type = VoiceDesignServiceType.VoxCpm2,
            SettingsJson = JsonSerializer.Serialize(new VoxCpm2VoiceDesignSettings { BaseUrl = "http://fake-voicedesign" }),
        });
    }

    /// <summary>
    /// Creates a project folder with a seeded project.db: one volume/chapter, one known
    /// character, three paragraphs — narration, an unattributed character line, narration.
    /// Returns the builder for named-id lookups.
    /// </summary>
    public static async Task<BookHierarchyBuilder> SeedProjectAsync(
        IServiceProvider services, string workspaceDir, string folderName,
        string title, string author, string characterName = "Alice")
    {
        var factory = services.GetRequiredService<IProjectDbContextFactory>();
        var folderPath = Path.Combine(workspaceDir, folderName);

        var builder = new BookHierarchyBuilder(() => factory.CreateAsync(folderPath));
        builder
            .WithProject(title: title, author: author)
            .WithCharacter(characterName, new Character { Id = Guid.NewGuid(), Name = characterName })
            .AddVolume("v1", v => v
                .AddChapter("ch1", c => c
                    .AddParagraph("p1", p => p
                        .AddNarration("n1", "It was a dark and stormy night."))
                    .AddParagraph("p2", p => p
                        .AddRawItem("line1", Data.Enums.ParagraphItemType.Character,
                            "“Hello there,” she said.", characterId: null))
                    .AddParagraph("p3", p => p
                        .AddNarration("n2", "The rain kept falling."))));
        await builder.BuildAsync();
        return builder;
    }

    /// <summary>
    /// Gives the built-in Narrator character a cloned voice with on-disk reference audio
    /// and the default VoiceRule, so narration items resolve a voice and can be synthesised.
    /// </summary>
    public static async Task SeedNarratorVoiceAsync(
        IServiceProvider services, string workspaceDir, string folderName)
    {
        var folderPath = Path.Combine(workspaceDir, folderName);
        var voicesDir = Path.Combine(folderPath, "voices");
        Directory.CreateDirectory(voicesDir);
        await File.WriteAllBytesAsync(Path.Combine(voicesDir, "narrator.wav"), FakeAiResponses.SilentWav());

        var factory = services.GetRequiredService<IProjectDbContextFactory>();
        await using var db = await factory.CreateAsync(folderPath);

        var voice = new Data.Entities.Voice
        {
            Id = Guid.NewGuid(),
            CharacterId = ProjectDbContext.NarratorId,
            Name = "Narrator Voice",
            Source = VoiceSource.Uploaded,
            AudioFileName = "voices/narrator.wav",
        };
        db.Voices.Add(voice);
        db.VoiceRules.Add(new VoiceRule
        {
            Id = Guid.NewGuid(),
            CharacterId = ProjectDbContext.NarratorId,
            VoiceId = voice.Id,
            IsDefault = true,
            Rank = "a0", // floor rank, same as CreateVoiceHandler's default rule
        });
        await db.SaveChangesAsync();
    }
}
