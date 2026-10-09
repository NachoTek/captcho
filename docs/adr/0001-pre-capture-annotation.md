# Pre-capture annotation

Status: **Superseded by the post-capture V1 architecture (see Spec #24 / Issue #25).**

The original decision was to annotate on a live screen overlay _before_ the
capture is taken, not on the captured frame afterward. The user would see
their actual screen with annotation tools overlaid, draw or mark what they
want, and then the system captures screen plus annotations together. For
Selection mode, drawing the region and annotating happen in a single combined
overlay pass; for other modes that use target selection, annotation follows
target selection on the same overlay. This was chosen over the post-capture
model (used by ShareX, Snipaste) because it preserves spatial context — the
user annotates against live UI elements rather than a frozen screenshot — and
avoids the extra step of selecting-then-editing.

V1 reverses this decision and adopts the **post-capture** model. The captured
Frame is presented in a full-screen, borderless Annotation overlay that
replaces the preview while Annotation is active; users annotate against the
frozen Frame rather than the live desktop. This decision was reached in the
KDE Spectacle parity resolution (Spec #24) and is being implemented through
the runtime workflow session (#25 and following). This ADR is retained as a
record of the original reasoning; production terminology and the domain
glossary now describe the post-capture workflow.

See also:

- Spec #24 — Spec: KDE Spectacle still-image feature parity
- Issue #25 — Route Full Desktop through the runtime workflow
- `CONTEXT.md` — Capture Pipeline and User Interface glossary entries
  (Annotation, Workflow Session, Capture Mode routing)
