import * as common from './common-dictionary';
import * as account from './account-dictionary';
import * as workflow from './workflow-dictionary';
import * as map from './map-dictionary';
import * as api from './api-dictionary';

export const spanish = {
  common: common.es,
  account: account.es,
  workflow: workflow.es,
  map: map.es,
  api: api.es,
};
export type Dictionary = typeof spanish;
export const english: Dictionary = {
  common: common.en,
  account: account.en,
  workflow: workflow.en,
  map: map.en,
  api: api.en,
};
export const dictionaries: Record<'es' | 'en', Dictionary> = { es: spanish, en: english };
