(function () {
    let currentAnalyticsMode = "published";
    let currentAnalyticsTab = document.querySelector("[data-analytics-default-tab]")?.dataset.analyticsDefaultTab || "7days";
    const tablessModes = new Set(["config", "diagnostics"]);

    function setAnalyticsVisibility() {
        document.querySelectorAll(".mp-analytics-mode-view").forEach(view => {
            view.classList.toggle("active", view.id === currentAnalyticsMode + "-view");
        });

        const dateTabs = document.getElementById("analyticsDateTabs");
        if (dateTabs) {
            dateTabs.classList.toggle("mp-analytics-tabs-hidden", tablessModes.has(currentAnalyticsMode));
        }

        if (tablessModes.has(currentAnalyticsMode)) {
            return;
        }

        document.querySelectorAll(`#${currentAnalyticsMode}-view .mp-analytics-section`).forEach(section => {
            section.classList.toggle("active", section.id === `${currentAnalyticsMode}-${currentAnalyticsTab}`);
        });
    }

    function switchAnalyticsMode(button) {
        const mode = button.dataset.mode;
        if (!mode) {
            return;
        }

        currentAnalyticsMode = mode;
        document.querySelectorAll(".mp-mode-btn").forEach(item => item.classList.remove("active"));
        button.classList.add("active");
        setAnalyticsVisibility();
    }

    function switchAnalyticsTab(button) {
        const tabId = button.dataset.tab;
        if (!tabId) {
            return;
        }

        currentAnalyticsTab = tabId;
        document.querySelectorAll(".mp-tab-btn").forEach(item => item.classList.remove("active"));
        button.classList.add("active");
        setAnalyticsVisibility();
    }

    document.addEventListener("DOMContentLoaded", function () {
        document.querySelectorAll(".mp-mode-btn[data-mode]").forEach(button => {
            button.addEventListener("click", () => switchAnalyticsMode(button));
        });

        document.querySelectorAll(".mp-tab-btn[data-tab]").forEach(button => {
            button.addEventListener("click", () => switchAnalyticsTab(button));
        });

        setAnalyticsVisibility();
    });
})();
