# Owner console redesign proposal

Assets for the owner console redesign feature request. Not a shipped feature.

- `before/`: the current console, captured from a local `adminpanel.demo_read_only` instance built from `main` (ee60d4f).
- `DESIGN.md`: the design system (themes, tokens, contrast, shape, type, icons, controls) for the console and sboxns.com.
- `after/`: the proposed screens, rendered from `mockup/`.
- `mockup/`: static HTML implementing `DESIGN.md` (Dark, Light and High contrast pages, solid icons in `icons.json`). Open any `.html` file in a browser. Resize below 760px for the mobile layout. Run `python3 generate.py .` inside `mockup/` to regenerate the pages.

Mockup data is illustrative, modeled on the demo seed (Island survival demo, `players` collection).
