import { routes } from './app.routes';

describe('rutas operativas', () => {
  it('retira las rutas del almacén local compartido', () => {
    const shell = routes.find((route) => route.path === '' && route.children)?.children ?? [];
    const paths = shell.map((route) => route.path);

    expect(paths).not.toContain('deteccion');
    expect(paths).not.toContain('deteccion/:id');
  });

  it('aplica la política de autenticación, cambio de contraseña y acceso anónimo', () => {
    const shell = routes.find((route) => route.path === '' && route.children);
    expect(shell).toMatchObject({ canActivateChild: expect.any(Array) });

    const login = routes.find((route) => route.path === 'login');
    expect(login).toMatchObject({ canActivate: expect.any(Array) });

    const passwordChange = shell?.children?.find((route) => route.path === 'cuenta/contrasena');
    const profile = shell?.children?.find((route) => route.path === 'cuenta/perfil');
    expect(passwordChange).toBeTruthy();
    expect(profile).toBeTruthy();
  });

  it('redirige enlaces históricos de detección al flujo PostgreSQL con ownership', () => {
    const legacy = routes.find((route) => route.path === 'deteccion');
    const legacyDetail = routes.find((route) => route.path === 'deteccion/:id');

    expect(legacy).toMatchObject({ redirectTo: 'incidencias', pathMatch: 'full' });
    expect(legacyDetail).toMatchObject({ redirectTo: 'incidencias', pathMatch: 'full' });
  });
});
