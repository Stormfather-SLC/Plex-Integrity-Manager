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

            const destinationPath =
                document.querySelector('input[name="OutputPath"]')?.value ?? "";
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

                const elapsedSeconds = Math.floor(
                    (Date.now() - startedAt) / 1000);
                const enhancedHtml = enhanceCompletionPage(
                    responseHtml,
                    isDryRun,
                    destinationPath,
                    formatTime(elapsedSeconds));

                // The POST redirects to a fully rendered Razor page containing the
                // TempData result message. Writing that returned page preserves the
                // message without issuing another GET that would consume it twice.
                document.open();
                document.write(enhancedHtml);
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

    function enhanceCompletionPage(
        responseHtml,
        isDryRun,
        destinationPath,
        elapsed) {
        const parser = new DOMParser();
        const completedDocument = parser.parseFromString(responseHtml, "text/html");
        const resultAlert = Array.from(
            completedDocument.querySelectorAll(".alert.alert-info"))
            .find(alert => {
                const text = alert.textContent ?? "";
                return text.includes("Dry Run Complete using") ||
                    text.includes("Changes Applied using");
            });

        if (!resultAlert) {
            return responseHtml;
        }

        const text = (resultAlert.textContent ?? "")
            .replace(/\s+/g, " ")
            .trim();
        const dryRunPattern =
            /Dry Run Complete using '(.+?)': (\d+) files would be moved, (\d+) duplicates would be skipped, (\d+) need review, (\d+) errors found\./i;
        const livePattern =
            /Changes Applied using '(.+?)': (\d+) files processed, (\d+) duplicates skipped, (\d+) need review, (\d+) errors found\./i;
        const match = text.match(isDryRun ? dryRunPattern : livePattern);

        if (!match) {
            return responseHtml;
        }

        const profileName = match[1];
        const handledCount = Number(match[2]);
        const duplicateCount = Number(match[3]);
        const reviewCount = Number(match[4]);
        const errorCount = Number(match[5]);
        const alertType = errorCount > 0
            ? "alert-warning"
            : isDryRun
                ? "alert-primary"
                : "alert-success";
        const title = isDryRun
            ? "Dry Run Complete — No files were changed"
            : "Live Commit Complete";
        const primaryLabel = isDryRun
            ? "Would move"
            : "Approved files processed";
        const reviewLabel = isDryRun
            ? "Files requiring review"
            : "Review items left unchanged";

        resultAlert.className = `alert ${alertType} mt-3`;
        resultAlert.innerHTML =
            `<div class="fw-bold fs-5 mb-2">${escapeHtml(title)}</div>` +
            `<div class="mb-2"><strong>Profile:</strong> ${escapeHtml(profileName)}</div>` +
            `<div class="row g-2 mb-2">` +
                `<div class="col-sm-6 col-lg-3"><strong>${primaryLabel}:</strong> ${handledCount}</div>` +
                `<div class="col-sm-6 col-lg-3"><strong>Duplicates skipped:</strong> ${duplicateCount}</div>` +
                `<div class="col-sm-6 col-lg-3"><strong>${reviewLabel}:</strong> ${reviewCount}</div>` +
                `<div class="col-sm-6 col-lg-3"><strong>Errors:</strong> ${errorCount}</div>` +
            `</div>` +
            `<div><strong>Destination:</strong> ` +
                `<span class="text-break">${escapeHtml(destinationPath || "Not available")}</span></div>` +
            `<div><strong>Total elapsed time:</strong> ${escapeHtml(elapsed)}</div>`;

        return "<!DOCTYPE html>\n" + completedDocument.documentElement.outerHTML;
    }

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