import { Component, OnInit, computed, signal, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Note } from '../../core/models/note.model';
import { NotesService } from '../../core/services/notes.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { noteProgress } from '../../core/models/note-workflow.model';

@Component({
  selector: 'app-record',
  imports: [RouterLink],
  templateUrl: './record.html',
  styleUrl: './record.less',
})
export class RecordPage implements OnInit {
  readonly i18n = inject(I18nService);
  get dictionary() {
    return this.i18n.dictionary();
  }
  readonly error = this.i18n.message();
  noteProgress(note: Note) {
    return noteProgress(note, this.dictionary.workflow);
  }
  coordinate(value: number | null) {
    return value === null ? '—' : this.i18n.number(value, { maximumFractionDigits: 8 });
  }
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
    } catch (error) {
      this.error.set(this.i18n.errorRef(error, 'map.recordsFailed'));
    } finally {
      this.loading.set(false);
    }
  }
}
