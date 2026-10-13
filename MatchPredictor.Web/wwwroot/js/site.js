window.dataLayer = window.dataLayer || [];
function gtag() {
    window.dataLayer.push(arguments);
}

const CONSENT_STORAGE_KEY = "cookieConsent";
const ANALYTICS_CONSENT_COOKIE = "MP_ANALYTICS_CONSENT";
const PWA_INSTALL_DISMISSED_KEY = "pwaInstallDismissed";

let deferredPwaInstallPrompt = null;

function registerServiceWorker() {
    if (!("serviceWorker" in navigator)) {
        return;
    }

    window.addEventListener("load", () => {
        navigator.serviceWorker.register("/service-worker.js").catch(() => {
            // Service worker registration should never block page usage.
        });
    });
}

function initPwaInstallBanner() {
    const banner = document.getElementById("pwaInstallBanner");
    const installBtn = document.getElementById("pwaInstallBtn");
    const dismissBtn = document.getElementById("pwaInstallDismissBtn");

    if (!banner || !installBtn || !dismissBtn) {
        return;
    }

    const hideBanner = () => {
        banner.style.display = "none";
        banner.hidden = true;
    };

    if (localStorage.getItem(PWA_INSTALL_DISMISSED_KEY) === "true") {
        hideBanner();
    }

    window.addEventListener("beforeinstallprompt", (event) => {
        event.preventDefault();
        deferredPwaInstallPrompt = event;

        if (localStorage.getItem(PWA_INSTALL_DISMISSED_KEY) === "true") {
            return;
        }

        banner.style.display = "flex";
        banner.hidden = false;
    });

    window.addEventListener("appinstalled", () => {
        deferredPwaInstallPrompt = null;
        localStorage.setItem(PWA_INSTALL_DISMISSED_KEY, "true");
        hideBanner();
    });

    installBtn.addEventListener("click", async () => {
        if (!deferredPwaInstallPrompt) {
            return;
        }

        deferredPwaInstallPrompt.prompt();
        await deferredPwaInstallPrompt.userChoice;
        deferredPwaInstallPrompt = null;
        hideBanner();
    });

    dismissBtn.addEventListener("click", () => {
        localStorage.setItem(PWA_INSTALL_DISMISSED_KEY, "true");
        hideBanner();
    });
}

registerServiceWorker();

function setAnalyticsConsentCookie(granted) {
    const value = granted ? "granted" : "denied";
    const maxAge = 365 * 24 * 60 * 60;
    const secure = window.location.protocol === "https:" ? "; Secure" : "";
    document.cookie = `${ANALYTICS_CONSENT_COOKIE}=${value}; Max-Age=${maxAge}; Path=/; SameSite=Lax${secure}`;
}

function applyConsentMode(granted) {
    // Ad personalization stays denied always: this site includes sports-selection /
    // betting-adjacent tools, so Google ads must remain non-personalized.
    gtag("consent", "update", {
        ad_storage: granted ? "granted" : "denied",
        ad_user_data: "denied",
        ad_personalization: "denied",
        analytics_storage: granted ? "granted" : "denied"
    });
}

function hasAnalyticsConsent() {
    return localStorage.getItem(CONSENT_STORAGE_KEY) === "true";
}

function syncStoredConsent() {
    const storedConsent = localStorage.getItem(CONSENT_STORAGE_KEY);
    if (storedConsent === "true") {
        setAnalyticsConsentCookie(true);
        applyConsentMode(true);
        return;
    }

    if (storedConsent === "false") {
        setAnalyticsConsentCookie(false);
        applyConsentMode(false);
    }
}

gtag("js", new Date());
gtag("config", "G-ET4NE2RTGC", { anonymize_ip: true });
syncStoredConsent();

window.matchPredictorTracking = {
    track(eventType, metadata) {
        if (!eventType || !hasAnalyticsConsent()) {
            return;
        }

        const payload = JSON.stringify({
            eventType,
            pagePath: window.location.pathname,
            metadata: metadata || {}
        });

        try {
            fetch("/api/tracking/event", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: payload,
                keepalive: true
            }).catch(() => {});
        } catch {
            // Tracking should never interrupt the page flow.
        }
    }
};

document.addEventListener("DOMContentLoaded", function () {
    initPwaInstallBanner();

    document.getElementById("menuToggle")?.addEventListener("click", function () {
        document.getElementById("navLinks")?.classList.toggle("show");
    });

    const currentPath = window.location.pathname.toLowerCase();
    document.querySelectorAll(".mp-nav-link").forEach(link => {
        if (link.getAttribute("href")?.toLowerCase() === currentPath) {
            link.classList.add("active");
        }
    });

    const scrollBtn = document.getElementById("scrollTopBtn");
    window.addEventListener("scroll", () => {
        if (window.scrollY > 300) {
            scrollBtn?.classList.add("visible");
        } else {
            scrollBtn?.classList.remove("visible");
        }
    });
    scrollBtn?.addEventListener("click", () => {
        window.scrollTo({ top: 0, behavior: "smooth" });
    });

    document.querySelectorAll("[data-cart-action]").forEach(element => {
        element.addEventListener("click", () => {
            switch (element.dataset.cartAction) {
                case "open":
                    window.openCartModal?.();
                    break;
                case "close":
                    window.closeCartModal?.();
                    break;
                case "clear":
                    window.clearCart?.();
                    break;
                case "book":
                    window.bookGames?.();
                    break;
            }
        });
    });

    const consentBanner = document.getElementById("cookieConsentBanner");
    const acceptBtn = document.getElementById("acceptCookiesBtn");
    const declineBtn = document.getElementById("declineCookiesBtn");

    if (consentBanner && acceptBtn && declineBtn) {
        const storedConsent = localStorage.getItem(CONSENT_STORAGE_KEY);

        if (!storedConsent) {
            consentBanner.style.display = "flex";
        }

        acceptBtn.addEventListener("click", function () {
            localStorage.setItem(CONSENT_STORAGE_KEY, "true");
            setAnalyticsConsentCookie(true);
            applyConsentMode(true);
            consentBanner.style.display = "none";
            window.location.reload();
        });

        declineBtn.addEventListener("click", function () {
            localStorage.setItem(CONSENT_STORAGE_KEY, "false");
            setAnalyticsConsentCookie(false);
            applyConsentMode(false);
            consentBanner.style.display = "none";
        });
    }

    // Support / Tip Modal handling
    const supportModal = document.getElementById("supportModal");
    const openSupportBtn = document.getElementById("openSupportModalBtn");
    const closeSupportBtn = document.getElementById("closeSupportModalBtn");

    function openSupportModal() {
        if (!supportModal) return;
        supportModal.style.display = "flex";
        document.body.style.overflow = "hidden";
    }

    function closeSupportModal() {
        if (!supportModal) return;
        supportModal.style.display = "none";
        document.body.style.overflow = "";
    }

    window.openSupportModal = openSupportModal;
    window.closeSupportModal = closeSupportModal;

    openSupportBtn?.addEventListener("click", openSupportModal);
    closeSupportBtn?.addEventListener("click", closeSupportModal);
    supportModal?.addEventListener("click", function (e) {
        if (e.target === supportModal) {
            closeSupportModal();
        }
    });

    document.addEventListener("keydown", function (e) {
        if (e.key === "Escape" && supportModal && supportModal.style.display === "flex") {
            closeSupportModal();
        }
    });

    // Globally delegate [data-support-trigger] clicks
    document.addEventListener("click", function (e) {
        const trigger = e.target.closest("[data-support-trigger]");
        if (trigger) {
            e.preventDefault();
            openSupportModal();
        }
    });

    // Copy to clipboard handler for bank accounts
    document.addEventListener("click", function (e) {
        const btn = e.target.closest(".mp-copy-acc-btn");
        if (!btn) return;

        const textToCopy = btn.getAttribute("data-copy-account") || btn.getAttribute("data-copy-target");
        if (!textToCopy) return;

        navigator.clipboard.writeText(textToCopy).then(() => {
            const originalHtml = btn.innerHTML;
            btn.classList.add("mp-btn-copied");
            btn.innerHTML = `<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><polyline points="20 6 9 17 4 12"></polyline></svg><span>Copied!</span>`;
            setTimeout(() => {
                btn.innerHTML = originalHtml;
                btn.classList.remove("mp-btn-copied");
            }, 2500);
        }).catch(() => {
            const textArea = document.createElement("textarea");
            textArea.value = textToCopy;
            document.body.appendChild(textArea);
            textArea.select();
            try {
                document.execCommand("copy");
                btn.classList.add("mp-btn-copied");
                btn.innerText = "✓ Copied!";
                setTimeout(() => {
                    btn.classList.remove("mp-btn-copied");
                    btn.innerText = "Copy";
                }, 2500);
            } catch {
                // Ignore fallback failure
            }
            document.body.removeChild(textArea);
        });
    });

    // Markets Dropdown Toggle on Desktop
    const marketsToggle = document.getElementById("marketsDropdownToggle");
    const marketsDropdownMenu = document.getElementById("marketsDropdownMenu");

    if (marketsToggle && marketsDropdownMenu) {
        marketsToggle.addEventListener("click", function (e) {
            e.stopPropagation();
            const isOpen = marketsDropdownMenu.classList.toggle("show");
            marketsToggle.setAttribute("aria-expanded", isOpen ? "true" : "false");
        });

        document.addEventListener("click", function (e) {
            if (!marketsToggle.contains(e.target) && !marketsDropdownMenu.contains(e.target)) {
                marketsDropdownMenu.classList.remove("show");
                marketsToggle.setAttribute("aria-expanded", "false");
            }
        });
    }
});
