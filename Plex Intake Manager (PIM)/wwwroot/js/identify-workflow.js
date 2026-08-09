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

        // Own the Identify click in the capture phase so the legacy inline handler
        // cannot start a second progress poll that may reload stale scan results.
        enrichButton.addEventListener("click", async function (event) {
            event.preventDefault();
            event.stopImmediatePropagation();

            if (enrichButton.dataset.pimIdentifying === "true") {
                return;
            }

            enrichButton.dataset.pimIdentifying = "true";
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
                const response = await fetch("?handler=Enrich", {
                    method: "POST",
                    cache: "no-store",
                    credentials: "same-origin",
                    headers: {
                        "RequestVerificationToken": tokenInput.value,
                        "X-Requested-With": "XMLHttpRequest"
                    }
                });

                if (!response.ok) {
                    throw new Error(
                        `PIM returned HTTP ${response.status} while identifying movies.`);
                }

                // Fetch follows the Razor redirect. Reaching this point means metadata,
                // duplicate analysis, destination planning, Plex validation, and the
                // final cache write have all completed. Only now is it safe to reload.
                polling = false;
                window.clearInterval(elapsedTimer);
                window.clearInterval(progressTimer);
                window.location.reload();
            } catch (error) {
                polling = false;
                window.clearInterval(elapsedTimer);
                window.clearInterval(progressTimer);

                workflowStatus.innerHTML =
                    "<strong>Movie identification failed.</strong><br>" +
                    escapeHtml(error instanceof Error
                        ? error.message
                        : "An unexpected browser error occurred.");

                enrichButton.disabled = false;
                enrichButton.dataset.pimIdentifying = "false";
                console.error("PIM identify request failed.", error);
            }
        }, true);
    });
})();
