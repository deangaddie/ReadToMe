using Read2Me.Services.Health;

namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>
    /// One check as every row of it reads: its id, group, name and tier, what it expects, the fix to
    /// offer when a verdict names none, and for a config row the Settings section that owns it. Each
    /// row is built from it, so a Not run row matches the row a check would have made.
    /// </summary>
    internal sealed record SetupCheckDefinition(
        string Id,
        SetupCheckGroup Group,
        string Name,
        SetupCheckTier Tier,
        string Expected,
        SetupFixHint Fix,
        SetupSettingsLink? Settings = null,
        bool Managed = false)
    {
        /// <summary>A row of this check; <paramref name="fix"/> null offers the check's own.</summary>
        public SetupCheckRow Row(SetupCheckResult result, string found, string? note = null, SetupFixHint? fix = null) =>
            new(Id, Group, Name, Tier, result, Expected, found, note, fix ?? Fix, Managed, Rerun: null);

        public SetupCheckRow Passed(string found, string? note = null, SetupFixHint? fix = null) =>
            Row(SetupCheckResult.Passed, found, note, fix);

        public SetupCheckRow Failed(string found, SetupFixHint? fix = null, string? note = null) =>
            Row(SetupCheckResult.Failed, found, note, fix);

        /// <summary>Not run, for one reason and one way on.</summary>
        public SetupCheckRow NotRun(string found, SetupFixHint fix) => Row(SetupCheckResult.NotRun, found, null, fix);
    }

    /// <summary>
    /// The fixed checks, in table order, and the Docker services check of each container, which the
    /// registry lists.
    /// </summary>
    internal static class SetupChecks
    {
        private const string ConfigExpected = "An active config with a parseable base URL";
        private const string ChainExpected = "Every config in the attribution chain has a parseable base URL";

        public static readonly SetupCheckDefinition Workspace = new(
            SetupCheckIds.Workspace, SetupCheckGroup.WorkspaceAndTools, "Workspace", SetupCheckTier.Quick,
            $"An absolute, writable folder ({WorkspaceStart.Key})",
            new SetupFixHint($"Set {WorkspaceStart.Key} to an absolute, writable folder, then restart the app.", null, null));

        /// <summary>A Full-tier row: the Quick check judges -version, the Full check the encode.</summary>
        public static readonly SetupCheckDefinition Ffmpeg = new(
            SetupCheckIds.Ffmpeg, SetupCheckGroup.WorkspaceAndTools, "ffmpeg", SetupCheckTier.Full,
            "ffmpeg -version and ffprobe -version",
            new SetupFixHint("Set the ffmpeg path to an ffmpeg with ffprobe beside it.", SetupSettingsLink.AudioProcessing, null));

        public static readonly SetupCheckDefinition Docker = new(
            SetupCheckIds.Docker, SetupCheckGroup.WorkspaceAndTools, "docker CLI", SetupCheckTier.Quick,
            "docker on PATH",
            new SetupFixHint("Install Docker Desktop so docker is on PATH, then restart the app.", null, "docker --version"));

        // Full-tier: the Quick check judges the config, the Full check sends it a real request.
        public static readonly SetupCheckDefinition Llm = Config(SetupCheckIds.Llm, "LLM", ConfigExpected, SetupSettingsLink.Llm);
        public static readonly SetupCheckDefinition AttributionChain = Config(SetupCheckIds.AttributionChain, "Attribution chain", ChainExpected, SetupSettingsLink.Llm);
        public static readonly SetupCheckDefinition ParagraphTts = Config(SetupCheckIds.ParagraphTts, "Paragraph TTS", ConfigExpected, SetupSettingsLink.Tts);
        public static readonly SetupCheckDefinition VoiceDesign = Config(SetupCheckIds.VoiceDesign, "Voice design", ConfigExpected, SetupSettingsLink.VoiceDesign);
        public static readonly SetupCheckDefinition Transcription = Config(SetupCheckIds.Transcription, "Transcription", ConfigExpected, SetupSettingsLink.Transcription);
        public static readonly SetupCheckDefinition Similarity = Config(SetupCheckIds.Similarity, "Similarity", ConfigExpected, SetupSettingsLink.Similarity);

        /// <summary>The AI configs rows, in table order.</summary>
        public static readonly IReadOnlyList<SetupCheckDefinition> AiConfigs =
            [Llm, AttributionChain, ParagraphTts, VoiceDesign, Transcription, Similarity];

        /// <summary>A container's Docker services row: managed, Quick-tier, named for the container.</summary>
        public static SetupCheckDefinition Container(DockerAiService service) => new(
            SetupCheckIds.Container(service.Name), SetupCheckGroup.DockerServices, service.ContainerName, SetupCheckTier.Quick,
            $"{service.ContainerName} running and answering at {service.BaseUrl}",
            ContainerVerdicts.RestartFix(service), Managed: true);

        private static SetupCheckDefinition Config(string id, string name, string expected, SetupSettingsLink settings) => new(
            id, SetupCheckGroup.AiConfigs, name, SetupCheckTier.Full, expected,
            new SetupFixHint($"Change it in {settings.Label}.", settings, null), settings);
    }
}
