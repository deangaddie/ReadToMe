using Read2Me.AppData.Entities;

namespace Read2Me.Services.Llm
{
    /// <summary>
    /// Ensures a switchable llama endpoint has its target model loaded before a request runs.
    /// </summary>
    public interface IModelLoadGate
    {
        /// <summary>
        /// Ensures the config's target model is loaded on a switchable llama endpoint before the caller
        /// proceeds. No-op when <see cref="LlmServerConfig.SupportsModelSwitch"/> is false or the model
        /// is already loaded. Throws <see cref="Read2Me.Core.Exceptions.ModelStillLoadingException"/>
        /// when the load exceeds the budget while the server stays responsive; throws
        /// <see cref="Read2Me.Core.Exceptions.LlmProviderException"/> when the endpoint is genuinely
        /// unreachable or rejects the switch outright.
        /// </summary>
        Task EnsureModelLoadedAsync(LlmServerConfig config, CancellationToken ct);
    }
}
