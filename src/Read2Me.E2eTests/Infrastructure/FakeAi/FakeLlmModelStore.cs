using System.Text.Json;

namespace Read2Me.E2eTests.Infrastructure.FakeAi;

/// <summary>
/// Simulates the llama.cpp <c>--models-max 1</c> autoload fork that <see cref="Read2Me.Services.Llm.ModelLoadGate"/>
/// drives: <c>GET /v1/models</c> reports every preset with a per-item <c>status.value</c>
/// (<c>unloaded</c>/<c>loading</c>/<c>loaded</c>), and a <c>/v1/chat/completions</c> request naming an
/// unloaded model begins an autoload that flips it <c>unloaded → loading → loaded</c> over subsequent
/// polls (evicting whatever was loaded, since only one model is resident at a time).
///
/// The transition is deterministic and poll-driven so an E2E can exercise the switch-and-wait path
/// fast: a model marked <c>loading</c> by <see cref="NoteRequest"/> flips to <c>loaded</c> after
/// <c>loadsAfterPolls</c> renders of <c>GET /v1/models</c>. <c>neverLoads</c> keeps it <c>loading</c>
/// forever (the budget-exceeded requeue path).
/// </summary>
public sealed class FakeLlmModelStore
{
    private const string Unloaded = "unloaded";
    private const string Loading = "loading";
    private const string Loaded = "loaded";

    private readonly object _lock = new();
    private readonly Dictionary<string, string> _status;
    private readonly Dictionary<string, int> _loadingPolls = new(StringComparer.Ordinal);
    private readonly int _loadsAfterPolls;
    private readonly bool _neverLoads;

    private FakeLlmModelStore(Dictionary<string, string> status, int loadsAfterPolls, bool neverLoads)
    {
        _status = status;
        _loadsAfterPolls = Math.Max(1, loadsAfterPolls);
        _neverLoads = neverLoads;
    }

    /// <summary>Every listed model reads <c>loaded</c>; no switch ever happens (the default, backward-compatible state).</summary>
    public static FakeLlmModelStore AllLoaded(params string[] models) =>
        new(models.ToDictionary(m => m, _ => Loaded, StringComparer.Ordinal), loadsAfterPolls: 1, neverLoads: false);

    /// <summary>
    /// A switchable endpoint where <paramref name="target"/> starts <c>unloaded</c> and, once an
    /// autoload request names it, flips to <c>loaded</c> after <paramref name="loadsAfterPolls"/>
    /// <c>GET /v1/models</c> polls. When <paramref name="neverLoads"/> is true it stays <c>loading</c>
    /// forever. <paramref name="alreadyLoaded"/> presets read <c>loaded</c> until evicted by the switch.
    /// </summary>
    public static FakeLlmModelStore Switching(
        string target, int loadsAfterPolls = 2, bool neverLoads = false, params string[] alreadyLoaded)
    {
        var status = new Dictionary<string, string>(StringComparer.Ordinal) { [target] = Unloaded };
        foreach (var m in alreadyLoaded)
            status[m] = Loaded;
        return new(status, loadsAfterPolls, neverLoads);
    }

    /// <summary>
    /// A <c>/v1/chat/completions</c> request naming <paramref name="model"/>: if it is not already
    /// loaded, begin its autoload (mark it <c>loading</c>) and evict any other loaded model.
    /// </summary>
    public void NoteRequest(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return;

        lock (_lock)
        {
            if (_status.TryGetValue(model, out var current) && current == Loaded)
                return;

            // --models-max 1: loading the target evicts whatever else was resident.
            foreach (var key in _status.Keys.ToList())
                if (!string.Equals(key, model, StringComparison.Ordinal) && _status[key] == Loaded)
                    _status[key] = Unloaded;

            _status[model] = Loading;
        }
    }

    /// <summary>
    /// Renders the <c>GET /v1/models</c> body, advancing any <c>loading</c> model one poll toward
    /// <c>loaded</c> (unless it never loads). The shape mirrors the fork exactly:
    /// <c>{ "data": [ { "id", "object", "status": { "value" } } ] }</c>.
    /// </summary>
    public string RenderJson()
    {
        lock (_lock)
        {
            foreach (var key in _status.Keys.ToList())
            {
                if (_status[key] != Loading || _neverLoads)
                    continue;

                var polls = _loadingPolls.GetValueOrDefault(key) + 1;
                _loadingPolls[key] = polls;
                if (polls >= _loadsAfterPolls)
                    _status[key] = Loaded;
            }

            var data = _status
                .Select(kv => new { id = kv.Key, @object = "model", status = new { value = kv.Value } })
                .ToArray();
            return JsonSerializer.Serialize(new { data });
        }
    }
}
