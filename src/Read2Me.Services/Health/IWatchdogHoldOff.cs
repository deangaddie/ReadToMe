using System;
using System.Collections.Generic;

namespace Read2Me.Services.Health;

/// <summary>
/// Keeps the watchdog's hands off services something else is deliberately driving (the Setup
/// check's Full check stops and starts them): while held, a failure reported against one never
/// counts toward a trip.
/// </summary>
public interface IWatchdogHoldOff
{
    /// <summary>Holds off <paramref name="services"/> (by name, without case) until the handle is disposed.</summary>
    IDisposable HoldOff(IReadOnlyCollection<string> services);
}
