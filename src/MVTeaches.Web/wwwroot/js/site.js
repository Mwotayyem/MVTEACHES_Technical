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
            var target = document.querySelector(input.getAttribute("data-mv-filter"));
            if (!target) { return; }

            // The same search box now also serves lists that are NOT tables -
            // the student register is a grid of cards, and re-implementing this
            // for it would have meant two search behaviours to keep in step. A
            // table filters its first tbody's rows; anything else filters the
            // elements inside it marked data-mv-filter-item.
            var container = target.tBodies && target.tBodies[0] ? target.tBodies[0] : target;
            function items() {
                return container.rows || container.querySelectorAll("[data-mv-filter-item]");
            }

            var counter = input.getAttribute("data-mv-filter-count")
                ? document.querySelector(input.getAttribute("data-mv-filter-count"))
                : null;

            function apply() {
                var term = input.value.trim().toLowerCase();
                var shown = 0;

                Array.prototype.forEach.call(items(), function (row) {
                    if (row.dataset.mvFilterEmpty === "true") { return; }
                    var match = term === "" || row.textContent.toLowerCase().indexOf(term) !== -1;
                    row.hidden = !match;
                    if (match) { shown++; }
                });

                var emptyRow = container.querySelector('[data-mv-filter-empty="true"]');
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

            // Owner report 2026-09-05: re-opening a combo that already had a
            // name in it listed only that one name, because the box's text -
            // the full label of the current pick - was being used as the search
            // term. Changing your mind therefore meant clearing the field with
            // the little x first, which is not something a picker should ask
            // for. Re-opening on the current selection now shows the WHOLE list
            // and selects the text, so typing replaces it and the existing pick
            // is still visible to keep or change.
            function openPanel() {
                panel.hidden = false;
                input.setAttribute("aria-expanded", "true");

                var term = input.value.trim();
                var showAll = term === "" || term === currentLabel();
                renderPanel(showAll ? "" : term.toLowerCase());

                if (showAll && term !== "" && document.activeElement === input) {
                    input.select();
                }
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
            input.addEventListener("input", function () {
                // Typing always filters by exactly what is in the box - only
                // FOCUS/CLICK re-opening treats the current label as "show all".
                panel.hidden = false;
                input.setAttribute("aria-expanded", "true");
                renderPanel(input.value.trim().toLowerCase());
            });
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

    // Rebuild an <option> from a snapshot WITHOUT losing any of its data-*
    // attributes. Callers below re-create options in order to filter a list;
    // anything chained off those options (a level match, a message draft) reads
    // them by attribute, so dropping one silently changes what the page says.
    // Copying a hand-picked list of attributes across meant each new attribute
    // had to remember to be added here - data-level-name was lost that way once,
    // and data-when a second time, which put a teacher's name and a session
    // status into a message meant for a parent. Snapshot them all instead.
    function snapshotOption(option) {
        var data = {};
        Array.prototype.forEach.call(option.attributes, function (attribute) {
            if (attribute.name.indexOf("data-") === 0) { data[attribute.name] = attribute.value; }
        });
        return { value: option.value, text: option.text, data: data };
    }

    function restoreOption(item) {
        var option = document.createElement("option");
        option.value = item.value;
        option.text = item.text;
        Object.keys(item.data).forEach(function (name) {
            option.setAttribute(name, item.data[name]);
        });
        return option;
    }

    // An optional data-* selector: absent, empty, or simply not on the page
    // must all mean "no such note", never a thrown selector error.
    function optionalTarget(selector) {
        if (!selector) { return null; }
        try { return document.querySelector(selector); } catch (e) { return null; }
    }

    // --- Narrow a session list to one student -------------------------------
    // <select data-mv-sessions-of="#studentSelect"> whose options carry
    // data-students="3,7,11": picking a student leaves only the sessions they
    // were actually enrolled in.
    //
    // Until a student IS picked the list is emptied and disabled on purpose.
    // Showing every past session of every student, at every level and with
    // every teacher, was the single worst thing on this screen: the admin
    // could read a lesson belonging to someone else as if it were an option,
    // and only discover the mistake after the server refused it. A list that
    // cannot yet be meaningful is better shown as not-yet-available than as a
    // wrong list.
    //
    // Optional notes, each a selector for an element toggled with [hidden]:
    //   data-mv-note-no-source — shown while no student is chosen.
    //   data-mv-note-empty     — shown when the chosen student has none.
    // Purely a display filter: the server still decides what is allowed.
    function wireSessionNarrowing() {
        document.querySelectorAll("[data-mv-sessions-of]").forEach(function (select) {
            var studentSelect = document.querySelector(select.getAttribute("data-mv-sessions-of"));
            if (!studentSelect) { return; }

            var noteNoSource = optionalTarget(select.getAttribute("data-mv-note-no-source"));
            var noteEmpty = optionalTarget(select.getAttribute("data-mv-note-empty"));

            var original = Array.prototype.map.call(select.options, function (option) {
                var item = snapshotOption(option);
                item.students = (option.getAttribute("data-students") || "").split(",").filter(Boolean);
                return item;
            });

            function apply() {
                var studentId = studentSelect.value;
                var previous = select.value;
                var kept = 0;
                select.innerHTML = "";

                original.forEach(function (item) {
                    var isPlaceholder = item.value === "";
                    // No student yet => placeholder only. Never another
                    // student's lesson.
                    var keep = isPlaceholder || (studentId !== "" && item.students.indexOf(studentId) !== -1);
                    if (!keep) { return; }
                    if (!isPlaceholder) { kept++; }
                    select.add(restoreOption(item));
                });

                select.value = previous;
                if (select.selectedIndex === -1) { select.selectedIndex = 0; }
                select.disabled = studentId === "";

                if (noteNoSource) { noteNoSource.hidden = studentId !== ""; }
                if (noteEmpty) { noteEmpty.hidden = !(studentId !== "" && kept === 0); }

                // Anything chained off this list (the level match below) has to
                // recompute against what is actually left in it.
                select.dispatchEvent(new Event("change", { bubbles: true }));
            }

            studentSelect.addEventListener("change", apply);
            apply();
        });
    }

    // --- Keep a replacement list at the same level as the original ----------
    // <select data-mv-level-of="#originalSelect"> whose options carry
    // data-level="4", against a source whose options carry the same attribute.
    // The server already refuses a make-up lesson at a different level
    // (ApproveReplacementOutcome.ReplacementSessionLevelMismatch); offering one
    // anyway only lets the admin pick something that is certain to be rejected.
    // data-mv-level-note is a selector for an element whose text ends with the
    // level being matched, so the admin can see WHY the list got shorter.
    function wireLevelNarrowing() {
        document.querySelectorAll("[data-mv-level-of]").forEach(function (select) {
            var source = document.querySelector(select.getAttribute("data-mv-level-of"));
            if (!source) { return; }

            var note = optionalTarget(select.getAttribute("data-mv-level-note"));
            var noteLabel = note ? note.querySelector("[data-mv-level-name]") : null;

            var original = Array.prototype.map.call(select.options, function (option) {
                var item = snapshotOption(option);
                item.level = option.getAttribute("data-level") || "";
                return item;
            });

            function apply() {
                var chosen = source.options[source.selectedIndex];
                var level = chosen ? (chosen.getAttribute("data-level") || "") : "";
                var levelName = chosen ? (chosen.getAttribute("data-level-name") || "") : "";
                var previous = select.value;
                select.innerHTML = "";

                original.forEach(function (item) {
                    var keep = item.value === "" || level === "" || item.level === level;
                    if (!keep) { return; }
                    select.add(restoreOption(item));
                });

                select.value = previous;
                if (select.selectedIndex === -1) { select.selectedIndex = 0; }

                if (note) {
                    note.hidden = level === "";
                    if (noteLabel) { noteLabel.textContent = levelName; }
                }
                select.dispatchEvent(new Event("change", { bubbles: true }));
            }

            source.addEventListener("change", apply);
            apply();
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
                //
                // Owner decision 2026-09-04 (multi-course levels): that key may
                // now be a SPACE-SEPARATED SET, because a student holds one
                // level per course and any of them can match. A single value is
                // just a set of one, so nothing else had to change.
                var option = owner.options ? owner.options[owner.selectedIndex] : null;
                if (option && option.hasAttribute("data-owner-key")) {
                    return owner.value === "" ? "" : option.getAttribute("data-owner-key");
                }
                return owner.value;
            }

            // Membership rather than equality — see ownerKey above. Kept as a
            // helper so the empty-key ("show everything") case stays in one
            // place. Splitting on whitespace makes a single-value key behave
            // exactly as it always did.
            function ownerMatches(itemOwner, key) {
                if (key === "") { return true; }
                return key.split(/\s+/).indexOf(itemOwner) !== -1;
            }

            function apply() {
                var ownerId = ownerKey();
                var previous = select.value;
                var kept = 0;
                select.innerHTML = "";

                original.forEach(function (item) {
                    var isPlaceholder = item.value === "";
                    var keep = isPlaceholder || ownerMatches(item.owner, ownerId);
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

    // --- Draft the message a human will send by hand ------------------------
    // <div data-mv-message data-mv-message-student="#sel" data-mv-message-from="#sel"
    //      data-mv-message-to="#sel" data-mv-message-template="... {guardian} {student} {from} {to} ...">
    //
    // Deliberately NOT an integration. Nothing is sent, nothing is stored, and
    // no number or address is read: this fills a textarea from three <select>
    // values already on the page so the admin can copy it into whatever they
    // actually use. WhatsApp is not connected and no credential exists.
    function wireMessageDrafts() {
        document.querySelectorAll("[data-mv-message]").forEach(function (box) {
            var studentSelect = optionalTarget(box.getAttribute("data-mv-message-student"));
            var fromSelect = optionalTarget(box.getAttribute("data-mv-message-from"));
            var toSelect = optionalTarget(box.getAttribute("data-mv-message-to"));
            var output = box.querySelector("[data-mv-message-text]");
            var who = box.querySelector("[data-mv-message-who]");
            var copyButton = box.querySelector("[data-mv-message-copy]");
            var template = box.getAttribute("data-mv-message-template") || "";
            var studentTemplate = box.getAttribute("data-mv-message-template-student") || "";
            var noContact = box.querySelector("[data-mv-message-nocontact]");
            if (!output) { return; }

            function chosen(select) {
                if (!select || select.selectedIndex < 0) { return null; }
                var option = select.options[select.selectedIndex];
                return option && option.value ? option : null;
            }

            function render() {
                var student = chosen(studentSelect);
                var from = chosen(fromSelect);
                var to = chosen(toSelect);

                var studentName = student ? student.text.trim() : "";
                var guardian = (student && student.getAttribute("data-guardian")) || "";
                // The moment alone, not the whole option label (which also
                // carries level, teacher and status - none of it the family's
                // business in a message about a time change).
                var fromWhen = from ? (from.getAttribute("data-when") || from.text.trim()) : "";
                var toWhen = to ? (to.getAttribute("data-when") || to.text.trim()) : "";

                // Who is actually written to. A student with no guardian on
                // file used to still get a draft opening "Hello guardian",
                // addressed to somebody who does not exist. Three cases now,
                // and the third one produces no message at all rather than a
                // polite letter to nobody.
                var hasOwnLogin = !!(student && student.getAttribute("data-has-login") === "1");
                var audience = guardian ? "guardian" : (hasOwnLogin ? "student" : "none");

                if (who) {
                    if (audience === "guardian") {
                        who.textContent = guardian + (studentName ? " (" + studentName + ")" : "");
                    } else if (audience === "student") {
                        who.textContent = studentName;
                    } else {
                        who.textContent = student ? "—" : "—";
                    }
                }
                if (noContact) {
                    noContact.hidden = !(student && audience === "none");
                }

                // Until all three are picked the draft would be a sentence with
                // holes in it, which is worse than an empty box.
                if (!student || !from || !to) {
                    output.value = "";
                    if (copyButton) { copyButton.disabled = true; }
                    return;
                }

                // No guardian and no account of their own: there is nobody to
                // address, so no draft is produced and there is nothing to copy.
                if (audience === "none") {
                    output.value = "";
                    if (copyButton) { copyButton.disabled = true; }
                    return;
                }

                var chosenTemplate = (audience === "student" && studentTemplate) ? studentTemplate : template;
                output.value = chosenTemplate
                    .replace("{guardian}", guardian)
                    .replace("{student}", studentName)
                    .replace("{from}", fromWhen)
                    .replace("{to}", toWhen);
                if (copyButton) { copyButton.disabled = false; }
            }

            [studentSelect, fromSelect, toSelect].forEach(function (select) {
                if (select) { select.addEventListener("change", render); }
            });

            if (copyButton) {
                copyButton.addEventListener("click", function () {
                    if (!output.value) { return; }
                    var done = function () {
                        var was = copyButton.textContent;
                        copyButton.textContent = T_copied();
                        window.setTimeout(function () { copyButton.textContent = was; }, 1600);
                    };
                    // navigator.clipboard needs a secure context and can be
                    // refused; the old selection path is the fallback, never a
                    // silent failure.
                    if (navigator.clipboard && navigator.clipboard.writeText) {
                        navigator.clipboard.writeText(output.value).then(done, function () {
                            output.removeAttribute("readonly");
                            output.select();
                            try { document.execCommand("copy"); done(); } catch (e) { /* leave it selected */ }
                            output.setAttribute("readonly", "readonly");
                        });
                    } else {
                        output.removeAttribute("readonly");
                        output.select();
                        try { document.execCommand("copy"); done(); } catch (e) { /* leave it selected */ }
                        output.setAttribute("readonly", "readonly");
                    }
                });
            }

            render();
        });
    }

    function T_copied() {
        return (document.documentElement.getAttribute("lang") || "").indexOf("ar") === 0
            ? "تم النسخ"
            : "Copied";
    }

    // --- Open the right accordion section on a deep link ---------------------
    // /Admin/Students UX pass (2026-09-03): a collapsed Bootstrap accordion
    // panel is `display:none`, so the browser's own fragment navigation has no
    // box to scroll to — a link like /Admin/Students?studentId=12#link-guardian
    // would land on the page with every section still collapsed and nothing
    // visibly different. This finds the accordion panel matching the current
    // #hash, opens it the same way clicking its header would (so any sibling
    // panel sharing its data-bs-parent still closes), and scrolls it into
    // view. Every panel still opens and closes by hand with JavaScript
    // disabled — this only fixes the one case a bare fragment cannot: a
    // target that starts collapsed.
    function wireHashAccordion() {
        if (!window.location.hash) { return; }
        var target;
        try {
            target = document.querySelector(window.location.hash);
        } catch (e) {
            return; // an unrelated, non-selector hash elsewhere on the site
        }
        if (!target || !target.classList.contains("accordion-collapse")) { return; }

        if (window.bootstrap && window.bootstrap.Collapse) {
            window.bootstrap.Collapse.getOrCreateInstance(target, { toggle: false }).show();
        } else {
            target.classList.add("show");
        }

        window.requestAnimationFrame(function () {
            target.scrollIntoView({ behavior: "smooth", block: "start" });
        });
    }

    // <div class="app-multiselect" data-mv-multiselect> — a closed-by-default
    // dropdown holding checkboxes, with its own search box and the ticked ones
    // echoed back as chips underneath.
    //
    // Owner report 2026-09-05: the previous version of this control was a
    // scrollable box that sat OPEN on the page all the time. Twenty-one courses
    // permanently unrolled made the form look like it was already asking for
    // something, and left no way to say "I am done choosing". It opens when
    // asked now, and closes on Escape, on an outside click, or on the toggle.
    //
    // Display only: the checkboxes inside are the same inputs with the same
    // names, and they are what the form posts. With scripting off the panel is
    // simply always visible (it is hidden by an attribute this code sets), so
    // the form still works.
    function wireMultiSelects() {
        document.querySelectorAll("[data-mv-multiselect]").forEach(function (root) {
            var toggle = root.querySelector("[data-mv-multiselect-toggle]");
            var panel = root.querySelector("[data-mv-multiselect-panel]");
            var search = root.querySelector("[data-mv-multiselect-search]");
            var chips = root.querySelector("[data-mv-multiselect-chips]");
            var summary = root.querySelector("[data-mv-multiselect-summary]");
            if (!toggle || !panel) { return; }

            var boxes = Array.prototype.slice.call(
                root.querySelectorAll('input[type="checkbox"][data-mv-chosen-label]'));

            // Hidden only once the script is running - see the note above.
            panel.hidden = true;
            toggle.setAttribute("aria-expanded", "false");

            function chosen() {
                return boxes.filter(function (box) { return box.checked; });
            }

            function renderSummary() {
                if (!summary) { return; }
                var picked = chosen();
                var none = summary.getAttribute("data-mv-empty-text") || "";
                if (picked.length === 0) {
                    summary.textContent = none;
                    summary.classList.add("is-empty");
                    return;
                }
                summary.classList.remove("is-empty");
                var one = summary.getAttribute("data-mv-one-text") || "{0}";
                var many = summary.getAttribute("data-mv-many-text") || "{0}";
                summary.textContent = picked.length === 1
                    ? one.replace("{0}", picked[0].getAttribute("data-mv-chosen-label"))
                    : many.replace("{0}", String(picked.length));
            }

            function renderChips() {
                if (!chips) { return; }
                chips.textContent = "";
                chosen().forEach(function (box) {
                    var chip = document.createElement("button");
                    chip.type = "button";
                    chip.className = "app-chip is-removable";
                    chip.textContent = box.getAttribute("data-mv-chosen-label");
                    chip.addEventListener("click", function () {
                        box.checked = false;
                        render();
                    });
                    chips.appendChild(chip);
                });
            }

            function render() {
                renderSummary();
                renderChips();
            }

            function filter(term) {
                boxes.forEach(function (box) {
                    var row = box.closest(".form-check") || box.parentNode;
                    var label = (box.getAttribute("data-mv-chosen-label") || "").toLowerCase();
                    row.hidden = term !== "" && label.indexOf(term) === -1;
                });
            }

            function open() {
                panel.hidden = false;
                toggle.setAttribute("aria-expanded", "true");
                if (search) {
                    search.value = "";
                    filter("");
                    search.focus();
                }
            }

            function close() {
                panel.hidden = true;
                toggle.setAttribute("aria-expanded", "false");
            }

            toggle.addEventListener("click", function () {
                if (panel.hidden) { open(); } else { close(); }
            });

            if (search) {
                search.addEventListener("input", function () {
                    filter(search.value.trim().toLowerCase());
                });
            }

            boxes.forEach(function (box) {
                box.addEventListener("change", render);
            });

            root.addEventListener("keydown", function (e) {
                if (e.key === "Escape" && !panel.hidden) {
                    e.preventDefault();
                    close();
                    toggle.focus();
                }
            });

            document.addEventListener("click", function (e) {
                if (!panel.hidden && !root.contains(e.target)) { close(); }
            });

            render();
        });
    }

    // <input type="file" data-mv-image-preview="#img" data-mv-image-preview-hide="#placeholder">
    // Shows the picked image immediately, before anything is uploaded.
    //
    // Owner report 2026-09-05: the poster screen previewed the title and the
    // details live but not the image, so the one thing you actually needed to
    // look at only appeared after saving. Nothing about the upload changes -
    // the file still reaches the server only when the form is submitted; this
    // reads it locally with a blob URL, and revokes the previous one so
    // choosing five files in a row does not leak five of them.
    function wireImagePreviews() {
        document.querySelectorAll("input[type=file][data-mv-image-preview]").forEach(function (input) {
            var img = document.querySelector(input.getAttribute("data-mv-image-preview"));
            var placeholder = optionalTarget(input.getAttribute("data-mv-image-preview-hide"));
            if (!img) { return; }

            var lastUrl = null;

            input.addEventListener("change", function () {
                var file = input.files && input.files[0];

                if (lastUrl) {
                    URL.revokeObjectURL(lastUrl);
                    lastUrl = null;
                }

                if (!file || file.type.indexOf("image/") !== 0) {
                    // Cleared, or something that is not an image: fall back to
                    // whatever the card showed before rather than a broken icon.
                    if (!img.getAttribute("src")) {
                        img.hidden = true;
                        if (placeholder) { placeholder.hidden = false; }
                    }
                    return;
                }

                lastUrl = URL.createObjectURL(file);
                img.src = lastUrl;
                img.hidden = false;
                if (placeholder) { placeholder.hidden = true; }
            });
        });
    }

    document.addEventListener("DOMContentLoaded", function () {
        wireTableFilters();
        wireSelectFilters();
        wireSessionNarrowing();
        wireLevelNarrowing();
        wireDependentFields();
        wireOwnedOptions();
        wireEchoes();
        wireMultiSelects();
        wireImagePreviews();
        wireStepProgress();
        wireConfirmations();
        wireFileModals();
        wireMessageDrafts();
        wireHashAccordion();
        focusFirstInvalidField();
    });
})();
