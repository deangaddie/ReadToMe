using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.AppData.Entities;
using Read2Me.Core.Models;
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
            // Switchable, matching the real llama fork. The default fake model store reports this model
            // loaded, so the switch-and-wait gate is a no-op until a test opts into a switch.
            Model = FakeAiRoutingHandler.DefaultModel,
            SupportsModelSwitch = true,
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
    /// Gives a character an uploaded voice whose reference audio is a real, editable Canonical WAV —
    /// dead air, a tone, dead air — so the voice audio editor has something a filter can visibly change.
    /// Returns the voice's id.
    /// </summary>
    public static async Task<Guid> SeedEditableVoiceAsync(
        IServiceProvider services, string workspaceDir, string folderName, Guid characterId,
        string voiceName = "Alice Voice")
    {
        var folderPath = Path.Combine(workspaceDir, folderName);
        var voiceId = Guid.NewGuid();
        var relativePath = $"voices/{characterId}/{voiceId}-{NameSanitizer.Sanitize(voiceName)}.wav";

        var fullPath = Path.Combine(folderPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, EditableWav());

        var factory = services.GetRequiredService<IProjectDbContextFactory>();
        await using var db = await factory.CreateAsync(folderPath);

        db.Voices.Add(new Data.Entities.Voice
        {
            Id = voiceId,
            CharacterId = characterId,
            Name = voiceName,
            Source = VoiceSource.Uploaded,
            AudioFileName = relativePath,
            CreatedUtc = DateTime.UtcNow,
        });
        db.VoiceRules.Add(new VoiceRule
        {
            Id = Guid.NewGuid(),
            CharacterId = characterId,
            VoiceId = voiceId,
            IsDefault = true,
            Rank = "a0",
        });
        await db.SaveChangesAsync();

        return voiceId;
    }

    /// 24 kHz mono 16-bit: 1 s of silence, 2 s of tone, 1 s of silence. Long enough that a
    /// -35 dB trim clears the 1000 ms voice-scope guard rather than being skipped by it.
    private static byte[] EditableWav()
    {
        const int rate = 24000;
        var samples = new List<short>();
        samples.AddRange(new short[rate]);
        for (var i = 0; i < rate * 2; i++)
            samples.Add((short)(short.MaxValue * 0.5 * Math.Sin(2 * Math.PI * 440 * i / rate)));
        samples.AddRange(new short[rate]);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var dataLen = samples.Count * 2;

        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataLen);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(dataLen);
        foreach (var s in samples) w.Write(s);

        w.Flush();
        return ms.ToArray();
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
