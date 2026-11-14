using Read2Me.Services.History;
using Read2Me.Services.Queueing;

namespace Read2Me.Tests.Fakes
{
    /// <summary>A <see cref="ProjectActivity"/> gate whose only busy source is a test's fake.</summary>
    public static class BusyGate
    {
        public static ProjectActivity With(IBusySource source)
        {
            var activity = new ProjectActivity();
            activity.Register(source);
            return activity;
        }
    }
}
