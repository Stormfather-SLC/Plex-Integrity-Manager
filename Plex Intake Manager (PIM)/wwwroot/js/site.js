// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

(function () {
    "use strict";

    document.addEventListener("DOMContentLoaded", function () {
        const applyButton = Array.from(
            document.querySelectorAll('button[type="submit"]'))
            .find(button => button.textContent.includes("Apply Changes"));

        const commitForm = applyButton?.closest("form");
        const workflowStatus = document.getElementById("workflowStatus");

        if (!applyButton || !commitForm || !workflowStatus) {
            return;
        }

        commitForm.addEventListener("submit", async function (event) {
            event.preventDefault();

            if (commitForm.dataset.pimSubmitting === "true") {
                return;
            }

            const dryRunCheckbox = commitForm.querySelector(
                'input[name="DryRun"][type="checkbox"]');
            const isDryRun = dryRunCheckbox?.checked !== false;

            if (!isDryRun) {
                const confirmed = window.confirm(
                    "Run PIM in live mode? Approved source files may be moved and renamed."
                );

                if (!confirmed) {
                    return;
                }
            }

            commitForm.dataset.pimSubmitting = "true";
            applyButton.disabled = true;

            const originalButtonText = applyButton.innerHTML;
            applyButton.innerHTML = isDryRun
                ? "Processing Dry Run..."
                : "Applying Changes...";

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

            const renderStatus = () => {
                if (!polling) {
                    return;
                }

                const elapsedSeconds = Math.floor(
                    (Date.now() - startedAt) / 1000);
                const elapsed = formatTime(elapsedSeconds);
                const data = latestProgress;

                if (data.isRunning && data.total > 0) {
                    const processed = Math.max(0, Number(data.processed) || 0);
                    const total = Math.max(0, Number(data.total) || 0);
                    const percent = total > 0
                        ? Math.min(100, Math.floor((processed / total) * 100))
                        : 0;
                    const hasCurrentFile = processed < total && data.currentFile;

                    workflowStatus.innerHTML =
                        `<div><strong>Processed ${processed} of ${total}</strong></div>` +
                        `<div class="progress mt-1 mb-2" style="height:20px;" ` +
                            `role="progressbar" aria-label="Overall batch progress" ` +
                            `aria-valuenow="${percent}" aria-valuemin="0" aria-valuemax="100">` +
                            `<div class="progress-bar" style="width:${percent}%">${percent}%</div>` +
                        `</div>` +
                        (hasCurrentFile
                            ? `<div><strong>Processing Current File:</strong> ` +
                                `${escapeHtml(data.currentFile)}</div>` +
                                `<div class="progress mt-1 mb-2" style="height:12px;" ` +
                                    `role="progressbar" aria-label="Current file is being processed">` +
                                    `<div class="progress-bar progress-bar-striped progress-bar-animated" ` +
                                        `style="width:100%"></div>` +
                                `</div>`
                            : `<div><strong>Finalizing results...</strong></div>`) +
                        `<div>Elapsed: ${elapsed}</div>`;
                    return;
                }

                if (data.isRunning) {
                    workflowStatus.innerHTML =
                        "<strong>Scanning destination for conflicts...</strong><br>" +
                        `Entries Checked: ${Number(data.processed) || 0}<br>` +
                        `Current Location: ${escapeHtml(data.currentFile || "Starting...")}<br>` +
                        `<div class="progress mt-1 mb-2" style="height:12px;" ` +
                            `role="progressbar" aria-label="Destination scan is running">` +
                            `<div class="progress-bar progress-bar-striped progress-bar-animated" ` +
                                `style="width:100%"></div>` +
                        `</div>` +
                        `Elapsed: ${elapsed}`;
                    return;
                }

                workflowStatus.innerHTML =
                    `<strong>${isDryRun ? "Preparing dry run" : "Preparing live commit"}...</strong><br>` +
                    "PIM is checking the destination and preparing the approved plan.<br>" +
                    `<div class="progress mt-1 mb-2" style="height:12px;" ` +
                        `role="progressbar" aria-label="PIM is preparing the operation">` +
                        `<div class="progress-bar progress-bar-striped progress-bar-animated" ` +
                            `style="width:100%"></div>` +
                    `</div>` +
                    `Elapsed: ${elapsed}`;
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
                        "PIM progress polling is temporarily unavailable.",
                        error);
                }
            };

            renderStatus();

            // Keep the elapsed clock smooth even when a progress request is delayed
            // by file-system or network activity. Server state is polled separately.
            const elapsedTimer = window.setInterval(renderStatus, 250);
            const progressTimer = window.setInterval(pollProgress, 750);
            await pollProgress();

            try {
                const response = await fetch(commitForm.action, {
                    method: "POST",
                    body: new FormData(commitForm),
                    credentials: "same-origin",
                    headers: {
                        "X-Requested-With": "XMLHttpRequest"
                    }
                });

                const responseHtml = await response.text();

                if (!response.ok) {
                    throw new Error(
                        `PIM returned HTTP ${response.status} while applying changes.`);
                }

                polling = false;
                window.clearInterval(elapsedTimer);
                window.clearInterval(progressTimer);

                // The POST redirects to a fully rendered Razor page containing the
                // TempData result message. Writing that returned page preserves the
                // message without issuing another GET that would consume it twice.
                document.open();
                document.write(responseHtml);
                document.close();
            } catch (error) {
                polling = false;
                window.clearInterval(elapsedTimer);
                window.clearInterval(progressTimer);

                workflowStatus.innerHTML =
                    "<strong>Processing failed.</strong><br>" +
                    escapeHtml(error instanceof Error
                        ? error.message
                        : "An unexpected browser error occurred.");
                applyButton.disabled = false;
                applyButton.innerHTML = originalButtonText;
                commitForm.dataset.pimSubmitting = "false";

                console.error("PIM commit request failed.", error);
            }
        });
    });

    function formatTime(totalSeconds) {
        const hours = Math.floor(totalSeconds / 3600);
        const minutes = Math.floor((totalSeconds % 3600) / 60);
        const seconds = totalSeconds % 60;

        return [hours, minutes, seconds]
            .map(value => value.toString().padStart(2, "0"))
            .join(":");
    }

    function escapeHtml(value) {
        const element = document.createElement("div");
        element.textContent = value ?? "";
        return element.innerHTML;
    }
})();
