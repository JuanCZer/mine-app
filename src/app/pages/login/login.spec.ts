import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter, Router } from '@angular/router';
import { LoginPage } from './login';
import { AuthService } from '../../core/services/auth.service';

describe('LoginPage', () => {
  let fixture: ComponentFixture<LoginPage>;
  let auth: { login: ReturnType<typeof vi.fn>; state: ReturnType<typeof signal<string>> };

  async function create(returnUrl: string | null = null) {
    auth = { login: vi.fn().mockResolvedValue(undefined), state: signal('anonymous') };
    await TestBed.configureTestingModule({
      imports: [LoginPage],
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: auth },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: { get: () => returnUrl } } },
        },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(LoginPage);
    fixture.detectChanges();
  }

  it('renders empty credential controls without demo access instructions', async () => {
    await create();
    const component = fixture.componentInstance;
    expect(component.form.getRawValue()).toEqual({ username: '', password: '' });
    expect(fixture.nativeElement.textContent).not.toContain('Demo123!');
    expect(fixture.nativeElement.textContent).not.toContain('demostración');
  });

  it('explains when session verification could not contact the service', async () => {
    await create();
    auth.state.set('unavailable');
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('No fue posible verificar la sesión');
    expect(fixture.nativeElement.querySelector('[role="status"]')).toBeTruthy();
  });

  it('returns only to a validated local destination after login', async () => {
    await create('/mapa?zoom=4');
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
    fixture.componentInstance.form.setValue({ username: 'operador', password: 'secreto' });
    await fixture.componentInstance.submit();

    expect(auth.login).toHaveBeenCalledWith('operador', 'secreto');
    expect(navigate).toHaveBeenCalledWith('/mapa?zoom=4');
  });

  it('rejects an external return URL', async () => {
    await create('//evil.example');
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
    fixture.componentInstance.form.setValue({ username: 'operador', password: 'secreto' });
    await fixture.componentInstance.submit();

    expect(navigate).toHaveBeenCalledWith('/incidencias');
  });
});
