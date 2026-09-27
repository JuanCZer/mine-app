import { DecimalPipe } from '@angular/common';
import { Component, computed, input, output } from '@angular/core';
import { DetectionCase, DETECTION_LAYERS } from '../../core/models/detection.model';

@Component({
  selector: 'app-detection-overview',
  imports: [DecimalPipe],
  templateUrl: './detection-overview.html',
  styleUrl: './detection-overview.less',
})
export class DetectionOverview {
  readonly cases = input<DetectionCase[]>([]);
  readonly selected = input('');
  readonly selectedChange = output<string>();
  readonly layers = DETECTION_LAYERS;
  readonly openCases = computed(() => this.cases().filter((item) => item.status === 'open'));
  readonly urgent = computed(
    () => this.openCases().filter((item) => item.priority === 'high').length,
  );
  readonly closed = computed(() => this.cases().filter((item) => item.status === 'closed').length);
  readonly falsePositiveRate = computed(() => {
    const reviewed = this.cases().filter(
      (item) =>
        item.status === 'discarded' || item.stage === 'surveillance' || item.stage === 'mitigation',
    );
    return reviewed.length
      ? `${Math.round((100 * reviewed.filter((item) => item.status === 'discarded').length) / reviewed.length)}%`
      : '—';
  });
  count(layer: string) {
    return this.openCases().filter((item) => item.stage === layer).length;
  }
}
