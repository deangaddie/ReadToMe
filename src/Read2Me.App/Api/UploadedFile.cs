using Microsoft.AspNetCore.Http;

namespace Read2Me.App.Api
{
    /// <summary>
    /// The one way a multipart upload is read off a request (cover image, voice audio): the
    /// <c>file</c> field, its client base name (a path in it would be both a traversal and a broken
    /// link), a lower-cased extension checked against the endpoint's whitelist, and a size cap.
    /// A refusal is the client's error and answers 400 with the reason.
    /// </summary>
    public static class UploadedFile
    {
        public sealed record Accepted(IFormFile File, string FileName, string Extension);

        public static async Task<(Accepted? File, IResult? Refusal)> ReadAsync(
            HttpRequest request, IReadOnlySet<string> extensions, long maxBytes, string sizeMessage,
            CancellationToken ct = default)
        {
            if (!request.HasFormContentType)
                return (null, Refuse("Expected multipart form data."));

            IFormFile? file;
            try
            {
                file = (await request.ReadFormAsync(ct)).Files.GetFile("file");
            }
            catch (InvalidDataException ex)
            {
                // A malformed multipart body (no boundary parts, truncated) is the client's error.
                return (null, Refuse(ex.Message));
            }
            if (file is null)
                return (null, Refuse("Field 'file' is required."));

            var fileName = Path.GetFileName(file.FileName);
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            if (!extensions.Contains(extension))
                return (null, Refuse($"Unsupported format. Use {string.Join(", ", extensions.Order())}."));
            if (file.Length > maxBytes)
                return (null, Refuse(sizeMessage));

            return (new Accepted(file, fileName, extension), null);
        }

        private static IResult Refuse(string message) =>
            Results.Problem(message, statusCode: StatusCodes.Status400BadRequest);
    }
}
