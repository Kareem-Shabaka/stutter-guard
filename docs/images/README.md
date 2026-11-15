# Images used by the top-level README

## Present

`processor-map.svg` - the processor placement diagram, drawn from the plan the app recorded on the
reference machine. It is a diagram, not a screenshot: it exists because the claim it illustrates is about
*which core owns which device*, and that is a fact about the plan, not about a UI. Regenerate it from the
journal if the placement changes.

## Wanted: one screenshot of the app

The README would carry one screenshot of the Interrupts screen with a completed plan. It is not there yet,
so the README does not reference it - a reference to a missing file shows a broken image on the front door.

To add it:

1. Launch `StutterGuard.exe`, apply a plan, restart, open **Interrupts**.
2. Capture **the window only** - native resolution, no desktop, no browser chrome. The labels are the point.
3. Save as `docs/images/interrupts.png`.
4. Add this line to `README.md`, immediately below the processor-map figure and its caption:

   ```markdown
   ![Stutter Guard: the Interrupts screen, showing which core each device owns](docs/images/interrupts.png)
   ```

5. Confirm it renders: `README.md` in the GitHub web view, not a local editor.

Same rules for any further images: Home with its verdict line, and the interrupt check results with the
per-CPU counters - that second one is the evidence a move actually happened.
