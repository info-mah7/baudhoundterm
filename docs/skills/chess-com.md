---
name: chess.com
description: Open chess.com in a browser and play a game against a bot
protocols: vnc
---
A whole game is long: give the endpoint a step limit of about 300 (Agent endpoints, Steps).

Getting there, keyboard first:
- Open the browser with `key win`, `type firefox` (or edge, chrome), `key enter`. Do not hunt for
  taskbar icons.
- Go to a page with `key ctrl+l`, then `type https://www.chess.com/play/computer` with enter.
- Close cookie banners, sign-up offers and tips with their Close, X, "No thanks" or `key esc`
  before anything else. They come back after games too.

Starting a game:
- The right-hand panel lists bots. Click a bot, then the green "Play" button at the bottom of that
  panel. If a second screen asks for colour or mode, choose and press "Play" again.
- If a click on "Play" is refused by the check, zoom on the bottom of the right panel once, then
  click the middle of the green button; it is wide, so aim at its centre, not its label.

Finding the board, once per game:
- Zoom on the board's top-left corner, then its bottom-right corner, and write both grid points in
  `seen`, e.g. `board 210,135 to 640,895`. Note which colour is at the bottom (white at the bottom
  when you play white: the a1 corner is bottom-left).
- Squares are equal: square width w = (right - left) / 8 and height h = (bottom - top) / 8, on
  the grid. They differ because the grid is 0-1000 in both directions on a wide screen.
- Centre of a square, file f = 0 for a to 7 for h, rank r = 1 to 8:
  - white at the bottom: x = left + (f + 0.5) * w, y = top + (8 - r + 0.5) * h
  - black at the bottom: x = left + (7 - f + 0.5) * w, y = top + (r - 0.5) * h
- Work squares out from these numbers. Do not re-find squares by eye.

Making a move:
- Click the piece's square, then the destination square. Targets: "white knight on g1", then
  "square f3" (or "black pawn on d5" for a capture), so the check knows a board square is meant.
- A move that is not legal snaps back; nothing happens. Read the position again.
- Promotion shows a column of pieces on the destination file: click the queen unless you need
  another piece.

After every move:
- Use `wait` for the bot's reply, then zoom on the move list in the right panel and read it.
- The move list is the truth. Keep the whole game in `seen` in algebraic notation, e.g.
  `moves: 1. e4 e5 2. Nf3 Nc6 3. Bb5 a6`. Pictures are dropped; this line is your only memory.
- Before choosing a move, rebuild the position from that list, not from an old picture. Check that
  your piece is really on the square you mean (zoom if unsure).

Playing:
- Develop pieces, castle early, keep the king safe, do not leave pieces where they can be taken
  for free. Before each move ask: what does the bot's last move attack?
- When the game ends (checkmate, resignation, draw), read the result from the dialog and say it,
  with the moves, in your final answer. Do not start another game unless the goal says so.
