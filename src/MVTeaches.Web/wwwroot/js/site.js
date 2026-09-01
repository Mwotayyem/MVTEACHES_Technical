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

    document.addEventListener("DOMContentLoaded", function () {
        wireTableFilters();
        wireSelectFilters();
        wireSessionNarrowing();
        wireConfirmations();
        focusFirstInvalidField();
    });
})();
