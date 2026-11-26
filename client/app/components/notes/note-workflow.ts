import { I18nService } from '../../core/i18n/i18n.service';
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
import { DetectionStage, detectionLayers } from '../../core/models/detection.model';
import { Note } from '../../core/models/note.model';
import {
  emptyNoteWorkflow,
  NotePhaseData,
  notePhaseIssues,
} from '../../core/models/note-workflow.model';
import { NotesService } from '../../core/services/notes.service';
import { CaseTimeline } from '../detection/case-timeline';
import { NotePhaseEditor } from './note-phase-editor';

@Component({
  selector: 'app-note-workflow',
  imports: [NotePhaseEditor, CaseTimeline],
  templateUrl: './note-workflow.html',
  styleUrl: './note-workflow.less',
})
export class NoteWorkflowPanel {
  readonly i18n = inject(I18nService);
  get dictionary() {
    return this.i18n.dictionary();
  }
  readonly note = input<Note | null>(null);
  readonly isNew = input(false);
  // Stable through the first server save; changes when the user starts another editing session.
  readonly contextKey = input<number | null>(null);
  readonly basePending = input(false);
  readonly parentBusy = input(false);
  readonly showEmpty = input(true);
  readonly resetToken = input(0);
  readonly updated = output<Note>();
  readonly dirtyChange = output<boolean>();
  readonly busyChange = output<boolean>();
  private readonly service = inject(NotesService);
  get layers() {
    return detectionLayers(this.dictionary.workflow);
  }
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
  readonly message = this.i18n.message();
  readonly error = this.i18n.message();
  private noteContext: string | number | null = null;

  constructor() {
    effect(() => this.busyChange.emit(this.busy()));
    effect(() => this.dirtyChange.emit(this.editorDirty()));
    effect(() => {
      const note = this.note();
      const context = this.contextKey() ?? note?.id ?? null;
      if (context === this.noteContext) return;
      this.noteContext = context;
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
    if (!this.note()) return this.dictionary.workflow.panel.register;
    if (this.isValidated(stage)) return this.dictionary.workflow.panel.validated;
    if (this.workflow().status !== 'open') return this.dictionary.workflow.panel.readOnly;
    const phase = this.workflow().phases.find((phase) => phase.stage === stage)!;
    const count = (
      this.editorFor(stage)?.issues() ??
      notePhaseIssues(this.note()!, stage, phase.data, this.dictionary.workflow)
    ).length;
    return count
      ? this.i18n.format(
          count === 1
            ? this.dictionary.workflow.panel.pendingOne
            : this.dictionary.workflow.panel.pendingMany,
          { count: this.i18n.number(count) },
        )
      : this.dictionary.workflow.panel.complete;
  }
  validationBlockedReason(stage: DetectionStage): string {
    if (this.isNew()) return this.dictionary.workflow.panel.blockedNew;
    if (this.workflow().currentStage !== stage) return this.dictionary.workflow.panel.blockedOrder;
    if (this.editors().some((editor) => editor.phase().stage !== stage && editor.dirty()))
      return this.dictionary.workflow.panel.blockedDrafts;
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
    if (
      !note ||
      this.isNew() ||
      !this.editable() ||
      this.busy() ||
      this.parentBusy() ||
      this.basePending()
    )
      return;
    await this.perform(async () => {
      const updated = await firstValueFrom(this.service.savePhase(note, this.selected(), data));
      this.editor()?.markSaved();
      this.updated.emit(updated);
      this.message.set({ key: 'workflow.panel.draftSaved' });
    });
  }
  async validate() {
    const note = this.note();
    if (
      !note ||
      this.isNew() ||
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
          ? { key: 'workflow.panel.processClosed' }
          : updated.workflow?.status === 'discarded'
            ? { key: 'workflow.panel.falsePositiveClosed' }
            : { key: 'workflow.panel.phaseValidated' },
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
      this.error.set(this.i18n.errorRef(error));
    } finally {
      this.busy.set(false);
    }
  }
}
