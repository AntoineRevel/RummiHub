// Umami events (RummiHub.Web/Services/Statistics.cs). The tracker loads with defer and may be
// blocked: until it is there, an event is dropped rather than queued.
window.rummiStats = {
    track(name, data) {
        window.umami?.track(name, data ?? undefined);
    }
};
