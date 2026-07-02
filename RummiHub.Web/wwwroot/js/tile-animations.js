// Tile animations for the game — a FLIP pass played ONE TILE AT A TIME.
//
// After every Blazor render, `animate()` measures the position of each tile inside the
// containers marked [data-anim-scope] and compares it with the snapshot from the previous
// render. Tiles are matched by their [data-tile-type] (value+color) using a two-pass
// nearest-neighbour assignment: stationary tiles claim their own old spot first, so a tile
// played from the rack/selection can fly onto the board while an identical tile sitting still
// is never hijacked. Matched-but-moved tiles slide to their new spot; brand-new tiles pop in.
//
// The animations are played SEQUENTIALLY, in DOM reading order (board runs then groups, left to
// right), so at most one tile moves at a time and the move reads like a deliberate rearrangement.
// Each pending animation is created PAUSED at its first keyframe, so tiles waiting their turn
// stay at their old position (or hidden, for newcomers) until released. The board is aligned
// server-side, so unchanged sets keep their place — only genuinely moved or new tiles animate.
//
// Positions are measured TRANSFORM-FREE (offsetLeft/offsetTop), so hover effects and the running
// animations themselves never pollute the snapshots. A crucial consequence: a tile mid-animation
// still measures at its final layout spot, so an unrelated re-render (highlight, hint, rack…) sees
// "nothing moved" and leaves the in-flight sequence untouched.

let MOVE_MS = 350;
let POP_MS = 300;
const MIN_DELTA = 4; // px — ignore sub-pixel / hover noise
const MOVE_EASING = 'cubic-bezier(0.22, 0.9, 0.26, 1)';
const POP_EASING = 'cubic-bezier(0.34, 1.4, 0.64, 1)';

let lastPositions = null; // Map<tileType, Array<{x, y}>>
let generation = 0;       // bumped per real pass so a newer render abandons an in-flight sequence
let activeAnims = [];      // animations of the current sequence (paused until their turn)
const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');

// Per-tile durations, kept in sync with the server (RummiHub.Web/Rendering/BoardAnimation.cs),
// which uses the same numbers to pace AI moves until the animation finishes.
export function configure(moveMs, popMs) {
    if (moveMs > 0) MOVE_MS = moveMs;
    if (popMs > 0) POP_MS = popMs;
}

// Position of `el` relative to `root`, ignoring CSS transforms.
function positionIn(root, el) {
    let x = 0, y = 0, node = el;
    while (node && node !== root && root.contains(node)) {
        x += node.offsetLeft;
        y += node.offsetTop;
        node = node.offsetParent;
    }
    for (let p = el.parentElement; p && p !== root; p = p.parentElement) {
        x -= p.scrollLeft;
        y -= p.scrollTop;
    }
    return { x, y };
}

function collect(root) {
    const byType = new Map();
    const all = [];
    let order = 0;
    for (const scope of root.querySelectorAll('[data-anim-scope]')) {
        for (const el of scope.querySelectorAll('[data-tile-type]')) {
            if (!(el.offsetWidth || el.offsetHeight)) continue;
            const type = el.dataset.tileType;
            const pos = positionIn(root, el);
            const entry = { el, x: pos.x, y: pos.y, order: order++ };
            all.push(entry);
            let list = byType.get(type);
            if (!list) byType.set(type, list = []);
            list.push(entry);
        }
    }
    return { byType, all };
}

function storePositions(byType) {
    // Plain positions only — never keep references to old DOM nodes.
    lastPositions = new Map();
    for (const [type, entries] of byType)
        lastPositions.set(type, entries.map(({ x, y }) => ({ x, y })));
}

function cancelActive() {
    for (const anim of activeAnims) {
        try { anim.cancel(); } catch { /* already gone */ }
    }
    activeAnims = [];
}

function elevate(el, animation) {
    const prev = el.style.zIndex;
    el.style.zIndex = '400';
    animation.finished.catch(() => {}).finally(() => { el.style.zIndex = prev; });
}

// Build a PAUSED animation held at its old position (move) or hidden (newcomer). With no fill,
// it shows the first keyframe while paused, and reverts to the natural layout once finished.
function makePaused(entry) {
    let anim;
    if (entry.from) {
        const dx = entry.from.x - entry.x;
        const dy = entry.from.y - entry.y;
        anim = entry.el.animate(
            [{ transform: `translate(${dx}px, ${dy}px)` }, { transform: 'translate(0, 0)' }],
            { duration: MOVE_MS, easing: MOVE_EASING });
        elevate(entry.el, anim);
    } else {
        anim = entry.el.animate(
            [{ opacity: 0, transform: 'translateY(-14px) scale(0.7)' }, { opacity: 1, transform: 'none' }],
            { duration: POP_MS, easing: POP_EASING });
    }
    anim.pause();
    return anim;
}

export async function animate() {
    const root = document.querySelector('.game-container');
    if (!root) { lastPositions = null; return; }

    const { byType, all } = collect(root);

    if (!lastPositions || reducedMotion.matches) {
        storePositions(byType);
        return;
    }

    // Decide, per tile type, which tiles moved (and from where) and which are new.
    for (const [type, entries] of byType) {
        const olds = (lastPositions.get(type) ?? []).slice();

        // Pass 1 — tiles that did not move claim their own old position.
        const pending = [];
        for (const entry of entries) {
            const i = olds.findIndex(o =>
                Math.abs(o.x - entry.x) < MIN_DELTA && Math.abs(o.y - entry.y) < MIN_DELTA);
            if (i >= 0) olds.splice(i, 1);
            else pending.push(entry);
        }

        // Pass 2 — remaining tiles fly from the nearest freed old position; leftovers are new.
        for (const entry of pending) {
            let best = -1, bestDist = Infinity;
            for (let i = 0; i < olds.length; i++) {
                const d = (olds[i].x - entry.x) ** 2 + (olds[i].y - entry.y) ** 2;
                if (d < bestDist) { bestDist = d; best = i; }
            }
            if (best >= 0) entry.from = olds.splice(best, 1)[0];
            else entry.newcomer = true;
        }
    }

    const sequence = all.filter(e => e.from || e.newcomer);
    storePositions(byType);

    // Nothing actually moved (e.g. an unrelated re-render): leave any running sequence alone.
    if (sequence.length === 0) return;

    // A real change — supersede any sequence still running from a previous pass.
    cancelActive();
    const gen = ++generation;
    activeAnims = sequence.map(makePaused);

    // Release one tile at a time, in DOM reading order.
    for (const anim of activeAnims) {
        if (gen !== generation) return; // a newer render took over (it cancelled these already)
        anim.play();
        await anim.finished.catch(() => {});
    }
}

export function reset() {
    cancelActive();
    lastPositions = null;
    generation++;
}
