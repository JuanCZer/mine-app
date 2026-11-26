import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Note, NoteInput } from '../models/note.model';
import { DetectionStage } from '../models/detection.model';
import { NotePhaseData } from '../models/note-workflow.model';

const API_URL = '/api/notes';
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
  delete(id: string, expectedRevision: number) {
    return this.http.delete<void>(`${API_URL}/${id}`, {
      params: { expectedRevision },
    });
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
