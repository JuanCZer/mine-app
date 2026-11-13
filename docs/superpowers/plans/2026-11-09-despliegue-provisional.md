# Seguimiento: integración provisional en servidor privado

- [x] Separar los workflows futuros para que no los ejecute GitHub.
- [x] Crear workflow provisional en `.github/workflows`, con push a `main`, ejecución manual desde `main` y runner privado dedicado.
- [x] Crear Compose provisional y plantilla de configuración externa sin secretos.
- [x] Documentar preparación, límites de seguridad y dependencia de TLS de PostgreSQL.
- [x] Comprobar contratos locales de configuración y flujo con pruebas automatizadas.
- [x] Elegir un contenedor de desarrollo independiente, con proyecto y volumen propios.
- [ ] Configurar runner, Docker, Caddy, base de datos de pruebas, runtime y certificados en el servidor.
- [ ] Ejecutar el workflow en GitHub y comprobar la imagen, el inicio, la conexión a PostgreSQL y los flujos reales.

La configuración no se ha desplegado ni validado en el servidor. El workflow de release definitivo permanece como plantilla inactiva en `deploy/futuro/workflows/`.
