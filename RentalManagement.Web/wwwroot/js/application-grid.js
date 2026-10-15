// =====================================================================================================================
// Module: RentalManagement.Web / wwwroot / js / application-grid.js
// Purpose: The client side of the ApplicationGrid view component. Loads each grid's rows from the JSON endpoint
//          (GET /api/applications/grid, or /api/applications/review-queue for a grid with data-queue), and reloads them
//          when a filter, the page size, a sort header or a page button changes, or when the page announces "app:changed"
//          (after a claim, release or review). Renders rows (with Claim / Review / Release for queue rows), summary, pager
//          and the loading / empty / error states. All filtering, sorting and paging happen on the server; this script
//          only sends the parameters and shows the answer. No framework.
// =====================================================================================================================
//
// Grid contract (see Views/Shared/Components/ApplicationGrid/Default.cshtml):
//
//   <section data-app-grid data-api-url data-queue data-page data-page-size data-sort-by data-sort-direction data-status
//            data-property-id data-default-* data-sync-url data-status-css data-empty-text data-empty-filtered-text>
//     [data-grid-filter="status|propertyId"]  [data-grid-page-size]  [data-grid-sort-select]  [data-grid-clear]
//     [data-grid-error] [data-grid-error-text] [data-grid-retry]  [data-grid-summary]
//     <table data-grid-table> <th data-col="key"> <button data-sort="key" data-default-direction> … <tbody data-grid-body>
//     <nav data-grid-pager>
//
// Text from the server is always inserted with textContent, never as HTML.
(() => {
    'use strict';

    /** How many page buttons to show on each side of the current page. */
    const PAGE_WINDOW = 2;

    const dateFormat = new Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
    const dateTimeFormat = new Intl.DateTimeFormat('en-US', {
        month: 'short', day: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit',
    });

    /**
     * Parses a positive integer attribute.
     * @param {string|undefined} value The attribute value.
     * @param {number} fallback Used when the value is missing or not a positive integer.
     * @returns {number} The number.
     */
    function toInt(value, fallback) {
        const number = Number.parseInt(value ?? '', 10);
        return Number.isInteger(number) && number > 0 ? number : fallback;
    }

    /**
     * Creates an element with optional classes and text.
     * @param {string} tag The tag name.
     * @param {string} [className] CSS classes.
     * @param {string} [text] Text content.
     * @returns {HTMLElement} The element.
     */
    function element(tag, className, text) {
        const node = document.createElement(tag);
        if (className) {
            node.className = className;
        }
        if (text !== undefined) {
            node.textContent = text;
        }
        return node;
    }

    /**
     * A <time> element for an ISO date, or an em dash when there is none.
     * @param {string|null|undefined} iso The ISO 8601 timestamp.
     * @param {Intl.DateTimeFormat} [format] Date only by default; date and time for claim timestamps.
     * @returns {Node} The node to insert.
     */
    function dateNode(iso, format = dateFormat) {
        if (!iso) {
            return document.createTextNode('—');
        }
        const time = element('time', '', format.format(new Date(iso)));
        time.dateTime = iso;
        return time;
    }

    /**
     * Wires up one grid and loads its first page.
     * @param {HTMLElement} root The [data-app-grid] element.
     */
    function initGrid(root) {
        const data = root.dataset;
        const defaults = {
            page: 1,
            pageSize: toInt(data.defaultPageSize, 10),
            sortBy: data.defaultSortBy || 'submittedDate',
            sortDirection: data.defaultSortDirection || 'desc',
        };
        const state = {
            page: toInt(data.page, 1),
            pageSize: toInt(data.pageSize, defaults.pageSize),
            sortBy: data.sortBy || defaults.sortBy,
            sortDirection: data.sortDirection || defaults.sortDirection,
            status: data.status || '',
            propertyId: data.propertyId || '',
        };
        let statusCss = {};
        try {
            statusCss = JSON.parse(data.statusCss || '{}');
        } catch {
            statusCss = {};
        }

        const table = root.querySelector('[data-grid-table]');
        const body = root.querySelector('[data-grid-body]');
        const summary = root.querySelector('[data-grid-summary]');
        const pager = root.querySelector('[data-grid-pager]');
        const errorBox = root.querySelector('[data-grid-error]');
        const errorText = root.querySelector('[data-grid-error-text]');
        const columns = [...table.querySelectorAll('thead th[data-col]')].map(th => ({ key: th.dataset.col, th }));

        let pending = null;       // AbortController of the request in flight
        let focusAfterRender = null; // pager key to re-focus after the pager is rebuilt

        const hasFilter = () => state.status !== '' || state.propertyId !== '';

        // ---- Loading ----------------------------------------------------------------------------------------------

        /**
         * Requests the current state's page and renders it. A newer request cancels an older one, so a slow answer
         * can never overwrite a newer one.
         * @returns {Promise<void>} Resolves when rendered (or abandoned).
         */
        async function load() {
            pending?.abort();
            const controller = new AbortController();
            pending = controller;
            setBusy(true);
            hideError();

            const params = new URLSearchParams({
                page: String(state.page),
                pageSize: String(state.pageSize),
                sortBy: state.sortBy,
                sortDirection: state.sortDirection,
            });
            if (state.status) {
                params.set('status', state.status);
            }
            if (state.propertyId) {
                params.set('propertyId', state.propertyId);
            }
            if (data.queue) {
                params.set('queue', data.queue);
            }

            try {
                const response = await fetch(`${data.apiUrl}?${params}`, {
                    credentials: 'same-origin',
                    headers: { Accept: 'application/json', 'X-Requested-With': 'XMLHttpRequest' },
                    signal: controller.signal,
                });
                if (response.status === 401) {
                    // Signed out: reload so the server redirects to the login page.
                    window.location.reload();
                    return;
                }
                if (!response.ok) {
                    showError(await describeError(response));
                    renderMessageRow('The applications could not be loaded.');
                    summary.textContent = '';
                    pager.replaceChildren();
                    return;
                }

                const page = await response.json();
                // A page past the end (e.g. a bookmarked page after rows went away) shows the last page instead.
                if (page.items.length === 0 && page.totalPages > 0 && page.page > page.totalPages) {
                    state.page = page.totalPages;
                    pending = null;
                    await load();
                    return;
                }
                Object.assign(state, {
                    page: page.page,
                    pageSize: page.pageSize,
                    sortBy: page.sortBy,
                    sortDirection: page.sortDirection,
                    status: page.status ?? '',
                    propertyId: page.propertyId == null ? '' : String(page.propertyId),
                });
                render(page);
                syncUrl();
            } catch (error) {
                if (error.name === 'AbortError') {
                    return;
                }
                showError('The applications could not be loaded. Check your connection and try again.');
                renderMessageRow('The applications could not be loaded.');
            } finally {
                if (pending === controller) {
                    pending = null;
                    setBusy(false);
                }
            }
        }

        /**
         * A user-facing message for a failed request; for 400 the server's validation messages.
         * @param {Response} response The failed response.
         * @returns {Promise<string>} The message.
         */
        async function describeError(response) {
            if (response.status === 400) {
                try {
                    const problem = await response.json();
                    const messages = Object.values(problem.errors || {}).flat();
                    if (messages.length > 0) {
                        return messages.join(' ');
                    }
                } catch {
                    // fall through to the generic message
                }
                return 'The list settings are not valid.';
            }
            if (response.status === 403) {
                return 'You are not allowed to view these applications.';
            }
            return 'The applications could not be loaded. Please try again.';
        }

        // ---- Rendering --------------------------------------------------------------------------------------------

        /**
         * Shows a page: rows (or the empty state), summary, sort indicators, pager and toolbar values.
         * @param {object} page The JSON response.
         */
        function render(page) {
            if (page.items.length === 0) {
                renderMessageRow(hasFilter() ? data.emptyFilteredText : data.emptyText);
            } else {
                body.replaceChildren(...page.items.map(renderRow));
            }
            renderSummary(page);
            renderSortState();
            renderPager(page);
            renderToolbar();
        }

        /**
         * One table row; each cell gets its column's classes so responsive hiding matches the header.
         * @param {object} item A row from the JSON response.
         * @returns {HTMLTableRowElement} The row.
         */
        function renderRow(item) {
            const row = element('tr');
            for (const column of columns) {
                const cell = element('td', column.th.className);
                cell.append(...cellContent(column.key, item));
                row.append(cell);
            }
            return row;
        }

        /**
         * The content of one cell.
         * @param {string} key The column key.
         * @param {object} item The row.
         * @returns {Node[]} The nodes to put in the cell.
         */
        function cellContent(key, item) {
            switch (key) {
                case 'id':
                    return [document.createTextNode(`#${item.id}`)];
                case 'propertyName': {
                    // Values of columns hidden on small screens are repeated here, visible only on those screens.
                    const nodes = [document.createTextNode(item.propertyName)];
                    nodes.push(element('div', 'small text-muted d-md-none', `Unit ${item.unitNumber}`));
                    if (item.applicantName) {
                        nodes.push(element('div', 'small text-muted d-sm-none', item.applicantName));
                    }
                    return nodes;
                }
                case 'unitNumber':
                    return [document.createTextNode(item.unitNumber)];
                case 'applicantName':
                    return [document.createTextNode(item.applicantName ?? '')];
                case 'status': {
                    const nodes = [element('span', `badge ${statusCss[item.status] || 'text-bg-light'}`, item.statusLabel)];
                    // Managers see who holds the review claim (the server never sends it to applicants).
                    if (item.claimedByName && !data.queue) {
                        nodes.push(element('div', 'small text-muted', `by ${item.claimedByName}`));
                    }
                    return nodes;
                }
                case 'submittedDate':
                    return [dateNode(item.submittedDate)];
                case 'updatedDate':
                    return [dateNode(item.updatedDate)];
                case 'claimedDate':
                    return [dateNode(item.claimedDate, dateTimeFormat)];
                case 'queueActions':
                    return [queueActions(item)];
                case 'action': {
                    const link = element('a', 'btn btn-sm btn-outline-primary', item.actionLabel);
                    link.href = item.url;
                    link.setAttribute('aria-label',
                        `${item.actionLabel} application #${item.id}, ${item.propertyName} unit ${item.unitNumber}`);
                    return [link];
                }
                default:
                    return [];
            }
        }

        /**
         * The review-queue row actions: Claim (a fetch-posted form handled by site.js, carrying the grid's anti-forgery
         * token), or Review and Release (modals), plus a link to the application. Only the actions the server listed for
         * the row are shown; the server re-checks each one when it is used.
         * @param {object} item The row.
         * @returns {HTMLElement} The button group.
         */
        function queueActions(item) {
            const actions = item.actions || {};
            const label = `application #${item.id}, ${item.propertyName} unit ${item.unitNumber}`;
            const group = element('div', 'd-inline-flex flex-wrap justify-content-end gap-1');
            const modalButton = (text, css, url) => {
                const button = element('button', `btn btn-sm ${css}`, text);
                button.type = 'button';
                button.dataset.modalUrl = url;
                button.setAttribute('aria-label', `${text} ${label}`);
                return button;
            };

            if (actions.claimUrl) {
                const form = element('form', 'd-inline');
                form.method = 'post';
                form.action = actions.claimUrl;
                form.dataset.ajaxForm = '';
                const token = root.querySelector('[data-grid-antiforgery] input[type="hidden"]');
                if (token) {
                    form.append(token.cloneNode());
                }
                const claim = element('button', 'btn btn-sm btn-primary', 'Claim');
                claim.type = 'submit';
                claim.setAttribute('aria-label', `Claim ${label}`);
                form.append(claim);
                group.append(form);
            }
            if (actions.reviewUrl) {
                group.append(modalButton('Review', 'btn-primary', actions.reviewUrl));
            }
            if (actions.releaseUrl) {
                group.append(modalButton('Release', 'btn-outline-secondary', actions.releaseUrl));
            }

            const open = element('a', 'btn btn-sm btn-link', 'Open');
            open.href = item.url;
            open.setAttribute('aria-label', `Open ${label}`);
            group.append(open);
            return group;
        }

        /**
         * Replaces the rows with a single full-width message row (empty state or error).
         * @param {string} message The text.
         */
        function renderMessageRow(message) {
            const cell = element('td', 'text-center text-muted py-5', message);
            cell.colSpan = columns.length;
            const row = element('tr');
            row.append(cell);
            body.replaceChildren(row);
        }

        /**
         * "Showing 11–20 of 42 applications. Page 2 of 5."
         * @param {object} page The JSON response.
         */
        function renderSummary(page) {
            const noun = page.totalCount === 1 ? 'application' : 'applications';
            const filter = hasFilter() ? ' matching the filter' : '';
            if (page.totalCount === 0) {
                summary.textContent = `No ${noun}${filter}.`;
                return;
            }
            const from = (page.page - 1) * page.pageSize + 1;
            const to = from + page.items.length - 1;
            summary.textContent = `Showing ${from}–${to} of ${page.totalCount} ${noun}${filter}. Page ${page.page} of ${page.totalPages}.`;
        }

        /** Marks the sorted column (aria-sort drives the arrow) and describes each sort button for screen readers. */
        function renderSortState() {
            for (const { key, th } of columns) {
                const button = th.querySelector('[data-sort]');
                if (!button) {
                    continue;
                }
                if (key === state.sortBy) {
                    const ascending = state.sortDirection === 'asc';
                    th.setAttribute('aria-sort', ascending ? 'ascending' : 'descending');
                    button.title = `Sorted ${ascending ? 'ascending' : 'descending'}; click to sort ${ascending ? 'descending' : 'ascending'}`;
                } else {
                    th.removeAttribute('aria-sort');
                    button.title = 'Click to sort by this column';
                }
            }
        }

        /**
         * Builds the pager: Previous, first page, a window around the current page, last page, Next.
         * @param {object} page The JSON response.
         */
        function renderPager(page) {
            pager.replaceChildren();
            if (page.totalPages <= 1) {
                return;
            }

            const list = element('ul', 'pagination pagination-sm flex-wrap justify-content-center mb-0');
            const add = (label, target, { key, current = false, disabled = false, ariaLabel } = {}) => {
                const item = element('li', `page-item${current ? ' active' : ''}${disabled ? ' disabled' : ''}`);
                const button = element('button', 'page-link', label);
                button.type = 'button';
                button.dataset.pagerKey = key ?? String(target);
                if (ariaLabel) {
                    button.setAttribute('aria-label', ariaLabel);
                }
                if (current) {
                    button.setAttribute('aria-current', 'page');
                }
                if (disabled || current) {
                    button.disabled = disabled;
                } else {
                    button.dataset.page = String(target);
                }
                item.append(button);
                list.append(item);
            };
            const gap = () => {
                const item = element('li', 'page-item disabled');
                item.append(element('span', 'page-link', '…'));
                item.firstChild.setAttribute('aria-hidden', 'true');
                list.append(item);
            };

            add('Previous', page.page - 1, { key: 'previous', disabled: page.page <= 1, ariaLabel: 'Previous page' });
            const first = Math.max(1, page.page - PAGE_WINDOW);
            const last = Math.min(page.totalPages, page.page + PAGE_WINDOW);
            if (first > 1) {
                add('1', 1, { ariaLabel: 'Page 1' });
                if (first > 2) {
                    gap();
                }
            }
            for (let number = first; number <= last; number++) {
                add(String(number), number, { current: number === page.page, ariaLabel: `Page ${number}` });
            }
            if (last < page.totalPages) {
                if (last < page.totalPages - 1) {
                    gap();
                }
                add(String(page.totalPages), page.totalPages, { ariaLabel: `Page ${page.totalPages}` });
            }
            add('Next', page.page + 1, { key: 'next', disabled: page.page >= page.totalPages, ariaLabel: 'Next page' });
            pager.append(list);

            if (focusAfterRender) {
                const target = pager.querySelector(`[data-pager-key="${focusAfterRender}"]:not(:disabled)`);
                (target || summary).focus();
                focusAfterRender = null;
            }
        }

        /** Puts the current state back into the toolbar controls (the server may have normalised it). */
        function renderToolbar() {
            root.querySelectorAll('[data-grid-filter]').forEach(select => {
                select.value = state[select.dataset.gridFilter] ?? '';
            });
            const pageSize = root.querySelector('[data-grid-page-size]');
            if (pageSize) {
                pageSize.value = String(state.pageSize);
            }
            const sortSelect = root.querySelector('[data-grid-sort-select]');
            if (sortSelect) {
                sortSelect.value = `${state.sortBy}:${state.sortDirection}`;
            }
            const clear = root.querySelector('[data-grid-clear]');
            if (clear) {
                clear.hidden = !hasFilter();
            }
        }

        /**
         * Dims the table while a request is in flight and tells assistive technology it is updating.
         * @param {boolean} busy Whether a request is in flight.
         */
        function setBusy(busy) {
            table.setAttribute('aria-busy', busy ? 'true' : 'false');
        }

        /**
         * Shows the error box.
         * @param {string} message The text.
         */
        function showError(message) {
            errorText.textContent = message;
            errorBox.hidden = false;
        }

        /** Hides the error box. */
        function hideError() {
            errorBox.hidden = true;
        }

        /** Writes the non-default state into the address bar (only for the page's main grid), so it can be bookmarked. */
        function syncUrl() {
            if (data.syncUrl !== 'true') {
                return;
            }
            const params = new URLSearchParams();
            if (state.status) {
                params.set('status', state.status);
            }
            if (state.propertyId) {
                params.set('propertyId', state.propertyId);
            }
            if (state.sortBy !== defaults.sortBy || state.sortDirection !== defaults.sortDirection) {
                params.set('sortBy', state.sortBy);
                params.set('sortDirection', state.sortDirection);
            }
            if (state.pageSize !== defaults.pageSize) {
                params.set('pageSize', String(state.pageSize));
            }
            if (state.page !== 1) {
                params.set('page', String(state.page));
            }
            const query = params.toString();
            history.replaceState(history.state, '', `${window.location.pathname}${query ? `?${query}` : ''}`);
        }

        // ---- Events -----------------------------------------------------------------------------------------------

        table.querySelector('thead').addEventListener('click', event => {
            const button = event.target.closest('[data-sort]');
            if (!button) {
                return;
            }
            const key = button.dataset.sort;
            // The same column toggles direction; a new column starts in its natural direction.
            state.sortDirection = key === state.sortBy
                ? (state.sortDirection === 'asc' ? 'desc' : 'asc')
                : (button.dataset.defaultDirection || 'asc');
            state.sortBy = key;
            state.page = 1;
            load();
        });

        pager.addEventListener('click', event => {
            const button = event.target.closest('button[data-page]');
            if (!button) {
                return;
            }
            state.page = toInt(button.dataset.page, 1);
            focusAfterRender = button.dataset.pagerKey;
            load();
        });

        root.addEventListener('change', event => {
            const target = event.target;
            if (target.matches('[data-grid-filter]')) {
                state[target.dataset.gridFilter] = target.value;
            } else if (target.matches('[data-grid-page-size]')) {
                state.pageSize = toInt(target.value, defaults.pageSize);
            } else if (target.matches('[data-grid-sort-select]')) {
                const [sortBy, sortDirection] = target.value.split(':');
                Object.assign(state, { sortBy, sortDirection });
            } else {
                return;
            }
            state.page = 1;
            load();
        });

        root.querySelector('[data-grid-clear]')?.addEventListener('click', () => {
            Object.assign(state, { status: '', propertyId: '', page: 1 });
            load();
        });

        root.querySelector('[data-grid-retry]')?.addEventListener('click', () => load());

        // Another part of the page changed data (a claim, release or review): reload this page of rows in place.
        document.addEventListener('app:changed', () => load());

        // The toolbar is a real GET form for the no-script case; with the script, changes apply immediately.
        root.querySelector('[data-grid-toolbar]')?.addEventListener('submit', event => {
            event.preventDefault();
            load();
        });

        load();
    }

    document.querySelectorAll('[data-app-grid]').forEach(initGrid);
})();
