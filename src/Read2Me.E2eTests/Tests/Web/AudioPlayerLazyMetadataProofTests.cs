using System.Collections.Concurrent;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// Throwaway proof for #506: an audio player reads its metadata only once it nears the viewport.
/// Deleted in the PR's final commit.
/// </summary>
public class AudioPlayerLazyMetadataProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    /// <summary>
    /// Every rendered player in the scroller: its source path, its distance outside the scroller's
    /// visible box (0 when it overlaps), its preload, and whether it shows a duration.
    /// </summary>
    private const string Players = """
        (scrollerSelector) => {
          const scroller = document.querySelector(scrollerSelector);
          const box = scroller.getBoundingClientRect();
          return [...scroller.querySelectorAll('r2m-audio-player')].map((p) => {
            const r = p.getBoundingClientRect();
            const outside = Math.max(0, box.top - r.bottom, r.top - box.bottom);
            const audio = p.querySelector('audio');
            return {
              src: new URL(audio.src).pathname,
              outside,
              preload: audio.getAttribute('preload'),
              duration: !p.querySelector('.r2m-audio-player__time')?.textContent.includes('–:––'),
            };
          });
        }
        """;

    private sealed record PlayerState(string Src, double Outside, string Preload, bool Duration);

    private async Task<List<PlayerState>> PlayersAsync(string scroller) =>
        (await Page.EvaluateAsync<JsonElement>(Players, scroller)).EnumerateArray()
            .Select(p => new PlayerState(
                p.GetProperty("src").GetString()!, p.GetProperty("outside").GetDouble(),
                p.GetProperty("preload").GetString()!, p.GetProperty("duration").GetBoolean()))
            .ToList();

    private ConcurrentBag<string> RecordAudioRequests()
    {
        var requests = new ConcurrentBag<string>();
        Page.Request += (_, r) =>
        {
            var path = new Uri(r.Url).AbsolutePath;
            if (path.StartsWith("/workspace/") && path.EndsWith(".wav")) requests.Add(path);
        };
        return requests;
    }

    private void Report(string title, IEnumerable<PlayerState> players, IEnumerable<string> requests)
    {
        var requested = requests.ToHashSet();
        var list = players.ToList();
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"{title}: {list.Count} rendered, {list.Count(p => p.Outside <= 200)} within 200 px, " +
            $"{list.Count(p => p.Outside > 200)} beyond; {requested.Count} distinct audio requests, " +
            $"{list.Count(p => p.Outside > 200 && requested.Contains(p.Src))} of them for players beyond 200 px");
    }

    private static void AssertOnlyNearRequested(List<PlayerState> players, IEnumerable<string> requests)
    {
        var requested = requests.ToHashSet();
        // A 20 px band either side of the 200 px margin absorbs rows that moved during settling.
        var far = players.Where(p => p.Outside > 220).ToList();
        var near = players.Where(p => p.Outside < 180).ToList();
        Assert.NotEmpty(far);
        Assert.NotEmpty(near);
        Assert.All(far, p => Assert.Equal("none", p.Preload));
        Assert.All(far, p => Assert.DoesNotContain(p.Src, requested));
        Assert.All(near, p => Assert.Equal("metadata", p.Preload));
        Assert.All(near.Where(p => p.Outside == 0), p => Assert.True(p.Duration, $"{p.Src} shows no duration"));
    }

    [Fact]
    public async Task Book_audio_mode_requests_only_players_near_the_viewport_and_play_works_unloaded()
    {
        var folder = "lazy-audio-book";
        var book = await App.SeedLongBookAsync(folder, "Lazy Audio Book", "A. Author", chapters: 1, paragraphs: 60);
        var alice = book.CharacterId("Alice");
        for (var p = 1; p <= 60; p++)
            await App.SeedItemAudioAsync(folder, book.ItemId(p % 2 == 1 ? $"c1n{p}" : $"c1l{p}"), alice);

        var requests = RecordAudioRequests();
        await GotoAppAsync($"projects/{folder}/book?mode=audio");
        await Page.WaitForSelectorAsync("r2m-measured-list r2m-audio-player .r2m-audio-player__time:not(:has-text('–:––'))");
        await Page.WaitForTimeoutAsync(1000);

        var before = await PlayersAsync("r2m-measured-list");
        Report("Before scrolling", before, requests);
        AssertOnlyNearRequested(before, requests);

        await Page.EvaluateAsync("() => { const l = document.querySelector('r2m-measured-list'); l.scrollTop += 2500; l.dispatchEvent(new Event('scroll')); }");
        await Page.WaitForTimeoutAsync(1500);
        var after = await PlayersAsync("r2m-measured-list");
        Report("After scrolling 2500 px", after, requests);
        AssertOnlyNearRequested(after, requests);

        // Play a rendered player that has not neared the viewport: play() loads it.
        var src = after.First(p => p.Outside > 220).Src;
        var played = await Page.EvaluateAsync<JsonElement>("""
            async (src) => {
              const player = [...document.querySelectorAll('r2m-audio-player')]
                .find((p) => new URL(p.querySelector('audio').src).pathname === src);
              const audio = player.querySelector('audio');
              const preloadAtPlay = audio.getAttribute('preload');
              const playing = new Promise((resolve) => audio.addEventListener('playing', () => resolve(true), { once: true }));
              player.toggle();
              const ok = await Promise.race([playing, new Promise((r) => setTimeout(() => r(false), 5000))]);
              return { preloadAtPlay, ok, signal: player.playing(), time: audio.currentTime };
            }
            """, src);
        TestContext.Current.TestOutputHelper!.WriteLine($"Play on an unloaded player: {played}");
        Assert.Equal("none", played.GetProperty("preloadAtPlay").GetString());
        Assert.True(played.GetProperty("ok").GetBoolean());
        Assert.True(played.GetProperty("signal").GetBoolean());
    }

    [Fact]
    public async Task Cast_voices_request_only_cards_near_the_viewport()
    {
        var folder = "lazy-audio-cast";
        var book = await App.SeedProjectAsync(folder, "Lazy Audio Cast", "A. Author");
        var alice = book.CharacterId("Alice");
        for (var v = 1; v <= 30; v++) await App.SeedEditableVoiceAsync(folder, alice, $"Voice {v:00}");

        var requests = RecordAudioRequests();
        await GotoAppAsync($"projects/{folder}/cast/{alice}");
        await Page.WaitForSelectorAsync("r2m-voice-card r2m-audio-player", new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
        await Page.WaitForTimeoutAsync(1500);
        TestContext.Current.TestOutputHelper!.WriteLine($"All 30 cards collapsed: {requests.Distinct().Count()} distinct audio requests");
        Assert.Empty(requests);

        // Expand every card, so each shows its reference player and the list runs far below the fold.
        await Page.EvaluateAsync("() => document.querySelectorAll('r2m-voice-card').forEach((c) => c.expanded.set(true))");
        await Page.WaitForTimeoutAsync(1500);

        var scroller = await Page.EvaluateAsync<string>("""
            () => {
              let el = document.querySelector('r2m-voice-card');
              while (el && !(el.scrollHeight > el.clientHeight && getComputedStyle(el).overflowY !== 'visible')) el = el.parentElement;
              if (!el) return 'body';
              el.setAttribute('data-proof-scroller', '');
              return '[data-proof-scroller]';
            }
            """);
        var players = await PlayersAsync(scroller);
        Report("Cast voices", players, requests);
        AssertOnlyNearRequested(players, requests);
    }
}
