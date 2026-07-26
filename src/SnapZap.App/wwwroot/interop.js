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
  let focusTrap = null;
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

        // Never re-scope the selection from behind a dialog: reaching for Cmd+A to select a
        // destination path would silently arm the export against the whole library.
        if (document.querySelector('.dlg, .modal, .cmp')) return;

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

    /**
     * Keep Tab inside whichever dialog is open. Blazor renders the overlays as siblings of the
     * app rather than in a top layer, so without this Tab walks straight out of a modal into
     * the toolbar and grid behind it — where the controls are still operable and, in the case
     * of Delete, destructive. Installed once; it reads the DOM per keypress rather than
     * capturing a node, so it stays correct as dialogs open, close and re-render.
     */
    trapFocus: function () {
      if (focusTrap) return;
      const FOCUSABLE = 'a[href], button:not([disabled]), input:not([disabled]), ' +
                        'select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

      focusTrap = function (e) {
        if (e.key !== 'Tab') return;

        // The last one rendered is the one on top — a preview opened from a dialog sits above it.
        const open = document.querySelectorAll('.dlg, .cmp, .modal');
        if (!open.length) return;
        const dialog = open[open.length - 1];

        const items = Array.prototype.filter.call(
          dialog.querySelectorAll(FOCUSABLE),
          function (el) { return el.offsetParent !== null || el === document.activeElement; });
        if (!items.length) { e.preventDefault(); dialog.focus(); return; }

        const first = items[0];
        const last = items[items.length - 1];

        // Focus outside the dialog entirely (it escaped earlier, or the dialog just opened)
        // gets pulled back to the appropriate end rather than left where it was.
        if (!dialog.contains(document.activeElement)) {
          e.preventDefault();
          (e.shiftKey ? last : first).focus();
          return;
        }
        if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
        else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
      };
      document.addEventListener('keydown', focusTrap, true);
    },

    /**
     * Mirror scrolling across the compare panes so the same region of each copy stays under
     * the eye. Judging a near-duplicate means looking at the same detail in both, which is
     * impossible if the panes drift apart.
     */
    syncComparePanes: function (container) {
      if (!container) return;
      const panes = Array.prototype.slice.call(container.querySelectorAll('.cmp-pane'));
      let echo = false;

      panes.forEach(function (pane) {
        pane.addEventListener('scroll', function () {
          if (echo) return;          // we are the ones moving the others
          echo = true;
          panes.forEach(function (other) {
            if (other === pane) return;
            other.scrollTop = pane.scrollTop;
            other.scrollLeft = pane.scrollLeft;
          });
          // Release on the next frame, after the programmatic scrolls have fired.
          requestAnimationFrame(function () { echo = false; });
        }, { passive: true });
      });
    },

    focusFirstCard: function () {
      const first = cards()[0];
      if (first) first.focus();
    },

    /**
     * Wire the reconnection overlay's buttons.
     *
     * These cannot be Blazor event handlers: the circuit is precisely what is missing when the
     * overlay is up, so the only code that can still run is plain browser JS. Called at load
     * rather than from a component, for the same reason.
     */
    initReconnect: function () {
      const modal = document.getElementById('components-reconnect-modal');
      if (!modal) return;

      const retry = modal.querySelector('.reconnect-retry');
      const reload = modal.querySelector('.reconnect-reload');

      if (reload) reload.addEventListener('click', function () { location.reload(); });

      if (retry) retry.addEventListener('click', function () {
        // `failed` and `resume-failed` are recoverable by different calls, and asking for the
        // wrong one throws. Fall back to a reload rather than leaving a dead button.
        try {
          if (modal.classList.contains('components-reconnect-resume-failed') &&
              window.Blazor && Blazor.resumeCircuit) {
            Blazor.resumeCircuit();
          } else if (window.Blazor && Blazor.reconnect) {
            Blazor.reconnect();
          } else {
            location.reload();
          }
        } catch (e) {
          location.reload();
        }
      });

      // Escape can't dismiss this one: there is nothing behind it to go back to while the app
      // is unreachable. Keep focus in it so the buttons are reachable when they do appear.
      modal.addEventListener('components-reconnect-state-changed', function (e) {
        const state = e.detail && e.detail.state;
        if (state === 'failed' || state === 'rejected' || state === 'resume-failed') {
          const target = modal.querySelector('.reconnect-actions button:not([style*="none"])');
          if (target) target.focus();
        }
      });
    }
  };
})();

// The overlay must work without a circuit, so it is wired at load, not by a component.
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', function () { snapzap.initReconnect(); });
} else {
  snapzap.initReconnect();
}
