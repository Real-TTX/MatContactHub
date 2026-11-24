/* MatContactHub – the pages of the contacts and the address books (module C): small additions to the toolkit, no texts of their own.
   - Tabs of one form (<md-panels>): a field that is wrong when the form is checked opens its tab (the first one only), and the tab that is open is sent with the
     form (a hidden input [data-panel-tab]), so the page comes back on the same tab after an error of the server.
   - The DN of an address book ([data-dn-preview]): "ou=<LDAP name>,<base DN>" while the LDAP name or the name is typed (an empty LDAP name is made from the name,
     like the server does). */
(function () {
  'use strict';

  var opening = false;

  // the first wrong field of a check opens its panel (the toolkit checks every field and focuses the first wrong one, which must be visible for that)
  document.addEventListener('invalid', function (e) {
    var panel = e.target && e.target.closest ? e.target.closest('[role="tabpanel"][data-panel-key]') : null;
    if (!panel || opening) return;
    opening = true;
    setTimeout(function () { opening = false; }, 0);
    if (!panel.hidden) return;
    var tab = document.getElementById(panel.getAttribute('aria-labelledby'));
    if (tab) tab.click();
  }, true);

  document.addEventListener('click', function (e) {
    var tab = e.target && e.target.closest ? e.target.closest('[role="tab"][data-panel-key]') : null;
    if (!tab) return;
    var form = tab.closest('form');
    var field = form ? form.querySelector('[data-panel-tab]') : null;
    if (field) field.value = tab.getAttribute('data-panel-key');
  });

  // ---- the DN of an address book --------------------------------------------------------------------------------------
  function ldapNameOf(name) {
    var text = (name || '').trim().toLowerCase()
      .replace(/ä/g, 'ae').replace(/ö/g, 'oe').replace(/ü/g, 'ue').replace(/ß/g, 'ss')
      .replace(/[^a-z0-9]/g, '-').replace(/-+/g, '-').replace(/^-|-$/g, '');
    if (text.length > 64) text = text.slice(0, 64).replace(/-+$/, '');
    return text || 'contacts';
  }

  document.querySelectorAll('[data-dn-preview]').forEach(function (out) {
    var ldap = document.getElementById(out.getAttribute('data-ldap-field'));
    var name = document.getElementById(out.getAttribute('data-name-field'));
    var base = out.getAttribute('data-base-dn') || '';
    function draw() {
      var value = ldap && ldap.value.trim() ? ldap.value.trim().toLowerCase() : ldapNameOf(name ? name.value : '');
      out.textContent = 'ou=' + value + (base ? ',' + base : '');
    }
    [ldap, name].forEach(function (input) { if (input) input.addEventListener('input', draw); });
    draw();
  });
})();
