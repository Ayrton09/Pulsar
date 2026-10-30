// Before the page is drawn: the light theme, when this browser chose it (or chose the system's, and it is light), so the
// page does not flash dark first. The panel's own script (src/app/theme.ts) takes over once it loads.
(function () {
  try {
    var choice = localStorage.getItem('pulsar.theme');
    var light =
      choice === 'light' ||
      (choice === 'system' && window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches);
    if (light) {
      document.documentElement.setAttribute('data-theme', 'light');
    }
  } catch (e) {
    // Storage refused: the default, dark.
  }
})();
