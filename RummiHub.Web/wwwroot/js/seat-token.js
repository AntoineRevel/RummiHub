// Per-seat secret for the browser tab that created or joined the game.
// sessionStorage survives a refresh of this tab and is not part of the shareable game URL.
const prefix = "rummihub-seat:";

export function save(gameId, token) {
    sessionStorage.setItem(prefix + gameId, token);
}

export function load(gameId) {
    return sessionStorage.getItem(prefix + gameId);
}

// Opens the game in a new tab; the lobby stays behind, so closing the game brings the
// player back to it. The tab is opened WITH an opener (no "noopener"): browsers copy the
// opener's sessionStorage into such a tab, which is how the seat secret gets there.
// Returns false when the browser blocked the tab (no user gesture): the lobby then shows a
// button for the player to click.
export function openGame(url) {
    const tab = window.open(url, "_blank");
    return tab !== null;
}
