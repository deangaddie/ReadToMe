namespace Read2Me.Services.Audio.VoiceDesign
{
    public interface IVoiceAudioGenerator
    {
        Task<VoiceGenerationResult> GenerateAsync(VoiceGenerationRequest request, CancellationToken ct);
    }
}
