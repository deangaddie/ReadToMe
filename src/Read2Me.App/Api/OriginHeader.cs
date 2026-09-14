using Microsoft.AspNetCore.Http;
using Read2Me.Services.Mutations;

namespace Read2Me.App.Api
{
    /// <summary>
    /// <c>X-Origin-Id</c>: the per-tab id the web client sends on every call so the receipt the hub
    /// echoes can be matched back to the tab that wrote (spec D6). Absent or not a GUID reads as
    /// unattributed — the header is a courtesy to the caller, never a validation gate.
    /// </summary>
    public static class OriginHeader
    {
        public const string Name = "X-Origin-Id";

        public static Guid Read(HttpRequest request) =>
            Guid.TryParse(request.Headers[Name].ToString(), out var id) ? id : Guid.Empty;

        /// <summary>Stamps the request scope so every mutation it commits carries the caller's origin.</summary>
        public static void Apply(HttpRequest request, MutationOrigin origin)
        {
            var id = Read(request);
            if (id != Guid.Empty) origin.Id = id;
        }
    }
}
