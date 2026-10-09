import { html, nothing } from 'lit-html';
import { type LlmServerConfig, LlmSettingsApi, toApiError } from '@app/api';
import { LlmStreamFeed } from '@app/activity/stream-feed';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import { LiveService } from '@app/live/live.service';
import { field } from '@app/ui/field';
import { icon } from '@app/ui/partials';
import { throughput } from '@app/ui/throughput';
import '@app/ui/stream-llm';

/**
 * Test console for the default config (ticket 21): prompt, Send / Stop, the LLM stream inline
 * and the run's throughput. The host runs the send in the background; tokens arrive on
 * `stream:llm` and the ending as one `llmTest` message to this connection. A reconnect may have
 * swallowed that message, so the run state is read back on every resync. The feed is held while
 * the console is connected, so the stream group is joined exactly while it is on screen.
 */
export class LlmTestConsole extends R2mElement {
  private readonly api = use(LlmSettingsApi);
  private readonly live = use(LiveService);
  private readonly feed = use(LlmStreamFeed);

  /** The default config — the one a test send goes to. */
  readonly #config = signal<LlmServerConfig | null>(null);
  get config() {
    return this.#config();
  }
  set config(value: LlmServerConfig | null) {
    this.#config.set(value);
  }

  readonly prompt = signal('');
  readonly streaming = signal(false);
  readonly error = signal<string | null>(null);
  readonly canSend = computed(() => !this.streaming() && !!this.prompt().trim());

  /** Bumped by every send and read-back, so a read-back that started earlier cannot undo them. */
  private stateSeq = 0;

  protected override connected(): void {
    this.classList.add('console');
    this.feed.acquire();
    this.onDisconnect(() => this.feed.release());
    this.onDisconnect(
      this.live.on('llmTest', (m) => {
        this.stateSeq++;
        this.streaming.set(false);
        if (m.kind === 'failed') this.error.set(`Request failed: ${m.reason ?? 'unknown error'}`);
      }),
    );
    // A send this connection did not start (another tab) ends without an llmTest message here.
    this.onDisconnect(
      this.live.on('llm', (m) => {
        if (m.kind === 'runEnded' && this.streaming()) void this.readBack();
      }),
    );
    this.onDisconnect(this.live.resynced(() => void this.readBack()));
    void this.readBack();
  }

  protected template() {
    const config = this.#config();
    if (!config) return nothing;
    const streaming = this.streaming();
    const error = this.error();
    const t = this.live.throughput();
    return html`
      <h2 class="console__title">Test "${config.name}"</h2>
      ${field(
        { id: 'llm-test-prompt', label: 'Prompt', cls: 'console__prompt' },
        (a) =>
          html`<textarea
            id=${a.id}
            aria-describedby=${a.describedBy}
            aria-invalid=${a.invalid}
            data-field="prompt"
            ?disabled=${streaming}
            .value=${this.prompt()}
            @input=${(e: Event) => this.prompt.set((e.target as HTMLTextAreaElement).value)}
          ></textarea>`,
      )}
      <div class="console__actions">
        <button
          type="button"
          class="r2m-button r2m-button--filled"
          data-action="send"
          ?disabled=${!this.canSend()}
          @click=${() => void this.send()}
        >
          ${icon('send')} Send
        </button>
        ${
          streaming
            ? html`<button
                type="button"
                class="r2m-button r2m-button--stroked"
                data-action="stop"
                @click=${() => void this.stop()}
              >
                ${icon('stop')} Stop
              </button>`
            : nothing
        }
      </div>
      ${error ? html`<p class="console__error" role="alert">${error}</p>` : nothing}
      ${t?.hasRun ? html`<div class="console__throughput">${throughput(t)}</div>` : nothing}
      <div class="console__stream">
        <r2m-stream-llm
          .events=${this.feed.events()}
          .maxTurns=${this.feed.maxUnits}
        ></r2m-stream-llm>
      </div>
    `;
  }

  private async send(): Promise<void> {
    const config = this.#config();
    if (!config || !this.canSend()) return;
    this.error.set(null);
    this.stateSeq++;
    this.streaming.set(true);
    try {
      await this.api.startTest(config.id, {
        prompt: this.prompt(),
        connectionId: this.live.connectionId(),
      });
    } catch (e) {
      const problem = toApiError(e);
      // 409: a send is already running (another tab, or one this page lost track of) — follow it.
      if (problem.status === 409) return;
      this.streaming.set(false);
      this.error.set(problem.message);
    }
  }

  private async stop(): Promise<void> {
    const config = this.#config();
    if (!config) return;
    try {
      await this.api.cancelTest(config.id);
    } catch (e) {
      this.error.set(toApiError(e).message);
    }
  }

  private async readBack(): Promise<void> {
    const seq = ++this.stateSeq;
    try {
      const { running } = await this.api.testStatus();
      if (seq === this.stateSeq) this.streaming.set(running);
    } catch {
      // The next resync or llmTest message settles it.
    }
  }
}
define('r2m-llm-test-console', LlmTestConsole);
