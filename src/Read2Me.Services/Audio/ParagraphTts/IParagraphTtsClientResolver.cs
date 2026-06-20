using Read2Me.AppData.Entities;

namespace Read2Me.Services.Audio.ParagraphTts
{
    public interface IParagraphTtsClientResolver
    {
        IParagraphTtsClient Resolve(ParagraphTtsServiceType type);
    }
}
