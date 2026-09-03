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
});
