import {
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  viewChildren,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { DetectionStage, DETECTION_LAYERS } from '../../core/models/detection.model';
import { Note } from '../../core/models/note.model';
import {
  emptyNoteWorkflow,
  NotePhaseData,
  notePhaseIssues,
} from '../../core/models/note-workflow.model';
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
  readonly visited = signal<DetectionStage[]>(['indications']);
  readonly editors = viewChildren(NotePhaseEditor);
  readonly editor = computed(() => this.editorFor(this.selected()));
  readonly completed = computed(
    () => this.workflow().phases.filter((phase) => phase.validatedAt).length,
  );
  readonly busy = signal(false);
  readonly editorDirty = computed(() => this.editors().some((editor) => editor.dirty()));
  readonly message = signal('');
  readonly error = signal('');
  private noteId: string | null = null;

  constructor() {
    effect(() => this.busyChange.emit(this.busy()));
    effect(() => this.dirtyChange.emit(this.editorDirty()));
    effect(() => {
      const note = this.note();
      if ((note?.id ?? null) === this.noteId) return;
      this.noteId = note?.id ?? null;
      const workflow = note?.workflow ?? emptyNoteWorkflow();
      const stage =
        workflow.currentStage ??
        (workflow.status === 'discarded' ? 'reconnaissance' : 'mitigation');
      this.selected.set(stage);
      this.visited.set([stage]);
      this.message.set('');
      this.error.set('');
    });
  }
  isLocked(stage: DetectionStage): boolean {
    return !this.note() || !this.workflow().phases.some((phase) => phase.stage === stage);
  }
  editable(stage: DetectionStage = this.selected()): boolean {
    return !!this.note() && this.workflow().status === 'open' && !this.isValidated(stage);
  }
  private editorFor(stage: DetectionStage) {
    return this.editors().find((editor) => editor.phase().stage === stage);
  }
  phaseStatus(stage: DetectionStage): string {
    if (!this.note()) return 'Registra la incidencia';
    if (this.isValidated(stage)) return 'Validada';
    if (this.workflow().status !== 'open') return 'Sin validar · solo lectura';
    const phase = this.workflow().phases.find((phase) => phase.stage === stage)!;
    const count = (
      this.editorFor(stage)?.issues() ?? notePhaseIssues(this.note()!, stage, phase.data)
    ).length;
    return count
      ? `${count} requisito${count === 1 ? '' : 's'} pendiente${count === 1 ? '' : 's'}`
      : 'Completa · sin validar';
  }
  validationBlockedReason(stage: DetectionStage): string {
    if (this.workflow().currentStage !== stage)
      return 'Para validar esta fase, valida primero las anteriores. Puedes capturar datos y cambiar de fase sin hacerlo.';
    if (this.editors().some((editor) => editor.phase().stage !== stage && editor.dirty()))
      return 'Guarda los borradores pendientes de las otras fases antes de validar.';
    return '';
  }
  isValidated(stage: DetectionStage) {
    return !!this.workflow().phases.find((phase) => phase.stage === stage)?.validatedAt;
  }
  select(stage: DetectionStage) {
    if (this.busy() || this.parentBusy() || this.isLocked(stage)) return;
    this.selected.set(stage);
    this.visited.update((stages) => (stages.includes(stage) ? stages : [...stages, stage]));
    this.error.set('');
    this.message.set('');
  }
  async save(data: NotePhaseData) {
    const note = this.note();
    if (!note || !this.editable() || this.busy() || this.parentBusy() || this.basePending()) return;
    await this.perform(async () => {
      const updated = await firstValueFrom(this.service.savePhase(note, this.selected(), data));
      this.editor()?.markSaved();
      this.updated.emit(updated);
      this.message.set(
        'Borrador guardado. Puedes cambiar de fase; los requisitos pendientes siguen sin validar.',
      );
    });
  }
  async validate() {
    const note = this.note();
    if (
      !note ||
      !this.editable() ||
      this.workflow().currentStage !== this.selected() ||
      this.editorDirty() ||
      this.busy() ||
      this.parentBusy() ||
      this.basePending()
    )
      return;
    await this.perform(async () => {
      const updated = await firstValueFrom(this.service.validatePhase(note, this.selected()));
      this.updated.emit(updated);
      if (updated.workflow?.currentStage) {
        const stage = updated.workflow.currentStage;
        this.selected.set(stage);
        this.visited.update((stages) => (stages.includes(stage) ? stages : [...stages, stage]));
      }
      this.message.set(
        updated.workflow?.status === 'closed'
          ? 'Las cuatro fases están validadas. Proceso cerrado.'
          : updated.workflow?.status === 'discarded'
            ? 'Dictamen validado. Incidencia cerrada como falso positivo.'
            : 'Fase validada. Puedes continuar con los requisitos pendientes de las demás fases.',
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
