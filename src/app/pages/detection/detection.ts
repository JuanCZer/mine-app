import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, ElementRef, OnInit, ViewChild, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CaseForm } from '../../components/detection/case-form';
import { CaseMap } from '../../components/detection/case-map';
import { DetectionOverview } from '../../components/detection/detection-overview';
import { CaseInput } from '../../core/models/detection.model';
import { CatalogItem, CatalogKey } from '../../core/models/catalog.model';
import { CatalogsService } from '../../core/services/catalogs.service';
import { DetectionService } from '../../core/services/detection.service';
import { downloadCases } from '../../core/services/detection-export';

@Component({
  selector: 'app-detection',
  imports: [RouterLink, DatePipe, DecimalPipe, CaseForm, CaseMap, DetectionOverview],
  templateUrl: './detection.html',
  styleUrl: './detection.less',
})
export class DetectionPage implements OnInit {
  readonly store = inject(DetectionService);
  private readonly catalogsService = inject(CatalogsService);
  private readonly router = inject(Router);
  @ViewChild('createDialog') dialog!: ElementRef<HTMLDialogElement>;
  private readonly catalogKeys: readonly CatalogKey[] = ['mexicoStates', 'soils', 'riskLevels', 'workflowStatuses', 'workflowStages'];
  readonly catalogs = signal<Partial<Record<CatalogKey, CatalogItem[]>>>({});
  readonly catalogLoading = signal(true);
  readonly catalogError = signal('');
  readonly search = signal('');
  readonly stage = signal('');
  readonly priority = signal('');
  readonly status = signal('');
  readonly mapVisible = signal(false);
  readonly page = signal(1);
  readonly error = signal('');
  readonly creating = signal(false);
  readonly filtered = computed(() => {
    const term = this.search().trim().toLocaleLowerCase('es');
    return this.store
      .cases()
      .filter(
        (item) =>
          (!term ||
            `${item.code} ${item.title} ${item.state} ${item.municipality} ${item.owner}`
              .toLocaleLowerCase('es')
              .includes(term)) &&
          (!this.stage() || item.stage === this.stage()) &&
          (!this.priority() || item.priority === this.priority()) &&
          (!this.status() || item.status === this.status()),
      );
  });
  readonly pageCount = computed(() => Math.max(1, Math.ceil(this.filtered().length / 10)));
  readonly visible = computed(() =>
    this.filtered().slice((this.page() - 1) * 10, this.page() * 10),
  );
  readonly hasFilters = computed(
    () => !!(this.search() || this.stage() || this.priority() || this.status()),
  );
  ngOnInit() {
    void this.loadCatalogs();
  }
  async loadCatalogs(reload = false) {
    this.catalogLoading.set(true);
    this.catalogError.set('');
    try {
      const response = await firstValueFrom(
        reload
          ? this.catalogsService.reload(this.catalogKeys, { includeInactive: true })
          : this.catalogsService.load(this.catalogKeys, { includeInactive: true }),
      );
      this.catalogs.set(response.catalogs);
    } catch {
      this.catalogError.set('No fue posible actualizar los catálogos. Los expedientes siguen disponibles para consulta.');
    } finally {
      this.catalogLoading.set(false);
    }
  }
  options(key: CatalogKey, selected = '') {
    return this.catalogsService.selectable(this.catalogs()[key] ?? [], selected);
  }
  label(key: CatalogKey, code: string) {
    return this.catalogsService.label(this.catalogs()[key] ?? [], code, code);
  }
  filter(field: 'search' | 'stage' | 'priority' | 'status', value: string) {
    this[field].set(value);
    this.page.set(1);
  }
  clearFilters() {
    this.search.set('');
    this.stage.set('');
    this.priority.set('');
    this.status.set('');
    this.page.set(1);
  }
  newCase() {
    this.error.set('');
    this.creating.set(true);
    this.dialog.nativeElement.showModal();
  }
  closeDialog() {
    this.dialog.nativeElement.close();
    this.creating.set(false);
  }
  create(input: CaseInput) {
    try {
      const item = this.store.create(input);
      this.closeDialog();
      void this.router.navigate(['/deteccion', item.id]);
    } catch (error) {
      this.error.set(error instanceof Error ? error.message : 'No se pudo crear el expediente.');
    }
  }
  open(id: string) {
    void this.router.navigate(['/deteccion', id]);
  }
  export(format: 'json' | 'geojson') {
    downloadCases(this.filtered(), format);
  }
  addExample() {
    try {
      const item = this.store.create(
        {
          title: 'Ejemplo · cambio de superficie',
          state: 'Michoacán',
          municipality: 'Zona de demostración',
          latitude: 19.7,
          longitude: -101.2,
          soil: 'andosol',
          season: 'rainy',
          priority: 'medium',
          owner: 'Responsable de ejemplo',
          description:
            'Registro ficticio para explorar el flujo. No corresponde a un hallazgo real. Las referencias de ejemplo no contienen archivos de sensores.',
        },
        true,
      );
      this.clearFilters();
      this.open(item.id);
    } catch (error) {
      this.error.set(error instanceof Error ? error.message : 'No se pudo crear el ejemplo.');
    }
  }
}
