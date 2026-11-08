import { observeMapResize } from './map-resize';

describe('observeMapResize', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it('resizes for container changes and disconnects during cleanup', () => {
    let notify: () => void = () => undefined;
    const observe = vi.fn();
    const disconnect = vi.fn();
    class ResizeObserverStub {
      constructor(callback: () => void) {
        notify = callback;
      }
      observe = observe;
      disconnect = disconnect;
    }
    vi.stubGlobal('ResizeObserver', ResizeObserverStub);
    const container = document.createElement('div');
    const resize = vi.fn();

    const cleanup = observeMapResize(container, resize);
    notify();

    expect(observe).toHaveBeenCalledWith(container);
    expect(resize).toHaveBeenCalledOnce();
    cleanup();
    expect(disconnect).toHaveBeenCalledOnce();
  });
});
