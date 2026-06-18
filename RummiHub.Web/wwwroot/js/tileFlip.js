// FLIP (First, Last, Invert, Play) animation for the RummiHub board, played ONE TILE AT A TIME.
//
// The board is re-rendered wholesale by Blazor on every move, so tiles would "teleport" to
// their new layout. To make them glide like real tiles, and slowly enough to follow how the
// board was rearranged:
//   1. snapshot()  — called BEFORE the new board renders: record each tile's screen rect,
//                    keyed by its stable [data-flip-id].
//   2. play()      — called AFTER the new board renders: invert every moved tile back to its
//                    old position, then animate them to their final position SEQUENTIALLY, in
//                    board reading order (run by run, then group by group) — so at most one tile
//                    moves at a time and the board appears to be reassembled set by set. Tiles
//                    with no "before" rect are entering tiles (played from a hand) and fade+zoom
//                    in at their turn in the sequence.
//
// The board is aligned server-side so unchanged sets keep their position; only genuinely moved
// or new tiles animate. Inline styles set here are not tracked by Blazor's diff (the .tile root
// has no style attribute), so they survive intermediate re-renders and are cleared per tile.

const EASING = 'cubic-bezier(0.22, 1, 0.36, 1)'; // quick start, gentle settle

// Per-tile durations, kept in sync with the server (RummiHub.Web/Rendering/BoardAnimation.cs)
// via configure(); the server uses the same numbers to pace AI moves after the animation.
let MOVE_STEP_MS = 350;
let ENTER_STEP_MS = 300;

// Map<flipId, {left, top}> captured by the most recent snapshot(), or null.
let snapshotRects = null;
// Bumped on every snapshot so a newer transition cleanly abandons an in-progress sequence.
let generation = 0;

export function configure(moveStepMs, enterStepMs) {
    if (moveStepMs > 0) MOVE_STEP_MS = moveStepMs;
    if (enterStepMs > 0) ENTER_STEP_MS = enterStepMs;
}

function prefersReducedMotion() {
    return window.matchMedia
        && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

function boardTiles() {
    // Document order = board reading order = runs left-to-right then groups, contiguous per set.
    return Array.from(document.querySelectorAll('[data-flip-id]'));
}

function clearInlineAnim(el) {
    el.style.transition = '';
    el.style.transform = '';
    el.style.opacity = '';
}

// Record the resting position of every board tile. Any in-flight animation is settled first so
// we always measure the natural layout, even when a new move lands mid-animation.
export function snapshot() {
    generation++;
    if (prefersReducedMotion()) {
        snapshotRects = null;
        return;
    }

    const tiles = boardTiles();
    for (const el of tiles) {
        el.style.transition = 'none';
        el.style.transform = '';
        el.style.opacity = '';
    }
    if (tiles.length) void tiles[0].getBoundingClientRect(); // force reflow

    snapshotRects = new Map();
    for (const el of tiles) {
        const id = el.getAttribute('data-flip-id');
        const r = el.getBoundingClientRect();
        snapshotRects.set(id, { left: r.left, top: r.top });
    }
}

// Animate the new board from the snapshotted positions, one tile at a time. Returns a Promise
// that resolves when the last tile settles.
export async function play() {
    if (prefersReducedMotion() || !snapshotRects) {
        snapshotRects = null;
        return;
    }

    const prev = snapshotRects;
    snapshotRects = null;
    const gen = generation;

    const sequence = [];
    for (const el of boardTiles()) {
        const id = el.getAttribute('data-flip-id');
        const before = prev.get(id);
        const after = el.getBoundingClientRect();
        if (before) {
            const dx = before.left - after.left;
            const dy = before.top - after.top;
            if (dx !== 0 || dy !== 0) sequence.push({ el, kind: 'move', dx, dy });
        } else {
            sequence.push({ el, kind: 'enter' });
        }
    }
    if (sequence.length === 0) return;

    // Invert every moved tile to its old position and hide every entering tile — all up front,
    // with no transition — so the not-yet-animated tiles wait in place until their turn.
    for (const item of sequence) {
        item.el.style.transition = 'none';
        if (item.kind === 'move') {
            item.el.style.transform = `translate(${item.dx}px, ${item.dy}px)`;
        } else {
            item.el.style.opacity = '0';
            item.el.style.transform = 'scale(0.85)';
        }
    }
    void sequence[0].el.getBoundingClientRect(); // commit the inverted state

    // Release the tiles one at a time, in reading order: build each set, then the next.
    for (const item of sequence) {
        if (gen !== generation) return; // a newer transition took over — abandon this one
        await animateOne(item);
        if (gen === generation) clearInlineAnim(item.el);
    }
}

function animateOne(item) {
    return new Promise(resolve => {
        const durationMs = item.kind === 'move' ? MOVE_STEP_MS : ENTER_STEP_MS;
        let done = false;
        const finish = () => {
            if (done) return;
            done = true;
            item.el.removeEventListener('transitionend', finish);
            resolve();
        };

        requestAnimationFrame(() => {
            if (item.kind === 'move') {
                item.el.style.transition = `transform ${durationMs}ms ${EASING}`;
                item.el.style.transform = 'translate(0, 0)';
            } else {
                item.el.style.transition = `opacity ${durationMs}ms ease-out, transform ${durationMs}ms ease-out`;
                item.el.style.opacity = '1';
                item.el.style.transform = 'scale(1)';
            }
            item.el.addEventListener('transitionend', finish);
            // Safety net: transitionend may not fire (interrupted, zero-delta, off-screen, …).
            setTimeout(finish, durationMs + 80);
        });
    });
}
