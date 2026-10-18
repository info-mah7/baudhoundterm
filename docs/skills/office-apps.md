---
name: Office apps
description: Word, Excel and PowerPoint, and the ArtCraft alternatives WordCraft, GridCraft and DeckCraft
protocols: vnc
group: Office
default: off
---
These apps work alike: WordCraft follows Word, GridCraft follows Excel and DeckCraft follows
PowerPoint, with the same ribbon tabs and keyboard shortcuts. The ArtCraft apps draw their own
window, so read the screen as for any other app; their menus and dialogs look like Office's but
may sit in slightly different places. Prefer the keyboard; check the screenshot after each step.

All of them:
- Open the app from Start (`key win`, `type` its name, `key enter`) and maximize it (`key win+up`).
- New document `ctrl+n`, open `ctrl+o`, save `ctrl+s`, save as `f12`. In a save dialog the file
  name has focus: `type` the full path with enter, then check the title bar shows the new name.
  Choose the format in the dialog's type list when asked for one (.docx, .xlsx, .pptx are native
  to all of them).
- Undo `ctrl+z`, redo `ctrl+y`, copy `ctrl+c`, paste `ctrl+v`, find `ctrl+f`, replace `ctrl+h`,
  select all `ctrl+a`, bold `ctrl+b`, italic `ctrl+i`, underline `ctrl+u`.
- `alt` (or `f10`) shows the ribbon's key letters; read them on screen before pressing the next.
- A dialog you did not expect (recover a file, sign in, activate, update): read it and say what it
  asks before answering; never sign in or enter licence keys yourself.

Word / WordCraft:
- Type text with `type`; a new paragraph is `key enter`. Headings: `ctrl+alt+1`, `ctrl+alt+2`.
- Go to the start or end of the document with `ctrl+home` / `ctrl+end`.
- Read long text back with ocr_read or zoom before reporting it.

Excel / GridCraft:
- Go to a cell: `key ctrl+g` (Go To), `type` the reference (B4) with enter; or click the Name Box
  left of the formula bar and type it there.
- Enter values across a row with `tab` between cells and `enter` at the end; down a column with
  `enter`. A formula starts with `=`: `type =SUM(B2:B10)` then `key enter`.
- `f2` edits the active cell; `escape` cancels an edit. `ctrl+arrow` jumps to the edge of the data.
- Before reporting a number, select the cell and read the formula bar, not the grid: a narrow column
  shows ##### or rounds.

PowerPoint / DeckCraft:
- New slide `ctrl+m`. Click into a placeholder ("Click to add title") before typing into it.
- Start the slide show from the beginning with `f5`, end it with `escape`.
- Check each slide's text on the slide itself after typing: placeholders shrink or cut long text.
