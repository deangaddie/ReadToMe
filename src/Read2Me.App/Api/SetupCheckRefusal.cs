using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.App.Services.SetupCheck;

namespace Read2Me.App.Api
{
    /// <summary>
    /// While the Full check drives the AI services, every endpoint that would drive one too answers
    /// one 409 "Setup check running", so nothing competes with what the check is measuring.
    /// </summary>
    public static class SetupCheckRefusal
    {
        /// <summary>The Full check is driving the AI services: no new AI task may start until it ends.</summary>
        public static IResult Problem() =>
            Results.Problem(
                $"{SetupCheckBusy.Reason}: wait for it to finish, or cancel it in Settings → Setup check.",
                statusCode: StatusCodes.Status409Conflict, title: SetupCheckBusy.Reason);

        /// <summary>Answers <see cref="Problem"/> instead of running the endpoint while a Full check runs.</summary>
        public static TBuilder RefusedWhileSetupCheckRuns<TBuilder>(this TBuilder builder)
            where TBuilder : IEndpointConventionBuilder =>
            builder.AddEndpointFilter(async (context, next) =>
                context.HttpContext.RequestServices.GetRequiredService<SetupCheckBusy>().Running
                    ? Problem()
                    : await next(context));
    }
}
