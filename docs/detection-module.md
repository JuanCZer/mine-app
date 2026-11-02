# Retiro seguro del panel local de detección

El flujo operativo es `/incidencias`: usa la API autenticada, PostgreSQL y `owner_user_id` para aislar los registros de cada cuenta. Las rutas anteriores `/deteccion` y `/deteccion/:id` redirigen a ese flujo.

El panel local se retiró porque guardaba todos los expedientes del origen en una sola clave `localStorage`, `gds-detection-cases-v1`. Esa clave no contiene un UUID de usuario autenticado. Después de cerrar la sesión de una cuenta, otra cuenta que usara el mismo perfil de navegador podía consultar y modificar sus expedientes. Agregar el nombre del usuario a la clave no habría creado una frontera de autorización.

## Conservación de los datos anteriores

El bundle nuevo no lee, reescribe ni borra la clave retirada. El cierre de sesión tampoco la elimina. Esto conserva el archivo original para una exportación y conciliación controladas, pero esos datos ya no se muestran ni se pueden modificar desde la interfaz multiusuario. El blob sigue siendo material sensible en el perfil hasta que un custodio verifique su exportación y conciliación o apruebe y verifique su eliminación. Antes de permitir un cambio de cuenta también deben cerrarse o inutilizarse todas las pestañas que conservaron el bundle anterior, siguiendo la barrera del [runbook de corte](runbooks/postgresql-cutover.md#barrera-obligatoria-para-retirar-el-bundle-local).

Antes de desplegar esta versión, el custodio debe exportar cada perfil autorizado desde la versión anterior con el botón JSON. Si la versión nueva ya fue desplegada, un custodio con acceso al perfil original puede recuperar la copia exacta desde las herramientas del navegador, en el origen de la aplicación:

```js
const key = 'gds-detection-cases-v1';
const raw = localStorage.getItem(key);
if (!raw) throw new Error('No existe un archivo local de detección en este perfil.');
const url = URL.createObjectURL(new Blob([raw], { type: 'application/json' }));
const link = Object.assign(document.createElement('a'), {
  href: url,
  download: 'gds-detection-cases-v1.json',
});
link.click();
setTimeout(() => URL.revokeObjectURL(url), 1000);
```

Guardar el archivo en almacenamiento protegido y registrar su SHA-256. No copiar cookies, tokens u otros valores del almacenamiento del navegador. La eliminación de la clave requiere una acción posterior, explícita y aprobada por el custodio una vez conciliada la fuente.

## Análisis y conciliación

`LegacyDetectionImporter` acepta tanto el sobre generado por el exportador (`schemaVersion: 1`) como la copia exacta de `localStorage` (`version: 1`), ambos con `cases`. También conserva un arreglo JSON heredado. El analizador valida el campo real `latitude`, conserva el JSON original y no convierte `stage`, revisiones o historial locales en validaciones autenticadas.

Cada valor `owner` es texto declarado y debe relacionarse de forma explícita con un UUID de Identity mediante el mapa de propietarios. Un valor ambiguo queda en cuarentena. La conciliación y las limitaciones del adaptador PostgreSQL se describen en [Corte de incidencias JSON a PostgreSQL](runbooks/postgresql-cutover.md).

## Verificación

```bash
npm test -- --watch=false
npm run build
python3 -m unittest backend-tests/test_legacy_import.py -v
```

Las pruebas comprueban que el servicio retirado no lee ni modifica el archivo, que el logout lo conserva, que las rutas ya no cargan el panel y que ambos sobres versionados pueden analizarse sin perder los registros originales.
