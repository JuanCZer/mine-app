import { Title } from '@angular/platform-browser';
import { I18nService } from '../core/i18n/i18n.service';
import {
  Component,
  computed,
  effect,
  DestroyRef,
  ElementRef,
  HostListener,
  inject,
  signal,
  ViewChild,
} from '@angular/core';
import {
  NavigationEnd,
  NavigationSkipped,
  Router,
  RouterLink,
  RouterLinkActive,
  RouterOutlet,
} from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { AuthService } from '../core/services/auth.service';

const SIDEBAR_STORAGE_KEY = 'mine.ui.sidebarCollapsed';
const MOBILE_SIDEBAR_QUERY = '(max-width: 767px)';

@Component({
  selector: 'app-layout',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './app-layout.html',
  styleUrls: ['./app-layout.less', './app-layout-responsive.less'],
})
export class AppLayout {
  readonly i18n = inject(I18nService);
  get dictionary() { return this.i18n.dictionary(); }
  private readonly title = inject(Title);
  @ViewChild('userMenuRoot') private userMenuRoot?: ElementRef<HTMLElement>;
  @ViewChild('userMenuTrigger') private userMenuTrigger?: ElementRef<HTMLButtonElement>;
  @ViewChild('sidebar') private sidebar?: ElementRef<HTMLElement>;
  @ViewChild('menuButton') private menuButton?: ElementRef<HTMLButtonElement>;
  @ViewChild('sidebarToggle') private sidebarToggle?: ElementRef<HTMLButtonElement>;

  private readonly destroyRef = inject(DestroyRef);
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);
  private readonly mediaQuery =
    typeof matchMedia === 'function' ? matchMedia(MOBILE_SIDEBAR_QUERY) : undefined;
  private focusTimer?: ReturnType<typeof setTimeout>;

  readonly sidebarCollapsed = signal(this.readSidebarPreference());
  readonly mobileMenuOpen = signal(false);
  readonly isMobile = signal(this.mediaQuery?.matches ?? false);
  readonly sidebarExpanded = computed(() =>
    this.isMobile() ? this.mobileMenuOpen() : !this.sidebarCollapsed(),
  );
  private readonly pageTitleKey = signal('account.incidentsTitle');
  readonly pageTitle = computed(() => this.i18n.text(this.pageTitleKey()));
  readonly currentUser = this.auth.user;
  readonly userMenuOpen = signal(false);
  readonly canManageUsers = computed(
    () => this.currentUser()?.role === 'administrador' && (this.currentUser()?.permissions ?? []).includes('users:provision'),
  );
  readonly logoutPending = signal(false);
  readonly logoutError = this.i18n.message();

  constructor() {
    const update = (url: string) => {
      let route = this.router.routerState.snapshot.root;
      while (route.firstChild) route = route.firstChild;
      const titleKey = route.data['titleKey'];
      this.pageTitleKey.set(
        typeof titleKey === 'string' ? titleKey :
        url.startsWith('/mapa/incidencia/')
          ? 'account.incidentDetailsTitle'
          : url.startsWith('/mapa')
          ? 'account.mapTitle'
          : url.startsWith('/historial')
            ? 'account.historyTitle'
            : url.startsWith('/administracion/usuarios/nuevo')
              ? 'account.createUser'
            : url.startsWith('/administracion/usuarios')
              ? 'account.usersTitle'
              : url.startsWith('/cuenta/perfil')
              ? 'account.profileTitle'
              : url.startsWith('/cuenta/contrasena')
                ? 'account.changePassword'
                : 'account.incidentsTitle',
      );
    };
    update(this.router.url);
    effect(() => this.title.setTitle(this.pageTitle()));
    this.router.events.pipe(takeUntilDestroyed()).subscribe((event) => {
      if (event instanceof NavigationEnd) {
        update(event.urlAfterRedirects);
        this.closeMobileMenu();
        this.closeUserMenu();
      } else if (event instanceof NavigationSkipped) {
        this.closeMobileMenu();
        this.closeUserMenu();
      }
    });

    const onBreakpointChange = (event: MediaQueryListEvent) => {
      const wasOpen = this.mobileMenuOpen();
      const hadSidebarFocus =
        !!document.activeElement && this.sidebar?.nativeElement.contains(document.activeElement);
      this.isMobile.set(event.matches);
      this.mobileMenuOpen.set(false);
      const sidebarWillBeHidden = event.matches || this.sidebarCollapsed();
      if ((hadSidebarFocus && sidebarWillBeHidden) || (!event.matches && wasOpen)) {
        event.matches ? this.focusMenuButton() : this.focusSidebarToggle();
      }
    };
    this.mediaQuery?.addEventListener('change', onBreakpointChange);
    this.destroyRef.onDestroy(() => {
      this.mediaQuery?.removeEventListener('change', onBreakpointChange);
      if (this.focusTimer) clearTimeout(this.focusTimer);
    });
  }

  toggleSidebar(): void {
    if (this.isMobile()) {
      this.mobileMenuOpen() ? this.closeMobileMenu() : this.openMobileMenu();
      return;
    }
    const collapsed = !this.sidebarCollapsed();
    this.sidebarCollapsed.set(collapsed);
    this.writeSidebarPreference(collapsed);
  }

  openMobileMenu(): void {
    if (!this.isMobile()) return;
    this.mobileMenuOpen.set(true);
    this.scheduleFocus(
      () => this.sidebar?.nativeElement,
      () => this.isMobile() && this.mobileMenuOpen(),
    );
  }

  closeMobileMenu(restoreFocus = true): void {
    if (!this.mobileMenuOpen()) return;
    this.mobileMenuOpen.set(false);
    if (restoreFocus) this.focusMenuButton();
  }

  trapSidebarFocus(event: KeyboardEvent): void {
    if (!this.isMobile() || !this.mobileMenuOpen() || event.key !== 'Tab') return;
    const sidebar = this.sidebar?.nativeElement;
    const controls = Array.from(
      sidebar?.querySelectorAll<HTMLElement>(
        'button:not([disabled]), a[href], [tabindex]:not([tabindex="-1"])',
      ) ?? [],
    ).filter((element) => !element.hasAttribute('inert'));
    if (!controls.length) return;
    const first = controls[0];
    const last = controls.at(-1)!;
    if (
      event.shiftKey &&
      (document.activeElement === sidebar || document.activeElement === first)
    ) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    this.closeMobileMenu();
    if (this.userMenuOpen()) {
      this.closeUserMenu();
      this.userMenuTrigger?.nativeElement.focus();
    }
  }

  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent): void {
    const target = event.target as Node | null;
    if (!target || !this.userMenuOpen()) return;
    if (!this.userMenuRoot?.nativeElement.contains(target)) this.closeUserMenu();
  }

  toggleUserMenu(): void {
    this.userMenuOpen.update((open) => !open);
  }

  onUserMenuFocusOut(event: FocusEvent): void {
    const next = event.relatedTarget;
    if (!(next instanceof Node) || !this.userMenuRoot?.nativeElement.contains(next)) this.closeUserMenu();
  }

  closeUserMenu(): void {
    this.userMenuOpen.set(false);
  }

  async logout(): Promise<void> {
    if (this.logoutPending()) return;
    this.logoutPending.set(true);
    this.logoutError.set('');
    try {
      await this.auth.logout();
    } catch {
      this.logoutError.set(
        { key: 'account.logoutError' },
      );
    } finally {
      this.logoutPending.set(false);
    }
  }

  private focusMenuButton(): void {
    this.scheduleFocus(
      () => this.menuButton?.nativeElement,
      () => !this.mobileMenuOpen(),
    );
  }

  private focusSidebarToggle(): void {
    this.scheduleFocus(
      () => this.sidebarToggle?.nativeElement,
      () => !this.isMobile(),
    );
  }

  private scheduleFocus(target: () => HTMLElement | undefined, allowed: () => boolean): void {
    if (this.focusTimer) clearTimeout(this.focusTimer);
    this.focusTimer = setTimeout(() => {
      this.focusTimer = undefined;
      if (allowed()) target()?.focus();
    });
  }

  private readSidebarPreference(): boolean {
    try {
      return localStorage.getItem(SIDEBAR_STORAGE_KEY) === 'true';
    } catch {
      return false;
    }
  }

  private writeSidebarPreference(collapsed: boolean): void {
    try {
      localStorage.setItem(SIDEBAR_STORAGE_KEY, String(collapsed));
    } catch {
      // Storage is optional; the in-memory interaction remains available.
    }
  }
}
