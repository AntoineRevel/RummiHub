// Umami events (RummiHub.Web/Services/Statistics.cs). The tracker loads with defer and may be
// blocked: until it is there, an event is dropped rather than queued.
//
// Each browser keeps an anonymous id in a first-party cookie (VisitorIds in GameJournal.cs):
// random, it says nothing about the person. The lobby hands it to the server with each game
// created or joined (game journal, /admin/statistiques), and Umami gets it as the distinct id
// (App.razor renders it on the tracker), so one player counts once across days and networks.
// The cookie lasts 13 months from the first visit and is not renewed.
(() => {
    const cookie = "rummi-visiteur";
    const maxAge = 395 * 24 * 3600;

    function read() {
        const match = document.cookie.match(new RegExp("(?:^|; )" + cookie + "=([A-Za-z0-9_-]{8,64})(?:;|$)"));
        return match ? match[1] : null;
    }

    function create() {
        const id = crypto.randomUUID
            ? crypto.randomUUID().replaceAll("-", "")
            : Array.from(crypto.getRandomValues(new Uint8Array(16)), b => b.toString(16).padStart(2, "0")).join("");
        const secure = location.protocol === "https:" ? "; Secure" : "";
        document.cookie = `${cookie}=${id}; Path=/; Max-Age=${maxAge}; SameSite=Lax${secure}`;
        return id;
    }

    const existing = read();
    const id = existing ?? create();

    // First visit: the page was rendered before the cookie existed, so the tracker has no
    // distinct id yet. Give it one once it has loaded (deferred scripts run before "load").
    if (!existing)
        addEventListener("load", () => window.umami?.identify?.(id));

    window.rummiStats = {
        track(name, data) {
            window.umami?.track(name, data ?? undefined);
        },

        // Read again: another tab may have created the cookie since this page loaded.
        visitor() {
            return read() ?? id;
        }
    };
})();
