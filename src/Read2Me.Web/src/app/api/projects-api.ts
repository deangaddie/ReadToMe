import { Injectable, inject } from '@angular/core';
import { ApiClient } from './api-client';
import type {
  CreateProjectRequest,
  CreateProjectResponse,
  ProjectDetailDto,
  ProjectSummary,
} from './dtos';

/** `ProjectEndpoints.cs`: the project catalog, create-from-upload, delete and import. */
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
}

/** `/api/projects/{folder}` with the folder segment encoded; shared by every project-scoped service. */
export function projectUrl(folder: string): string {
  return `/api/projects/${encodeURIComponent(folder)}`;
}
