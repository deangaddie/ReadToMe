namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>
    /// The bundled sample clip: a short WAV of a known Project Gutenberg sentence and its text,
    /// embedded in the host (provenance in <c>SampleClip/README.md</c>). The Full check transcribes
    /// it and scores the transcript against <see cref="Text"/>.
    /// </summary>
    public static class SampleClip
    {
        public const string FileName = "sample-clip.wav";

        private static readonly Lazy<string> _text = new(() =>
        {
            using var reader = new StreamReader(Open("Read2Me.SetupCheck.sample-clip.txt"));
            return reader.ReadToEnd().Trim();
        });

        /// <summary>What the clip says.</summary>
        public static string Text => _text.Value;

        /// <summary>The clip's WAV bytes, as a new stream the caller disposes.</summary>
        public static Stream OpenWav() => Open("Read2Me.SetupCheck.sample-clip.wav");

        private static Stream Open(string name) =>
            typeof(SampleClip).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The host is missing its embedded {name}.");
    }
}
