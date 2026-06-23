namespace Read2Me.Services.Audio.Assembly
{
    public abstract record ConcatEntry
    {
        public sealed record Audio(string RelativePath) : ConcatEntry;
        public sealed record Silence(int Milliseconds) : ConcatEntry;
    }
}
