import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AiServicesApi } from './ai-services-api';
import { AssemblyApi } from './assembly-api';
import { AttributionApi } from './attribution-api';
import { AudioApi } from './audio-api';
import { AudioProcessingApi } from './audio-processing-api';
import { BookApi } from './book-api';
import { DiscoveryApi } from './discovery-api';
import { ProjectsApi, projectUrl } from './projects-api';
import { PromptsApi } from './prompts-api';
import { LlmSettingsApi, ParagraphTtsSettingsApi } from './settings-api';
import { ThemesApi } from './themes-api';
import { VoicesApi } from './voices-api';

/**
 * One request per service: right verb, right URL, right body. ProblemDetails mapping and the origin
 * header are covered once in api-client.spec.ts.
 */
describe('per-area API services', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('projectUrl encodes the folder segment', () => {
    expect(projectUrl('My Book/2')).toBe('/api/projects/My%20Book%2F2');
  });

  it('ProjectsApi.create posts multipart form fields', async () => {
    const file = new File(['x'], 'book.epub');
    const call = TestBed.inject(ProjectsApi).create({ title: 'T', file });
    const req = http.expectOne({ method: 'POST', url: '/api/projects' });
    const form = req.request.body as FormData;
    expect(form.get('title')).toBe('T');
    expect(form.get('bookTitle')).toBe('');
    expect(form.get('file')).toBeInstanceOf(File);
    req.flush({ folderName: 'T' }, { status: 201, statusText: 'Created' });
    await expect(call).resolves.toEqual({ folderName: 'T' });
  });

  it('ProjectsApi.update patches only the fields given and resolves the detail', async () => {
    const call = TestBed.inject(ProjectsApi).update('f', { title: 'New' });
    const req = http.expectOne({ method: 'PATCH', url: '/api/projects/f' });
    expect(req.request.body).toEqual({ title: 'New' });
    req.flush({ folderName: 'f', title: 'New' });
    await expect(call).resolves.toEqual({ folderName: 'f', title: 'New' });
  });

  it('ProjectsApi.setNarratorOnlyMode puts the flag', async () => {
    const call = TestBed.inject(ProjectsApi).setNarratorOnlyMode('f', true);
    const req = http.expectOne({ method: 'PUT', url: '/api/projects/f/narrator-only-mode' });
    expect(req.request.body).toEqual({ enabled: true });
    req.flush(null, { status: 204, statusText: 'No Content' });
    await call;
  });

  it('ProjectsApi status reads the project and node roll-ups', async () => {
    const api = TestBed.inject(ProjectsApi);
    const project = api.status('My Book');
    http.expectOne({ method: 'GET', url: '/api/projects/My%20Book/status' }).flush({ revision: 3 });
    await expect(project).resolves.toEqual({ revision: 3 });

    const node = api.nodeStatus('f', 'chapter', 'c1');
    http
      .expectOne({ method: 'GET', url: '/api/projects/f/nodes/chapter/c1/status' })
      .flush({ attributionRemaining: 2 });
    await expect(node).resolves.toEqual({ attributionRemaining: 2 });
  });

  it('ProjectsApi cover: PUT multipart file, DELETE to clear', async () => {
    const api = TestBed.inject(ProjectsApi);
    const upload = api.uploadCover('f', new File(['x'], 'cover.png', { type: 'image/png' }));
    const put = http.expectOne({ method: 'PUT', url: '/api/projects/f/cover' });
    const form = put.request.body as FormData;
    expect((form.get('file') as File).name).toBe('cover.png');
    put.flush({ coverImage: 'cover.png' });
    await expect(upload).resolves.toEqual({ coverImage: 'cover.png' });

    const clear = api.deleteCover('f');
    http
      .expectOne({ method: 'DELETE', url: '/api/projects/f/cover' })
      .flush(null, { status: 204, statusText: 'No Content' });
    await clear;
  });

  it('ProjectsApi.import posts the reread flag', async () => {
    const call = TestBed.inject(ProjectsApi).import('f', true);
    const req = http.expectOne({ method: 'POST', url: '/api/projects/f/import' });
    expect(req.request.body).toEqual({ reread: true });
    req.flush(null);
    await call;
  });

  it('BookApi.children addresses the node route and execute posts the command as-is', async () => {
    const book = TestBed.inject(BookApi);
    const children = book.children('f', 'chapter', 'abc');
    http
      .expectOne({ method: 'GET', url: '/api/projects/f/nodes/chapter/abc/children' })
      .flush({ paragraphs: [] });
    await expect(children).resolves.toEqual({ paragraphs: [] });

    const exec = book.execute('f', { type: 'CreateCharacter', name: 'Hari' });
    const req = http.expectOne({ method: 'POST', url: '/api/projects/f/commands' });
    expect(req.request.body).toEqual({ type: 'CreateCharacter', name: 'Hari' });
    req.flush({ newEntityId: 'id-1' });
    await expect(exec).resolves.toEqual({ newEntityId: 'id-1' });
  });

  it('AttributionApi.enqueue posts the node request; queue is global', async () => {
    const api = TestBed.inject(AttributionApi);
    const enqueue = api.enqueue('f', { level: 'chapter', nodeId: 'c1', unprocessedOnly: true });
    const req = http.expectOne({ method: 'POST', url: '/api/projects/f/attribution/enqueue' });
    expect(req.request.body).toEqual({ level: 'chapter', nodeId: 'c1', unprocessedOnly: true });
    req.flush({ enqueued: 3 }, { status: 202, statusText: 'Accepted' });
    await expect(enqueue).resolves.toEqual({ enqueued: 3 });

    const queue = api.queue();
    http.expectOne({ method: 'GET', url: '/api/attribution/queue' }).flush({});
    await queue;
  });

  it('AudioApi.itemStatus reads the per-item route and cancel posts globally', async () => {
    const api = TestBed.inject(AudioApi);
    const status = api.itemStatus('f', 'i1');
    http
      .expectOne({ method: 'GET', url: '/api/projects/f/audio/items/i1' })
      .flush({ status: null, outcome: null, audioVersion: 2 });
    await expect(status).resolves.toMatchObject({ audioVersion: 2 });

    const cancel = api.cancel();
    http.expectOne({ method: 'POST', url: '/api/audio/cancel' }).flush(null);
    await cancel;
  });

  it('DiscoveryApi.discover sends thinking as a query param', async () => {
    const call = TestBed.inject(DiscoveryApi).discover('f', true);
    const req = http.expectOne((r) => r.url === '/api/projects/f/characters/discover');
    expect(req.request.method).toBe('POST');
    expect(req.request.params.get('thinking')).toBe('true');
    req.flush({});
    await call;
  });

  it('VoicesApi.startPromptBatch posts regenerateAll', async () => {
    const call = TestBed.inject(VoicesApi).startPromptBatch('f', true);
    const req = http.expectOne({ method: 'POST', url: '/api/projects/f/voice-batch/prompts' });
    expect(req.request.body).toEqual({ regenerateAll: true });
    req.flush({}, { status: 202, statusText: 'Accepted' });
    await call;
  });

  it('AssemblyApi.start posts allowPartial', async () => {
    const call = TestBed.inject(AssemblyApi).start('f');
    const req = http.expectOne({ method: 'POST', url: '/api/projects/f/assembly' });
    expect(req.request.body).toEqual({ allowPartial: false });
    req.flush({}, { status: 202, statusText: 'Accepted' });
    await call;
  });

  it('SettingsApi subclasses target their area; active() maps 404 to null', async () => {
    const llm = TestBed.inject(LlmSettingsApi);
    const list = llm.list();
    http.expectOne({ method: 'GET', url: '/api/settings/llm' }).flush([]);
    await expect(list).resolves.toEqual([]);

    const active = TestBed.inject(ParagraphTtsSettingsApi).active();
    http
      .expectOne({ method: 'GET', url: '/api/settings/paragraph-tts/active' })
      .flush({ title: 'Not Found' }, { status: 404, statusText: 'Not Found' });
    await expect(active).resolves.toBeNull();

    const setActive = llm.setActive(7);
    const req = http.expectOne({ method: 'PUT', url: '/api/settings/llm/active' });
    expect(req.request.body).toEqual({ id: 7 });
    req.flush(null);
    await setActive;
  });

  it('PromptsApi.set puts the template by kind', async () => {
    const call = TestBed.inject(PromptsApi).set('voice-plan', 'Plan {{name}}');
    const req = http.expectOne({ method: 'PUT', url: '/api/settings/prompts/voice-plan' });
    expect(req.request.body).toEqual({ template: 'Plan {{name}}' });
    req.flush(null);
    await call;
  });

  it('AudioProcessingApi.update puts the patch to the single row', async () => {
    const call = TestBed.inject(AudioProcessingApi).update({ werThreshold: 0.2 });
    const req = http.expectOne({ method: 'PUT', url: '/api/settings/audio-processing' });
    expect(req.request.body).toEqual({ werThreshold: 0.2 });
    req.flush({ werThreshold: 0.2 });
    await expect(call).resolves.toMatchObject({ werThreshold: 0.2 });
  });

  it('AiServicesApi.status encodes the service name', async () => {
    const call = TestBed.inject(AiServicesApi).status('qwen3 tts');
    http
      .expectOne({ method: 'GET', url: '/api/ai-services/qwen3%20tts/status' })
      .flush({ name: 'qwen3 tts', status: 'Healthy' });
    await expect(call).resolves.toMatchObject({ status: 'Healthy' });
  });

  it('ThemesApi.setSelection puts the partial selection', async () => {
    const call = TestBed.inject(ThemesApi).setSelection({ followSystemPreference: true });
    const req = http.expectOne({ method: 'PUT', url: '/api/settings/themes/selection' });
    expect(req.request.body).toEqual({ followSystemPreference: true });
    req.flush({ selectedThemeId: null, followSystemPreference: true });
    await call;
  });
});
