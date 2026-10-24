// Per-seat secret for the browser tab that created or joined the game.
// sessionStorage survives a refresh of this tab and is not part of the shareable game URL.
const prefix = "rummihub-seat:";
const hashKey = "seat=";

export function save(gameId, token) {
    sessionStorage.setItem(prefix + gameId, token);
}

// The lobby hands the secret to the game tab in the URL fragment (#seat=…), which never
// reaches the server: Safari does not reliably copy sessionStorage into a tab opened from a
// link or window.open. The fragment is moved into sessionStorage and removed from the
// address bar and history, so the URL left to share or bookmark has no secret in it.
export function load(gameId) {
    if (location.hash.startsWith("#" + hashKey)) {
        save(gameId, decodeURIComponent(location.hash.substring(1 + hashKey.length)));
        history.replaceState(history.state, "", location.pathname + location.search);
    }
    return sessionStorage.getItem(prefix + gameId);
}

// Tab opened, still blank, by the host's click on Start, waiting for the game URL.
let pendingTab = null;

// Safari only lets a page open a tab inside the click itself, and the Start click reaches
// the server before the game exists. So the click opens a blank tab right away, here in the
// browser, and openGame points it at the game once the server has started it.
export function armStart(button) {
    if (button.dataset.armed) return;
    button.dataset.armed = "1";
    button.addEventListener("click", () => {
        if (!pendingTab || pendingTab.closed)
            pendingTab = window.open("", "_blank");
    });
}

// Start failed: the blank tab has nothing to show.
export function discardPendingTab() {
    if (pendingTab && !pendingTab.closed) pendingTab.close();
    pendingTab = null;
}

// Opens the game in a new tab; the lobby stays behind, so closing the game brings the
// player back to it. A player who did not click Start has no gesture to open a tab with, and
// browsers then block it (Safari always does): the game takes over this tab instead, so it
// opens everywhere without touching any popup setting. Back returns to the lobby.
export function openGame(url) {
    if (pendingTab && !pendingTab.closed) {
        pendingTab.location.href = url;
        pendingTab = null;
        return;
    }
    pendingTab = null;
    if (window.open(url, "_blank") === null)
        location.assign(url);
}
