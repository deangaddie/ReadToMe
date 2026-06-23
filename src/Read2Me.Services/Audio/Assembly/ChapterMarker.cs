using System;

namespace Read2Me.Services.Audio.Assembly
{
    public sealed record ChapterMarker(string Title, TimeSpan Start, TimeSpan End);
}
