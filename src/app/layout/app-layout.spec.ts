import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Component, signal, Type } from '@angular/core';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { render } from 'less';
import { By } from '@angular/platform-browser';
import { provideRouter, Router } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../core/services/auth.service';
import { CatalogsService } from '../core/services/catalogs.service';
import { NotesService } from '../core/services/notes.service';
import { NotesPage } from '../pages/notes/notes';
import { AppLayout } from './app-layout';

@Component({ selector: 'test-incidents', template: '<p>Incidencias</p>' })
class IncidentsStub {}

@Component({ selector: 'test-history', template: '<p>Historial</p>' })
class HistoryStub {}

const SIDEBAR_STORAGE_KEY = 'mine.ui.sidebarCollapsed';
const SPEC_DIRECTORY = dirname(fileURLToPath(import.meta.url));

let injectedLayoutStyles: HTMLStyleElement[] = [];

async function installLayoutStyles() {
  const componentStylesPath = join(SPEC_DIRECTORY, 'app-layout.less');
  const responsiveStylesPath = join(SPEC_DIRECTORY, 'app-layout-responsive.less');
  const globalStylesPath = join(SPEC_DIRECTORY, '../../styles.less');
  const [componentStyles, responsiveStyles, globalStyles] = await Promise.all([
    render(readFileSync(componentStylesPath, 'utf8'), { filename: componentStylesPath }),
    render(readFileSync(responsiveStylesPath, 'utf8'), { filename: responsiveStylesPath }),
    render(readFileSync(globalStylesPath, 'utf8').replace(/^@import[^;]+;\s*/m, ''), {
      filename: globalStylesPath,
    }),
  ]);

  // Angular scopes :host to the component host; mirror that selector in this DOM/CSS test.
  for (const cssText of [componentStyles.css.replaceAll(':host', 'app-layout'), responsiveStyles.css.replaceAll(':host', 'app-layout'), globalStyles.css]) {
    const style = document.createElement('style');
    style.textContent = cssText;
    document.head.append(style);
    injectedLayoutStyles.push(style);
  }
}

function layoutStyleRules(): CSSStyleRule[] {
  const visit = (rules: CSSRuleList): CSSStyleRule[] =>
    Array.from(rules).flatMap((rule) => {
      if (rule.type === CSSRule.STYLE_RULE) return [rule as CSSStyleRule];
      return 'cssRules' in rule ? visit((rule as CSSGroupingRule).cssRules) : [];
    });

  return injectedLayoutStyles.flatMap((style) => (style.sheet ? visit(style.sheet.cssRules) : []));
}

function mediaQuery(viewportWidth: number) {
  let matches = false;
  let media = '';
  const listeners = new Set<(event: MediaQueryListEvent) => void>();
  const query = {
    get media() {
      return media;
    },
    get matches() {
      return matches;
    },
    onchange: null,
    addEventListener: (_type: string, listener: (event: MediaQueryListEvent) => void) =>
      listeners.add(listener),
    removeEventListener: (_type: string, listener: (event: MediaQueryListEvent) => void) =>
      listeners.delete(listener),
    addListener: (listener: (event: MediaQueryListEvent) => void) => listeners.add(listener),
    removeListener: (listener: (event: MediaQueryListEvent) => void) => listeners.delete(listener),
    dispatchEvent: () => true,
    configure(nextMedia: string) {
      media = nextMedia;
      const maxWidth = nextMedia.match(/max-width:\s*(\d+)px/);
      matches = maxWidth ? viewportWidth <= Number(maxWidth[1]) : false;
    },
    setMatches(next: boolean) {
      matches = next;
      const event = { matches: next, media: query.media } as MediaQueryListEvent;
      listeners.forEach((listener) => listener(event));
    },
  };
  return query;
}

describe('AppLayout', () => {
  let fixture: ComponentFixture<AppLayout>;
  let router: Router;
  let allowLeave: boolean;
  let viewport: ReturnType<typeof mediaQuery>;
  let logout: ReturnType<typeof vi.fn>;

  async function createLayout(
    mobileOrWidth: boolean | number = false,
    incidentsComponent: Type<unknown> = IncidentsStub,
    canManage = false,
    role = canManage ? 'administrador' : 'operador',
  ) {
    const viewportWidth =
      typeof mobileOrWidth === 'number' ? mobileOrWidth : mobileOrWidth ? 767 : 1280;
    viewport = mediaQuery(viewportWidth);
    vi.stubGlobal(
      'matchMedia',
      vi.fn((query: string) => {
        viewport.configure(query);
        return viewport as unknown as MediaQueryList;
      }),
    );
    allowLeave = true;
    logout = vi.fn().mockResolvedValue(undefined);
    const authenticatedUser = signal({
      id: '1c504531-ab30-49d4-a860-ae69fdbb7b11',
      username: 'operador',
      displayName: 'Operador Uno',
      role,
      permissions: canManage ? ['incidents:read', 'users:provision'] : ['incidents:read'],
    });
    await TestBed.configureTestingModule({
      imports: [AppLayout],
      providers: [
        provideRouter([
          {
            path: 'incidencias',
            component: incidentsComponent,
            canDeactivate: [() => allowLeave],
          },
          { path: 'historial', component: HistoryStub },
          { path: 'cuenta/perfil', component: HistoryStub },
          { path: 'administracion/usuarios', component: HistoryStub },
        ]),
        { provide: AuthService, useValue: { user: authenticatedUser.asReadonly(), logout } },
        { provide: NotesService, useValue: { list: () => of([]) } },
        {
          provide: CatalogsService,
          useValue: {
            load: () => of({ version: 1, catalogs: {} }),
            reload: () => of({ version: 1, catalogs: {} }),
            selectable: (items: readonly unknown[]) => [...items],
          },
        },
      ],
    }).compileComponents();
    router = TestBed.inject(Router);
    await router.navigateByUrl('/incidencias');
    fixture = TestBed.createComponent(AppLayout);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  function element<T extends Element>(selector: string): T {
    return fixture.nativeElement.querySelector(selector) as T;
  }

  async function waitForFocus() {
    await new Promise<void>((resolve) => setTimeout(resolve));
  }

  beforeEach(() => localStorage.clear());

  afterEach(() => {
    fixture?.destroy();
    injectedLayoutStyles.forEach((style) => style.remove());
    injectedLayoutStyles = [];
    vi.useRealTimers();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    localStorage.clear();
  });

  it('shows a neutral desktop layout without the GDS brand or fictitious workspace status', async () => {
    await createLayout();
    const toggle = element<HTMLButtonElement>('.sidebar-toggle');

    expect(element<HTMLElement>('.shell').classList).not.toContain('sidebar-collapsed');
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(element<HTMLElement>('.sidebar').hasAttribute('inert')).toBe(false);
    expect(toggle.getAttribute('aria-controls')).toBe('primary-nav-links');
    expect(element<HTMLElement>('.sidebar nav').id).toBe('primary-nav-links');
    expect(fixture.nativeElement.textContent).not.toContain('GDS');
    expect(fixture.nativeElement.textContent).not.toContain('Espacio de trabajo');
    expect(fixture.nativeElement.textContent).not.toContain('Operativo');
    expect(fixture.nativeElement.textContent).not.toContain('ENTORNO CONTROLADO');
    expect(element<HTMLElement>('.context').textContent).toContain('Gestión de incidencias');
    expect(element<HTMLElement>('.session-user-button').textContent).toContain('Operador Uno');
    expect(fixture.nativeElement.querySelector('a[href="/deteccion"]')).toBeNull();
  });

  it('keeps the confirmed user visible and offers retry when logout cannot be confirmed', async () => {
    await createLayout();
    logout.mockRejectedValueOnce(new Error('network'));

    element<HTMLButtonElement>('.sidebar-logout').click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(element<HTMLElement>('.session-error').textContent).toContain(
      'No se pudo confirmar el cierre de sesión',
    );
    expect(element<HTMLElement>('.session-user-button').textContent).toContain('operador');

    element<HTMLButtonElement>('.session-error button').click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(logout).toHaveBeenCalledTimes(2);
    expect(fixture.nativeElement.querySelector('.session-error')).toBeNull();
  });

  it('collapses and reopens the desktop sidebar while persisting the preference', async () => {
    await installLayoutStyles();
    await createLayout();
    const toggle = element<HTMLButtonElement>('.sidebar-toggle');

    toggle.click();
    fixture.detectChanges();
    expect(element<HTMLElement>('.shell').classList).toContain('sidebar-collapsed');
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(element<HTMLElement>('.sidebar').hasAttribute('inert')).toBe(false);
    expect(element<HTMLElement>('.sidebar nav').hasAttribute('inert')).toBe(false);
    expect(element<HTMLElement>('.sidebar nav').getAttribute('aria-hidden')).toBeNull();
    const railLinks = Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLAnchorElement>('.sidebar nav a'),
    );
    expect(railLinks.map((link) => link.getAttribute('aria-label'))).toEqual([
      'Incidencias',
      'Historial',
      'Mapa',
    ]);
    expect(railLinks.map((link) => link.getAttribute('data-tooltip'))).toEqual([
      'Incidencias',
      'Historial',
      'Mapa',
    ]);
    expect(railLinks.every((link) => link.tabIndex === 0)).toBe(true);
    expect(railLinks.map((link) => link.querySelector('.nav-label')?.textContent?.trim())).toEqual([
      'Incidencias',
      'Historial',
      'Mapa',
    ]);
    expect(railLinks[0].classList).toContain('active');
    expect(element<HTMLElement>('.sidebar nav').querySelector('.nav-caption')).not.toBeNull();
    expect(element<HTMLButtonElement>('.sidebar-toggle').getAttribute('aria-label')).toBe(
      'Abrir menú',
    );
    expect(localStorage.getItem(SIDEBAR_STORAGE_KEY)).toBe('true');

    const host = fixture.nativeElement as HTMLElement;
    const railToken = getComputedStyle(host).getPropertyValue('--sidebar-rail-width').trim();
    const label = element<HTMLElement>('.nav-label');
    const styles = layoutStyleRules();
    const tooltipBase = styles.find(
      (rule) =>
        rule.selectorText ===
        'app-layout .shell.sidebar-collapsed .sidebar nav a[data-tooltip]::after',
    );
    const tooltipInteraction = styles.find(
      (rule) =>
        rule.selectorText ===
        'app-layout .shell.sidebar-collapsed .sidebar nav a[data-tooltip]:hover::after, app-layout .shell.sidebar-collapsed .sidebar nav a[data-tooltip]:focus-visible::after',
    );

    expect(railToken).toBe('72px');
    expect(getComputedStyle(element<HTMLElement>('.sidebar')).width).toBe(
      'var(--sidebar-rail-width)',
    );
    expect(getComputedStyle(element<HTMLElement>('.workspace')).marginLeft).toBe(
      'var(--sidebar-rail-width)',
    );
    expect(getComputedStyle(railLinks[0]).width).toBe('48px');
    expect(getComputedStyle(railLinks[0]).minHeight).toBe('48px');
    expect(getComputedStyle(label).display).toBe('none');
    expect(tooltipBase?.style.getPropertyValue('pointer-events')).toBe('none');
    expect(tooltipBase?.style.getPropertyValue('position')).toBe('absolute');
    expect(tooltipInteraction?.style.getPropertyValue('display')).toBe('block');
    expect(
      injectedLayoutStyles.some((style) =>
        style.textContent?.includes('content: attr(data-tooltip);'),
      ),
    ).toBe(true);

    toggle.click();
    fixture.detectChanges();
    expect(element<HTMLElement>('.shell').classList).not.toContain('sidebar-collapsed');
    expect(getComputedStyle(element<HTMLElement>('.sidebar')).width).toBe(
      'var(--sidebar-expanded-width)',
    );
    expect(getComputedStyle(element<HTMLElement>('.workspace')).marginLeft).toBe(
      'var(--sidebar-expanded-width)',
    );
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(toggle.getAttribute('aria-label')).toBe('Ocultar menú');
    expect(element<HTMLElement>('.sidebar nav').hasAttribute('inert')).toBe(false);
    expect(element<HTMLElement>('.sidebar nav').hasAttribute('aria-hidden')).toBe(false);
    expect(getComputedStyle(label).display).not.toBe('none');
    expect(localStorage.getItem(SIDEBAR_STORAGE_KEY)).toBe('false');
  });

  it('keeps the real routed notes form and its dirty title draft mounted while toggling the sidebar', async () => {
    await createLayout(false, NotesPage);
    const notesPage = fixture.debugElement.query(By.directive(NotesPage))
      .componentInstance as NotesPage;
    const titleControl = notesPage.form.controls.title;
    const titleInput = element<HTMLInputElement>('input[formcontrolname="title"]');
    const draftTitle = 'Borrador I&W conservado';

    titleInput.value = draftTitle;
    titleInput.dispatchEvent(new Event('input', { bubbles: true }));
    fixture.detectChanges();

    expect(titleControl.value).toBe(draftTitle);
    expect(notesPage.baseDirty()).toBe(true);

    const toggle = element<HTMLButtonElement>('.sidebar-toggle');
    toggle.click();
    fixture.detectChanges();
    toggle.click();
    fixture.detectChanges();

    const reopenedNotesPage = fixture.debugElement.query(By.directive(NotesPage))
      .componentInstance as NotesPage;
    const reopenedTitleInput = element<HTMLInputElement>('input[formcontrolname="title"]');
    expect(reopenedNotesPage).toBe(notesPage);
    expect(reopenedNotesPage.form.controls.title).toBe(titleControl);
    expect(reopenedTitleInput).toBe(titleInput);
    expect(reopenedTitleInput.value).toBe(draftTitle);
    expect(reopenedNotesPage.baseDirty()).toBe(true);
  });

  it('recovers the saved desktop preference when the component is recreated', async () => {
    localStorage.setItem(SIDEBAR_STORAGE_KEY, 'true');
    await createLayout();

    expect(element<HTMLElement>('.shell').classList).toContain('sidebar-collapsed');
    fixture.destroy();
    fixture = TestBed.createComponent(AppLayout);
    fixture.detectChanges();

    expect(element<HTMLElement>('.shell').classList).toContain('sidebar-collapsed');
    expect(element<HTMLButtonElement>('.sidebar-toggle').getAttribute('aria-expanded')).toBe(
      'false',
    );
  });

  it('keeps the menu usable when browser storage cannot be read or written', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('Storage blocked');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('Storage blocked');
    });
    await createLayout();

    const toggle = element<HTMLButtonElement>('.sidebar-toggle');
    toggle.click();
    fixture.detectChanges();

    expect(element<HTMLElement>('.shell').classList).toContain('sidebar-collapsed');
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
  });

  it('opens the mobile panel as a modal and restores trigger focus after Escape', async () => {
    await createLayout(true);
    const toggle = element<HTMLButtonElement>('.menu');
    const sidebar = element<HTMLElement>('.sidebar');
    const workspace = element<HTMLElement>('.workspace');

    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(sidebar.hasAttribute('inert')).toBe(true);
    expect(workspace.hasAttribute('inert')).toBe(false);

    toggle.focus();
    toggle.click();
    fixture.detectChanges();
    await fixture.whenStable();
    await waitForFocus();
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(sidebar.hasAttribute('inert')).toBe(false);
    expect(workspace.hasAttribute('inert')).toBe(true);
    expect(document.activeElement).toBe(sidebar);

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    fixture.detectChanges();
    await fixture.whenStable();
    await waitForFocus();
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(sidebar.hasAttribute('inert')).toBe(true);
    expect(workspace.hasAttribute('inert')).toBe(false);
    expect(document.activeElement).toBe(toggle);
  });

  it('keeps 767 px in the modal mobile mode', async () => {
    await installLayoutStyles();
    await createLayout(767);
    const toggle = element<HTMLButtonElement>('.menu');
    const sidebar = element<HTMLElement>('.sidebar');
    const workspace = element<HTMLElement>('.workspace');

    expect(element<HTMLElement>('.shell').classList).toContain('mobile');
    expect(sidebar.getAttribute('role')).toBe('dialog');
    expect(sidebar.getAttribute('aria-modal')).toBeNull();
    expect(sidebar.hasAttribute('inert')).toBe(true);
    expect(toggle.getAttribute('aria-label')).toBe('Abrir menú');
    expect(toggle.getAttribute('aria-controls')).toBe('primary-navigation');
    expect(element<HTMLButtonElement>('.sidebar-toggle').hasAttribute('inert')).toBe(true);
    expect(element<HTMLButtonElement>('.sidebar-toggle').getAttribute('aria-hidden')).toBe('true');
    expect(element<HTMLElement>('.shell').classList).not.toContain('sidebar-collapsed');
    expect(getComputedStyle(element<HTMLElement>('.nav-label')).display).not.toBe('none');

    toggle.click();
    fixture.detectChanges();
    expect(sidebar.getAttribute('aria-modal')).toBe('true');
    expect(workspace.hasAttribute('inert')).toBe(true);
  });

  it('uses the expanded push layout at 768 px', async () => {
    await createLayout(768);
    const toggle = element<HTMLButtonElement>('.sidebar-toggle');
    const sidebar = element<HTMLElement>('.sidebar');
    const workspace = element<HTMLElement>('.workspace');

    expect(element<HTMLElement>('.shell').classList).not.toContain('mobile');
    expect(sidebar.hasAttribute('role')).toBe(false);
    expect(sidebar.hasAttribute('aria-modal')).toBe(false);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(toggle.getAttribute('aria-label')).toBe('Ocultar menú');
    expect(sidebar.hasAttribute('inert')).toBe(false);
    expect(element<HTMLElement>('.sidebar nav').hasAttribute('inert')).toBe(false);
    expect(workspace.hasAttribute('inert')).toBe(false);
  });

  it('moves focus after the modal bindings have rendered', async () => {
    await createLayout(true);
    const sidebar = element<HTMLElement>('.sidebar');
    const focus = vi.spyOn(sidebar, 'focus');
    vi.useFakeTimers();

    element<HTMLButtonElement>('.menu').click();
    fixture.detectChanges();
    await Promise.resolve();

    expect(focus).not.toHaveBeenCalled();
    await vi.runAllTimersAsync();
    expect(focus).toHaveBeenCalledOnce();
  });

  it('keeps reverse tabbing inside the mobile panel from its initial focus target', async () => {
    await createLayout(true);
    element<HTMLButtonElement>('.menu').click();
    fixture.detectChanges();
    await waitForFocus();
    const sidebar = element<HTMLElement>('.sidebar');
    const lastLink = element<HTMLButtonElement>('.sidebar-logout');

    sidebar.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Tab', shiftKey: true, bubbles: true }),
    );

    expect(document.activeElement).toBe(lastLink);
  });

  it('wraps Tab from the last mobile navigation link to the close button', async () => {
    await createLayout(true);
    element<HTMLButtonElement>('.menu').click();
    fixture.detectChanges();
    await waitForFocus();
    const sidebar = element<HTMLElement>('.sidebar');
    const lastLink = element<HTMLButtonElement>('.sidebar-logout');
    const closeButton = element<HTMLButtonElement>('.sidebar-close');
    lastLink.focus();
    const event = new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true });

    sidebar.dispatchEvent(event);

    expect(event.defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(closeButton);
  });

  it('wraps Shift+Tab from the close button to the last mobile navigation link', async () => {
    await createLayout(true);
    element<HTMLButtonElement>('.menu').click();
    fixture.detectChanges();
    await waitForFocus();
    const sidebar = element<HTMLElement>('.sidebar');
    const closeButton = element<HTMLButtonElement>('.sidebar-close');
    const lastLink = element<HTMLButtonElement>('.sidebar-logout');
    closeButton.focus();
    const event = new KeyboardEvent('keydown', {
      key: 'Tab',
      shiftKey: true,
      bubbles: true,
      cancelable: true,
    });

    sidebar.dispatchEvent(event);

    expect(event.defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(lastLink);
  });

  it('moves focus to the toggle when entering mobile mode hides the focused sidebar', async () => {
    await createLayout();
    element<HTMLAnchorElement>('a[href="/historial"]').focus();

    viewport.setMatches(true);
    fixture.detectChanges();
    await waitForFocus();

    expect(document.activeElement).toBe(element<HTMLButtonElement>('.menu'));
    expect(element<HTMLElement>('.sidebar').hasAttribute('inert')).toBe(true);
  });

  it('preserves the desktop preference across mobile breakpoint changes', async () => {
    localStorage.setItem(SIDEBAR_STORAGE_KEY, 'true');
    await createLayout();
    expect(element<HTMLElement>('.shell').classList).toContain('sidebar-collapsed');

    viewport.setMatches(true);
    fixture.detectChanges();
    expect(element<HTMLElement>('.shell').classList).toContain('mobile');
    expect(element<HTMLButtonElement>('.menu').getAttribute('aria-expanded')).toBe('false');

    element<HTMLButtonElement>('.menu').click();
    fixture.detectChanges();
    expect(element<HTMLButtonElement>('.menu').getAttribute('aria-expanded')).toBe('true');

    viewport.setMatches(false);
    fixture.detectChanges();
    expect(element<HTMLElement>('.shell').classList).not.toContain('mobile');
    expect(element<HTMLElement>('.shell').classList).toContain('sidebar-collapsed');
    expect(localStorage.getItem(SIDEBAR_STORAGE_KEY)).toBe('true');
    expect(element<HTMLElement>('.workspace').hasAttribute('inert')).toBe(false);
  });

  it('closes the mobile panel only after navigation succeeds', async () => {
    await createLayout(true);
    element<HTMLButtonElement>('.menu').click();
    fixture.detectChanges();
    await fixture.whenStable();
    await waitForFocus();
    const historyLink = element<HTMLAnchorElement>('a[href="/historial"]');
    historyLink.focus();

    allowLeave = false;
    historyLink.click();
    await fixture.whenStable();
    fixture.detectChanges();
    await waitForFocus();
    expect(router.url).toBe('/incidencias');
    expect(element<HTMLButtonElement>('.menu').getAttribute('aria-expanded')).toBe('true');
    expect(document.activeElement).toBe(historyLink);

    allowLeave = true;
    historyLink.click();
    await fixture.whenStable();
    fixture.detectChanges();
    await waitForFocus();
    expect(router.url).toBe('/historial');
    expect(element<HTMLButtonElement>('.menu').getAttribute('aria-expanded')).toBe('false');
    expect(document.activeElement).toBe(element<HTMLButtonElement>('.menu'));
  });

  it('closes the mobile panel when the current route is selected again', async () => {
    await createLayout(true);
    element<HTMLButtonElement>('.menu').click();
    fixture.detectChanges();
    await fixture.whenStable();

    element<HTMLAnchorElement>('a[href="/incidencias"]').click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(router.url).toBe('/incidencias');
    expect(element<HTMLButtonElement>('.menu').getAttribute('aria-expanded')).toBe('false');
  });
  it('navigates to the profile using the account control', async () => {
    await createLayout();
    const trigger = element<HTMLButtonElement>('.session-user-button');
    expect(trigger.getAttribute('aria-label')).toBe('Ir al perfil de usuario');
    trigger.click();
    await fixture.whenStable();
    expect(router.url).toBe('/cuenta/perfil');
  });

  it('exposes an accessible logout icon in the collapsed sidebar', async () => {
    await createLayout();
    element<HTMLButtonElement>('.sidebar-toggle').click();
    fixture.detectChanges();
    const button = element<HTMLButtonElement>('.sidebar-logout');
    expect(button.getAttribute('aria-label')).toBe('Cerrar sesión');
    expect(button.querySelector('svg')).toBeTruthy();
  });

  it('keeps the logout error actionable inside the mobile panel', async () => {
    await createLayout(true);
    logout.mockRejectedValueOnce(new Error('network'));
    element<HTMLButtonElement>('.menu').click();
    fixture.detectChanges();
    element<HTMLButtonElement>('.sidebar-logout').click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(element<HTMLElement>('.sidebar .session-error').textContent).toContain('No se pudo confirmar');
    expect(element<HTMLButtonElement>('.sidebar .session-error button').disabled).toBe(false);
  });

  it('keeps the account menu visible at responsive breakpoints', async () => {
    await installLayoutStyles();
    await createLayout(400);
    const hidden = layoutStyleRules().filter(rule => rule.selectorText.includes('.session-user') && rule.style.display === 'none');
    expect(hidden).toHaveLength(0);
  });

  it('shows the users administration link only to authorized accounts', async () => {
    await createLayout();
    expect(fixture.nativeElement.querySelector('.sidebar a[href="/administracion/usuarios"]')).toBeNull();
    fixture.destroy();
    TestBed.resetTestingModule();
    await createLayout(false, IncidentsStub, true);
    expect(fixture.nativeElement.querySelector('.sidebar a[href="/administracion/usuarios"]')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('.sidebar a[href="/administracion/usuarios/nuevo"]')).toBeTruthy();
    fixture.destroy();
    TestBed.resetTestingModule();
    await createLayout(false, IncidentsStub, true, 'operador');
    expect(fixture.nativeElement.querySelector('.sidebar a[href="/administracion/usuarios"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('.sidebar a[href="/administracion/usuarios/nuevo"]')).toBeNull();
  });

  it('labels the administration page correctly', async () => {
    await createLayout();
    await router.navigateByUrl('/administracion/usuarios');
    fixture.detectChanges();
    expect(element<HTMLElement>('header h1').textContent).toContain('Administración de usuarios');
  });

});
