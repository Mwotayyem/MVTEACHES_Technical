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

    // --- Filter a long <select> ---------------------------------------------
    // <input data-mv-select-filter="#studentSelect"> narrows the options as the
    // admin types, so a 200-name dropdown stays usable without a picker library.
    function wireSelectFilters() {
        document.querySelectorAll("[data-mv-select-filter]").forEach(function (input) {
            var select = document.querySelector(input.getAttribute("data-mv-select-filter"));
            if (!select) { return; }

            var original = Array.prototype.map.call(select.options, function (option) {
                return { value: option.value, text: option.text };
            });

            input.addEventListener("input", function () {
                var term = input.value.trim().toLowerCase();
                var previous = select.value;
                select.innerHTML = "";

                original.forEach(function (item) {
                    if (item.value === "" || term === "" || item.text.toLowerCase().indexOf(term) !== -1) {
                        var option = document.createElement("option");
                        option.value = item.value;
                        option.text = item.text;
                        select.add(option);
                    }
                });

                select.value = previous;
                if (select.selectedIndex === -1) { select.selectedIndex = 0; }
            });
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

    document.addEventListener("DOMContentLoaded", function () {
        wireTableFilters();
        wireSelectFilters();
        wireSessionNarrowing();
        wireDependentFields();
        wireOwnedOptions();
        wireEchoes();
        wireStepProgress();
        wireConfirmations();
        focusFirstInvalidField();
    });
})();
