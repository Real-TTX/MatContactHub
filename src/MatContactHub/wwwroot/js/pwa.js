/* MatContactHub as an app (PWA): the service worker, the install button and the clean-up when somebody signs out.
   - The service worker (sw.js) keeps the shell of the app and a copy of the contact pages seen last (cache mch-pages) for offline use.
   - "Install the app": the browser offers it by an event; the button (data-pwa-install, in the user menu) is shown only when the browser can do it.
   - Signing out clears what was kept on this device for the person who was signed in: the copies of the contact pages (mch-pages, the name sw.js uses; deleted here
     directly too, as a page that is not controlled by the service worker, after a hard reload, cannot ask it) and the md- settings of localStorage. */
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

  document.addEventListener('submit', function (e) {
    var form = e.target;
    if (!form || !form.matches || !form.matches('[data-logout]')) { return; }
    try {
      Object.keys(localStorage).filter(function (k) { return k.indexOf('md-') === 0; }).forEach(function (k) { localStorage.removeItem(k); });
    } catch (err) { /* private mode */ }
    if (window.caches) { caches.delete('mch-pages'); }
    if (navigator.serviceWorker && navigator.serviceWorker.controller) { navigator.serviceWorker.controller.postMessage('md-clear'); }
  });
})();
