import { Injectable, inject } from '@angular/core';
import { ApiClient } from './api-client';
import type {
  CoverImageResponse,
  CreateProjectRequest,
  CreateProjectResponse,
  ProjectDetailDto,
  ProjectSummary,
  UpdateProjectRequest,
} from './dtos';

/** Cover uploads the host accepts (`ProjectEndpoints.SaveCoverAsync`): same list, same limit. */
export const COVER_ACCEPT = '.jpg,.jpeg,.png,.webp';
export const COVER_MAX_BYTES = 10 * 1024 * 1024;

/** Book files `POST /api/projects` reads; anything else is a Text import the reader rejects. */
export const BOOK_ACCEPT = '.epub,.txt';
export const BOOK_MAX_BYTES = 100 * 1024 * 1024;

/**
 * `ProjectEndpoints.cs`: the project catalog, create-from-upload, delete, import, metadata,
 * narrator-only policy and cover image.
 */
@Injectable({ providedIn: 'root' })
export class ProjectsApi {
  private readonly api = inject(ApiClient);

  list(): Promise<ProjectSummary[]> {
    return this.api.get<ProjectSummary[]>('/api/projects');
  }

  /** 201 with the new folder name; 400 when title/file are missing, 422 when the reader rejects the file. */
  create(request: CreateProjectRequest): Promise<CreateProjectResponse> {
    const form = new FormData();
    form.set('title', request.title);
    form.set('bookTitle', request.bookTitle ?? '');
    form.set('author', request.author ?? '');
    form.set('file', request.file, request.file.name);
    return this.api.postForm<CreateProjectResponse>('/api/projects', form);
  }

  get(folder: string): Promise<ProjectDetailDto> {
    return this.api.get<ProjectDetailDto>(projectUrl(folder));
  }

  delete(folder: string): Promise<void> {
    return this.api.delete(projectUrl(folder));
  }

  /** Reads the stored book file into structure; `reread` clears existing content first. 422 on reader failure. */
  import(folder: string, reread = false): Promise<void> {
    return this.api.post<void>(`${projectUrl(folder)}/import`, { reread });
  }

  /** Title / book title / author; 400 when a sent title is blank. Returns the updated detail. */
  update(folder: string, request: UpdateProjectRequest): Promise<ProjectDetailDto> {
    return this.api.patch<ProjectDetailDto>(projectUrl(folder), request);
  }

  setNarratorOnlyMode(folder: string, enabled: boolean): Promise<void> {
    return this.api.put<void>(`${projectUrl(folder)}/narrator-only-mode`, { enabled });
  }

  /** Multipart `file`; 400 for a type outside {@link COVER_ACCEPT} or over {@link COVER_MAX_BYTES}. */
  uploadCover(folder: string, file: File): Promise<CoverImageResponse> {
    const form = new FormData();
    form.set('file', file, file.name);
    return this.api.putForm<CoverImageResponse>(`${projectUrl(folder)}/cover`, form);
  }

  deleteCover(folder: string): Promise<void> {
    return this.api.delete(`${projectUrl(folder)}/cover`);
  }
}

/** `/api/projects/{folder}` with the folder segment encoded; shared by every project-scoped service. */
export function projectUrl(folder: string): string {
  return `/api/projects/${encodeURIComponent(folder)}`;
}
