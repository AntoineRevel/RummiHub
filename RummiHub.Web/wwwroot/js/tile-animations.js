// Tile animations — a whole-page FLIP pass, replayed as a SEQUENCE of steps.
//
// After every Blazor render, animate() measures every tile inside the [data-anim-scope]
// containers and diffs against the previous render. Matching runs at two levels:
//   1. Board sets ([data-anim-set]) are paired prev↔next by maximal multiset overlap of
//      their tile types (mirroring the server's BoardAlignment/BoardPacing); common tiles
//      match occurrence-by-occurrence in set order — exact origins, old slots consumed.
//   2. Every other tile claims its own old spot if it didn't move, else flies from the
//      nearest freed slot (plays fly hand → board, deselections fly back to their stack),
//      else it's new and pops in.
//
// A step is the unit of sequencing: animable tiles of one PAIRED set sharing one delta are
// ONE step (the run/group glides as a block); every other mover or pop is a step of its
// own. Steps are released one per START_INTERVAL_MS — board rearrangements, then flights,
// then pops, each in DOM order — with NO duration cap: big rearrangements take their time.
// Steps from a NEW render join the END of the queue, so an in-flight sequence always
// finishes before the next transition plays: animations are built paused on their first
// keyframe (movers hold their old spot, newcomers stay hidden) and their deltas were
// measured against the previous snapshot, so waiting in line is safe. Only a runaway
// backlog (background-throttled tab, burst of updates) fast-forwards to the final layout.
// The rack ([data-anim-enter-only]) only pops newcomers (the draw) and glides cross-scope
// arrivals; internal reshuffles are instant. [data-anim-ignore] subtrees (the greyed-out
// placeholder of an emptied stack) are never measured.
//
// Each tile is measured in TWO frames: root-relative (flight deltas) and scope-relative
// (did it really move inside its container?) — a section appearing above the board shifts
// nothing scope-relatively, so it animates nothing. Positions are TRANSFORM-FREE
// (offsetLeft/offsetTop), so hovers and the running animations never pollute snapshots:
// a re-render without a real diff leaves the in-flight sequence untouched. First render,
// reconnects and window resizes just (re)snapshot — nothing animates from nothing.

let MOVE_MS = 350, POP_MS = 300, START_INTERVAL_MS = 350;
const MIN_DELTA = 4;    // px — cross-scope flights below this just display
// An intra-scope move below this counts as "didn't move". Real moves span at least one
// tile (~50px); this absorbs the whole grid compacting a few px when a scrollbar appears,
// which would otherwise break stationary claims and let new tiles steal their slots.
const STILL_DELTA = 18;
const BLOCK_DELTA = 6;  // px — same-set deltas this close glide together as one block
const MOVE_EASING = 'cubic-bezier(0.22, 0.9, 0.26, 1)';
const POP_EASING = 'cubic-bezier(0.34, 1.4, 0.64, 1)';

let last = null;      // previous snapshot — plain data, never old DOM nodes
let queue = [];       // steps waiting for release — one inner array of paused Animations per step
let running = [];     // every animation not yet finished (queued or playing), for reset/fast-forward
let pumping = false;  // single release loop draining the queue; new steps just join the tail
const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');

// A backlog this deep (~10s of steps) means the client fell behind (throttled background
// tab, burst of updates): snap everything to the final layout instead of replaying history.
const MAX_QUEUED_STEPS = 30;

// A resize reflows the whole page; the next render must re-snapshot, not "animate" it.
window.addEventListener('resize', () => reset());

// A hidden tab throttles timers and freezes rendering: finish everything instantly so the
// backlog never replays as a ghost show when the player comes back.
document.addEventListener('visibilitychange', () => {
    if (document.hidden) fastForward();
});

// Timing pushed from the server (Rendering/BoardPacing.cs), which paces AI moves so they
// never land on top of a still-running animation.
export function configure(moveMs, popMs, startIntervalMs) {
    if (moveMs > 0) MOVE_MS = moveMs;
    if (popMs > 0) POP_MS = popMs;
    if (startIntervalMs > 0) START_INTERVAL_MS = startIntervalMs;
}

export function reset() {
    queue = [];
    for (const anim of running) {
        try { anim.cancel(); } catch { /* element already gone */ }
    }
    running = [];
    last = null;
}

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

const bucket = (map, key) => {
    let list = map.get(key);
    if (!list) map.set(key, list = []);
    return list;
};

// Releases one queued step per START_INTERVAL_MS until the queue drains. A single loop
// survives across renders — animate() only ever appends, so a sequence in progress always
// finishes before the steps of the next transition start.
async function pump() {
    if (pumping) return;
    pumping = true;
    try {
        while (queue.length > 0) {
            for (const anim of queue.shift()) anim.play();
            await sleep(START_INTERVAL_MS);
        }
    } finally {
        pumping = false;
        running = running.filter(a => a.playState === 'running' || a.playState === 'paused');
    }
}

// Snaps every pending animation to its end state. The DOM already holds the final layout —
// this just reveals it at once instead of replaying a long backlog.
function fastForward() {
    queue = [];
    for (const anim of running) {
        try { anim.finish(); } catch { /* element already gone */ }
    }
    running = [];
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
    const tiles = [], sets = [], setIndexOf = new Map();
    for (const scopeEl of root.querySelectorAll('[data-anim-scope]')) {
        const scope = scopeEl.getAttribute('data-anim-scope') || 'default';
        const enterOnly = scopeEl.hasAttribute('data-anim-enter-only');
        const origin = positionIn(root, scopeEl);
        for (const el of scopeEl.querySelectorAll('[data-tile-type]')) {
            if (!(el.offsetWidth || el.offsetHeight)) continue;
            if (el.closest('[data-anim-ignore]')) continue;
            const setEl = el.closest('[data-anim-set]');
            let setIndex = -1;
            if (setEl) {
                if (!setIndexOf.has(setEl)) { setIndexOf.set(setEl, sets.length); sets.push({ tiles: [] }); }
                setIndex = setIndexOf.get(setEl);
            }
            const { x, y } = positionIn(root, el);
            const tile = { el, type: el.dataset.tileType, scope, enterOnly, setIndex,
                           x, y, sx: x - origin.x, sy: y - origin.y };
            tiles.push(tile);
            if (setIndex >= 0) sets[setIndex].tiles.push(tile);
        }
    }
    return { tiles, sets };
}

function store(snap) {
    const tiles = snap.tiles.map(({ type, scope, setIndex, x, y, sx, sy }) =>
        ({ type, scope, setIndex, x, y, sx, sy }));
    const sets = snap.sets.map(() => ({ tiles: [] }));
    for (const tile of tiles) if (tile.setIndex >= 0) sets[tile.setIndex].tiles.push(tile);
    last = { tiles, sets };
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
// match their common tiles occurrence-by-occurrence in set order: exact deltas, slots used.
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
        best.paired = true;
        const pool = new Map();
        for (const t of prevSet.tiles) bucket(pool, t.type).push(t);
        for (const tile of best.tiles) {
            const from = pool.get(tile.type)?.shift();
            if (from) { tile.from = from; from.used = true; }
        }
    }
}

// Level 2 — remaining tiles: claim your own old spot if you didn't move (same scope,
// scope-relative), else fly from the nearest freed slot, else you're new.
function matchTiles(snap, prev) {
    const free = new Map();
    for (const t of prev.tiles) if (!t.used) bucket(free, t.type).push(t);

    const pending = [];
    for (const tile of snap.tiles) {
        if (tile.from) continue;
        const slots = free.get(tile.type) ?? [];
        const i = slots.findIndex(o => o.scope === tile.scope
            && Math.abs(o.sx - tile.sx) < STILL_DELTA && Math.abs(o.sy - tile.sy) < STILL_DELTA);
        if (i >= 0) tile.from = slots.splice(i, 1)[0];
        else pending.push(tile);
    }

    for (const tile of pending) {
        const slots = free.get(tile.type) ?? [];
        let best = -1, bestDist = Infinity;
        for (let i = 0; i < slots.length; i++) {
            const dist = (slots[i].x - tile.x) ** 2 + (slots[i].y - tile.y) ** 2;
            if (dist < bestDist) { bestDist = dist; best = i; }
        }
        if (best >= 0) tile.from = slots.splice(best, 1)[0];
    }
}

// Intra-scope moves animate their scope-relative delta (immune to sections above growing
// or shrinking); cross-scope flights need the root-relative one.
function deltaOf(tile) {
    return tile.from.scope === tile.scope
        ? { dx: tile.from.sx - tile.sx, dy: tile.from.sy - tile.sy }
        : { dx: tile.from.x - tile.x, dy: tile.from.y - tile.y };
}

function buildSteps(snap) {
    const rearrangements = [], flights = [], pops = [];
    for (const tile of snap.tiles) {
        if (!tile.from) { pops.push({ items: [{ tile }] }); continue; }
        const { dx, dy } = deltaOf(tile);
        const still = tile.from.scope === tile.scope ? STILL_DELTA : MIN_DELTA;
        if (Math.abs(dx) < still && Math.abs(dy) < still) continue;
        if (tile.from.scope !== tile.scope) { flights.push({ items: [{ tile, dx, dy }] }); continue; }
        if (tile.enterOnly) continue; // rack-internal reshuffle: instant by design
        // Same-scope slide: joins its paired set's block when the deltas agree.
        const block = tile.setIndex >= 0 && snap.sets[tile.setIndex].paired
            && rearrangements.find(s => s.setIndex === tile.setIndex
                && Math.abs(s.dx - dx) <= BLOCK_DELTA && Math.abs(s.dy - dy) <= BLOCK_DELTA);
        if (block) block.items.push({ tile, dx, dy });
        else rearrangements.push({ setIndex: tile.setIndex, dx, dy, items: [{ tile, dx, dy }] });
    }
    // The board settles first, then played/deselected tiles fly in, then new tiles pop.
    return [...rearrangements, ...flights, ...pops];
}

function elevate(el, animation) {
    const prev = el.style.zIndex;
    el.style.zIndex = '400';
    animation.finished.catch(() => {}).finally(() => { el.style.zIndex = prev; });
}

// Build each animation PAUSED at its first keyframe: movers hold their old position and
// newcomers stay hidden until their step is released. No fill — natural layout wins after.
function makePaused({ tile, dx, dy }) {
    let anim;
    if (tile.from) {
        anim = tile.el.animate(
            [{ transform: `translate(${dx}px, ${dy}px)` }, { transform: 'translate(0, 0)' }],
            { duration: MOVE_MS, easing: MOVE_EASING });
        elevate(tile.el, anim);
    } else {
        anim = tile.el.animate(
            [{ opacity: 0, transform: 'translateY(-14px) scale(0.7)' }, { opacity: 1, transform: 'none' }],
            { duration: POP_MS, easing: POP_EASING });
    }
    anim.pause();
    return anim;
}

export async function animate() {
    const root = document.querySelector('.game-container');
    if (!root) { last = null; return; }

    const snap = collect(root);

    // No usable snapshot (first render, reconnect, resize, loading screen → game):
    // show everything as-is; animations start from the NEXT change.
    if (!last || last.tiles.length === 0 || reducedMotion.matches) { store(snap); return; }

    const prev = last;
    matchSets(snap, prev);
    matchTiles(snap, prev);
    const steps = buildSteps(snap);
    store(snap);

    // Nothing really moved (highlight, hint, banner…): leave any running sequence alone.
    if (steps.length === 0) return;

    // A real change QUEUES behind whatever sequence is still playing — never on top of it.
    for (const step of steps) {
        const anims = step.items.map(makePaused);
        queue.push(anims);
        running.push(...anims);
    }

    if (queue.length > MAX_QUEUED_STEPS) {
        fastForward();
        return;
    }

    void pump();
}
