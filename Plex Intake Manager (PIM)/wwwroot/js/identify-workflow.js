(function () {
    "use strict";

    document.addEventListener("DOMContentLoaded", function () {
        const enrichButton = document.getElementById("enrichBtn");
        const workflowStatus = document.getElementById("workflowStatus");
        const tokenInput = document.querySelector(
            'input[name="__RequestVerificationToken"]');

        if (!enrichButton || !workflowStatus || !tokenInput) {
            return;
        }

        // =====================================================
        // Cancel: stops Identify (or a dry run's lookups) after the movie
        // currently being looked up. Finished lookups are kept.
        // =====================================================
        const cancelButton = document.getElementById("cancelBtn");
        const cancelUrl = new URL(window.location.href);
        cancelUrl.search = "";
        cancelUrl.hash = "";
        cancelUrl.searchParams.set("handler", "Cancel");
        let cancelPending = false;
        let busyWith = workflowStatus.dataset.busyWith || "";

        const setCancelState = progress => {
            if (!cancelButton) {
                return;
            }

            if (progress?.busyWith) {
                busyWith = progress.busyWith;
            }

            cancelButton.disabled =
                cancelPending || !progress || progress.canCancel !== true;
        };

        if (cancelButton) {
            cancelButton.addEventListener("click", async function () {
                if (cancelButton.disabled) {
                    return;
                }

                cancelPending = true;
                cancelButton.disabled = true;
                // Lets the commit's own progress display show the stop note
                // at once, before the server's next progress report.
                document.body.dataset.pimCancelRequested = "true";
                workflowStatus.innerHTML = busyWith === "a live commit"
                    ? "<strong>Stopping after the current file...</strong><br>" +
                        "Files already moved stay moved. The rest are left where they are."
                    : "<strong>Stopping after the current movie...</strong><br>" +
                        "Finished lookups will be kept.";

                try {
                    await fetch(cancelUrl.toString(), {
                        method: "POST",
                        cache: "no-store",
                        credentials: "same-origin",
                        headers: {
                            "RequestVerificationToken": tokenInput.value,
                            "X-Requested-With": "XMLHttpRequest"
                        }
                    });
                } catch (error) {
                    cancelPending = false;
                    delete document.body.dataset.pimCancelRequested;
                    console.error("PIM cancel request failed.", error);
                }
            });
        }

        const cancelProgressUrl = new URL(window.location.href);
        cancelProgressUrl.search = "";
        cancelProgressUrl.hash = "";
        cancelProgressUrl.searchParams.set("handler", "Progress");

        // A dry run or live commit is an ordinary form post; while it is in
        // flight, keep the Cancel button in step with the server. A dry run's
        // lookups can be stopped, and a live commit can be stopped between
        // files.
        const commitForm = document.querySelector('form[action*="handler=Commit"]');

        if (commitForm && cancelButton) {
            commitForm.addEventListener("submit", function () {
                window.setInterval(async function () {
                    try {
                        const response = await fetch(cancelProgressUrl.toString(), {
                            cache: "no-store",
                            credentials: "same-origin"
                        });

                        if (response.ok) {
                            setCancelState(await response.json());
                        }
                    } catch {
                        // Polling stops naturally when the page navigates.
                    }
                }, 750);
            });
        }

        // Opened or refreshed while PIM is already working (for example in a
        // second tab): start locked, follow the progress, and reload when the
        // action ends so its results are shown.
        const busyWithAtLoad = workflowStatus.dataset.busyWith;

        if (busyWithAtLoad) {
            window.pimBusy?.lock();

            const busyWatchTimer = window.setInterval(async function () {
                try {
                    const response = await fetch(cancelProgressUrl.toString(), {
                        cache: "no-store",
                        credentials: "same-origin"
                    });

                    if (!response.ok) {
                        return;
                    }

                    const progress = await response.json();
                    setCancelState(progress);

                    if (progress.busy !== true) {
                        window.clearInterval(busyWatchTimer);
                        window.location.reload();
                        return;
                    }

                    const busyWith = progress.busyWith || busyWithAtLoad;
                    const counts = Number(progress.total) > 0
                        ? `${(Number(progress.processed) || 0).toLocaleString()} of ` +
                            `${Number(progress.total).toLocaleString()}`
                        : "";

                    workflowStatus.textContent =
                        `PIM is busy with ${busyWith}` +
                        (progress.step ? `. ${progress.step}` : "") +
                        (counts ? `: ${counts}...` : "...");
                } catch {
                    // Try again on the next tick.
                }
            }, 1000);
        }

        // Own the Identify click in the capture phase so the legacy inline handler
        // cannot start a second progress poll that may reload stale scan results.
        enrichButton.addEventListener("click", async function (event) {
            event.preventDefault();
            event.stopImmediatePropagation();

            if (enrichButton.dataset.pimIdentifying === "true") {
                return;
            }

            enrichButton.dataset.pimIdentifying = "true";
            // One action at a time: everything except Cancel is disabled
            // until identification ends.
            window.pimBusy?.lock();
            enrichButton.disabled = true;

            const startedAt = Date.now();
            let polling = true;
            let latestProgress = {
                total: 0,
                processed: 0,
                currentFile: "",
                isRunning: false
            };

            const progressUrl = new URL(window.location.href);
            progressUrl.searchParams.set("handler", "Progress");

            const formatTime = totalSeconds => {
                const hours = Math.floor(totalSeconds / 3600);
                const minutes = Math.floor((totalSeconds % 3600) / 60);
                const seconds = totalSeconds % 60;

                return [hours, minutes, seconds]
                    .map(value => value.toString().padStart(2, "0"))
                    .join(":");
            };

            const escapeHtml = value => {
                const element = document.createElement("div");
                element.textContent = value ?? "";
                return element.innerHTML;
            };

            const renderStatus = () => {
                if (!polling) {
                    return;
                }

                const elapsedSeconds = Math.floor(
                    (Date.now() - startedAt) / 1000);
                const data = latestProgress;
                const currentFile = data.currentFile ||
                    "Starting identification...";

                if (data.cancelRequested || cancelPending) {
                    workflowStatus.innerHTML =
                        "<strong>Stopping after the current movie...</strong><br>" +
                        `Looked up ${Number(data.processed) || 0} of ${Number(data.total) || 0}. ` +
                        "Finished lookups will be kept.<br>" +
                        `Elapsed: ${formatTime(elapsedSeconds)}`;
                    return;
                }

                // The server names the step the numbers belong to.
                if (data.step && data.total > 0) {
                    workflowStatus.innerHTML =
                        `<strong>${escapeHtml(data.step)}</strong><br>` +
                        `${(Number(data.processed) || 0).toLocaleString()} of ` +
                        `${(Number(data.total) || 0).toLocaleString()}<br>` +
                        `Current: ${escapeHtml(currentFile)}<br>` +
                        `Elapsed: ${formatTime(elapsedSeconds)}`;
                    return;
                }

                if (data.step) {
                    workflowStatus.innerHTML =
                        `<strong>${escapeHtml(data.step)}</strong><br>` +
                        `Elapsed: ${formatTime(elapsedSeconds)}`;
                    return;
                }

                if (data.isRunning && data.total > 0) {
                    workflowStatus.innerHTML =
                        `<strong>Identifying ${Number(data.processed) || 0} of ` +
                        `${Number(data.total) || 0}</strong><br>` +
                        `Current: ${escapeHtml(currentFile)}<br>` +
                        `Elapsed: ${formatTime(elapsedSeconds)}`;
                    return;
                }

                if (data.isRunning) {
                    workflowStatus.innerHTML =
                        `<strong>${escapeHtml(currentFile)}</strong><br>` +
                        `Elapsed: ${formatTime(elapsedSeconds)}`;
                    return;
                }

                // The request itself is the source of truth for completion. A false
                // progress value before the POST establishes its running state must
                // never trigger a page reload.
                workflowStatus.innerHTML =
                    `<strong>Starting movie identification...</strong><br>` +
                    `Elapsed: ${formatTime(elapsedSeconds)}`;
            };

            const pollProgress = async () => {
                if (!polling) {
                    return;
                }

                try {
                    const response = await fetch(progressUrl.toString(), {
                        method: "GET",
                        cache: "no-store",
                        credentials: "same-origin"
                    });

                    if (!response.ok) {
                        return;
                    }

                    latestProgress = await response.json();
                    setCancelState(latestProgress);
                    renderStatus();
                } catch (error) {
                    console.debug(
                        "PIM identify progress polling is temporarily unavailable.",
                        error);
                }
            };

            renderStatus();
            const elapsedTimer = window.setInterval(renderStatus, 250);
            const progressTimer = window.setInterval(pollProgress, 750);
            void pollProgress();

            try {
                // Do not follow the Razor redirect here: following it would
                // render (and so consume) the page's one-time message, such as
                // "Identify Movies was stopped...", before the reload shows it.
                const response = await fetch("?handler=Enrich", {
                    method: "POST",
                    cache: "no-store",
                    credentials: "same-origin",
                    redirect: "manual",
                    headers: {
                        "RequestVerificationToken": tokenInput.value,
                        "X-Requested-With": "XMLHttpRequest"
                    }
                });

                if (!response.ok && response.type !== "opaqueredirect") {
                    throw new Error(
                        `PIM returned HTTP ${response.status} while identifying movies.`);
                }

                // The handler redirects only after metadata, duplicate analysis,
                // destination planning, Plex validation, and the final save have
                // all completed. Only now is it safe to reload.
                polling = false;
                window.clearInterval(elapsedTimer);
                window.clearInterval(progressTimer);
                window.location.reload();
            } catch (error) {
                polling = false;
                cancelPending = false;
                delete document.body.dataset.pimCancelRequested;
                setCancelState(null);
                window.clearInterval(elapsedTimer);
                window.clearInterval(progressTimer);

                workflowStatus.innerHTML =
                    "<strong>Movie identification failed.</strong><br>" +
                    escapeHtml(error instanceof Error
                        ? error.message
                        : "An unexpected browser error occurred.");

                window.pimBusy?.unlock();
                enrichButton.disabled = false;
                enrichButton.dataset.pimIdentifying = "false";
                console.error("PIM identify request failed.", error);
            }
        }, true);
    });
})();
