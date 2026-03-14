// ── Cart State Management ──
const CART_KEY = 'mp_cart';

function getCart() {
    try {
        return JSON.parse(localStorage.getItem(CART_KEY) || '[]');
    } catch {
        return [];
    }
}

function saveCart(cart) {
    localStorage.setItem(CART_KEY, JSON.stringify(cart));
    updateCartBadge();
}

function addToCart(match) {
    const cart = getCart();
    // Avoid duplicates by checking the full bet identity, not just the fixture.
    const exists = cart.some(
        m =>
            m.homeTeam === match.homeTeam &&
            m.awayTeam === match.awayTeam &&
            m.league === match.league &&
            (m.market || '') === (match.market || '') &&
            (m.prediction || '') === (match.prediction || '')
    );
    if (exists) {
        showToast('Already in betslip');
        return;
    }
    cart.push(match);
    saveCart(cart);
    window.matchPredictorTracking?.track('add_to_cart', {
        market: match.market || '',
        prediction: match.prediction || '',
        league: match.league || ''
    });
    showToast('Added to betslip');
}

function removeFromCart(index) {
    const cart = getCart();
    cart.splice(index, 1);
    saveCart(cart);
    renderCartItems();
}

function clearCart() {
    const count = getCart().length;
    localStorage.removeItem(CART_KEY);
    updateCartBadge();
    renderCartItems();
    if (count > 0) {
        window.matchPredictorTracking?.track('clear_cart', { count: String(count) });
    }
}

// ── Badge ──
function updateCartBadge() {
    const badge = document.getElementById('cartBadge');
    const count = getCart().length;
    if (badge) {
        badge.textContent = count;
        badge.style.display = count > 0 ? 'flex' : 'none';
    }
}

// ── Toast ──
function showToast(msg) {
    let toast = document.getElementById('cartToast');
    if (!toast) {
        toast = document.createElement('div');
        toast.id = 'cartToast';
        toast.className = 'mp-cart-toast';
        document.body.appendChild(toast);
    }
    toast.textContent = msg;
    toast.classList.add('show');
    setTimeout(() => toast.classList.remove('show'), 2000);
}

// ── Modal ──
function openCartModal() {
    const modal = document.getElementById('cartModal');
    if (modal) {
        modal.classList.add('open');
        renderCartItems();
        window.matchPredictorTracking?.track('open_betslip', {
            count: String(getCart().length)
        });
    }
}

function closeCartModal() {
    const modal = document.getElementById('cartModal');
    if (modal) modal.classList.remove('open');
}

function renderCartItems() {
    const container = document.getElementById('cartItemsList');
    const emptyState = document.getElementById('cartEmpty');
    const footer = document.getElementById('cartFooter');
    if (!container) return;

    const cart = getCart();
    container.innerHTML = '';

    if (cart.length === 0) {
        if (emptyState) emptyState.style.display = 'block';
        if (footer) footer.style.display = 'none';
        return;
    }

    if (emptyState) emptyState.style.display = 'none';
    if (footer) footer.style.display = 'flex';

    cart.forEach((item, index) => {
        const div = document.createElement('div');
        div.className = 'mp-cart-item';
        div.innerHTML = `
            <div class="mp-cart-item-info">
                <div class="mp-cart-item-teams">${item.homeTeam} vs ${item.awayTeam}</div>
                <div class="mp-cart-item-meta">
                    <span class="mp-cart-item-league">${item.league}</span>
                    <span class="mp-cart-item-prediction">${item.prediction}</span>
                </div>
            </div>
            <button class="mp-cart-item-remove" onclick="removeFromCart(${index})" title="Remove">✕</button>
        `;
        container.appendChild(div);
    });
}

// ── Booking ──
async function bookGames() {
    const cart = getCart();
    if (cart.length === 0) {
        showToast('Cart is empty');
        return;
    }

    const bookBtn = document.getElementById('bookGamesBtn');
    const resultDiv = document.getElementById('bookingResult');
    if (bookBtn) {
        bookBtn.disabled = true;
        bookBtn.textContent = 'Booking...';
    }

    try {
        const selections = cart.map(item => ({
            homeTeam: item.homeTeam,
            awayTeam: item.awayTeam,
            league: item.league,
            market: item.market || 'Unknown',
            prediction: item.prediction
        }));

        const response = await fetch('/api/booking/book', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ selections })
        });

        const result = await response.json();

        if (resultDiv) {
            if (result.success) {
                const urlHtml = result.bookingUrl
                    ? `<a href="${result.bookingUrl}" target="_blank" class="mp-booking-url-btn">🔗 Open in SportyBet</a>`
                    : '';

                resultDiv.innerHTML = `
                    <div class="mp-booking-success">
                        <button class="mp-booking-close" onclick="this.closest('.mp-booking-success').parentElement.style.display='none'">&times;</button>
                        <div class="mp-booking-code-label">Booking Code</div>
                        <div class="mp-booking-code">${result.bookingCode}</div>
                        <div style="display:flex; gap:10px; justify-content:center; margin-bottom:15px;">
                            <button class="mp-copy-code-btn" onclick="copyBookingCode('${result.bookingCode}')">📋 Copy</button>
                            ${urlHtml}
                        </div>
                        <p class="mp-booking-msg">${result.message}</p>
                    </div>
                `;
                clearCart();
            } else {
                resultDiv.innerHTML = `
                    <div class="mp-booking-error">
                        <button class="mp-booking-close" onclick="this.closest('.mp-booking-error').parentElement.style.display='none'">&times;</button>
                        <p>❌ ${result.message}</p>
                    </div>
                `;
            }
            resultDiv.style.display = 'block';
        }
    } catch (err) {
        if (resultDiv) {
            resultDiv.innerHTML = `<div class="mp-booking-error"><p>❌ Network error. Please try again.</p></div>`;
            resultDiv.style.display = 'block';
        }
    } finally {
        if (bookBtn) {
            bookBtn.disabled = false;
            bookBtn.textContent = '🎫 Book Games';
        }
    }
}

function copyBookingCode(code) {
    navigator.clipboard.writeText(code)
        .then(() => {
            window.matchPredictorTracking?.track('copy_booking_code', {
                hasCode: String(Boolean(code))
            });
            showToast('Copied!');
        })
        .catch(() => showToast('Copy failed'));
}

// ── Init ──
document.addEventListener('DOMContentLoaded', () => {
    updateCartBadge();

    // Event delegation for "Add Bet" buttons using data-attributes
    document.addEventListener('click', (e) => {
        const btn = e.target.closest('.mp-add-cart-btn');
        if (!btn) return;

        e.preventDefault();
        addToCart({
            homeTeam: btn.dataset.home || '',
            awayTeam: btn.dataset.away || '',
            league: btn.dataset.league || '',
            market: btn.dataset.market || '',
            prediction: btn.dataset.prediction || ''
        });
    });
});
