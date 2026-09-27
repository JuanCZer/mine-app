import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Note, NoteInput } from '../models/note.model';
import { DetectionStage } from '../models/detection.model';
import { NotePhaseData } from '../models/note-workflow.model';

const API_URL = 'http://localhost:5068/api/notes';
@Injectable({ providedIn: 'root' })
export class NotesService {
  constructor(private readonly http: HttpClient) {}
  list() {
    return this.http.get<Note[]>(API_URL);
  }
  create(note: NoteInput) {
    return this.http.post<Note>(API_URL, note);
  }
  update(id: string, note: NoteInput) {
    return this.http.put<Note>(`${API_URL}/${id}`, note);
  }
  delete(id: string) {
    return this.http.delete<void>(`${API_URL}/${id}`);
  }
  savePhase(note: Note, stage: DetectionStage, data: NotePhaseData) {
    return this.http.put<Note>(`${API_URL}/${note.id}/phases/${stage}`, {
      expectedRevision: note.revision ?? 0,
      data,
    });
  }
  validatePhase(note: Note, stage: DetectionStage) {
    return this.http.post<Note>(`${API_URL}/${note.id}/phases/${stage}/validate`, {
      expectedRevision: note.revision ?? 0,
    });
  }
}

export function noteError(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    if (error.status === 0)
      return 'No se pudo conectar con la API de incidencias. Inicia el backend e intenta de nuevo.';
    if (typeof error.error?.error === 'string')
      return [
        error.error.error,
        ...(Array.isArray(error.error.issues) ? error.error.issues : []),
      ].join(' ');
  }
  return 'No se pudo guardar el cambio. Intenta de nuevo.';
}
