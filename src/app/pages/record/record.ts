import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Note } from '../../core/models/note.model';
import { NotesService } from '../../core/services/notes.service';
import { noteProgress } from '../../core/models/note-workflow.model';

@Component({
  selector: 'app-record',
  imports: [DatePipe, RouterLink],
  templateUrl: './record.html',
  styleUrl: './record.less',
})
export class RecordPage implements OnInit {
  readonly noteProgress = noteProgress;
  readonly records = signal<Note[]>([]);
  readonly search = signal('');
  readonly loading = signal(true);
  readonly filtered = computed(() => {
    const term = this.search().trim().toLowerCase();
    return term
      ? this.records().filter((note) =>
          `${note.title} ${note.content} ${note.state}`.toLowerCase().includes(term),
        )
      : this.records();
  });
  constructor(private readonly notes: NotesService) {}
  async ngOnInit() {
    try {
      this.records.set(await firstValueFrom(this.notes.list()));
    } finally {
      this.loading.set(false);
    }
  }
}
