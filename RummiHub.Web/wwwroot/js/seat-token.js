// Per-seat secret for the browser tab that created or joined the game.
// sessionStorage survives a refresh of this tab and is not part of the shareable game URL.
const prefix = "rummihub-seat:";

export function save(gameId, token) {
    sessionStorage.setItem(prefix + gameId, token);
}

export function load(gameId) {
    return sessionStorage.getItem(prefix + gameId);
}
