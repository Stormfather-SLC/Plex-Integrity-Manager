(function () {
    "use strict";

    // A dry run must be built from authoritative metadata. When a scanned movie
    // already contains an IMDb ID, PIM can resolve its official title, year,
    // rating, and genre automatically before the dry-run plan is generated.
    document.addEventListener("submit", async function (event) {
        const form = event.target;

        if (!(form instanceof HTMLFormElement)) {
            return;
        }

        const applyButton = Array.from(
            form.querySelectorAll('button[type="submit"]'))
            .find(button => button.textContent.includes("Apply Changes"));

        if (!applyButton) {
            return;
        }

        const dryRunCheckbox = form.querySelector(
            'input[name="DryRun"][type="checkbox"]');
        const isDryRun = dryRunCheckbox?.checked !== false;

        // Live commit must use the exact metadata and plan approved by the prior
        // dry run. Do not run enrichment again immediately before live commit.
        if (!isDryRun || form.dataset.pimMetadataPrepared === "true") {
            return;
        }

        event.preventDefault();
        event.stopImmediatePropagation();

        const workflowStatus = document.getElementById("workflowStatus");
        const originalButtonText = applyButton.innerHTML;
        const token = form.querySelector(
            'input[name="__RequestVerificationToken"]')?.value;

        applyButton.disabled = true;
        applyButton.innerHTML = "Identifying Movies...";

        if (workflowStatus) {
            workflowStatus.innerHTML =
                "<strong>Identifying movies before dry run...</strong><br>" +
                "IMDb IDs found in either the file or folder name are being used " +
                "to retrieve authoritative title, year, rating, and genre metadata.";
        }

        try {
            const enrichUrl = new URL(window.location.href);
            enrichUrl.search = "";
            enrichUrl.searchParams.set("handler", "Enrich");

            const body = new FormData();

            if (token) {
                body.append("__RequestVerificationToken", token);
            }

            const response = await fetch(enrichUrl.toString(), {
                method: "POST",
                body,
                credentials: "same-origin",
                headers: {
                    "X-Requested-With": "XMLHttpRequest"
                }
            });

            if (!response.ok) {
                throw new Error(
                    `PIM returned HTTP ${response.status} while identifying movies.`);
            }

            // Let the existing site.js submit handler perform the actual dry run.
            // The one-use flag prevents this capture handler from intercepting the
            // resubmitted form a second time.
            form.dataset.pimMetadataPrepared = "true";
            applyButton.disabled = false;
            applyButton.innerHTML = originalButtonText;
            form.requestSubmit(applyButton);
        } catch (error) {
            applyButton.disabled = false;
            applyButton.innerHTML = originalButtonText;

            if (workflowStatus) {
                workflowStatus.innerHTML =
                    "<strong>Movie identification failed.</strong><br>" +
                    escapeHtml(error instanceof Error
                        ? error.message
                        : "An unexpected error occurred.");
            }

            console.error("PIM automatic IMDb enrichment failed.", error);
        }
    }, true);

    function escapeHtml(value) {
        const element = document.createElement("div");
        element.textContent = value ?? "";
        return element.innerHTML;
    }
})();
