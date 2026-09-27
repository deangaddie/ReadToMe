using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Read2Me.Tests.Fakes
{
    /// <summary>
    /// An in-memory audio.cpp server: <c>GET /v1/models</c> reports <see cref="LoadedModel"/> as
    /// <c>loaded</c>, and <c>POST /v1/audio/speech</c> records the JSON body and answers a WAV —
    /// or, while <see cref="BusyResponses"/> is above zero, the 503 audio.cpp sends for a different
    /// model's request mid-generation. A speech request loads the model it names, as audio.cpp does.
    /// </summary>
    public sealed class FakeAudioCppHandler : HttpMessageHandler
    {
        public static readonly byte[] Wav = Encoding.ASCII.GetBytes("RIFF____WAVEfmt ");

        public string? LoadedModel { get; set; }
        public int BusyResponses { get; set; }
        public HttpStatusCode? FailWith { get; set; }

        public List<string> Paths { get; } = [];
        public List<JsonObject> SpeechBodies { get; } = [];
        public int ModelsGets { get; private set; }

        /// <summary>Runs inside a speech request, before it answers — to observe overlap.</summary>
        public Func<Task>? DuringSpeech { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Paths) Paths.Add($"{request.Method} {path}");

            if (request.Method == HttpMethod.Get && path == "/v1/models")
            {
                ModelsGets++;
                var models = new[] { "breeze-q8", "breeze-design", "voxcpm2" }
                    .Select(id => new { id, loaded = id == LoadedModel });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { @object = "list", data = models }),
                        Encoding.UTF8, "application/json"),
                };
            }

            if (request.Method == HttpMethod.Post && path == "/v1/audio/speech")
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
                lock (SpeechBodies) SpeechBodies.Add(body);

                if (FailWith is { } status)
                    return new HttpResponseMessage(status);

                if (BusyResponses > 0)
                {
                    BusyResponses--;
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent("""{"error":{"code":"server_busy"}}"""),
                    };
                }

                if (DuringSpeech is { } during) await during();
                LoadedModel = body["model"]?.GetValue<string>();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Wav) };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    public sealed class SingleHandlerHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public List<string> Names { get; } = [];

        public HttpClient CreateClient(string name)
        {
            Names.Add(name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }
}
