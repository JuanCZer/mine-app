# Diccionario bilingüe EN / ESP

El usuario solicita plan e implementación, confirmados en esta conversación. Añadir un selector EN / ESP junto al usuario conectado y extraer los textos de interfaz de HTML/TypeScript a diccionarios editables centralizados. Trabajar en `main`, respetando la preferencia existente.

## Comportamiento

- Español por defecto. La selección manual EN/ESP corresponde a `en`/`es`, se recuerda en `localStorage` con clave `mine.ui.language`; almacenamiento bloqueado o inválido conserva funcionamiento en memoria y cae a español.
- El cambio actualiza la interfaz en la misma página: formularios, borradores, selección de fase, mapa, sesión y solicitudes en curso permanecen intactos. Actualizar `document.documentElement.lang` y título del documento.
- Selector nativo accesible por teclado, selección anunciada, junto al perfil, adaptable a móvil. La página de acceso también permite elegir idioma usando el mismo estado.
- Traducir títulos, navegación, formularios, placeholders, tooltips, aria-labels, botones, instrucciones, estados, avisos, confirmaciones, validaciones, errores reconocidos y texto de controles del mapa. Fechas/números usan es-MX o en-US.
- Los textos contienen parámetros completos (`{count}`, `{name}`, `{phase}`); no construir frases traducidas concatenando palabras. Las variantes singular/plural se definen en el diccionario cuando correspondan.
- No traducir contenido escrito por usuarios, identificadores, rutas, nombres propios, modelos de equipos, unidades técnicas ni originales históricos. Catálogos estándar se presentan mediante código estable y etiqueta localizada; elementos desconocidos y etiquetas congeladas de validación conservan su original.
- Los errores de API y eventos nuevos deben exponer código estable y parámetros, además del texto actual para compatibilidad. El frontend muestra esos códigos desde su diccionario. Eventos históricos sin código conservan su texto original; no deducir códigos analizando frases españolas ni reescribir datos auditados.

## Arquitectura y alternativas

Decisión de implementación: servicio Angular con signals y diccionarios TypeScript tipados por módulo, ensamblados desde `client/app/core/i18n/dictionary.ts`. Dos versiones por módulo (`es`, `en`) mantienen las mismas claves. Así las vistas usan `{{ dictionary.workflow.layers.indicationsTitle }}` y no contienen el texto final. Editar textos requiere cambiar recursos y desplegar, sin modificar vistas; cambiar idioma en la app no requiere recargar ni descargar otra aplicación.

Se consideró i18n compilado de Angular, cuyo despliegue habitual crea variantes por locale, y una biblioteca de traducción en runtime. Para dos idiomas, acceso directo a `dictionary` y conservación de borradores, un servicio pequeño evita dependencia adicional y resuelve el cambio en runtime. Referencias: [Angular i18n](https://angular.dev/guide/i18n/merge), [signals](https://angular.dev/guide/signals).

`I18nService`: `language()`, `locale()`, `dictionary()`, `setLanguage('es'|'en')`, `text(key, params)`, `format(template, params)`, `date(value)`, `number(value)`, `message(initial?)`, `errorRef(error, fallbackKey?)`. `message()` devuelve un getter reactivo con `.set()` que almacena referencias `{key, params}` o listas de ellas, para traducir mensajes ya visibles al cambiar idioma. Un getter de componente `dictionary` expone `i18n.dictionary()`.

Recursos separados por responsabilidad: `common`, `account` (shell/acceso/perfil/administración), `workflow` (fases/evidencias/validaciones), `map` (mapa/historial/ficha), `api` (errores/eventos/catálogos). Los módulos se integran bajo un único objeto por idioma; claves faltantes se detectan en pruebas. Identificadores persistidos y reglas de negocio permanecen iguales.

## Aceptación

Alternar EN/ESP actualiza todas las pantallas y mensajes visibles, sin navegación ni pérdida de formularios nuevos/anteriores. Recargar recuerda el idioma. Pruebas de paridad de claves/parámetros y de textos en vistas impiden traducciones incompletas. API conserva compatibilidad y códigos independientes del idioma. Suite cliente, pruebas de API afectadas y build de producción deben pasar.
