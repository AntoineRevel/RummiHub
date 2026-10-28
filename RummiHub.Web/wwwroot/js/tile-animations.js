// Tile animations — a whole-page FLIP pass that tells every move as a short, causal story.
//
// Whenever Blazor patches the page, run() measures every tile inside the [data-anim-scope]
// containers and diffs against the previous render. Matching runs at two levels:
//   1. Board sets ([data-anim-set]) are paired prev↔next by maximal multiset overlap of
//      their tile types (mirroring the server's BoardAlignment/BoardPacing); their common
//      tiles match occurrence-by-occurrence in set order — the set KEEPS them.
//   2. Every other tile claims its own old spot if it didn't move, else a slot of its type
//      freed in its own container, else the nearest freed slot anywhere (a play comes from
//      the play zone, a deselection goes back to its stack), else it is new and enters from
//      its scope's origin: [data-anim-enter-from] names a [data-anim-origin] element as it
//      stood BEFORE this render (the mover's chip for the board, the pool for the rack).
//      With no origin on screen, it pops in place.
//
// On the board ([data-anim-staged]) a tile at rest never covers another. A change is told
// step by step: one step per group of sets that change together — a set the move builds or
// touches, plus every set a tile passes to or from it (a tile taken from one set for two new
// ones links all three). Steps follow the reading order of their first set. Each is told in
// three acts, on that step's sets only:
//   1. Lift  — its board tiles changing set rise in place.
//   2. Room  — ONE glide: the board takes the layout it has once this step is done (its sets
//              open gaps or close up, the sets after them reflow; later steps' sets keep their
//              old shape) while the lifted tiles travel to hover just above their new gap.
//   3. Place — its tiles come in one after the other, set by set: the played ones fly out of
//              the play zone (or the mover's chip) into their open gap, the hovering ones drop.
// Then the next step. Played tiles wait in the play zone (or hidden in the chip) until their
// step. Each intermediate layout is measured on a hidden copy of the board. A beat follows
// acts 1 and 2, so each reads on its own; a step with nothing to lift or make room for (a new
// set laid at the end) is act 3 alone. Lifted and hovering tiles rest in the lane the board
// leaves between its rows, above their own (empty) slot. A board change arriving while
// another one plays waits for it to end — its animations hold their first keyframe meanwhile
// (movers on their old spot, newcomers hidden); only a runaway backlog fast-forwards.
//
// The hand (the rack, the play zone) answers clicks at once and is never queued behind
// the board. [data-anim-enter-only] scopes (the rack) only animate arrivals — internal
// reshuffles are instant. [data-anim-ignore] subtrees (the greyed-out placeholder of an
// emptied stack) are never measured.
//
// Each tile is measured in TWO frames: root-relative (flights between scopes) and
// scope-relative (did it really move inside its container?) — a section appearing above
// the board shifts nothing scope-relatively, so it animates nothing. Positions are
// TRANSFORM-FREE (offsetLeft/offsetTop), so hovers and running animations never pollute
// snapshots. First render, reconnects, resizes and hidden tabs just (re)snapshot.

// Tempo, pushed from the server (Rendering/BoardPacing.cs), which mirrors the board story
// to pace AI moves so they never land on top of a still-running animation.
const tempo = {
    moveMs: 350,              // a hand tile gliding between the rack and the play zone
    popMs: 300,               // a newcomer with no origin on screen popping in
    liftMs: 400,              // act 1: a step's board tiles changing set rising in place
    pauseMs: 300,             // beat after acts 1 and 2
    roomMs: 900,              // act 2: the board making room for the step, one glide
    dropMs: 600,              // act 3: one hovering tile dropping into its gap
    flightMs: 900,            // act 3: a played tile flying into its gap
    tileGapMs: 200,           // act 3: between two tiles of a step
    setGapMs: 300,            // between two sets of a step, and between two steps
};

const MIN_DELTA = 4;    // px — cross-scope flights below this just display
// An intra-scope move below this counts as "didn't move". Real moves span at least one
// tile (~50px); this absorbs the whole grid compacting a few px when a scrollbar appears,
// which would otherwise break stationary claims and let new tiles steal their slots.
const STILL_DELTA = 18;
const BLOCK_DELTA = 6;  // px — tiles changing set together this close fly as one block
const HAND_STAGGER_MS = 40; // a burst of picks (hint, clear) cascades instead of jumping
// px — how high a lifted or hovering tile rests above its slot. The board leaves a lane at
// least this tall between rows (SolutionComponent), so it never covers the row above.
const LIFT = 34;
const LIFT_SCALE = 1.06;
const LIFT_SHADOW = '0 12px 18px rgba(0, 0, 0, 0.28)';
// A board story due to start this late means the client fell behind (burst of updates):
// snap to the final layout instead of replaying history.
const MAX_BACKLOG_MS = 20000;
const FLIGHT_EASING = 'cubic-bezier(0.22, 0.9, 0.26, 1)';
const ROOM_EASING = 'cubic-bezier(0.45, 0, 0.25, 1)';
const LIFT_EASING = 'cubic-bezier(0.3, 0, 0.2, 1)';
const DROP_EASING = 'cubic-bezier(0.5, 0, 0.5, 1.4)'; // falls in, settles with a tiny bump

let last = null;         // previous snapshot — plain data, never old DOM nodes
let replayFrom = null;   // clean snapshot from BEFORE the last board story, for replays
let boardBusyUntil = 0;  // performance.now() at which the queued board story ends
const live = new Set();  // every animation not finished yet, for reset/fast-forward
const mover = new WeakMap(); // element → the animation moving it (at most one at a time)
const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');

// iOS Safari only applies :active (the tap feedback on tiles) when a touch listener exists.
document.addEventListener('touchstart', () => {}, { passive: true });

// A resize reflows the whole page; the next render must re-snapshot, not "animate" it.
window.addEventListener('resize', () => reset());

// A hidden tab renders no frames: finish everything so nothing replays as a ghost show
// when the player comes back (renders meanwhile just snapshot, see run()).
document.addEventListener('visibilitychange', () => {
    if (document.hidden) fastForward();
});

export function configure(timing) {
    for (const [key, value] of Object.entries(timing ?? {})) {
        if (key in tempo && value > 0) tempo[key] = value;
    }
}

export function reset() {
    for (const anim of [...live]) {
        try { anim.cancel(); } catch { /* element already gone */ }
    }
    boardBusyUntil = 0;
    last = null;
    replayFrom = null;
}

// Snaps every pending animation to its end state. The DOM already holds the final layout —
// this just reveals it at once.
function fastForward() {
    for (const anim of [...live]) {
        try { anim.finish(); } catch { /* element already gone */ }
    }
    boardBusyUntil = 0;
}

const bucket = (map, key) => {
    let list = map.get(key);
    if (!list) map.set(key, list = []);
    return list;
};

const isStill = (dx, dy, threshold) => Math.abs(dx) < threshold && Math.abs(dy) < threshold;

// Position of `el` relative to `root`, ignoring CSS transforms. Offsets are measured from
// the offsetParent's padding edge, so each positioned ancestor's border (the sticky rack's)
// is added back — otherwise flights out of it would start a couple of px off.
function positionIn(root, el) {
    let x = 0, y = 0, node = el;
    while (node && node !== root && root.contains(node)) {
        x += node.offsetLeft;
        y += node.offsetTop;
        node = node.offsetParent;
        if (node && node !== root && root.contains(node)) {
            x += node.clientLeft;
            y += node.clientTop;
        }
    }
    for (let p = el.parentElement; p && p !== root; p = p.parentElement) {
        x -= p.scrollLeft;
        y -= p.scrollTop;
    }
    return { x, y };
}

function collect(root) {
    // Origins are kept by their centre: newcomers come out of the middle of the chip.
    const origins = {};
    for (const el of root.querySelectorAll('[data-anim-origin]')) {
        if (!(el.offsetWidth || el.offsetHeight)) continue;
        const { x, y } = positionIn(root, el);
        origins[el.dataset.animOrigin] = { x: x + el.offsetWidth / 2, y: y + el.offsetHeight / 2 };
    }

    const tiles = [], sets = [], setIndexOf = new Map(), scopes = {};
    for (const scopeEl of root.querySelectorAll('[data-anim-scope]')) {
        const scope = scopeEl.dataset.animScope || 'default';
        scopes[scope] = { el: scopeEl, h: scopeEl.offsetHeight };
        const enterOnly = scopeEl.hasAttribute('data-anim-enter-only');
        const staged = scopeEl.hasAttribute('data-anim-staged');
        const enterFrom = scopeEl.dataset.animEnterFrom ?? null;
        const origin = positionIn(root, scopeEl);
        for (const el of scopeEl.querySelectorAll('[data-tile-type]')) {
            if (!(el.offsetWidth || el.offsetHeight)) continue;
            if (el.closest('[data-anim-ignore]')) continue;
            const setEl = el.closest('[data-anim-set]');
            let setIndex = -1;
            if (setEl) {
                if (!setIndexOf.has(setEl)) { setIndexOf.set(setEl, sets.length); sets.push({ el: setEl, tiles: [] }); }
                setIndex = setIndexOf.get(setEl);
            }
            const { x, y } = positionIn(root, el);
            const tile = { el, type: el.dataset.tileType, scope, enterOnly, staged, enterFrom, setIndex,
                           order: tiles.length, x, y, sx: x - origin.x, sy: y - origin.y,
                           w: el.offsetWidth, h: el.offsetHeight };
            tiles.push(tile);
            if (setIndex >= 0) sets[setIndex].tiles.push(tile);
        }
    }
    return { tiles, sets, origins, scopes };
}

// Plain-data copy of a snapshot: fresh tile objects (no DOM refs, no matching flags),
// sets rebuilt to point at the copies.
function toPlainData(snap) {
    const tiles = snap.tiles.map(({ type, scope, setIndex, x, y, sx, sy }) =>
        ({ type, scope, setIndex, x, y, sx, sy }));
    const sets = snap.sets.map(() => ({ tiles: [] }));
    for (const tile of tiles) if (tile.setIndex >= 0) sets[tile.setIndex].tiles.push(tile);
    const scopes = Object.fromEntries(Object.entries(snap.scopes ?? {}).map(([k, v]) => [k, { h: v.h }]));
    return { tiles, sets, origins: { ...snap.origins }, scopes };
}

function store(snap) {
    last = toPlainData(snap);
}

// Multiset overlap of two tile lists, by type, occurrence-aware.
function overlap(aTiles, bTiles) {
    const counts = new Map();
    for (const t of aTiles) counts.set(t.type, (counts.get(t.type) ?? 0) + 1);
    let shared = 0;
    for (const t of bTiles) {
        const count = counts.get(t.type) ?? 0;
        if (count > 0) { counts.set(t.type, count - 1); shared++; }
    }
    return shared;
}

// Level 1 — pair board sets prev↔next greedily by overlap (ties go to the nearest), then
// match their common tiles occurrence-by-occurrence in set order: the set keeps them.
function matchSets(snap, prev) {
    const taken = new Set();
    for (const prevSet of prev.sets) {
        let best = null, bestShared = 0, bestDist = Infinity;
        for (const set of snap.sets) {
            if (taken.has(set)) continue;
            const shared = overlap(prevSet.tiles, set.tiles);
            if (shared === 0) continue;
            const dist = (set.tiles[0].x - prevSet.tiles[0].x) ** 2
                       + (set.tiles[0].y - prevSet.tiles[0].y) ** 2;
            if (shared > bestShared || (shared === bestShared && dist < bestDist)) {
                best = set; bestShared = shared; bestDist = dist;
            }
        }
        if (!best) continue;
        taken.add(best);
        const pool = new Map();
        for (const t of prevSet.tiles) bucket(pool, t.type).push(t);
        for (const tile of best.tiles) {
            const from = pool.get(tile.type)?.shift();
            if (from) { tile.from = from; tile.kept = true; from.used = true; }
        }
    }
}

// Level 2 — remaining tiles: claim your own old spot if you didn't move (same scope,
// scope-relative); else you moved within your container (a rack reshuffle, a tile changing
// set) if it freed a slot of your type; else you came from the nearest freed slot anywhere
// (a play, a deselection); else you're new. Container first: when I play one of my two
// 9s, the 9 left in my rack must not swap places with the one flying to the board.
function matchTiles(snap, prev) {
    const free = new Map();
    for (const t of prev.tiles) if (!t.used) bucket(free, t.type).push(t);

    let pending = [];
    for (const tile of snap.tiles) {
        if (tile.from) continue;
        const slots = free.get(tile.type) ?? [];
        const i = slots.findIndex(o => o.scope === tile.scope
            && isStill(o.sx - tile.sx, o.sy - tile.sy, STILL_DELTA));
        if (i >= 0) tile.from = slots.splice(i, 1)[0];
        else pending.push(tile);
    }

    for (const sameScope of [true, false]) {
        pending = pending.filter(tile => {
            const slots = free.get(tile.type) ?? [];
            let best = -1, bestDist = Infinity;
            for (let i = 0; i < slots.length; i++) {
                if (sameScope && slots[i].scope !== tile.scope) continue;
                const dist = (slots[i].x - tile.x) ** 2 + (slots[i].y - tile.y) ** 2;
                if (dist < bestDist) { bestDist = dist; best = i; }
            }
            if (best < 0) return true;
            tile.from = slots.splice(best, 1)[0];
            return false;
        });
    }
}

// Intra-scope moves animate their scope-relative delta (immune to sections above growing
// or shrinking); cross-scope flights need the root-relative one.
function deltaOf(tile) {
    return tile.from.scope === tile.scope
        ? { dx: tile.from.sx - tile.sx, dy: tile.from.sy - tile.sy }
        : { dx: tile.from.x - tile.x, dy: tile.from.y - tile.y };
}

// Board tiles leaving the same set for the same set, along the same delta, fly as a block.
function joinBlock(blocks, item) {
    const { tile, dx, dy } = item;
    const fromSet = tile.from.setIndex, toSet = tile.setIndex;
    const block = blocks.find(b => b.fromSet === fromSet && b.toSet === toSet
        && Math.abs(b.dx - dx) <= BLOCK_DELTA && Math.abs(b.dy - dy) <= BLOCK_DELTA);
    if (block) block.items.push(item);
    else blocks.push({ fromSet, toSet, dx, dy, items: [item] });
}

// Sorts every tile that has to move: into the board's story (tiles entering the board,
// board tiles changing set, tiles making room) or the hand's immediate moves.
function diff(snap, prev) {
    matchSets(snap, prev);
    matchTiles(snap, prev);

    const story = { entering: [], migrating: [], room: [] };
    const hand = [];
    for (const tile of snap.tiles) {
        if (!tile.from) {
            const item = { tile, origin: prev.origins[tile.enterFrom] ?? null };
            if (tile.staged) story.entering.push({ entering: true, items: [item] });
            else hand.push(item);
            continue;
        }
        const { dx, dy } = deltaOf(tile);
        const crossScope = tile.from.scope !== tile.scope;
        if (isStill(dx, dy, crossScope ? MIN_DELTA : STILL_DELTA)) continue;
        const item = { tile, dx, dy };
        if (!tile.staged) {
            if (crossScope || !tile.enterOnly) hand.push(item); // rack reshuffles stay instant
        } else if (crossScope) {
            story.entering.push({ entering: true, items: [item] }); // played from the hand
        } else if (tile.kept) {
            story.room.push(item); // its set moved, grew or shrank around it
        } else {
            joinBlock(story.migrating, item); // a board tile changing set
        }
    }
    return { story, hand };
}

// Where a moving tile starts, relative to its final spot: its old spot, else the centre
// of its origin (shrunk, transparent), else — nothing to come from — right in its slot,
// popping in.
function startOf(item) {
    if (item.dx !== undefined) return { x: item.dx, y: item.dy, scale: 1, opacity: 1 };
    const { tile, origin } = item;
    if (origin) {
        return { x: origin.x - (tile.x + tile.w / 2), y: origin.y - (tile.y + tile.h / 2),
                 scale: 0.5, opacity: 0 };
    }
    return { x: 0, y: 0, scale: 0.6, opacity: 0 };
}

// Keyframes of a tile flying straight into its slot, swelling a little on the way like a
// tile carried over the table. Properties a keyframe leaves out (the shadow at both ends)
// fall back to the tile's own style, highlight included.
function placeFrames(start, { flight }) {
    const from = { offset: 0, translate: `${start.x}px ${start.y}px`, scale: start.scale,
                   easing: FLIGHT_EASING };
    if (start.opacity < 1) from.opacity = start.opacity;
    return [
        from,
        { offset: 0.4, scale: LIFT_SCALE, boxShadow: LIFT_SHADOW, opacity: 1, easing: 'ease-in-out' },
        { offset: 1, translate: '0px 0px', scale: 1 },
    ];
}

// Keyframes from timed poses ({ t, x?, y?, scale, opacity?, shadow?, easing? }), t in ms
// from the first one. A pose without x/y leaves the translation to its neighbours (a tile
// swelling mid-flight); one without a shadow leaves the shadow to them.
function timeline(poses) {
    const t0 = poses[0].t, total = Math.max(1, poses.at(-1).t - t0);
    return poses.map(({ t, x, y, scale, opacity, shadow, easing }) => {
        const frame = { offset: (t - t0) / total, scale };
        if (x !== undefined) frame.translate = `${x}px ${y}px`;
        if (opacity !== undefined) frame.opacity = opacity;
        if (shadow) frame.boxShadow = shadow;
        if (easing) frame.easing = easing;
        return frame;
    });
}

// Runs one animation on `el`, replacing whatever was moving it. Until `delay` it holds its
// first keyframe (fill: backwards) — movers stay on their old spot, newcomers hidden.
function move(el, keyframes, { delay = 0, duration, elevate = false, lands = false }) {
    mover.get(el)?.cancel();
    if (lands) delete el.dataset.landed;
    el.style.zIndex = elevate ? '400' : '';
    const anim = el.animate(keyframes, { delay, duration, fill: 'backwards' });
    mover.set(el, anim);
    live.add(anim);
    anim.finished.then(() => {
        // The played-tile highlight pulses once the tile has landed (TileComponent).
        if (lands && mover.get(el) === anim) el.dataset.landed = '';
    }, () => { /* cancelled: replaced by a newer move, or reset */ }).finally(() => {
        live.delete(anim);
        if (mover.get(el) !== anim) return; // a newer move took this element over
        mover.delete(el);
        el.style.zIndex = '';
    });
}

// The hand answers at once: picks fly to the play zone, deselections back to their stack,
// a drawn tile comes out of the pool (and pulses once it lands, being highlighted).
// A container a tile flew out of closes its gap only once that tile has landed: first the
// tile leaves, the hole shows, then its neighbours slide in — not the other way round.
function playHand(hand, snap, prev) {
    const departedAt = new Map(); // scope → when its last departing tile lands
    hand.forEach((item, i) => {
        if (item.dx === undefined || item.tile.from.scope === item.tile.scope) return;
        const lands = i * HAND_STAGGER_MS + tempo.moveMs;
        const scope = item.tile.from.scope;
        departedAt.set(scope, Math.max(departedAt.get(scope) ?? 0, lands));
    });
    holdHeights(departedAt, snap, prev);
    hand.forEach((item, i) => {
        const delay = i * HAND_STAGGER_MS;
        if (item.dx === undefined) {
            const flight = item.origin ? tempo.flightMs : tempo.popMs;
            move(item.tile.el, placeFrames(startOf(item), { flight }),
                 { delay, duration: flight, elevate: true, lands: true });
        } else if (item.tile.from.scope !== item.tile.scope) {
            move(item.tile.el, placeFrames(startOf(item), { flight: tempo.moveMs }),
                 { delay, duration: tempo.moveMs, elevate: true });
        } else {
            // Held on its old spot (fill: backwards) until the gap it closes is empty.
            const closesGap = departedAt.get(item.tile.scope) ?? 0;
            move(item.tile.el, [
                { translate: `${item.dx}px ${item.dy}px`, easing: FLIGHT_EASING },
                { translate: '0px 0px' },
            ], { delay: Math.max(delay, closesGap), duration: tempo.moveMs });
        }
    });
}

// A container a tile left that lost a row keeps its old height until its tiles have closed
// the gap, then folds down: otherwise the frame would shrink at once, leaving the tiles still
// waiting to move up sticking out of it. Heights are border-box (Bootstrap's reset).
function holdHeights(departedAt, snap, prev) {
    for (const [scope, closesGap] of departedAt) {
        const now = snap.scopes[scope], before = prev.scopes?.[scope];
        if (!now || !before || before.h - now.h < 1) continue;
        mover.get(now.el)?.cancel();
        const anim = now.el.animate([
            { minHeight: `${before.h}px`, easing: FLIGHT_EASING },
            { minHeight: `${now.h}px` },
        ], { delay: closesGap + tempo.moveMs, duration: tempo.moveMs, fill: 'backwards' });
        mover.set(now.el, anim);
        live.add(anim);
        anim.finished.catch(() => { /* cancelled */ }).finally(() => {
            live.delete(anim);
            if (mover.get(now.el) === anim) mover.delete(now.el);
        });
    }
}

// Splits the board story into steps (see the top of this file): union-find over the next
// board's sets (0..m-1) and the previous board's (m..m+n-1), linking a set to the previous set
// it keeps tiles of, and to every previous set a tile of it comes from. A group with a set
// that gains or loses tiles is a step; steps follow the reading order of their first set.
function stepsOf(snap, prev) {
    const m = snap.sets.length, n = prev.sets.length;
    const up = [...Array(m + n).keys()];
    const find = k => (up[k] === k ? k : (up[k] = find(up[k])));
    const join = (a, b) => { up[find(a)] = find(b); };

    const board = snap.tiles.filter(tile => tile.staged && tile.setIndex >= 0);
    const fromBoard = tile => tile.from && tile.from.scope === tile.scope && tile.from.setIndex >= 0;
    const changed = new Array(m).fill(false), kept = new Array(m).fill(0), paired = new Array(m).fill(-1);
    for (const tile of board) {
        if (tile.kept) {
            kept[tile.setIndex]++;
            paired[tile.setIndex] = tile.from.setIndex;
        } else {
            changed[tile.setIndex] = true;
        }
        if (fromBoard(tile)) join(tile.setIndex, m + tile.from.setIndex);
    }
    for (let j = 0; j < m; j++) {
        if (paired[j] >= 0 && prev.sets[paired[j]].tiles.length !== kept[j]) changed[j] = true;
    }

    const stepOf = new Map(); // group root → step index
    for (let j = 0; j < m; j++) {
        if (changed[j] && !stepOf.has(find(j))) stepOf.set(find(j), stepOf.size);
    }

    // Board tiles still in their previous set, by previous set, in their old order.
    const prevOrder = new Map();
    prev.sets.forEach(set => set.tiles.forEach((tile, i) => prevOrder.set(tile, i)));
    const stillIn = Array.from({ length: n }, () => []);
    for (const tile of board) if (fromBoard(tile)) stillIn[tile.from.setIndex].push(tile);
    for (const list of stillIn) list.sort((a, b) => prevOrder.get(a.from) - prevOrder.get(b.from));

    // The board once `done` steps are over: their sets (and unchanged ones) as they end up,
    // every later step's previous sets as they were, where its first set will stand.
    const layoutAfter = done => {
        const sets = [], shown = new Set();
        for (let j = 0; j < m; j++) {
            const group = find(j), step = stepOf.get(group);
            if (step === undefined || step < done) { sets.push(snap.sets[j].tiles); continue; }
            if (shown.has(group)) continue;
            shown.add(group);
            for (let i = 0; i < n; i++) {
                if (find(m + i) === group && stillIn[i].length) sets.push(stillIn[i]);
            }
        }
        return sets;
    };

    return { count: stepOf.size, stepOfSet: j => stepOf.get(find(j)), layoutAfter };
}

// Where each tile of `layout` (lists of next-board tiles, one per set) would stand, as an
// offset from its real (final) spot: the layout is built on a hidden copy of the board, made
// of copies of its own elements so it wraps exactly like it, measured, then removed before
// the browser paints. The observer is told to forget those mutations.
function measure(root, snap, layout) {
    const template = snap.sets[0]?.el;
    const real = template?.parentElement;
    const at = new Map();
    if (!real) return at;

    const ghost = real.cloneNode(false);
    Object.assign(ghost.style, {
        position: 'absolute', visibility: 'hidden', pointerEvents: 'none',
        left: `${real.offsetLeft}px`, top: `${real.offsetTop}px`, width: `${real.offsetWidth}px`,
    });
    const copies = new Map();
    for (const tiles of layout) {
        const setEl = template.cloneNode(false);
        const row = template.firstElementChild.cloneNode(false);
        setEl.append(row);
        for (const tile of tiles) {
            const copy = tile.el.cloneNode(true);
            copy.removeAttribute('style');
            row.append(copy);
            copies.set(tile, copy);
        }
        ghost.append(setEl);
    }
    real.parentElement.append(ghost);
    for (const [tile, copy] of copies) {
        const { x, y } = positionIn(root, copy);
        at.set(tile, { x: x - tile.x, y: y - tile.y });
    }
    ghost.remove();
    observer?.takeRecords();
    return at;
}

// Schedules the board story, step by step, to start when the story already playing ends.
// Returns false when nothing on the board moves.
function tell(story, snap, prev, root) {
    const units = [...story.entering, ...story.migrating];
    if (units.length === 0 && story.room.length === 0) return false;

    const now = performance.now();
    if (boardBusyUntil - now > MAX_BACKLOG_MS) fastForward();
    const wait = Math.max(0, boardBusyUntil - now);

    const steps = stepsOf(snap, prev);
    const count = Math.max(1, steps.count);
    const stepOfUnit = unit => steps.stepOfSet(unit.items[0].tile.setIndex) ?? 0;

    // offsets[k]: where every board tile stands once k steps are done, from its final spot.
    // Before any step: the old board. After the last: the real layout.
    const board = snap.tiles.filter(tile => tile.staged);
    const offsets = [new Map()];
    for (const tile of board) {
        if (tile.from && tile.from.scope === tile.scope) {
            const { dx, dy } = deltaOf(tile);
            offsets[0].set(tile, { x: dx, y: dy });
        }
    }
    for (let k = 1; k < count; k++) offsets.push(measure(root, snap, steps.layoutAfter(k)));
    offsets.push(new Map(board.map(tile => [tile, { x: 0, y: 0 }])));
    const moves = (tile, k) => {
        const a = offsets[k - 1].get(tile), b = offsets[k].get(tile);
        return a && b && !isStill(a.x - b.x, a.y - b.y, MIN_DELTA);
    };

    // Each step's timing: lift, room, then its units one after the other, set by set.
    const plan = [];
    let t = 0;
    for (let k = 1; k <= count; k++) {
        const mine = units.filter(unit => stepOfUnit(unit) === k - 1)
            .sort((a, b) => a.items[0].tile.setIndex - b.items[0].tile.setIndex
                         || a.items[0].tile.order - b.items[0].tile.order);
        const lifting = mine.some(unit => unit.entering !== true);
        const roomStart = t + (lifting ? tempo.liftMs + tempo.pauseMs : 0);
        const room = lifting || board.some(tile => moves(tile, k));
        let at = room ? roomStart + tempo.roomMs + tempo.pauseMs : t, end = at;
        mine.forEach((unit, i) => {
            if (i > 0) {
                const sameSet = unit.items[0].tile.setIndex === mine[i - 1].items[0].tile.setIndex;
                at += tempo.tileGapMs + (sameSet ? 0 : tempo.setGapMs);
            }
            unit.at = at;
            end = Math.max(end, at + (unit.entering === true ? tempo.flightMs : tempo.dropMs));
        });
        plan.push({ start: t, roomStart, roomEnd: roomStart + tempo.roomMs, room, end });
        t = end + tempo.setGapMs;
    }
    const storyEnd = plan.at(-1).end;

    // Every board tile follows the steps: it rests, glides when its step (or a reflow) moves
    // it, rises and drops when it changes set. A placed tile comes in at its unit's time.
    const unitOf = new Map();
    for (const unit of units) for (const item of unit.items) unitOf.set(item.tile, { unit, item });
    const rest = '0 4px 6px rgba(0, 0, 0, 0.1)'; // TileComponent's own shadow
    for (const tile of board) {
        const placed = unitOf.get(tile);
        const entering = placed?.unit.entering === true;
        const joinStep = placed ? stepOfUnit(placed.unit) + 1 : 0;
        const poses = [];
        let lifted = false;
        const pose = (time, offset, extra = {}) => poses.push({ t: time, x: offset.x, y: offset.y, scale: 1, ...extra });

        if (entering) {
            // Waits in the play zone (or hidden in the chip) until its turn, then flies into
            // its gap as the board stands after its step. Popping in, with no origin on screen.
            const slot = offsets[joinStep].get(tile);
            const start = startOf(placed.item);
            const from = placed.item.dx === undefined && !placed.item.origin
                ? { ...start, x: slot.x, y: slot.y } : start;
            const at = placed.unit.at, flight = tempo.flightMs;
            poses.push({ t: at, ...from, easing: FLIGHT_EASING });
            poses.push({ t: at + flight * 0.4, scale: LIFT_SCALE, opacity: 1, shadow: LIFT_SHADOW, easing: 'ease-in-out' });
            pose(at + flight, slot); // its own style from here: the played-tile highlight
        } else if (offsets[0].has(tile)) {
            pose(0, offsets[0].get(tile));
        } else {
            continue;
        }

        for (let k = Math.max(1, joinStep + (entering ? 1 : 0)); k <= count; k++) {
            const step = plan[k - 1], before = offsets[k - 1].get(tile), after = offsets[k].get(tile);
            if (!entering && placed && k === joinStep) {
                const up = o => ({ x: o.x, y: o.y - LIFT });
                const high = { scale: LIFT_SCALE, shadow: LIFT_SHADOW };
                pose(step.start, before, { easing: LIFT_EASING });
                pose(step.start + tempo.liftMs, up(before), { ...high, easing: 'linear' });
                pose(step.roomStart, up(before), { ...high, easing: ROOM_EASING });
                pose(step.roomEnd, up(after), { ...high, easing: 'linear' });
                pose(placed.unit.at, up(after), { ...high, easing: DROP_EASING });
                pose(placed.unit.at + tempo.dropMs, after, { shadow: rest });
                lifted = true;
            } else if (step.room && moves(tile, k)) {
                pose(step.roomStart, before, { easing: ROOM_EASING, ...(lifted ? { shadow: rest } : {}) });
                pose(step.roomEnd, after, lifted ? { shadow: rest } : {});
            }
        }
        if (poses.length < 2) {
            // No step moves it beyond a few px: snap, or glide with the first step.
            if (isStill(poses[0].x, poses[0].y, STILL_DELTA)) continue;
            poses[0].t = plan[0].roomStart;
            poses[0].easing = ROOM_EASING;
            pose(plan[0].roomEnd, { x: 0, y: 0 });
        }
        const t0 = poses[0].t;
        move(tile.el, timeline(poses), {
            delay: wait + t0, duration: Math.max(1, poses.at(-1).t - t0),
            elevate: placed !== undefined, lands: entering,
        });
    }

    boardBusyUntil = now + wait + storyEnd;
    return true;
}

// Milliseconds until the board stops moving (0 when idle): the page waits for it before
// showing whose turn it is, so an opponent's move plays out before my side changes.
const boardIdleIn = () => Math.max(0, Math.ceil(boardBusyUntil - performance.now()));

// Runs the FLIP pass right when Blazor patches the DOM: a MutationObserver callback is a
// microtask, so it runs before the browser paints the new layout. Waiting for the server's
// after-render call instead costs a network round trip, during which the final layout is
// already on screen: tiles jump to their new spot, then back to the old one to glide.
// Our own writes (inline z-index, data-landed) are not watched, so they never re-trigger it.
let observer = null;

function watch() {
    observer = new MutationObserver(() => run());
    observer.observe(document.body, {
        childList: true, subtree: true, characterData: true,
        attributes: true, attributeFilter: ['class', 'data-tile-type'],
    });
}

// Called by the server after every render: the observer has normally animated that render
// already; this only catches up on a change it has not delivered yet, then says how long
// the board keeps moving.
export function animate() {
    if (!observer) {
        watch();
        run();
    } else if (observer.takeRecords().length > 0) {
        run();
    }
    return boardIdleIn();
}

function run() {
    const root = document.querySelector('.game-container');
    if (!root) { last = null; return; }

    const snap = collect(root);

    // No usable snapshot (first render, reconnect, resize, loading screen → game), or
    // nobody watching: show everything as-is; animations start from the NEXT change.
    if (!last || last.tiles.length === 0 || reducedMotion.matches || document.hidden) {
        store(snap);
        return;
    }

    // Kept clean (matching flags prev) so the board story can be replayed on demand.
    const prev = last;
    const prevClean = toPlainData(prev);
    const { story, hand } = diff(snap, prev);
    store(snap);

    // Board first: a runaway backlog fast-forwards in tell(), which must not swallow the
    // hand's fresh moves.
    if (tell(story, snap, prev, root)) replayFrom = prevClean;
    playHand(hand, snap, prevClean);
}

// True once a board story has been played and can be run again — a review, an AI move, a win.
export function canReplay() {
    return replayFrom !== null && !reducedMotion.matches;
}

// Replays the most recent board story: snap whatever is pending to its end, re-measure the
// (unchanged) DOM, and diff it against the snapshot saved from before that story.
// Positions are transform-free, so re-measuring mid/post animation is safe, and
// replayFrom is cloned per run — replay as many times as you like.
// Returns how many milliseconds the board keeps moving, or 0 when there is nothing to
// replay (fresh page, resize, reduced motion).
export function replayLast() {
    const root = document.querySelector('.game-container');
    if (!root || !replayFrom || reducedMotion.matches) return 0;

    fastForward();
    const snap = collect(root);
    const prev = toPlainData(replayFrom);
    const { story } = diff(snap, prev);
    store(snap);
    // The page holds the next move for this long, so a replay is not cancelled mid-flight.
    return tell(story, snap, prev, root) ? Math.max(1, boardIdleIn()) : 0;
}
