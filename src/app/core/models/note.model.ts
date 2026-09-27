import type { NoteWorkflow } from './note-workflow.model';

export interface Note {
  id: string;
  title: string;
  content: string;
  state: string;
  altitude: number | null;
  longitude: number | null;
  createdAt: string;
  updatedAt: string;
  workflow?: NoteWorkflow;
  revision?: number;
}
export type NoteInput = Pick<Note, 'title' | 'content' | 'state' | 'altitude' | 'longitude'> & {
  expectedRevision?: number;
};
