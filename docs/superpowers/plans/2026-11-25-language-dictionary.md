# Plan de implementación: diccionario EN / ESP

**Objetivo:** selector EN/ESP y todos los textos de interfaz centralizados, con cambio reactivo y conservación de borradores.
**Arquitectura:** diccionarios tipados por módulo ensamblados en un servicio Angular; errores/eventos del servidor con códigos aditivos compatibles.
**Tecnologías:** Angular 22, TypeScript, signals, Intl, ASP.NET Core, Vitest y pruebas HTTP Python.
**Diseño:** [Alcance y contratos](../specs/2026-11-25-language-dictionary-design.md).

## Restricciones

Trabajar en main. No cambiar códigos de catálogos, rutas, permisos ni validaciones. No traducir datos de usuario ni reescribir auditoría histórica. Cambiar idioma no recrea vistas ni mapa. No publicar sin instrucción. Recursos ES/EN con idénticas claves y parámetros; ningún texto visible nuevo queda en plantillas o lógica de componentes.

## Interfaces y distribución

Servicio central: `client/app/core/i18n/i18n.service.ts`; recursos `*-dictionary.ts` exportan `es` y `en` con estructura idéntica. `dictionary.ts` los agrupa como `common`, `account`, `workflow`, `map`, `api`. Componentes usan `get dictionary() { return this.i18n.dictionary(); }`; mensajes dinámicos guardan `{key, params}` en `i18n.message()`, no traducciones ya resueltas. Errores de API: `code`, `parameters`, `issueDetails: [{code, parameters}]`; historial nuevo: `code`, `parameters` opcionales, conservando `text`.

## Tarea 1: infraestructura de traducción

- [x] Escribir pruebas para idioma inicial, preferencia persistida/inválida, almacenamiento no disponible, mensajes reactivos, parámetros y locale.
- [x] Crear servicio, recursos comunes, ensamblador y tipos de mensajes. Registrar locales o usar Intl explícito.
- [x] Verificar pruebas enfocadas en ES y EN; errores de recursos no muestran claves internas al usuario.

## Tarea 2: encabezado, acceso y administración

- [x] Crear `account-dictionary.ts` y migrar `layout`, `pages/login`, `profile`, `change-password`, `admin-users`, y títulos en `app.routes.ts`.
- [x] Añadir selector accesible EN/ESP junto al perfil y en acceso, estado compartido y estilos adaptables.
- [x] Cubrir alternancia con formularios de acceso/administración y errores ya visibles, sin perder datos ni reemitir operaciones.

## Tarea 3: incidencias y fases

- [x] Crear `workflow-dictionary.ts`; migrar `pages/notes`, componentes de notas/detección, modelos de workflow y sus textos derivados.
- [x] Mantener funciones puras con diccionario español opcional por defecto para compatibilidad; vistas pasan diccionario vigente.
- [x] Cubrir nueva incidencia, borradores ocultos, avisos de pendientes, confirmaciones y validaciones en ambos idiomas sin alterar valores/códigos.

## Tarea 4: errores y datos de presentación del servidor

- [x] Añadir códigos/parámetros a errores de incidencias y sus requisitos, manteniendo `error` e `issues` actuales. Cubrir disponibilidad y catálogos.
- [x] Añadir códigos opcionales a eventos nuevos usando metadata existente; conservar eventos antiguos y el texto original. Crear `api-dictionary.ts` con ES/EN para códigos y etiquetas estándar de catálogos.
- [x] Probar HTTP y persistencia compatibles; no deducir códigos desde el texto español.

## Tarea 5: mapa, historial y ficha

- [x] Crear `map-dictionary.ts`; migrar páginas mapa/historial/ficha e `incident-fields.ts`, atributos accesibles, popups y controles.
- [x] Actualizar texto y formatos al alternar idioma sin reconstruir el mapa, perder filtros ni traducir texto de usuarios.
- [x] Traducir etiquetas estándar por códigos, conservar snapshots históricos y valores desconocidos.

## Tarea 6: integración y verificación

- [x] Comprobar paridad ES/EN y parámetros; inventariar HTML y templates inline para detectar literales visibles residuales.
- [x] Ejecutar `npm test -- --watch=false`, pruebas HTTP afectadas, `npm run build:clean`, `git diff --check`.
- [x] Revisión independiente del cambio integrado, corregir defectos con regresiones y documentar cómo modificar/agregar traducciones.

## Riesgos a comprobar

1. Mensaje visible antes del cambio: debe traducirse también, no quedar fijado en español.
2. Borrador nuevo o evidencia incompleta: cambiar idioma conserva valores y estado pendiente.
3. Catálogo dinámico/etiqueta validada: se preservan código y original histórico.
4. Error desconocido/almacenamiento bloqueado: fallback legible, aplicación operativa.
5. Mapa y menú móvil: controles traducidos y navegación accesible sin recreación ni desbordamientos.

## Ejecución

Inventario inicial: no hay infraestructura i18n; textos dispersos entre HTML, templates inline, constantes y respuestas del servidor. El usuario confirmó plan e implementación. La separación por archivos permite migrar cuenta, fases y API en paralelo mientras se implementa el servicio común y el mapa.


## Resultado — 26 de noviembre de 2026

Implementado en la rama local `main`, sin commit, push ni despliegue. Se añadieron cinco módulos de recursos ES/EN y un servicio reactivo; selector junto al perfil y en acceso; migración de las 14 plantillas HTML/inline; títulos, formularios, catálogos, avisos, mapa, ficha e historial. Las fechas y números usan Intl. Los errores y eventos nuevos del servidor añaden metadatos compatibles, sin migrar la base de datos.

Verificación:

- Cliente: `npm test -- --watch=false`, 179 pruebas de 25 archivos, todas correctas. Incluye paridad de claves/parámetros, preferencia persistida/bloqueada, formularios y borradores, mensajes ya visibles, datos históricos y mapa sin reconstrucción.
- Servidor: `npm run test:server`, 260 pruebas correctas; comprobación enfocada final de 48 pruebas correcta y compilación .NET sin errores. Se verificó persistencia JSON real y el contrato HTTP. PostgreSQL fue revisado en código y con el harness existente; no se ejecutó un roundtrip contra una base PostgreSQL real durante este cambio.
- Producción: `npm run build:clean`, correcto sin advertencias ni presupuestos excedidos. `git diff --check`, correcto.
- Auditoría AST de las 14 plantillas: sin frases o atributos de interfaz pendientes fuera del diccionario. Se conservan iconos, códigos, nombres propios, fuentes y contenido original de usuarios/proveedores.
- Revisión independiente: sin regresiones bloqueantes en estado, navegación o compatibilidad API. Se corrigió una etiqueta accesible del logotipo de Mapbox y se cubrió su alternancia con una prueba.
- Documentación de edición y ampliación de recursos añadida a [README](../../../README.md#idiomas-en--esp).

Límites de verificación: no se ha publicado en `app.glenorchydev.com` ni realizado una revisión visual en navegador real. Las pruebas de componentes cubren selector, accesibilidad, comportamiento adaptable existente y conservación de estado; la apariencia final en los navegadores del servidor se comprobará tras desplegar.
