using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Read2Me.Services.Audio;
using Read2Me.Services.Characters;

namespace Read2Me.App.Api
{
    public static class QueueStatusEndpoints
    {
        public static void MapQueueStatusEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/attribution/queue",
                    (CharacterQueueService queue) => TypedResults.Ok(queue.Snapshot()))
                .WithSummary("Attribution queue snapshot: counts, ETA and the paragraph currently being attributed.");

            endpoints.MapGet("/api/audio/queue",
                    (AudioQueueService queue) => TypedResults.Ok(queue.Snapshot()))
                .WithSummary("Audio generation queue snapshot: counts, average seconds per item and ETA.");
        }
    }
}
