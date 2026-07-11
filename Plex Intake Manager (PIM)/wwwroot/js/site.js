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

            workflowStatus.innerHTML =
                `<strong>${isDryRun ? "Preparing dry run" : "Preparing live commit"}...</strong><br>` +
                "PIM is checking the destination for conflicts before processing files.";

            const progressUrl = new URL(window.location.href);
            progressUrl.searchParams.set("handler", "Progress");

            const renderProgress = async () => {
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

                    const data = await response.json();
                    const elapsedSeconds = Math.floor(
                        (Date.now() - startedAt) / 1000);
                    const elapsed = formatTime(elapsedSeconds);

                    if (data.isRunning) {
                        if (data.total > 0) {
                            workflowStatus.innerHTML =
                                `<strong>Processing ${data.processed} of ${data.total}</strong><br>` +
                                `Current File: ${escapeHtml(data.currentFile || "Starting...")}<br>` +
                                `Elapsed: ${elapsed}`;
                        } else {
                            workflowStatus.innerHTML =
                                "<strong>Scanning destination for conflicts...</strong><br>" +
                                `Entries Checked: ${data.processed}<br>` +
                                `Current Location: ${escapeHtml(data.currentFile || "Starting...")}<br>` +
                                `Elapsed: ${elapsed}`;
                        }
                    } else {
                        workflowStatus.innerHTML =
                            `<strong>${isDryRun ? "Dry run" : "Live commit"} request is active...</strong><br>` +
                            "Waiting for the next processing phase.<br>" +
                            `Elapsed: ${elapsed}`;
                    }
                } catch (error) {
                    console.debug("PIM progress polling is temporarily unavailable.", error);
                }
            };

            await renderProgress();
            const progressTimer = window.setInterval(renderProgress, 750);

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
                window.clearInterval(progressTimer);

                // The POST redirects to a fully rendered Razor page containing the
                // TempData result message. Writing that returned page preserves the
                // message without issuing another GET that would consume it twice.
                document.open();
                document.write(responseHtml);
                document.close();
            } catch (error) {
                polling = false;
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
