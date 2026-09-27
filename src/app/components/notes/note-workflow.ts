import {
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  ViewChild,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { DetectionStage, DETECTION_LAYERS } from '../../core/models/detection.model';
import { Note } from '../../core/models/note.model';
import { emptyNoteWorkflow, NotePhaseData } from '../../core/models/note-workflow.model';
import { noteError, NotesService } from '../../core/services/notes.service';
import { CaseTimeline } from '../detection/case-timeline';
import { NotePhaseEditor } from './note-phase-editor';

@Component({
  selector: 'app-note-workflow',
  imports: [NotePhaseEditor, CaseTimeline],
  templateUrl: './note-workflow.html',
  styleUrl: './note-workflow.less',
})
export class NoteWorkflowPanel {
  readonly note = input<Note | null>(null);
  readonly basePending = input(false);
  readonly parentBusy = input(false);
  readonly showEmpty = input(true);
  readonly resetToken = input(0);
  readonly updated = output<Note>();
  readonly dirtyChange = output<boolean>();
  readonly busyChange = output<boolean>();
  private readonly service = inject(NotesService);
  readonly layers = DETECTION_LAYERS;
  readonly workflow = computed(() => this.note()?.workflow ?? emptyNoteWorkflow());
  readonly selected = signal<DetectionStage>('indications');
  readonly phase = computed(() =>
    this.workflow().phases.find((phase) => phase.stage === this.selected())!,
  );
  readonly completed = computed(
    () => this.workflow().phases.filter((phase) => phase.validatedAt).length,
  );
  readonly busy = signal(false);
  readonly editorDirty = signal(false);
  readonly message = signal('');
  readonly error = signal('');
  readonly editable = computed(
    () => this.workflow().status === 'open' && this.workflow().currentStage === this.selected(),
  );
  @ViewChild(NotePhaseEditor) editor?: NotePhaseEditor;
  private noteId: string | null = null;

  constructor() {
    effect(() => this.busyChange.emit(this.busy()));
    effect(() => this.dirtyChange.emit(this.editorDirty()));
    effect(() => {
      const note = this.note();
      if ((note?.id ?? null) === this.noteId) return;
      this.noteId = note?.id ?? null;
      const workflow = note?.workflow ?? emptyNoteWorkflow();
      this.selected.set(
        workflow.currentStage ??
          (workflow.status === 'discarded' ? 'reconnaissance' : 'mitigation'),
      );
      this.editorDirty.set(false);
      this.message.set('');
      this.error.set('');
    });
  }
  isLocked(stage: DetectionStage): boolean {
    return (
      !this.note() ||
      !(
        this.workflow().phases.find((phase) => phase.stage === stage)?.validatedAt ||
        this.workflow().currentStage === stage
      )
    );
  }
  isValidated(stage: DetectionStage) {
    return !!this.workflow().phases.find((phase) => phase.stage === stage)?.validatedAt;
  }
  select(stage: DetectionStage) {
    if (this.busy() || this.parentBusy() || this.isLocked(stage)) return;
    if (this.editorDirty()) {
      this.error.set('Guarda el borrador antes de consultar otra fase.');
      return;
    }
    this.selected.set(stage);
    this.error.set('');
    this.message.set('');
  }
  async save(data: NotePhaseData) {
    const note = this.note();
    if (!note || !this.editable() || this.busy() || this.parentBusy() || this.basePending()) return;
    await this.perform(async () => {
      const updated = await firstValueFrom(this.service.savePhase(note, this.selected(), data));
      this.editor?.markSaved();
      this.editorDirty.set(false);
      this.updated.emit(updated);
      this.message.set('Borrador guardado. La siguiente fase continúa bloqueada hasta validar.');
    });
  }
  async validate() {
    const note = this.note();
    if (
      !note ||
      !this.editable() ||
      this.editorDirty() ||
      this.busy() ||
      this.parentBusy() ||
      this.basePending()
    )
      return;
    await this.perform(async () => {
      const updated = await firstValueFrom(this.service.validatePhase(note, this.selected()));
      this.updated.emit(updated);
      if (updated.workflow?.currentStage) this.selected.set(updated.workflow.currentStage);
      this.message.set(
        updated.workflow?.status === 'closed'
          ? 'Las cuatro fases están validadas. Proceso cerrado.'
          : updated.workflow?.status === 'discarded'
            ? 'Dictamen validado. Incidencia cerrada como falso positivo.'
            : 'Fase validada. Ya puedes iniciar la siguiente.',
      );
    });
  }
  private async perform(action: () => Promise<void>) {
    this.busy.set(true);
    this.error.set('');
    this.message.set('');
    try {
      await action();
    } catch (error) {
      this.error.set(noteError(error));
    } finally {
      this.busy.set(false);
    }
  }
}
