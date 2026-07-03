window.dataLayer = window.dataLayer || [];
function gtag() {
    window.dataLayer.push(arguments);
}

gtag("js", new Date());
gtag("config", "G-ET4NE2RTGC");

window.matchPredictorTracking = {
    track(eventType, metadata) {
        if (!eventType) {
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
        const hasConsent = localStorage.getItem("cookieConsent");

        if (!hasConsent) {
            consentBanner.style.display = "flex";
        }

        acceptBtn.addEventListener("click", function () {
            localStorage.setItem("cookieConsent", "true");
            consentBanner.style.display = "none";
        });

        declineBtn.addEventListener("click", function () {
            localStorage.setItem("cookieConsent", "false");
            consentBanner.style.display = "none";
        });
    }
});
