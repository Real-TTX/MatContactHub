/* MatContactHub as an app (PWA): the service worker, the install button, the colour of the status bar and the clean-up when somebody signs out.
   - The service worker (sw.js) keeps the shell of the app and a copy of the contact pages seen last (cache mch-pages) for offline use.
   - "Install the app": the browser offers it by an event; the button (data-pwa-install, in the user menu) is shown only when the browser can do it.
   - Signing out clears what was kept on this device for the person who was signed in: the copies of the contact pages (mch-pages, the name sw.js uses; deleted here
     directly too, as a page that is not controlled by the service worker, after a hard reload, cannot ask it) and the md- settings of localStorage.
   - A session can also end without signing out (it expired, an administrator revoked it, the account was deleted): a page without a session (the sign-in, the
     registration: data-signed-out on <html>) deletes the copies too, and a page of another person (data-user) than the one they were kept for. */
(function () {
  'use strict';

  if ('serviceWorker' in navigator && window.isSecureContext) {
    window.addEventListener('load', function () { navigator.serviceWorker.register('/sw.js').catch(function () { /* the app works without it */ }); });
  }

  var deferred = null;
  window.addEventListener('beforeinstallprompt', function (e) {
    e.preventDefault();
    deferred = e;
    Array.prototype.forEach.call(document.querySelectorAll('[data-pwa-install]'), function (b) { b.hidden = false; });
  });
  window.addEventListener('appinstalled', function () {
    deferred = null;
    Array.prototype.forEach.call(document.querySelectorAll('[data-pwa-install]'), function (b) { b.hidden = true; });
  });
  document.addEventListener('click', function (e) {
    var button = e.target.closest && e.target.closest('[data-pwa-install]');
    if (!button || !deferred) { return; }
    deferred.prompt();
    deferred.userChoice.finally(function () { deferred = null; button.hidden = true; });
  });

  function clearPages() {
    if (window.caches) { caches.delete('mch-pages'); }
    if (navigator.serviceWorker && navigator.serviceWorker.controller) { navigator.serviceWorker.controller.postMessage('md-clear'); }
  }

  var root = document.documentElement;
  try {
    var owner = root.getAttribute('data-user');
    var kept = localStorage.getItem('mch-pages-user');
    if (root.hasAttribute('data-signed-out')) {
      clearPages();
      localStorage.removeItem('mch-pages-user');
    } else if (owner) {
      if (kept && kept !== owner) { clearPages(); }
      localStorage.setItem('mch-pages-user', owner);
    }
  } catch (err) {
    // private mode: no localStorage; a page without a session still clears
    if (root.hasAttribute('data-signed-out')) { clearPages(); }
  }

  // the status bar and the window have the colour of the page (theme-color = --mat-bg): the layout writes the theme and the scheme of the person; this follows
  // a change on the page (the theme switch, data-theme / data-scheme), a theme only the device knows (matui.boot.js from localStorage) and the device itself
  function syncThemeColor() {
    var bg = getComputedStyle(root).getPropertyValue('--mat-bg').trim();
    if (!bg) { return; }
    var metas = document.querySelectorAll('meta[name="theme-color"]');
    for (var i = 1; i < metas.length; i++) { metas[i].parentNode.removeChild(metas[i]); }
    var meta = metas[0];
    if (!meta) { meta = document.createElement('meta'); meta.name = 'theme-color'; document.head.appendChild(meta); }
    meta.removeAttribute('media');
    meta.content = bg;
  }
  syncThemeColor();
  if (window.MutationObserver) { new MutationObserver(syncThemeColor).observe(root, { attributes: true, attributeFilter: ['data-theme', 'data-scheme'] }); }
  if (window.matchMedia) {
    var dark = window.matchMedia('(prefers-color-scheme: dark)');
    if (dark.addEventListener) { dark.addEventListener('change', syncThemeColor); }
  }

  document.addEventListener('submit', function (e) {
    var form = e.target;
    if (!form || !form.matches || !form.matches('[data-logout]')) { return; }
    try {
      Object.keys(localStorage).filter(function (k) { return k.indexOf('md-') === 0; }).forEach(function (k) { localStorage.removeItem(k); });
      localStorage.removeItem('mch-pages-user');
    } catch (err) { /* private mode */ }
    clearPages();
  });
})();
