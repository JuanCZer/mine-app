export const CATALOG_KEYS = [
  'mexicoStates',
  'soils',
  'seasons',
  'riskLevels',
  'sensors',
  'platforms',
  'indicationSources',
  'indicationSignatures',
  'radioTechnologies',
  'surveillanceMethods',
  'surveillanceDeployments',
  'humanActivities',
  'environmentDecisions',
  'protectionLevels',
  'neutralizationResults',
  'routeResults',
  'findings',
  'environmentReviews',
  'workflowStatuses',
  'workflowStages',
] as const;

export type CatalogKey = (typeof CATALOG_KEYS)[number];
export type CatalogStage = 'indications' | 'reconnaissance' | 'surveillance' | 'mitigation';

export interface CatalogItem {
  id: string;
  code: string;
  label: string;
  sortOrder: number;
  active: boolean;
}

export interface CatalogResponse {
  version: number;
  catalogs: Partial<Record<CatalogKey, CatalogItem[]>>;
}

export interface CatalogQuery {
  stage?: CatalogStage;
  includeInactive?: boolean;
}
