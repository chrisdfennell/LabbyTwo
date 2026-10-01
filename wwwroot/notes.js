// Scrolls to a heading inside one note, for a [[Note#Heading]] link. The fragment in the
// address already brought the note itself into view; a fragment naming the heading could
// not, because two notes on one page can both have a "Restart" heading and the browser
// would always pick the first. So the heading is looked for inside the note's own element,
// by its words (any case) or by the anchor id the renderer gave it.
window.labbyNotes = {
    scrollToHeading(noteElementId, heading) {
        const note = document.getElementById(noteElementId);
        if (!note) {
            return;
        }
        const wanted = (heading || '').trim().toLowerCase();
        for (const element of note.querySelectorAll('h1, h2, h3, h4, h5, h6')) {
            if (element.textContent.trim().toLowerCase() === wanted || element.id === wanted) {
                element.scrollIntoView({ block: 'start' });
                return;
            }
        }
    },
};
