import { DatePipe } from '@angular/common';
import { Component, HostListener, OnInit, computed, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { NotePhaseEditor } from '../../components/notes/note-phase-editor';
import { NoteWorkflowPanel } from '../../components/notes/note-workflow';
import { Note } from '../../core/models/note.model';
import { emptyNoteWorkflow, NotePhase, NotePhaseData, noteProgress } from '../../core/models/note-workflow.model';
import { noteError, NotesService } from '../../core/services/notes.service';

@Component({
  selector: 'app-notes',
  imports: [ReactiveFormsModule, DatePipe, NoteWorkflowPanel, NotePhaseEditor],
  templateUrl: './notes.html',
  styleUrl: './notes.less',
})
export class NotesPage implements OnInit {
  readonly notes = signal<Note[]>([]);
  readonly selected = signal<Note | null>(null);
  readonly search = signal('');
  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly workflowBusy = signal(false);
  readonly baseDirty = signal(false);
  readonly phaseDirty = signal(false);
  readonly resetToken = signal(0);
  readonly draftVersion = signal(0);
  readonly message = signal('');
  readonly error = signal('');
  readonly noteProgress = noteProgress;
  readonly busy = computed(() => this.saving() || this.workflowBusy() || this.loading());
  readonly baseLocked = computed(
    () => !!this.selected()?.workflow?.phases.some((phase) => phase.validatedAt),
  );
  readonly initialPhase: NotePhase = emptyNoteWorkflow().phases[0];
  readonly draftNote = computed<Note>(() => {
    this.draftVersion();
    const values = this.form.getRawValue();
    return {
      id: 'phase-one-draft',
      title: values.title,
      content: values.content,
      state: values.state,
      altitude: values.altitude,
      longitude: values.longitude,
      createdAt: '',
      updatedAt: '',
      workflow: emptyNoteWorkflow(),
      revision: 0,
    };
  });
  readonly filtered = computed(() => {
    const term = this.search().trim().toLocaleLowerCase('es');
    return term
      ? this.notes().filter((n) =>
          `${n.title} ${n.content} ${n.state}`.toLocaleLowerCase('es').includes(term),
        )
      : this.notes();
  });
  readonly form = new FormGroup({
    title: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.pattern(/\S/), Validators.maxLength(120)],
    }),
    content: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.pattern(/\S/), Validators.maxLength(10000)],
    }),
    state: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.pattern(/\S/), Validators.maxLength(100)],
    }),
    altitude: new FormControl<number | null>(null, [
      Validators.required,
      Validators.min(-90),
      Validators.max(90),
    ]),
    longitude: new FormControl<number | null>(null, [
      Validators.required,
      Validators.min(-180),
      Validators.max(180),
    ]),
  });
  constructor(
    private readonly notesService: NotesService,
    private readonly router: Router,
    private readonly route: ActivatedRoute,
  ) {
    this.form.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => {
      this.baseDirty.set(true);
      this.draftVersion.update((value) => value + 1);
    });
  }
  ngOnInit() {
    void this.load();
  }
  async load() {
    if (!this.canLeave()) return;
    this.loading.set(true);
    this.error.set('');
    this.message.set('');
    try {
      const notes = await firstValueFrom(this.notesService.list());
      this.notes.set(notes);
      this.resetToken.update((value) => value + 1);
      const id = this.selected()?.id ?? this.route.snapshot.queryParamMap.get('note');
      const selected = id ? notes.find((note) => note.id === id) : notes[0];
      if (selected) this.selectNote(selected);
      else {
        this.resetNote();
        if (id) this.error.set('No se encontró la incidencia solicitada en tus registros.');
      }
    } catch (error) {
      this.error.set(noteError(error));
    } finally {
      this.loading.set(false);
    }
  }
  newNote() {
    if (this.busy() || !this.canLeave()) return;
    this.resetNote();
  }
  edit(note: Note) {
    if (this.busy() || note.id === this.selected()?.id || !this.canLeave()) return;
    this.selectNote(note);
    this.message.set('');
    this.error.set('');
  }
  private resetNote() {
    this.selected.set(null);
    this.form.enable({ emitEvent: false });
    this.form.reset(
      { title: '', content: '', state: '', altitude: null, longitude: null },
      { emitEvent: false },
    );
    this.baseDirty.set(false);
    this.draftVersion.update((value) => value + 1);
    this.phaseDirty.set(false);
    this.message.set('');
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { note: null },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }
  private selectNote(note: Note) {
    const changedNote = this.selected()?.id !== note.id;
    this.selected.set(note);
    this.form.reset(
      {
        title: note.title,
        content: note.content,
        state: note.state ?? '',
        altitude: note.altitude,
        longitude: note.longitude,
      },
      { emitEvent: false },
    );
    if (this.baseLocked()) this.form.disable({ emitEvent: false });
    else this.form.enable({ emitEvent: false });
    this.baseDirty.set(false);
    this.draftVersion.update((value) => value + 1);
    if (changedNote) this.phaseDirty.set(false);
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { note: note.id },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }
  async save() {
    if (this.form.invalid || this.busy() || this.baseLocked()) {
      this.form.markAllAsTouched();
      return;
    }
    this.saving.set(true);
    this.message.set('');
    this.error.set('');
    try {
      const current = this.selected();
      const input = { ...this.form.getRawValue(), expectedRevision: current?.revision ?? 0 };
      const saved = current
        ? await firstValueFrom(this.notesService.update(current.id, input))
        : await firstValueFrom(this.notesService.create(input));
      this.updated(saved);
      this.message.set(
        'Datos de anomalía I&W guardados. Completa y valida la fase 1 para habilitar el siguiente punto.',
      );
    } catch (error) {
      this.error.set(noteError(error));
    } finally {
      this.saving.set(false);
    }
  }
  async createWithPhase(data: NotePhaseData) {
    if (this.form.invalid || this.busy() || this.selected()) {
      this.form.markAllAsTouched();
      return;
    }
    this.saving.set(true);
    this.message.set('');
    this.error.set('');
    let created: Note | null = null;
    try {
      created = await firstValueFrom(this.notesService.create(this.form.getRawValue()));
      const saved = await firstValueFrom(this.notesService.savePhase(created, 'indications', data));
      this.updated(saved);
      this.message.set('Incidencia y borrador completo de Fase 1 I&W guardados. Valida la fase para habilitar reconocimiento.');
    } catch (error) {
      if (created) this.updated(created);
      this.error.set(noteError(error));
    } finally {
      this.saving.set(false);
    }
  }
  updated(note: Note) {
    this.notes.update((items) => [note, ...items.filter((item) => item.id !== note.id)]);
    this.selectNote(note);
  }
  async remove(note: Note) {
    if (
      this.busy() ||
      !confirm(`¿Eliminar la incidencia “${note.title}” y todo su proceso de validación?`)
    )
      return;
    this.saving.set(true);
    this.error.set('');
    try {
      await firstValueFrom(this.notesService.delete(note.id, note.revision ?? 0));
      this.notes.update((items) => items.filter((item) => item.id !== note.id));
      if (this.selected()?.id === note.id) this.resetNote();
    } catch (error) {
      this.error.set(noteError(error));
    } finally {
      this.saving.set(false);
    }
  }
  canLeave() {
    if (this.saving() || this.workflowBusy()) return false;
    return (
      (!this.baseDirty() && !this.phaseDirty()) ||
      confirm('Hay cambios sin guardar en esta incidencia. ¿Descartarlos y continuar?')
    );
  }
  @HostListener('window:beforeunload', ['$event']) beforeUnload(event: BeforeUnloadEvent) {
    if (this.baseDirty() || this.phaseDirty() || this.saving() || this.workflowBusy()) {
      event.preventDefault();
      event.returnValue = '';
    }
  }
}
