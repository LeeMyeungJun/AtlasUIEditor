# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Atlas UI Editor — a single-file C# WinForms desktop app for laying out 2D game UI. It loads a
sprite atlas (a PNG + a JSON sprite-rect list), lets you build a tree of UI nodes (Panel/Canvas/
Button/Text/Image/Rect) with a TreeView + PropertyGrid + live canvas, and saves/loads the layout
as JSON.

Everything lives in `AtlasUiEditor/Program.cs` (~930 lines, plain top-to-bottom WinForms code,
no MVVM/DI). Sections in the file, in order: `Program` (entry point) → `UINode`/`LayoutRoot`
(data model) → `AtlasSprite`/`AtlasRegistry` (atlas data) → `NodeTypeConverter`/
`SpriteNameConverter` (PropertyGrid dropdowns) → `NodeProxy` (PropertyGrid-facing wrapper around
`UINode`) → `CanvasPanel` (rendering/hit-testing/drag) → `MainForm` (toolbar, TreeView wiring,
load/save).

## Commands

```
dotnet build                 # build
dotnet run --project AtlasUiEditor   # run the WinForms app (Windows only)
```

No test project, linter, or CI config exists in this repo.

## Architecture notes worth knowing before editing

- **Dual JSON schema compatibility is the core design constraint.** `UINode` carries fields for
  two different schemas at once: `ColorHex`/`ImageUrl` (the editor's own `base_format.json`
  style) and `AtlasRegion` (the real game export style, alongside `Type` values like
  Canvas/Panel/Button/Text/Image). `EffectiveSprite` picks whichever of `AtlasRegion`/`ImageUrl`
  is set, and `NodeProxy.Sprite` writes to *both* fields simultaneously so a loaded file of either
  schema round-trips correctly. Any change to node fields must preserve this dual-write/dual-read
  behavior — see the example files under
  `AtlasUiEditor/bin/Debug/net8.0-windows/Resources/` (`base_format.json`-style: `1.json`;
  game-export-style: `Atlas.json`'s sibling `Resources` sprite list).
- **`NodeProxy` is the only thing PropertyGrid ever sees.** It wraps a `UINode` + the `CanvasPanel`
  so that every setter both mutates the model and triggers the right side effect (`Invalidate()`,
  `UpdateScrollSize()`, tree label refresh). Never bind `UINode` directly to PropertyGrid — new
  editable properties belong on `NodeProxy`, not `UINode`.
- **Nodes with no color and no sprite intentionally render as a dashed outline only** (see
  `CanvasPanel.DrawNode`), so structural containers (Panel/Canvas) don't paint over their
  children. Don't default `ColorHex` to a non-empty value — that was tried and hides content.
- **Coordinates are parent-relative**, not absolute; `CanvasPanel.DrawNode`/`HitTest` accumulate
  `absX/absY` recursively down `Children`. Dragging (`CanvasPanel_MouseMove`) writes back to the
  node's local X/Y, not the absolute position.
- **SplitContainer sizing is deliberately deferred to `Shown`/`Resize`**, not set at construction
  time, because setting `Panel1MinSize`/`SplitterDistance` before the control has real layout
  bounds throws. If you touch `MainForm`'s layout, keep using `ConfigureSplitter`/
  `SafeSetSplitterDistance` rather than setting `SplitterDistance` directly.
- Comments and UI strings in this file are in Korean; match that convention for any new
  user-facing text or comments in `Program.cs`.
