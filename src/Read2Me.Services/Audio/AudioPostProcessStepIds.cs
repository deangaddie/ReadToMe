namespace Read2Me.Services.Audio
{
    /// <summary>Well-known step ids shared by step implementations, defaults, and UI.</summary>
    public static class AudioPostProcessStepIds
    {
        public const string DePlosive = "de-plosive";
        public const string Denoise = "denoise";
        public const string HissReduce = "hiss-reduce";
        public const string SilenceTrim = "silence-trim";
        public const string ConsonantSoften = "consonant-soften";
    }
}
