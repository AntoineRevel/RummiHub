// FLIP (First, Last, Invert, Play) animation for the RummiHub board.
//
// The board is re-rendered wholesale by Blazor on every move, so tiles would "teleport"
// to their new layout. To make them glide like real tiles:
//   1. snapshot()  — called BEFORE the new board renders: record each tile's screen rect,
//                    keyed by its stable [data-flip-id].
//   2. play()      — called AFTER the new board has rendered: for each tile that existed
//                    before, invert it back to its old position, then transition it to its
//                    natural (new) position. Tiles with no "before" rect are entering tiles
//                    (played from the hand) and simply fade + zoom in.
//
// Inline styles set here are not tracked by Blazor's diff (the .tile root has no style
// attribute), so they survive intermediate re-renders and are cleared after each animation.

const FLIP = {
    durationMs: 700,                              // glissement lisible
    easing: 'cubic-bezier(0.22, 1, 0.36, 1)',    // départ vif, arrivée douce
    enterDelayMs: 250,                           // les nouvelles tuiles attendent la fin du réarrangement
    enterDurationMs: 400,
    staggerMsPerTile: 12,                        // léger décalage entre tuiles
    maxStaggerMs: 180,
};

// Map<flipId, {left, top}> captured by the most recent snapshot(), or null.
let snapshotRects = null;
// Bumped on every snapshot so stale cleanup timers never clobber a fresh animation.
let generation = 0;

function prefersReducedMotion() {
    return window.matchMedia
        && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

function boardTiles() {
    return Array.from(document.querySelectorAll('[data-flip-id]'));
}

function clearInlineAnim(el) {
    el.style.transition = '';
    el.style.transform = '';
    el.style.opacity = '';
}

// Record the resting position of every board tile. Any in-flight animation is settled
// first so we always measure the natural layout, even on rapid successive moves.
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
    }
    if (tiles.length) void tiles[0].getBoundingClientRect(); // force reflow

    snapshotRects = new Map();
    for (const el of tiles) {
        const id = el.getAttribute('data-flip-id');
        const r = el.getBoundingClientRect();
        snapshotRects.set(id, { left: r.left, top: r.top });
    }
}

// Animate the new board from the snapshotted positions.
export function play() {
    if (prefersReducedMotion() || !snapshotRects) {
        snapshotRects = null;
        return;
    }

    const prev = snapshotRects;
    snapshotRects = null;
    const gen = generation;

    const tiles = boardTiles();
    const moves = [];
    const enters = [];

    for (const el of tiles) {
        const id = el.getAttribute('data-flip-id');
        const before = prev.get(id);
        const after = el.getBoundingClientRect();
        if (before) {
            const dx = before.left - after.left;
            const dy = before.top - after.top;
            if (dx !== 0 || dy !== 0) moves.push({ el, dx, dy, dist: Math.hypot(dx, dy) });
        } else {
            enters.push(el);
        }
    }

    // Invert: snap moved tiles back to their old position (no transition yet).
    for (const m of moves) {
        m.el.style.transition = 'none';
        m.el.style.transform = `translate(${m.dx}px, ${m.dy}px)`;
    }
    // Entering tiles start hidden and slightly shrunk.
    for (const el of enters) {
        el.style.transition = 'none';
        el.style.opacity = '0';
        el.style.transform = 'scale(0.85)';
    }

    if (tiles.length) void tiles[0].getBoundingClientRect(); // commit the inverted state

    requestAnimationFrame(() => {
        // Stagger by travel distance so a complex rearrangement stays readable.
        moves.sort((a, b) => a.dist - b.dist);
        moves.forEach((m, i) => {
            const delay = Math.min(i * FLIP.staggerMsPerTile, FLIP.maxStaggerMs);
            m.el.style.transition = `transform ${FLIP.durationMs}ms ${FLIP.easing} ${delay}ms`;
            m.el.style.transform = 'translate(0, 0)';
            scheduleCleanup(m.el, FLIP.durationMs + delay, gen);
        });

        for (const el of enters) {
            const t = `${FLIP.enterDurationMs}ms ease-out ${FLIP.enterDelayMs}ms`;
            el.style.transition = `opacity ${t}, transform ${t}`;
            el.style.opacity = '1';
            el.style.transform = 'scale(1)';
            scheduleCleanup(el, FLIP.enterDurationMs + FLIP.enterDelayMs, gen);
        }
    });
}

function scheduleCleanup(el, afterMs, gen) {
    setTimeout(() => {
        if (gen !== generation) return; // a newer move took over; leave it alone
        clearInlineAnim(el);
    }, afterMs + 50);
}
