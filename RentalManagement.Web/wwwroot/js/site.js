// =====================================================================================================================
// Module: RentalManagement.Web / wwwroot / js / site.js
// Purpose: The client side of the app's modal contract. Opens Bootstrap modals filled with partial views returned by
//          controller actions, posts modal forms with fetch, re-renders the form in place when the server returns
//          validation errors, and on success closes the modal, refreshes only the affected page regions and shows a
//          confirmation toast. No framework: plain JavaScript plus Bootstrap and jQuery unobtrusive validation.
// =====================================================================================================================
//
// Modal contract (see Infrastructure/AppController.cs):
//
//   <button data-modal-url="/Units/Edit/5">            click → GET the partial view → show it in #app-modal
//   <form data-modal-form data-refresh="#a #b">        submit → POST with fetch (anti-forgery token is in the form)
//       422/409 + HTML → the same partial with validation messages, swapped into the modal in place
//       200 + JSON { success: true, message }  → close the modal, then reload each region named in data-refresh
//   <div id="a" data-refresh-url="/Properties/List">   a region that knows the URL that re-renders it
//   data-refresh="page"                                 reload the whole page instead (status changes)
//   <form data-ajax-form data-refresh="#a">            one-click POST without a dialog (Claim): JSON either way,
//                                                       success or { success: false, message } with 404/409/422
//   document event "app:changed"                        fired after every action; application grids reload their rows
//
// Requests carry X-Requested-With so the server answers 401/403 instead of redirecting to the login page.
(() => {
    'use strict';

    const modalElement = document.getElementById('app-modal');
    if (!modalElement || !window.bootstrap) {
        return;
    }

    const modal = bootstrap.Modal.getOrCreateInstance(modalElement);
    const modalContent = modalElement.querySelector('.modal-content');

    /**
     * Sends a same-origin request marked as AJAX. A 401 (session expired) reloads the page so the server can
     * redirect to the login page, and rejects so the caller stops.
     * @param {string} url The URL to call.
     * @param {RequestInit} [options] fetch options (method, body, extra headers).
     * @returns {Promise<Response>} The response for any status other than 401.
     */
    async function request(url, options = {}) {
        const response = await fetch(url, {
            credentials: 'same-origin',
            ...options,
            headers: { 'X-Requested-With': 'XMLHttpRequest', ...(options.headers || {}) },
        });
        if (response.status === 401) {
            // Signed out (cookie expired): reload so the server redirects to the login page.
            window.location.reload();
            throw new Error('Not signed in.');
        }
        return response;
    }

    /**
     * Whether the response body is HTML (a partial view).
     * @param {Response} response The response to inspect.
     * @returns {boolean} True for text/html.
     */
    function isHtml(response) {
        return (response.headers.get('Content-Type') || '').includes('text/html');
    }

    /**
     * Whether the response body is JSON (the success signal).
     * @param {Response} response The response to inspect.
     * @returns {boolean} True for application/json.
     */
    function isJson(response) {
        return (response.headers.get('Content-Type') || '').includes('application/json');
    }

    /**
     * Puts a partial view into the modal, enables client-side validation on its form and focuses the autofocus field.
     * @param {string} html The partial view's HTML.
     */
    function showInModal(html) {
        modalContent.innerHTML = html;
        const form = modalContent.querySelector('form');
        if (form && window.jQuery && jQuery.validator && jQuery.validator.unobtrusive) {
            // Wire up client-side validation for the newly inserted form.
            jQuery.validator.unobtrusive.parse(form);
        }
        modalContent.querySelector('[autofocus]')?.focus();
    }

    /**
     * Replaces the modal's content with a generic error panel. The message is inserted as text, never as HTML.
     * @param {string} message The message to show.
     */
    function showModalError(message) {
        modalContent.innerHTML = `
            <div class="modal-header">
                <h5 class="modal-title">Something went wrong</h5>
                <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button>
            </div>
            <div class="modal-body"><div class="alert alert-danger mb-0"></div></div>
            <div class="modal-footer">
                <button type="button" class="btn btn-outline-secondary" data-bs-dismiss="modal">Close</button>
            </div>`;
        modalContent.querySelector('.alert').textContent = message;
    }

    /**
     * A user-facing explanation for an HTTP status that came back without a usable partial.
     * @param {number} status The HTTP status code.
     * @returns {string} The message.
     */
    function messageFor(status) {
        switch (status) {
            case 403: return 'You are not allowed to do that.';
            case 404: return 'That item no longer exists. Refresh the page to see the latest data.';
            default: return 'The request could not be completed. Please try again.';
        }
    }

    /**
     * Shows a short-lived toast in #page-alerts (removed from the DOM once hidden): green for a success, red (and
     * longer, announced as an alert) for a refusal.
     * @param {string|null|undefined} message The text to show; nothing happens when empty.
     * @param {'success'|'danger'} [variant] The toast colour.
     */
    function flash(message, variant = 'success') {
        const container = document.getElementById('page-alerts');
        if (!container || !message) {
            return;
        }
        const toast = document.createElement('div');
        toast.className = `toast align-items-center text-bg-${variant} border-0`;
        toast.setAttribute('role', variant === 'danger' ? 'alert' : 'status');
        toast.innerHTML = '<div class="d-flex"><div class="toast-body"></div>' +
            '<button type="button" class="btn-close btn-close-white me-2 m-auto" data-bs-dismiss="toast" aria-label="Close"></button></div>';
        toast.querySelector('.toast-body').textContent = message;
        container.appendChild(toast);
        toast.addEventListener('hidden.bs.toast', () => toast.remove());
        bootstrap.Toast.getOrCreateInstance(toast, { delay: variant === 'danger' ? 8000 : 4000 }).show();
    }

    /**
     * Tells script-driven parts of the page (the application grids) that data changed, so they reload their rows in
     * place. Fired after every modal or fetch-posted action, whether it succeeded or was refused as stale.
     */
    function announceChange() {
        document.dispatchEvent(new CustomEvent('app:changed'));
    }

    /**
     * Re-renders the page regions named in a form's data-refresh attribute, in parallel. Each region is fetched
     * from its own data-refresh-url and its inner HTML replaced; "page" reloads the whole page instead.
     * @param {string|undefined} selectors Space-separated CSS selectors, or "page".
     * @returns {Promise<void>} Resolves when every region has been refreshed.
     */
    async function refreshRegions(selectors) {
        if ((selectors || '').trim() === 'page') {
            // The action changed what the whole page may show (e.g. a withdrawn application is now read-only).
            window.location.reload();
            return;
        }
        const targets = (selectors || '').split(/\s+/).filter(Boolean);
        await Promise.all(targets.map(async selector => {
            const region = document.querySelector(selector);
            const url = region?.dataset.refreshUrl;
            if (!url) {
                return;
            }
            const response = await request(url);
            if (response.ok) {
                region.innerHTML = await response.text();
            }
        }));
    }

    // Open a modal: any element with data-modal-url (delegated, so buttons inside refreshed regions keep working).
    document.addEventListener('click', async event => {
        const trigger = event.target.closest('[data-modal-url]');
        if (!trigger) {
            return;
        }
        event.preventDefault();
        try {
            const response = await request(trigger.dataset.modalUrl);
            // 409/422 with HTML: the server explains why the action isn't available (e.g. no longer editable).
            if (isHtml(response) && (response.ok || response.status === 409 || response.status === 422)) {
                showInModal(await response.text());
                if (response.status === 409 || response.status === 422) {
                    announceChange(); // the row that offered this action was stale
                }
            } else {
                showModalError(messageFor(response.status));
            }
        } catch (error) {
            if (error.message === 'Not signed in.') {
                return;
            }
            showModalError('The server could not be reached.');
        }
        modal.show();
    });

    // Submit a modal form: post it with fetch, then either show the returned partial (errors) or close and refresh.
    modalContent.addEventListener('submit', async event => {
        const form = event.target.closest('form[data-modal-form]');
        if (!form) {
            return;
        }
        event.preventDefault();

        if (window.jQuery && jQuery(form).valid && !jQuery(form).valid()) {
            return; // client-side errors are already shown
        }

        const submitButton = form.querySelector('[type="submit"]');
        submitButton?.setAttribute('disabled', 'disabled');
        try {
            const response = await request(form.action, { method: 'POST', body: new FormData(form) });
            if (response.ok && isJson(response)) {
                const result = await response.json();
                modal.hide();
                await refreshRegions(form.dataset.refresh);
                announceChange();
                flash(result.message);
            } else if (isHtml(response) && (response.status === 422 || response.status === 409 || response.ok)) {
                showInModal(await response.text());
            } else {
                showModalError(messageFor(response.status));
            }
        } catch (error) {
            if (error.message !== 'Not signed in.') {
                showModalError('The server could not be reached.');
            }
        } finally {
            submitButton?.removeAttribute('disabled');
        }
    });

    // A one-click action without a dialog (e.g. Claim): <form data-ajax-form data-refresh="…"> posts with fetch. The
    // server answers { success, message } (JSON, 200) or { success: false, message } with 404/409/422. Either way the
    // message is shown, the named regions refresh and the grids reload, so a stale row is corrected in place.
    // Delegated, so forms the grid script builds for its rows work too.
    document.addEventListener('submit', async event => {
        const form = event.target.closest('form[data-ajax-form]');
        if (!form) {
            return;
        }
        event.preventDefault();

        const submitButton = form.querySelector('[type="submit"]');
        submitButton?.setAttribute('disabled', 'disabled');
        try {
            const response = await request(form.action, { method: 'POST', body: new FormData(form) });
            const result = isJson(response) ? await response.json() : null;
            if (response.ok && result?.success) {
                flash(result.message);
            } else {
                flash(result?.message || messageFor(response.status), 'danger');
            }
            await refreshRegions(form.dataset.refresh);
            announceChange();
        } catch (error) {
            if (error.message !== 'Not signed in.') {
                flash('The server could not be reached. Please try again.', 'danger');
            }
        } finally {
            submitButton?.removeAttribute('disabled');
        }
    });

    // Leave nothing stale behind when the modal closes.
    modalElement.addEventListener('hidden.bs.modal', () => {
        modalContent.innerHTML = '';
    });
})();
