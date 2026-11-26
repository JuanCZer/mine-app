import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ReactiveFormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { AuthService } from '../../core/services/auth.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { ChangePasswordPage } from './change-password';

describe('ChangePasswordPage', () => {
  let fixture: ComponentFixture<ChangePasswordPage>;
  let component: ChangePasswordPage;
  let changePassword: ReturnType<typeof vi.fn>;

  beforeEach(async () => {
    localStorage.removeItem('mine.ui.language');
    changePassword = vi.fn();
    await TestBed.configureTestingModule({
      imports: [ReactiveFormsModule, ChangePasswordPage],
      providers: [
        { provide: AuthService, useValue: { changePassword } },
        {
          provide: Router,
          useValue: { navigate: vi.fn().mockResolvedValue(true) },
        },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(ChangePasswordPage);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  afterEach(() => localStorage.removeItem('mine.ui.language'));

  it('translates a visible validation error without replacing a dirty password form', async () => {
    component.form.setValue({ currentPassword: 'actual', newPassword: 'corta', confirmation: 'corta' });
    component.form.markAsDirty();
    await component.submit();
    fixture.detectChanges();
    const input = fixture.nativeElement.querySelector('#newPassword');
    TestBed.inject(I18nService).setLanguage('en');
    fixture.detectChanges();
    expect(component.error()).toContain('15 to 128');
    expect(fixture.nativeElement.querySelector('#newPassword')).toBe(input);
    expect(input.value).toBe('corta');
    expect(component.form.dirty).toBe(true);
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(changePassword).not.toHaveBeenCalled();
  });

  it('shows a length error when the new password is too short', () => {
    component.form.setValue({
      currentPassword: 'vieja123',
      newPassword: 'corta',
      confirmation: 'corta',
    });

    component.submit();

    expect(component.error()).toContain('15 y 128');
  });

  it('shows a length error when the new password exceeds 128 characters', () => {
    const password = 'a'.repeat(129);
    component.form.setValue({ currentPassword: 'actual', newPassword: password, confirmation: password });
    component.submit();
    expect(component.error()).toContain('15 y 128');
  });

  it('shows an error when confirmation differs', () => {
    component.form.setValue({ currentPassword: 'actual', newPassword: 'contraseña-segura', confirmation: 'otra-contraseña-segura' });
    component.submit();
    expect(component.error()).toContain('coincidir');
    fixture.detectChanges();
    const confirmation = fixture.nativeElement.querySelector('#confirmation') as HTMLInputElement;
    expect(confirmation.getAttribute('aria-invalid')).toBe('true');
    expect(confirmation.getAttribute('aria-describedby')).toContain('change-password-error');
  });

  it('shows required fields when the form is submitted empty', () => {
    component.submit();
    expect(component.error()).toContain('Completa todos los campos');
    expect(component.form.controls.currentPassword.touched).toBe(true);
  });

  it('marks an overly long password invalid and associates its error', () => {
    const password = 'a'.repeat(129);
    component.form.setValue({ currentPassword: 'actual', newPassword: password, confirmation: password });
    component.submit();
    fixture.detectChanges();
    const input = fixture.nativeElement.querySelector('#newPassword') as HTMLInputElement;
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(input.getAttribute('aria-describedby')).toContain('change-password-error');
  });

  it('does not promise a fixed retry time on rate limiting', async () => {
    changePassword.mockRejectedValue(new HttpErrorResponse({ status: 429 }));
    component.form.setValue({ currentPassword: 'actual', newPassword: 'contraseña-segura', confirmation: 'contraseña-segura' });
    await component.submit();
    expect(component.error()).toContain('límite de intentos');
    expect(component.error()).not.toContain('un minuto');
  });

  it.each([15, 128])('accepts %i Unicode characters', async (length) => {
    const password = '🔒'.repeat(length);
    changePassword.mockResolvedValue(undefined);
    component.form.setValue({ currentPassword: 'actual', newPassword: password, confirmation: password });
    await component.submit();
    expect(changePassword).toHaveBeenCalledWith('actual', password, password);
  });

  it.each([14, 129])('rejects %i Unicode characters', async (length) => {
    const password = '🔒'.repeat(length);
    component.form.setValue({ currentPassword: 'actual', newPassword: password, confirmation: password });
    await component.submit();
    expect(changePassword).not.toHaveBeenCalled();
    expect(component.error()).toContain('15 y 128');
  });
});
