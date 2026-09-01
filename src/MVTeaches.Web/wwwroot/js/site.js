// MVTEACHES — progressive enhancement only. No framework, no new dependency:
// every screen still works with JavaScript disabled, this file only makes long
// lists findable and validation errors easier to land on.

(function () {
    "use strict";

    // --- Filter a long table -------------------------------------------------
    // <input data-mv-filter="#studentsTable"> hides rows that don't contain the
    // typed text. An explicit "no match" row keeps the result legible instead
    // of leaving a silently empty table.
    function wireTableFilters() {
        document.querySelectorAll("[data-mv-filter]").forEach(function (input) {
            var table = document.querySelector(input.getAttribute("data-mv-filter"));
            if (!table) { return; }

            var body = table.tBodies[0];
            if (!body) { return; }

            var counter = input.getAttribute("data-mv-filter-count")
                ? document.querySelector(input.getAttribute("data-mv-filter-count"))
                : null;

            function apply() {
                var term = input.value.trim().toLowerCase();
                var shown = 0;

                Array.prototype.forEach.call(body.rows, function (row) {
                    if (row.dataset.mvFilterEmpty === "true") { return; }
                    var match = term === "" || row.textContent.toLowerCase().indexOf(term) !== -1;
                    row.hidden = !match;
                    if (match) { shown++; }
                });

                var emptyRow = body.querySelector('[data-mv-filter-empty="true"]');
                if (emptyRow) {
                    emptyRow.hidden = !(shown === 0 && term !== "");
                }
                if (counter) {
                    counter.textContent = shown;
                }
            }

            input.addEventListener("input", apply);
            apply();
        });
    }

    // --- Turn a long <select> into a searchable combobox --------------------
    // <input data-mv-select-filter="#studentSelect"> used to sit ABOVE the real
    // dropdown and filter its options from the outside — so opening the native
    // list, then moving the cursor into the search box, closed the list (focus
    // left it) before the typed letters ever narrowed anything. This rebuilds
    // the same markup into one real combobox: typing opens a panel of matches
    // that stays open while you type, and picking one sets the real <select>
    // (still the only source of truth — every asp-for/data-mv-options-of/
    // data-mv-sessions-of/data-mv-when-source hookup elsewhere keeps reading
    // and writing that same element exactly as before) and fires a real
    // "change" event on it. No new library — the same progressive-enhancement
    // contract as everything else here: with JS disabled, the native <select>
    // below the (now plain) search box still works.
    function wireSelectFilters() {
        document.querySelectorAll("[data-mv-select-filter]").forEach(function (input) {
            var select = document.querySelector(input.getAttribute("data-mv-select-filter"));
            if (!select || select.dataset.mvComboReady === "true") { return; }
            select.dataset.mvComboReady = "true";

            var wrap = document.createElement("div");
            wrap.className = "app-combo";
            input.parentNode.insertBefore(wrap, input);
            wrap.appendChild(input);

            var panel = document.createElement("div");
            panel.className = "app-combo-panel";
            panel.hidden = true;
            wrap.appendChild(panel);

            select.classList.add("app-combo-native");
            input.classList.add("app-combo-input");
            input.setAttribute("role", "combobox");
            input.setAttribute("autocomplete", "off");
            input.setAttribute("aria-expanded", "false");
            input.setAttribute("aria-haspopup", "listbox");

            // The <label for="..."> already on the page still names the
            // hidden select — copy that name onto the input (which is what a
            // screen reader, and now a mouse, actually lands on) instead of
            // leaving both the label and the id-based lookups to sort it out.
            var boundLabel = select.id ? document.querySelector('label[for="' + select.id + '"]') : null;
            if (boundLabel) {
                input.setAttribute("aria-label", boundLabel.textContent.trim());
                boundLabel.addEventListener("click", function (e) {
                    e.preventDefault();
                    input.focus();
                });
            }

            var visible = [];
            var activeIndex = -1;

            function liveOptions() {
                // Read fresh every time rather than a snapshot taken once at
                // wire-up — data-mv-options-of can rebuild this very select's
                // <option> list at runtime (e.g. plans narrowed to a chosen
                // student's level), and a cached list would go stale.
                return Array.prototype.map.call(select.options, function (option) {
                    return { value: option.value, text: option.text };
                }).filter(function (item) { return item.value !== ""; });
            }

            function currentLabel() {
                var option = select.options[select.selectedIndex];
                return option && option.value !== "" ? option.text : "";
            }

            function syncInputToSelection() {
                input.value = currentLabel();
            }

            function renderPanel(term) {
                panel.innerHTML = "";
                visible = liveOptions().filter(function (item) {
                    return term === "" || item.text.toLowerCase().indexOf(term) !== -1;
                });
                activeIndex = -1;

                if (visible.length === 0) {
                    var empty = document.createElement("div");
                    empty.className = "app-combo-empty";
                    empty.textContent = input.getAttribute("data-mv-select-empty") || T_noMatch();
                    panel.appendChild(empty);
                    return;
                }

                visible.forEach(function (item) {
                    var row = document.createElement("button");
                    row.type = "button";
                    row.className = "app-combo-option";
                    row.textContent = item.text;
                    // mousedown (not click) commits the pick BEFORE the input's
                    // own blur handler fires and closes the panel out from under it.
                    row.addEventListener("mousedown", function (e) {
                        e.preventDefault();
                        pick(item);
                    });
                    panel.appendChild(row);
                });
            }

            function T_noMatch() {
                return (document.documentElement.getAttribute("lang") || "").toLowerCase().indexOf("ar") === 0
                    ? "لا نتائج مطابقة"
                    : "No matches";
            }

            function pick(item) {
                select.value = item.value;
                input.value = item.text;
                select.dispatchEvent(new Event("change", { bubbles: true }));
                closePanel();
            }

            function openPanel() {
                panel.hidden = false;
                input.setAttribute("aria-expanded", "true");
                renderPanel(input.value.trim().toLowerCase());
            }

            function closePanel() {
                panel.hidden = true;
                input.setAttribute("aria-expanded", "false");
                syncInputToSelection();
            }

            function highlight(delta) {
                if (visible.length === 0) { return; }
                activeIndex = (activeIndex + delta + visible.length) % visible.length;
                Array.prototype.forEach.call(panel.children, function (el, i) {
                    el.classList.toggle("is-active", i === activeIndex);
                });
                var el = panel.children[activeIndex];
                if (el && el.scrollIntoView) { el.scrollIntoView({ block: "nearest" }); }
            }

            input.addEventListener("focus", openPanel);
            input.addEventListener("click", openPanel);
            input.addEventListener("input", function () { openPanel(); });
            input.addEventListener("blur", function () {
                // Delayed so a mousedown pick on a panel option still lands first.
                window.setTimeout(closePanel, 150);
            });
            input.addEventListener("keydown", function (e) {
                if (e.key === "ArrowDown") {
                    e.preventDefault();
                    if (panel.hidden) { openPanel(); } else { highlight(1); }
                } else if (e.key === "ArrowUp") {
                    e.preventDefault();
                    highlight(-1);
                } else if (e.key === "Enter") {
                    if (!panel.hidden) {
                        e.preventDefault();
                        var chosen = activeIndex >= 0 ? visible[activeIndex] : (visible.length === 1 ? visible[0] : null);
                        if (chosen) { pick(chosen); }
                    }
                } else if (e.key === "Escape") {
                    if (!panel.hidden) {
                        e.preventDefault();
                        closePanel();
                    }
                }
            });

            // The select can change from OUTSIDE this widget too (data-mv-
            // options-of resetting it when its owner changes) — keep the
            // input's text following it either way.
            select.addEventListener("change", function () {
                if (document.activeElement !== input) { syncInputToSelection(); }
            });

            syncInputToSelection();
        });
    }

    // --- Narrow a session list to one student -------------------------------
    // <select data-mv-sessions-of="#studentSelect"> whose options carry
    // data-students="3,7,11": picking a student hides the sessions they are not
    // enrolled in. Purely a display filter — the server still decides what is
    // allowed, and clearing the student shows every session again.
    function wireSessionNarrowing() {
        document.querySelectorAll("[data-mv-sessions-of]").forEach(function (select) {
            var studentSelect = document.querySelector(select.getAttribute("data-mv-sessions-of"));
            if (!studentSelect) { return; }

            var original = Array.prototype.map.call(select.options, function (option) {
                return {
                    value: option.value,
                    text: option.text,
                    students: (option.getAttribute("data-students") || "").split(",").filter(Boolean)
                };
            });

            function apply() {
                var studentId = studentSelect.value;
                var previous = select.value;
                select.innerHTML = "";

                original.forEach(function (item) {
                    var keep = item.value === "" || studentId === "" || item.students.indexOf(studentId) !== -1;
                    if (!keep) { return; }
                    var option = document.createElement("option");
                    option.value = item.value;
                    option.text = item.text;
                    option.setAttribute("data-students", item.students.join(","));
                    select.add(option);
                });

                select.value = previous;
                if (select.selectedIndex === -1) { select.selectedIndex = 0; }
            }

            studentSelect.addEventListener("change", apply);
        });
    }

    // --- Land the user on the field that is actually wrong -------------------
    // After a failed post the server re-renders with .input-validation-error on
    // the offending inputs; focus the first one so the admin doesn't have to
    // hunt for it down a four-form page.
    function focusFirstInvalidField() {
        var invalid = document.querySelector(".input-validation-error, [aria-invalid='true']");
        if (!invalid) { return; }

        if (typeof invalid.focus === "function") {
            invalid.focus();
        }
        if (typeof invalid.scrollIntoView === "function") {
            invalid.scrollIntoView({ block: "center", behavior: "auto" });
        }
    }

    // --- Confirm the irreversible -------------------------------------------
    // <button data-mv-confirm="...">: one plain question before an action that
    // cannot be taken back (approve, reject, mark paid, close a period).
    function wireConfirmations() {
        document.querySelectorAll("[data-mv-confirm]").forEach(function (element) {
            element.addEventListener("click", function (event) {
                if (!window.confirm(element.getAttribute("data-mv-confirm"))) {
                    event.preventDefault();
                }
            });
        });
    }


    // --- Show only the fields that belong to the chosen kind ----------------
    // A container carries data-mv-when-source="#typeSelect"; each field group
    // inside carries data-mv-when="CliQ BankTransfer" (a space-separated list
    // of the values it belongs to). Picking Cash hides the CliQ/IBAN fields
    // instead of leaving the admin to guess which ones matter. Progressive
    // enhancement only: with JavaScript off every field is simply visible, and
    // the server still decides what a method needs.
    function wireDependentFields() {
        document.querySelectorAll("[data-mv-when-source]").forEach(function (scope) {
            var source = document.querySelector(scope.getAttribute("data-mv-when-source"));
            if (!source) { return; }

            var groups = scope.querySelectorAll("[data-mv-when]");

            function apply() {
                var value = source.value;
                groups.forEach(function (group) {
                    var allowed = group.getAttribute("data-mv-when").split(/\s+/).filter(Boolean);
                    group.hidden = allowed.indexOf(value) === -1;
                });
            }

            source.addEventListener("change", apply);
            apply();
        });
    }

    // --- Keep a dependent list to the chosen owner --------------------------
    // <select data-mv-options-of="#studentSelect"> whose options carry
    // data-owner="7": picking a student leaves only that student's own rows.
    // An element with data-mv-options-empty explains an empty result rather
    // than presenting a list containing nothing but the placeholder.
    function wireOwnedOptions() {
        document.querySelectorAll("[data-mv-options-of]").forEach(function (select) {
            var owner = document.querySelector(select.getAttribute("data-mv-options-of"));
            if (!owner) { return; }

            var emptyNote = select.getAttribute("data-mv-options-empty")
                ? document.querySelector(select.getAttribute("data-mv-options-empty"))
                : null;

            var original = Array.prototype.map.call(select.options, function (option) {
                return {
                    value: option.value,
                    text: option.text,
                    owner: option.getAttribute("data-owner") || ""
                };
            });

            function ownerKey() {
                // The source select may identify its owner by something other
                // than its own value — a student option can carry
                // data-owner-key="3" (their level) so a plan list filters by
                // level rather than by student id.
                var option = owner.options ? owner.options[owner.selectedIndex] : null;
                if (option && option.hasAttribute("data-owner-key")) {
                    return owner.value === "" ? "" : option.getAttribute("data-owner-key");
                }
                return owner.value;
            }

            function apply() {
                var ownerId = ownerKey();
                var previous = select.value;
                var kept = 0;
                select.innerHTML = "";

                original.forEach(function (item) {
                    var isPlaceholder = item.value === "";
                    var keep = isPlaceholder || ownerId === "" || item.owner === ownerId;
                    if (!keep) { return; }
                    var option = document.createElement("option");
                    option.value = item.value;
                    option.text = item.text;
                    if (item.owner) { option.setAttribute("data-owner", item.owner); }
                    select.add(option);
                    if (!isPlaceholder) { kept++; }
                });

                select.value = previous;
                if (select.selectedIndex === -1) { select.selectedIndex = 0; }

                if (emptyNote) {
                    emptyNote.hidden = !(ownerId !== "" && kept === 0);
                }
            }

            owner.addEventListener("change", apply);
            apply();
        });
    }

    // --- Echo a chosen option back to the reader ----------------------------
    // <span data-mv-echo="#studentSelect" data-mv-echo-empty="—"> prints the
    // selected option's own label, so a review step can restate the choices in
    // words rather than making the admin scroll back up to re-read them.
    function wireEchoes() {
        document.querySelectorAll("[data-mv-echo]").forEach(function (target) {
            var source = document.querySelector(target.getAttribute("data-mv-echo"));
            if (!source) { return; }

            var placeholder = target.getAttribute("data-mv-echo-empty") || "";

            function apply() {
                var option = source.options ? source.options[source.selectedIndex] : null;
                target.textContent = (source.value && option) ? option.text : placeholder;
            }

            source.addEventListener("change", apply);
            apply();
        });
    }

    // --- Mark a step as done once its own answer is given -------------------
    // <select data-mv-step="1"> inside an .app-step-panel: the panel turns
    // green and the matching entry in the .app-steps rail follows it, so the
    // sequence shows how far along it is. Display only.
    function wireStepProgress() {
        var rail = document.querySelector("[data-mv-step-rail]");
        var panels = document.querySelectorAll(".app-step-panel[data-mv-step-panel]");
        if (panels.length === 0) { return; }

        function apply() {
            panels.forEach(function (panel) {
                var fields = panel.querySelectorAll("[data-mv-step-field]");
                var answered = fields.length > 0;
                fields.forEach(function (field) {
                    if (!field.value) { answered = false; }
                });
                panel.classList.toggle("is-done", answered);

                if (!rail) { return; }
                var index = panel.getAttribute("data-mv-step-panel");
                var entry = rail.querySelector('[data-mv-step-entry="' + index + '"]');
                if (entry) {
                    entry.classList.toggle("is-done", answered);
                    entry.classList.toggle("is-current", !answered);
                }
            });
        }

        panels.forEach(function (panel) {
            panel.querySelectorAll("[data-mv-step-field]").forEach(function (field) {
                field.addEventListener("change", apply);
                field.addEventListener("input", apply);
            });
        });
        apply();
    }

    // --- Open an authenticated file in a modal instead of a new tab --------
    // <button data-mv-file-modal="#receiptModal" data-mv-file-url="/Files/...">
    // sets the modal's <iframe data-mv-file-frame> and its "open in a new
    // tab" link to that url, then lets Bootstrap's own data-bs-toggle handle
    // showing it. The iframe is cleared on close so a second receipt never
    // shows through a moment of the first one's cached frame, and nothing is
    // fetched at all until a viewer actually opens one.
    function wireFileModals() {
        document.querySelectorAll("[data-mv-file-modal]").forEach(function (trigger) {
            var modal = document.querySelector(trigger.getAttribute("data-mv-file-modal"));
            if (!modal) { return; }

            trigger.addEventListener("click", function () {
                var url = trigger.getAttribute("data-mv-file-url");
                var frame = modal.querySelector("[data-mv-file-frame]");
                var openNew = modal.querySelector("[data-mv-file-open-new]");
                if (frame) { frame.src = url; }
                if (openNew) { openNew.href = url; }

                if (window.bootstrap && window.bootstrap.Modal) {
                    window.bootstrap.Modal.getOrCreateInstance(modal).show();
                }
            });

            modal.addEventListener("hidden.bs.modal", function () {
                var frame = modal.querySelector("[data-mv-file-frame]");
                if (frame) { frame.src = "about:blank"; }
            });
        });
    }

    document.addEventListener("DOMContentLoaded", function () {
        wireTableFilters();
        wireSelectFilters();
        wireSessionNarrowing();
        wireDependentFields();
        wireOwnedOptions();
        wireEchoes();
        wireStepProgress();
        wireConfirmations();
        wireFileModals();
        focusFirstInvalidField();
    });
})();
