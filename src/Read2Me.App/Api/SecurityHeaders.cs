using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Read2Me.App.Api
{
    /// <summary>
    /// The frame, sniff, opener and resource-policy headers, on every response the host sends
    /// (modern-web-guidance security/security §1.4 and its companion policies).
    /// </summary>
    public static class SecurityHeaders
    {
        /// <summary>
        /// First in the pipeline. The headers are written as the response starts, not on the way in,
        /// so an exception handler's or developer page's <c>Response.Clear()</c> can't drop them from
        /// an error response.
        /// </summary>
        public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
            app.Use(async (context, next) =>
            {
                var response = context.Response;
                response.OnStarting(() =>
                {
                    var headers = response.Headers;
                    // The app never frames itself; another page framing it is clickjacking.
                    headers.XFrameOptions = "DENY";
                    // /workspace serves uploads with types from their extension: never read them as anything else.
                    headers.XContentTypeOptions = "nosniff";
                    // The app's only popups (the help panel's GitHub links) are already rel="noopener".
                    headers["Cross-Origin-Opener-Policy"] = "same-origin";
                    // Another page's <img>, <audio> or <script> of a host response fails to load.
                    headers["Cross-Origin-Resource-Policy"] = "same-origin";
                    return Task.CompletedTask;
                });
                await next(context);
            });
    }
}
