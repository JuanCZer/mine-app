import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthService } from '../../core/services/auth.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { ProfilePage } from './profile';

describe('ProfilePage', () => {
  beforeEach(() => localStorage.removeItem('mine.ui.language'));
  afterEach(() => localStorage.removeItem('mine.ui.language'));

  it('updates standard role and date presentation while preserving account data and the embedded password form', async () => {
    const user = signal({ username: 'ana', displayName: 'Ana Pérez', role: 'operador', createdAt: '2026-10-01T10:00:00Z' });
    await TestBed.configureTestingModule({ imports: [ProfilePage], providers: [provideRouter([]), { provide: AuthService, useValue: { user } }] }).compileComponents();
    const fixture = TestBed.createComponent(ProfilePage);
    fixture.detectChanges();
    const form = fixture.nativeElement.querySelector('app-change-password form');
    TestBed.inject(I18nService).setLanguage('en');
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Operator');
    expect(fixture.nativeElement.textContent).toContain('Oct 1, 2026');
    expect(fixture.nativeElement.textContent).toContain('Ana Pérez');
    expect(fixture.nativeElement.querySelector('app-change-password form')).toBe(form);
    user.update(value => ({ ...value, role: 'Equipo especial' }));
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Equipo especial');
  });

  it('shows the authenticated account data and the existing password form', async () => {
    const user = signal({ username: 'ana', displayName: 'Ana Pérez', role: 'Supervisor', createdAt: '2026-10-01T10:00:00Z' });
    await TestBed.configureTestingModule({ imports: [ProfilePage], providers: [provideRouter([]), { provide: AuthService, useValue: { user } }] }).compileComponents();
    const fixture = TestBed.createComponent(ProfilePage);
    fixture.detectChanges();
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Ana Pérez');
    expect(text).toContain('ana');
    expect(text).toContain('Supervisor');
    expect(text).toContain(new Intl.DateTimeFormat('es-MX', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date('2026-10-01T10:00:00Z')));
    expect(fixture.nativeElement.querySelector('app-change-password form')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('app-change-password.embedded')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('a[href="/cuenta/contrasena"]')).toBeNull();
  });

  it('does not invent a role or creation date when they are missing', async () => {
    const user = signal({ username: 'ana', displayName: '', role: null, createdAt: null });
    await TestBed.configureTestingModule({ imports: [ProfilePage], providers: [provideRouter([]), { provide: AuthService, useValue: { user } }] }).compileComponents();
    const fixture = TestBed.createComponent(ProfilePage);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Rol no disponible');
    expect(fixture.nativeElement.textContent).toContain('Verifica la asignación de rol y la versión del backend');
    expect(fixture.nativeElement.textContent).toContain('No disponible');
  });
});
