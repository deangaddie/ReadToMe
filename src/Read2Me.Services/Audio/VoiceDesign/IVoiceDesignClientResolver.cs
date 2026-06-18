using Read2Me.AppData.Entities;

namespace Read2Me.Services.Audio.VoiceDesign
{
    public interface IVoiceDesignClientResolver
    {
        IVoiceDesignClient Resolve(VoiceDesignServiceType type);
    }
}
