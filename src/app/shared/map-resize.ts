export function observeMapResize(container: HTMLElement, resize: () => void): () => void {
  if (typeof ResizeObserver === 'undefined') return () => undefined;
  const observer = new ResizeObserver(() => resize());
  observer.observe(container);
  return () => observer.disconnect();
}
