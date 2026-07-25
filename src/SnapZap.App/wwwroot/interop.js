// Browser-side support for the photo grid.
//
// Three jobs, all of them genuinely DOM concerns:
//   1. Measure how many columns fit and how tall a row is.
//   2. Report the scroll position, so Blazor can render only the rows in view.
//   3. Move focus between cards on the arrow keys.
// Everything that changes application state stays in Blazor.
//
// Windowing is done here rather than with <Virtualize> because that component derives its
// viewport from an IntersectionObserver on its own spacers, which never settles inside this
// flex/scroll layout — it reports a zero-capacity viewport and renders no rows at all.
// Measuring the container directly is deterministic and needs no guesswork.

window.snapzap = (function () {
  const MIN_COL = 150; // keep in sync with the grid's minimum column width in app.css
  const GAP = 10;

  let gridEl = null;
  let dotnetRef = null;
  let resizeObserver = null;
  let scrollHandler = null;
  let gridKeyHandler = null;
  let globalKeyHandler = null;
  let ticking = false;

  let cols = 0;
  let rowHeight = 0;

  function report() {
    if (!gridEl || !dotnetRef) return;
    const width = gridEl.clientWidth;
    const height = gridEl.clientHeight;
    if (width <= 0 || height <= 0) return;

    const nextCols = Math.max(1, Math.floor((width + GAP) / (MIN_COL + GAP)));
    // Cards are square, so a row is one column width plus the gap beneath it.
    const cardWidth = (width - (nextCols - 1) * GAP) / nextCols;
    const nextRowHeight = Math.max(1, Math.round(cardWidth + GAP));

    cols = nextCols;
    rowHeight = nextRowHeight;
    dotnetRef.invokeMethodAsync('SetViewport', nextCols, nextRowHeight, gridEl.scrollTop, height);
  }

  function onScroll() {
    if (ticking) return;
    ticking = true;
    requestAnimationFrame(function () {
      ticking = false;
      if (gridEl && dotnetRef) {
        dotnetRef.invokeMethodAsync('SetScroll', gridEl.scrollTop, gridEl.clientHeight);
      }
    });
  }

  function cards() {
    return gridEl ? Array.prototype.slice.call(gridEl.querySelectorAll('[data-card-index]')) : [];
  }

  return {
    initGrid: function (el, dotnet) {
      this.disposeGrid();
      if (!el) return;
      gridEl = el;
      dotnetRef = dotnet;

      resizeObserver = new ResizeObserver(report);
      resizeObserver.observe(gridEl);
      report();

      scrollHandler = onScroll;
      gridEl.addEventListener('scroll', scrollHandler, { passive: true });

      gridKeyHandler = function (e) {
        const card = e.target.closest && e.target.closest('[data-card-index]');
        if (!card) return;

        // Space would scroll the grid; the Blazor handler still sees the event.
        if (e.key === ' ') { e.preventDefault(); return; }

        const list = cards();
        const i = list.indexOf(card);
        if (i < 0) return;

        let target = -1;
        switch (e.key) {
          case 'ArrowRight': target = i + 1; break;
          case 'ArrowLeft': target = i - 1; break;
          case 'ArrowDown': target = i + cols; break;
          case 'ArrowUp': target = i - cols; break;
          case 'Home': target = 0; break;
          case 'End': target = list.length - 1; break;
          default: return;
        }
        if (target >= 0 && target < list.length) {
          list[target].focus();
          e.preventDefault();
        }
      };
      gridEl.addEventListener('keydown', gridKeyHandler);

      globalKeyHandler = function (e) {
        const mod = e.metaKey || e.ctrlKey;
        if (!mod) return;
        const t = e.target;
        if (t && /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName)) return;

        if (e.key === 'a' || e.key === 'A') {
          e.preventDefault();
          dotnetRef.invokeMethodAsync('SelectAllVisible');
        } else if (e.key === 'd' || e.key === 'D') {
          e.preventDefault();
          dotnetRef.invokeMethodAsync('ClearSelection');
        }
      };
      document.addEventListener('keydown', globalKeyHandler);
    },

    disposeGrid: function () {
      if (resizeObserver) { resizeObserver.disconnect(); resizeObserver = null; }
      if (gridEl && scrollHandler) gridEl.removeEventListener('scroll', scrollHandler);
      if (gridEl && gridKeyHandler) gridEl.removeEventListener('keydown', gridKeyHandler);
      if (globalKeyHandler) document.removeEventListener('keydown', globalKeyHandler);
      scrollHandler = gridKeyHandler = globalKeyHandler = null;
      gridEl = null;
      dotnetRef = null;
      cols = 0;
      rowHeight = 0;
    },

    scrollToTop: function () {
      if (gridEl) gridEl.scrollTop = 0;
    },

    focusFirstCard: function () {
      const first = cards()[0];
      if (first) first.focus();
    }
  };
})();
