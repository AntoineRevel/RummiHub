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

// Opens the game in a new tab; the lobby stays behind, so closing the game brings the
// player back to it. Returns false when the browser blocked the tab (no user gesture, which
// Safari requires for every window.open): the lobby then shows links for the player to tap.
export function openGame(url) {
    const tab = window.open(url, "_blank");
    return tab !== null;
}
