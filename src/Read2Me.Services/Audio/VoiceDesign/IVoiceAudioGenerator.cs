using System.Threading;
using System.Threading.Tasks;

namespace Read2Me.Services.Audio.VoiceDesign
{
    public interface IVoiceAudioGenerator
    {
        Task<VoiceGenerationResult> GenerateAsync(VoiceGenerationRequest request, CancellationToken ct);
    }
}
